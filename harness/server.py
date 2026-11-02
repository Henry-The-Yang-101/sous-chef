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
from harness.contents import ContentsObserver
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

    def __init__(self, port=24703, factory=None, clock=time.monotonic, focus=focus_game, contents=None):
        self.factory = factory or (lambda: ActionClient(port, timeout=1.0))
        self.clock = clock
        self.focus = focus
        self.contents = contents
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
                      'bridgeVersion': None, 'supportedActions': None,
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
                              bridgeVersion=self.client.hello.get('bridgeVersion'),
                              supportedActions=self.client.hello.get('actions'),
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
            self.value.update(connected=False, sessionId=None, bridgeVersion=None,
                              supportedActions=None, catalog={}, control={}, error=str(error))

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
            if self.contents:
                self.contents.decorate(result)
            return result

    def navigate(self, viewer, session, destination, instance):
        return self.act(viewer, session, destination, instance, 'navigate_to')

    def act(self, viewer, session, destination, instance, operation, arguments=None):
        allowed = {'navigate_to', 'pick_up', 'place_at', 'add_ingredient', 'work_at',
                   'activate', 'discard', 'wait_until', 'throw_to', 'throw_to_player',
                   'move_item', 'fetch_ingredient', 'get_plate', 'get_utensil', 'stage_item', 'return_utensil', 'clear_station'}
        jobs = {'move_item', 'fetch_ingredient', 'get_plate', 'get_utensil', 'stage_item', 'return_utensil', 'clear_station'}
        if not isinstance(operation, str):
            raise HarnessError('Unsupported action')
        arguments = {} if arguments is None else arguments
        keys = {'work_at': {'until', 'timeout'}, 'wait_until': {'condition', 'timeout'},
                'activate': {'desired_state'}, 'move_item': {'item_id', 'source_instance_id'},
                'stage_item': {'item_id', 'source_instance_id'}, 'clear_station': {'station_id', 'source_instance_id'},
                'fetch_ingredient': {'ingredient'}, 'get_utensil': {'utensil_type'}}.get(operation, set())
        if operation not in allowed or not isinstance(arguments, dict) or arguments.keys() - keys:
            raise HarnessError('Unsupported action or arguments')
        if operation == 'wait_until' and 'condition' not in arguments:
            raise HarnessError('Choose a wait condition')
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
                options = catalog.get('destinations', []) if operation == 'navigate_to' else (
                    catalog.get('players', []) if operation == 'throw_to_player' else
                    catalog.get('items', []) if operation == 'return_utensil' else
                    catalog.get('deliveryDestinations', []) if operation in jobs else catalog.get('targets', []))
                chosen = next((d for d in options
                               if d['id'] == destination and d['objectInstanceId'] == instance), None)
                if chosen is None:
                    reason = catalog.get('navigation', {}).get('catalogReason')
                    raise HarnessError(reason or 'That destination is no longer reachable')
                if operation not in jobs and operation != 'navigate_to' and operation not in chosen.get('operations', []):
                    raise HarnessError('That action is no longer available for this target')
                job_args = None
                if operation in jobs:
                    if not any(a.get('name') == operation for a in catalog.get('actions', [])):
                        raise HarnessError('That job is no longer available')
                    if operation in {'move_item', 'stage_item', 'clear_station'}:
                        key = 'station_id' if operation == 'clear_station' else 'item_id'
                        sources = catalog.get('targets', []) if operation == 'clear_station' else catalog.get('items', [])
                        source = next((s for s in sources if s['id'] == arguments.get(key) and
                                       type(arguments.get('source_instance_id')) is int and s['objectInstanceId'] == arguments['source_instance_id']), None)
                        if source is None:
                            raise HarnessError('That source item or station changed; choose it again')
                        job_args = (source['id'], destination)
                    elif operation == 'fetch_ingredient':
                        ingredient = next((i for i in catalog.get('ingredients', []) if i['id'] == arguments.get('ingredient')), None)
                        if ingredient is None:
                            raise HarnessError('Choose a currently observed ingredient')
                        job_args = (ingredient['id'], destination)
                    elif operation == 'get_utensil':
                        if arguments.get('utensil_type') not in catalog.get('utensilTypes', []):
                            raise HarnessError('Choose a currently observed utensil type')
                        job_args = (arguments['utensil_type'], destination)
                    else:
                        job_args = (destination,)
                    selector = '' if operation == 'get_plate' else job_args[0]
                    pairs = catalog.get('jobOptions', {}).get(operation, [])
                    if not any(p.get('sourceId') == selector and p.get('destinationIds') and
                               (operation == 'return_utensil' or destination in p['destinationIds']) for p in pairs):
                        raise HarnessError('That source and destination no longer form a valid job; choose again')
                with self.state_lock:
                    if generation != self.generation:
                        raise HarnessError('Cancelled by Stop')
                target = chosen['playerId'] if operation == 'throw_to_player' else destination
                control = getattr(self.client, operation)(*job_args) if job_args is not None else getattr(self.client, operation)(target, **arguments)
                with self.state_lock:
                    self.owner = viewer
                    self.owner_seen = self.clock()
                    self.value['lastCommand'] = {'operation': operation, 'ok': True,
                                                 'target': chosen, 'message': operation.replace('_', ' ') + ' requested'}
                self._publish(catalog, control)
                return self.state(viewer)
            except ControlError as error:
                raise HarnessError(str(error)) from error
            except (ValueError, TypeError) as error:
                raise HarnessError(str(error)) from error
            except (OSError, KeyError) as error:
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
            if path in {'/api/navigate', '/api/action'}:
                if not isinstance(data.get('destinationId'), str) or type(data.get('objectInstanceId')) is not int or not isinstance(data.get('sessionId'), str):
                    raise ValueError('Missing destination identity')
                if path == '/api/navigate':
                    result = self.server.bridge.navigate(viewer, data['sessionId'], data['destinationId'], data['objectInstanceId'])
                else:
                    if not isinstance(data.get('operation'), str):
                        raise ValueError('Missing action')
                    result = self.server.bridge.act(viewer, data['sessionId'], data['destinationId'], data['objectInstanceId'], data['operation'], data.get('arguments'))
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


def serve(port=8765, bridge_port=24703, local_only=False, tunnel=False, public_url=None, state_port=24702):
    contents = ContentsObserver(state_port)
    bridge = HarnessBridge(bridge_port, contents=contents)
    server = ThreadingHTTPServer(('127.0.0.1' if local_only else '0.0.0.0', port), Handler)
    server.bridge, server.token = bridge, secrets.token_urlsafe(32)
    server.sharing = PhoneShare(server.server_port, server.token,
                                () if local_only else lan_addresses(), public_url)
    print(f'Sous Chef actions: http://127.0.0.1:{server.server_port}', flush=True)
    print('Scan the phone QR code on that page; keep your phone on the same Wi-Fi.', flush=True)
    print('Player 2 only. Ctrl+C stops the harness and releases its action.', flush=True)
    try:
        if threading.current_thread() is threading.main_thread():
            signal.signal(signal.SIGTERM, signal.default_int_handler)
        if tunnel:
            server.sharing.start_tunnel(server.server_port)
        contents.start()
        bridge.start()
        server.serve_forever(poll_interval=.2)
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()
        bridge.close()
        contents.close()
        server.sharing.close()


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--port', type=int, default=8765)
    parser.add_argument('--bridge-port', type=int, default=24703)
    parser.add_argument('--state-port', type=int, default=24702, help='Full observation stream for live item contents')
    parser.add_argument('--local-only', action='store_true', help='Disable access over local Wi-Fi')
    sharing = parser.add_mutually_exclusive_group()
    sharing.add_argument('--tunnel', action='store_true', help='Start a temporary internet link using cloudflared')
    sharing.add_argument('--public-url', help='Existing forwarded http(s) origin to encode in the phone QR code')
    args = parser.parse_args()
    try:
        serve(args.port, args.bridge_port, args.local_only, args.tunnel, args.public_url, args.state_port)
    except (RuntimeError, ValueError) as error:
        parser.exit(1, str(error) + '\n')
