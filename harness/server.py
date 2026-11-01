#!/usr/bin/env python3
"""Run with python3 -m harness.server; no third-party packages required."""
import argparse
import copy
import json
import secrets
import signal
import subprocess
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlsplit

from clients.actions import ActionClient
from clients.control import ControlError
from harness.sharing import PhoneShare, lan_addresses


class HarnessError(Exception):
    pass


def focus_game():
    """Bring the already running Mac game forward after an explicit browser click."""
    if sys.platform != 'darwin':
        raise HarnessError('Focus the game before navigating on this platform')
    try:
        subprocess.run(['/usr/bin/open', '-b', 'unity.Team17.Overcooked2'],
                       check=True, capture_output=True, timeout=2)
    except (OSError, subprocess.SubprocessError) as error:
        raise HarnessError('Could not focus Overcooked! 2') from error


class HarnessBridge:
    """One persistent action connection shared by all browser views.

    Network requests are serialized, but state reads and browser heartbeats never
    wait for game I/O. Reconnection observes only: commands are never replayed.
    """
    POLL_SECONDS = .2
    STALE_SECONDS = 1.5
    VIEWER_TIMEOUT = 5.0

    def __init__(self, port=24703, factory=None, clock=time.monotonic, focus=focus_game):
        self.factory = factory or (lambda: ActionClient(port, timeout=1.0))
        self.clock = clock
        self.focus = focus
        self.io_lock = threading.Lock()
        self.state_lock = threading.RLock()
        self.halt = threading.Event()
        self.client = None
        self.thread = None
        self.owner = None
        self.owner_seen = 0
        self.generation = 0
        self.observed = None
        self.value = {'connected': False, 'sessionId': None, 'catalog': {},
                      'control': {}, 'error': 'Connecting to the game bridge',
                      'lastCommand': None}

    def start(self):
        self.thread = threading.Thread(target=self._run, name='bridge-observer', daemon=True)
        self.thread.start()

    def _run(self):
        while not self.halt.is_set():
            self.poll()
            self.halt.wait(self.POLL_SECONDS)

    def _connect(self):
        if self.client is None:
            self.client = self.factory()
        return self.client

    def _publish(self, catalog, control):
        with self.state_lock:
            self.observed = self.clock()
            self.value.update(connected=True, sessionId=self.client.hello['sessionId'],
                              catalog=catalog, control=control, error=None,
                              updatedAt=time.time())
            if not control.get('ownedByYou'):
                self.owner = None

    def _failed(self, error):
        # Closing the owning socket also cancels navigation in the plugin.
        if self.client is not None:
            try:
                self.client.close()
            except (OSError, ControlError):
                pass
            self.client = None
        with self.state_lock:
            self.observed = None
            self.owner = None
            self.value.update(connected=False, sessionId=None, catalog={}, control={}, error=str(error))

    def poll(self):
        with self.io_lock:
            try:
                client = self._connect()
                with self.state_lock:
                    expired = self.owner is not None and self.clock() - self.owner_seen > self.VIEWER_TIMEOUT
                if expired:
                    # Release only our connection's lease; do not stop another API
                    # client if our action completed and it took over in the meantime.
                    try:
                        client.release()
                    except ControlError as error:
                        if str(error) != 'not_owner':
                            raise
                    with self.state_lock:
                        self.owner = None
                        self.value['lastCommand'] = {'operation': 'stop', 'ok': True,
                                                     'message': 'Browser disconnected; released Player 2'}
                catalog = client.available_actions()
                self._publish(catalog, client.last_response['control'])
            except (OSError, ControlError, ValueError, KeyError) as error:
                self._failed(error)

    def state(self, viewer=None):
        with self.state_lock:
            if viewer and viewer == self.owner:
                self.owner_seen = self.clock()
            result = copy.deepcopy(self.value)
            age = None if self.observed is None else self.clock() - self.observed
            result['ageMs'] = None if age is None else round(age * 1000)
            result['fresh'] = age is not None and age <= self.STALE_SECONDS and result['connected']
            result['viewerOwnsAction'] = bool(viewer and viewer == self.owner)
            if not result['fresh']:
                result['catalog'] = {}
            return result

    def navigate(self, viewer, session, destination, instance):
        with self.state_lock:
            generation = self.generation
        with self.io_lock:
            with self.state_lock:
                if generation != self.generation:
                    raise HarnessError('Cancelled by Stop')
                if not self.state()['fresh'] or self.client is None:
                    raise HarnessError('Live game state is unavailable')
                if session != self.client.hello['sessionId']:
                    raise HarnessError('The game restarted; choose a current destination')
            try:
                # Revalidate against the game, not just the browser's most recent
                # rendered list. Never substitute another pot/plate with the same name.
                catalog = self.client.available_actions()
                self._publish(catalog, self.client.last_response['control'])
                if self.client.last_response['control'].get('reason') == 'game_unfocused':
                    self.focus()
                    deadline = time.monotonic() + 1.5
                    while True:
                        control = self.client.status()
                        if control.get('available'):
                            break
                        if control.get('reason') != 'game_unfocused' or time.monotonic() >= deadline:
                            raise HarnessError(control.get('reason') or 'The game is not accepting input')
                        with self.state_lock:
                            if generation != self.generation:
                                raise HarnessError('Cancelled by Stop')
                        time.sleep(.03)
                    # Focus may cross a game update or round transition. Re-observe
                    # before validating the exact target and issuing the sole command.
                    catalog = self.client.available_actions()
                    self._publish(catalog, self.client.last_response['control'])
                chosen = next((d for d in catalog.get('destinations', [])
                               if d['id'] == destination and d['objectInstanceId'] == instance), None)
                if chosen is None:
                    reason = catalog.get('navigation', {}).get('catalogReason')
                    raise HarnessError(reason or 'That destination is no longer reachable')
                with self.state_lock:
                    if generation != self.generation:
                        raise HarnessError('Cancelled by Stop')
                control = self.client.navigate_to(destination)
                with self.state_lock:
                    self.owner = viewer
                    self.owner_seen = self.clock()
                    self.value['lastCommand'] = {'operation': 'navigate_to', 'ok': True,
                                                 'target': chosen, 'message': 'Navigation requested'}
                self._publish(catalog, control)
                return self.state(viewer)
            except ControlError as error:
                raise HarnessError(str(error)) from error
            except (OSError, ValueError, KeyError) as error:
                self._failed(error)
                raise HarnessError('Bridge disconnected; command was not retried') from error

    def stop(self):
        with self.state_lock:
            self.generation += 1
        with self.io_lock:
            try:
                client = self._connect()
                control = client.stop()
                with self.state_lock:
                    self.owner = None
                    self.value['lastCommand'] = {'operation': 'stop', 'ok': True, 'message': 'Stopped'}
                # Discard the old catalogue; the next poll establishes fresh options.
                self._publish({}, control)
                return self.state()
            except (OSError, ControlError, ValueError, KeyError) as error:
                self._failed(error)
                raise HarnessError('Bridge unavailable; owning connection closed') from error

    def leave(self, viewer):
        with self.io_lock:
            with self.state_lock:
                if viewer != self.owner:
                    return
            if self.client is not None:
                try:
                    self.client.release()
                except (OSError, ControlError):
                    self._failed('Browser closed')
            with self.state_lock:
                self.owner = None

    def close(self):
        self.halt.set()
        if self.thread:
            self.thread.join(timeout=4)
        with self.io_lock:
            self._failed('Harness stopped')


ASSETS = Path(__file__).parent / 'web'


class Handler(BaseHTTPRequestHandler):
    def setup(self):
        super().setup()
        self.connection.settimeout(4)

    def log_message(self, *_):
        pass

    def send(self, code, data, content_type='application/json'):
        body = json.dumps(data).encode() if content_type == 'application/json' else data
        self.send_response(code)
        self.send_header('Content-Type', content_type)
        self.send_header('Content-Length', str(len(body)))
        self.send_header('Cache-Control', 'no-store')
        self.send_header('X-Content-Type-Options', 'nosniff')
        self.send_header('Referrer-Policy', 'no-referrer')
        self.send_header('Content-Security-Policy', "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' blob:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'")
        self.end_headers()
        try:
            self.wfile.write(body)
        except (BrokenPipeError, ConnectionResetError):
            pass

    def local_request(self):
        host = self.headers.get('Host')
        origin = self.headers.get('Origin')
        if not self.server.sharing.valid_origin(host, origin):
            self.send(403, {'error': 'Unrecognized host or cross-origin request'})
            return False
        return True

    def can_read(self):
        sharing = self.server.sharing
        if (sharing.is_local(self.headers.get('Host'), self.client_address[0])
                or sharing.authorized(self.headers.get('X-Page-Token'))):
            return True
        self.send(403, {'error': 'Scan the QR code on the Mac to open this harness'})
        return False

    def do_GET(self):
        if not self.local_request():
            return
        path = urlsplit(self.path).path
        if path.startswith('/api/') and not self.can_read():
            return
        if path == '/api/bootstrap':
            self.send(200, {'token': self.server.token, 'pollMs': 250})
        elif path == '/api/share':
            self.send(200, self.server.sharing.describe())
        elif path == '/api/state':
            self.send(200, self.server.bridge.state(self.headers.get('X-Viewer-Id')))
        elif path in {'/', '/app.js', '/style.css'}:
            name, content_type = {'/': ('index.html', 'text/html; charset=utf-8'),
                                  '/app.js': ('app.js', 'text/javascript; charset=utf-8'),
                                  '/style.css': ('style.css', 'text/css; charset=utf-8')}[path]
            self.send(200, (ASSETS / name).read_bytes(), content_type)
        else:
            self.send(404, {'error': 'Not found'})

    def do_POST(self):
        if not self.local_request():
            return
        try:
            size = int(self.headers.get('Content-Length', '0'))
            if not 0 < size <= 4096:
                raise ValueError('Invalid request size')
            data = json.loads(self.rfile.read(size))
            if not isinstance(data, dict) or not isinstance(data.get('token'), str) or not secrets.compare_digest(data['token'], self.server.token):
                self.send(403, {'error': 'Invalid page token; reload the harness'})
                return
            viewer = data.get('viewerId')
            if not isinstance(viewer, str) or not 1 <= len(viewer) <= 80:
                raise ValueError('Missing browser identity')
            path = urlsplit(self.path).path
            if path == '/api/navigate':
                if not isinstance(data.get('destinationId'), str) or not isinstance(data.get('objectInstanceId'), int) or not isinstance(data.get('sessionId'), str):
                    raise ValueError('Missing destination identity')
                result = self.server.bridge.navigate(viewer, data['sessionId'], data['destinationId'], data['objectInstanceId'])
            elif path == '/api/stop':
                result = self.server.bridge.stop()
            elif path == '/api/leave':
                self.server.bridge.leave(viewer)
                result = {'ok': True}
            else:
                self.send(404, {'error': 'Not found'})
                return
            self.send(200, result)
        except HarnessError as error:
            self.send(409, {'error': str(error)})
        except (ValueError, TypeError) as error:
            self.send(400, {'error': str(error)})


def serve(port=8765, bridge_port=24703, local_only=False, tunnel=False, public_url=None):
    bridge = HarnessBridge(bridge_port)
    server = ThreadingHTTPServer(('127.0.0.1' if local_only else '0.0.0.0', port), Handler)
    server.bridge, server.token = bridge, secrets.token_urlsafe(32)
    server.sharing = PhoneShare(server.server_port, server.token,
                                () if local_only else lan_addresses(), public_url)
    print(f'Sous Chef navigation: http://127.0.0.1:{server.server_port}', flush=True)
    print('Scan the phone QR code on that page; keep your phone on the same Wi-Fi.', flush=True)
    print('Player 2 only. Ctrl+C stops the harness and releases its action.', flush=True)
    try:
        if threading.current_thread() is threading.main_thread():
            signal.signal(signal.SIGTERM, signal.default_int_handler)
        if tunnel:
            server.sharing.start_tunnel(server.server_port)
        bridge.start()
        server.serve_forever(poll_interval=.2)
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()
        bridge.close()
        server.sharing.close()


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--port', type=int, default=8765)
    parser.add_argument('--bridge-port', type=int, default=24703)
    parser.add_argument('--local-only', action='store_true', help='Disable access over local Wi-Fi')
    sharing = parser.add_mutually_exclusive_group()
    sharing.add_argument('--tunnel', action='store_true', help='Start a temporary internet link using cloudflared')
    sharing.add_argument('--public-url', help='Existing forwarded http(s) origin to encode in the phone QR code')
    args = parser.parse_args()
    try:
        serve(args.port, args.bridge_port, args.local_only, args.tunnel, args.public_url)
    except (RuntimeError, ValueError) as error:
        parser.exit(1, str(error) + '\n')
