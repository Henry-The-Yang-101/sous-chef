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
from clients.capabilities import ACTION_NAMES, registry
from clients.control import ControlError
from harness.capabilities import snapshot as capability_snapshot
from harness.contents import ContentsObserver
from harness.scheduler import Scheduler, QueueConflict, context_of
from harness.sharing import PhoneShare, lan_addresses
from harness.voice import VoiceService
from harness.voice.game_input import GamePushToTalk
from harness.decision.service import InterpreterService
from harness.decision.usage import RoundUsage, round_key


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

    Network requests are serialized, but browser state reads never
    wait for game I/O. Reconnection observes only: commands are never replayed.
    """
    POLL_SECONDS = .2
    STALE_SECONDS = 1.5

    def __init__(self, port=24703, factory=None, clock=time.monotonic, focus=focus_game,
                 contents=None, journal_path=':memory:', decision_settings=None,
                 decision_transport=None, jev_usage_path=None):
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
        self.generation = 0
        self.observed = None
        self.value = {'connected': False, 'sessionId': None, 'catalog': {},
                      'bridgeVersion': None, 'supportedActions': None, 'nativeCapabilities': None,
                      'control': {}, 'error': 'Connecting to the game bridge',
                      'lastCommand': None}
        self.scheduler = Scheduler(self, journal_path)
        self.voice = VoiceService()
        self.voice_endpoint = None
        self.game_voice = GamePushToTalk(self.voice, self._voice_endpoint)
        self.jev_usage = RoundUsage(jev_usage_path)
        self.interpreter = InterpreterService(self, self.voice.gate, decision_settings, decision_transport, self.jev_usage)
        self.voice.on_command = self.interpreter.submit
        if self.interpreter.configured:
            self.voice.settings['inspection'] = 'jev'

    def start(self):
        self.game_voice.start()
        self.thread = threading.Thread(target=self._run, name='bridge-observer', daemon=True)
        self.thread.start()

    def _run(self):
        while not self.halt.is_set():
            self.poll()
            try:
                self.scheduler.tick()
            except Exception as error:
                # Any unexpected coordinator error fails closed; Stop stays usable.
                self.scheduler.freeze('Queue unavailable: ' + str(error))
                with self.state_lock:
                    self.value['error'] = 'Queue unavailable: ' + str(error)
            self.halt.wait(self.POLL_SECONDS)

    def _connect(self):
        if self.client is None:
            self.client = self.factory()
        return self.client

    def _voice_endpoint(self):
        with self.state_lock:
            return self.voice_endpoint

    def _publish(self, catalog, control, hello=None):
        with self.state_lock:
            hello = hello or self.client.hello
            prior = self.value['control']
            if self.value['sessionId'] == hello['sessionId'] and prior.get('dispatchGeneration', -1) > control.get('dispatchGeneration', -1):
                return  # A pre-Stop response must not roll back the native fence.
            self.observed = self.clock()
            voice_port = hello.get('voicePort')
            self.voice_endpoint = ((hello['sessionId'], voice_port)
                                   if type(voice_port) is int and 1 <= voice_port <= 65535 else None)
            self.value.update(connected=True, sessionId=hello['sessionId'],
                              bridgeVersion=hello.get('bridgeVersion'),
                              supportedActions=hello.get('actions'),
                              nativeCapabilities=hello.get('capabilities'),
                              catalog=catalog, control=control, error=None,
                              updatedAt=time.time())
            self.jev_usage.observe(round_key(hello['sessionId'], control))
            if not control.get('ownedByYou'):
                self.owner = None

    def _failed(self, error):
        # Accepted autonomous work stays in the plugin across reconnection.
        if self.client is not None:
            try:
                self.client.close()
            except (OSError, ControlError):
                pass
            self.client = None
        with self.state_lock:
            self.observed = None
            self.owner = None
            self.voice_endpoint = None
            self.value.update(connected=False, sessionId=None, bridgeVersion=None,
                              supportedActions=None, nativeCapabilities=None, catalog={}, control={}, error=str(error))

    def poll(self):
        with self.io_lock:
            try:
                client = self._connect()
                catalog = client.available_actions()
                self._publish(catalog, client.last_response['control'])
            except (OSError, ControlError, ValueError, KeyError) as error:
                self._failed(error)

    def observation(self, viewer=None):
        """Raw game state only; safe under scheduler/gate locks without lock inversion."""
        with self.state_lock:
            result = copy.deepcopy(self.value)
            age = None if self.observed is None else self.clock() - self.observed
            result['ageMs'] = None if age is None else round(age * 1000)
            result['fresh'] = age is not None and age <= self.STALE_SECONDS and result['connected']
            result['viewerOwnsAction'] = bool(viewer and viewer == self.owner)
            if not result['fresh']:
                result['catalog'] = {}
            result['capabilities'] = capability_snapshot(result)
            if self.contents:
                self.contents.decorate(result)
        return result

    def state(self, viewer=None):
        result = self.observation(viewer)
        result['queue'] = self.scheduler.snapshot()
        result['queue']['nativeContext'] = context_of(result)
        result['voice'] = self.voice.snapshot()
        result['gamePushToTalkKey'] = self.game_voice.key
        result['interpreter'] = self.interpreter.snapshot()
        result['interpreter']['usage'] = self.jev_usage.snapshot()
        return result

    def navigate(self, viewer, session, destination, instance):
        return self.act(viewer, session, destination, instance, 'navigate_to')

    def act(self, viewer, session, destination, instance, operation, arguments=None):
        if operation == 'task':
            raise HarnessError('Task controls use the shared plan queue')
        allowed = set(ACTION_NAMES) - {'stop'}
        jobs = {c['name'] for c in registry()['contracts'] if c.get('category') == 'logistics'}
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
        try:
            self.scheduler.immediate()
        except QueueConflict as error:
            raise HarnessError(str(error)) from error
        with self.state_lock:
            generation = self.generation
        with self.io_lock:
            with self.state_lock:
                if generation != self.generation:
                    raise HarnessError('Cancelled by Stop')
                if self.observed is None or self.clock() - self.observed > self.STALE_SECONDS or self.client is None:
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
                # Capture the fence from this connection's validation response,
                # before the final local generation check. A concurrent Stop may
                # publish a newer fence; this old command must never adopt it.
                context = context_of({'fresh': True, 'sessionId': self.client.hello['sessionId'],
                                      'control': self.client.last_response['control']})
                with self.state_lock:
                    if generation != self.generation:
                        raise HarnessError('Cancelled by Stop')
                target = chosen['playerId'] if operation == 'throw_to_player' else destination
                # The same fence protects immediate actions from a racing Stop.
                if context and isinstance(self.client, ActionClient):
                    self.client.dispatch_context = context
                control = (getattr(self.client, operation)(*job_args) if job_args is not None else getattr(self.client, operation)(target, **arguments))
                with self.state_lock:
                    self.owner = viewer
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

    def queue_io(self, operation, entry):
        if operation in ('cancel', 'suspend'):
            # A separate socket bypasses a normal action/catalog request waiting
            # on I/O. Native priority and fences decide the actual ordering.
            with self.factory() as client:
                ctx = entry['context']
                if client.hello['sessionId'] != ctx['session']:
                    raise HarnessError('Bridge session changed during cancellation')
                if operation == 'suspend':
                    receipt = client.suspend_task(entry['id'], ctx['epoch'], ctx['fence'], entry['requestId'])
                elif entry.get('preserveTasks') and self.value['control'].get('taskSuspensionVersion'):
                    receipt = client.cancel(entry['id'], ctx['epoch'], ctx['fence'], preserve_tasks=True)
                else:
                    receipt = client.cancel(entry['id'], ctx['epoch'], ctx['fence'])
                self._publish({}, client.last_response['control'], client.hello)
                return receipt
        with self.io_lock:
            try:
                client = self._connect()
                ctx = entry['context']
                if client.hello['sessionId'] != ctx['session']:
                    raise HarnessError('Bridge session changed; dispatch was withheld')
                if operation == 'hold':
                    receipt = client.hold_schedule(entry['id'], ctx['epoch'], ctx['fence'], entry['hold'])
                elif operation == 'enqueue':
                    spec = entry['spec']
                    receipt = client.enqueue(entry['requestId'], ctx['epoch'], ctx['fence'], spec['action'], spec['arguments'],
                        spec['repeat'].get('count'), urgent=entry.get('urgent', False), version=spec['capability_version'])
                elif operation == 'forget':
                    receipt = client.forget_task(entry['id'], ctx['epoch'], ctx['fence'], entry['requestId'])
                elif operation == 'resume':
                    receipt = client.resume_task(entry['resumeId'], ctx['epoch'], ctx['fence'], entry['requestId'])
                elif operation == 'receipt':
                    receipt = client.receipt(entry['requestId'])
                else:
                    spec = entry['spec']
                    receipt = client.execute(entry['requestId'], ctx['epoch'], ctx['fence'],
                        spec['action'], spec['arguments'], spec['repeat'].get('count'), version=spec['capability_version'])
                self._publish(self.value['catalog'], client.last_response['control'])
                return receipt
            except (OSError, ControlError, ValueError, KeyError) as error:
                self._failed(error)
                raise

    def accept_plan(self, plan, context, focus=False):
        try:
            result = self.scheduler.accept(plan, context)
        except ValueError as error:
            raise HarnessError(str(error)) from error
        with self.state_lock:
            needs_focus = focus and self.value['control'].get('reason') == 'game_unfocused'
        if needs_focus:
            # Older bridges still require focus to execute an explicit Run now.
            try:
                self.focus()
            except HarnessError:
                pass  # Accepted work remains visibly waiting for native input.
        return dict(self.state(), operationResult=result)

    def edit_queue(self, request):
        try:
            result = self.scheduler.edit(request)
        except ValueError as error:
            raise HarnessError(str(error)) from error
        return dict(self.state(), operationResult=result)

    def stop(self):
        self.voice.invalidate()
        with self.state_lock:
            self.generation += 1
        # Native Stop remains available even if the local journal cannot write.
        journal_error = None
        try:
            barrier = self.scheduler.stop()
        except Exception as error:
            journal_error = error
            self.scheduler.freeze('Queue journal failed during Stop')
            barrier = None
        try:
            client = self.factory()
            try:
                control = client.stop()
                hello = client.hello
            finally:
                client.close()
            self._publish({}, control, hello)
            if barrier:
                self.scheduler.confirm_stop(barrier['id'], context_of(self.state()))
            with self.state_lock:
                self.owner = None
                self.value['lastCommand'] = {'operation': 'stop', 'ok': True, 'message': 'Stopped'}
            if journal_error:
                raise HarnessError('Native work stopped; queue journal failed: ' + str(journal_error))
            return self.state()
        except (OSError, ControlError, ValueError, KeyError) as error:
            raise HarnessError('Stop is unconfirmed; dispatch is held until the bridge reconnects') from error

    def leave(self, viewer):
        # Page lifetime is independent of an accepted job. Stop is the explicit
        # cancellation path, including from a new tab or a reconnected harness.
        with self.state_lock:
            if viewer == self.owner:
                self.owner = None

    def close(self):
        self.halt.set()
        self.game_voice.close()
        self.voice.close()
        self.interpreter.close()
        if self.thread:
            self.thread.join(timeout=4)
        with self.io_lock:
            self._failed('Harness stopped')
        usage_error = self.jev_usage.close()
        if usage_error:
            print(usage_error, file=sys.stderr, flush=True)
        self.scheduler.close()


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
            self.send(200, {'token': self.server.token, 'pollMs': 250, 'capabilityRegistry': registry()})
        elif path == '/api/share':
            self.send(200, self.server.sharing.describe())
        elif path == '/api/state':
            self.send(200, self.server.bridge.state(self.headers.get('X-Viewer-Id')))
        elif path == '/api/capabilities':
            self.send(200, {'registry': registry(), 'state': self.server.bridge.state()['capabilities']})
        elif path in {'/', '/app.js', '/visuals.js', '/style.css'}:
            name, content_type = {'/': ('index.html', 'text/html; charset=utf-8'),
                                  '/app.js': ('app.js', 'text/javascript; charset=utf-8'),
                                  '/visuals.js': ('visuals.js', 'text/javascript; charset=utf-8'),
                                  '/style.css': ('style.css', 'text/css; charset=utf-8')}[path]
            self.send(200, (ASSETS / name).read_bytes(), content_type)
        else:
            self.send(404, {'error': 'Not found'})

    def do_POST(self):
        if not self.local_request():
            return
        try:
            size = int(self.headers.get('Content-Length', '0'))
            if not 0 < size <= 32768:
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
            elif path == '/api/plan':
                if type(data.get('focus', False)) is not bool:
                    raise ValueError('Invalid focus setting')
                result = self.server.bridge.accept_plan(data.get('plan'), data.get('context'), data.get('focus', False))
            elif path == '/api/queue':
                result = self.server.bridge.edit_queue(data.get('edit'))
            elif path == '/api/voice':
                self.server.bridge.voice.command(data.get('voice'), viewer)
                result = self.server.bridge.state(viewer)
            elif path == '/api/interpreter':
                self.server.bridge.interpreter.control(data.get('interpreter'))
                result = self.server.bridge.state(viewer)
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


def serve(port=8765, bridge_port=24703, local_only=False, tunnel=None, public_url=None, state_port=24702, journal_path=None):
    contents = ContentsObserver(state_port)
    local = Path(__file__).resolve().parents[1] / '.local'
    journal_path = journal_path or local / f'queue-{bridge_port}.sqlite3'
    bridge = HarnessBridge(bridge_port, contents=contents, journal_path=journal_path,
                           jev_usage_path=local / 'jev-input-tokens.csv')
    server = ThreadingHTTPServer(('127.0.0.1' if local_only else '0.0.0.0', port), Handler)
    server.bridge, server.token = bridge, secrets.token_urlsafe(32)
    server.sharing = PhoneShare(server.server_port, server.token,
                                () if local_only else lan_addresses(), public_url)
    print(f'Sous Chef actions: http://127.0.0.1:{server.server_port}', flush=True)
    use_tunnel = tunnel if tunnel is not None else not (local_only or public_url)
    print('Scan the phone QR code on that page to connect.', flush=True)
    print('Player 2 only. Use Stop to cancel a job; Ctrl+C closes the harness.', flush=True)
    try:
        if threading.current_thread() is threading.main_thread():
            signal.signal(signal.SIGTERM, signal.default_int_handler)
        if use_tunnel:
            try:
                server.sharing.start_tunnel(server.server_port)
            except (RuntimeError, OSError) as error:
                if tunnel is True:
                    raise
                server.sharing.status = f'Internet link unavailable: {error}. Same Wi-Fi access is still available.'
                print(server.sharing.status, file=sys.stderr, flush=True)
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


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--port', type=int, default=8765)
    parser.add_argument('--bridge-port', type=int, default=24703)
    parser.add_argument('--queue-journal', type=Path, help='Local SQLite journal (one harness per journal)')
    parser.add_argument('--state-port', type=int, default=24702, help='Full observation stream for live item contents')
    parser.add_argument('--local-only', action='store_true', help='Disable access over local Wi-Fi')
    sharing = parser.add_mutually_exclusive_group()
    sharing.add_argument('--tunnel', action='store_true', default=None, help='Require a temporary Cloudflare internet link (started by default when available)')
    sharing.add_argument('--no-tunnel', action='store_false', dest='tunnel', help='Use same-Wi-Fi links without starting Cloudflare')
    sharing.add_argument('--public-url', help='Existing forwarded http(s) origin to encode in the phone QR code')
    args = parser.parse_args(argv)
    try:
        serve(args.port, args.bridge_port, args.local_only, args.tunnel, args.public_url, args.state_port, args.queue_journal)
    except (RuntimeError, ValueError, OSError) as error:
        parser.exit(1, str(error) + '\n')


if __name__ == '__main__':
    main()
