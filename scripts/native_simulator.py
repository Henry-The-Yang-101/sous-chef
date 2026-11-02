#!/usr/bin/env python3
"""Control a silent, windowless instance of the installed game's Unity player."""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time
import uuid

from build import DEFAULT_GAME, build
from toolchain import ROOT, TOOLS

DEFAULT_RUNTIME = ROOT / '.local/native-player'


def is_running(runtime):
    try:
        pid = json.loads((runtime / 'process.json').read_text())['pid']
        result = subprocess.run(['ps', '-p', str(pid), '-o', 'command='], capture_output=True, text=True)
        return result.returncode == 0 and str(runtime / 'player.log') in result.stdout
    except (FileNotFoundError, ValueError, KeyError):
        return False


def read_status(runtime):
    try:
        status = json.loads((runtime / 'status.json').read_text())
        status['running'] = is_running(runtime)
        if not status['running']:
            status['phase'] = 'stopped'
            status['inRound'] = False
        return status
    except (FileNotFoundError, json.JSONDecodeError):
        return {}


def wait_ready(runtime, generation=1, timeout=90):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        status = read_status(runtime)
        if not is_running(runtime):
            raise RuntimeError(f'Simulator exited; inspect {runtime}/player.log')
        if status.get('error'):
            raise RuntimeError(status['error'])
        if status.get('generation', 0) >= generation and status.get('inRound'):
            return status
        time.sleep(.2)
    raise TimeoutError(f'Kitchen did not become ready; inspect {runtime}/status.json and player.log')


def command(runtime, value, timeout=5):
    if not is_running(runtime):
        raise RuntimeError('Simulator is not running; run start first')
    if (runtime / 'command').exists():
        raise RuntimeError('A simulator command is already pending')
    temporary = runtime / 'command.tmp'
    ident = uuid.uuid4().hex
    temporary.write_text(ident + ' ' + value + '\n')
    temporary.replace(runtime / 'command')
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        status = read_status(runtime)
        if value == 'quit' and not is_running(runtime):
            return {'running': False}
        if status.get('lastCommand') == ident:
            if status.get('commandError'):
                raise RuntimeError(status['commandError'])
            if value != 'quit':
                return status
        time.sleep(.1)
    raise TimeoutError('Simulator command did not finish: ' + value)


def start(args):
    if subprocess.run(['pgrep', '-x', 'Overcooked2'], capture_output=True).returncode == 0:
        raise RuntimeError('Overcooked2 is already running; stop that instance first')
    runtime = args.runtime
    runtime.mkdir(parents=True, exist_ok=True)
    output = build(args.game)
    bep = runtime / 'BepInEx'
    shutil.copytree(TOOLS / 'bepinex/BepInEx/core', bep / 'core', dirs_exist_ok=True)
    (bep / 'plugins').mkdir(exist_ok=True)
    shutil.copy2(output / 'SousChef.Bridge.dll', bep / 'plugins')
    (bep / 'config').mkdir(exist_ok=True)
    # Application's static constructor is not guaranteed in -nographics.
    (bep / 'config/BepInEx.cfg').write_text(
        '[Preloader.Entrypoint]\nAssembly = Assembly-CSharp.dll\nType = BootFlow\nMethod = Awake\n')
    for name in ('command', 'status.json', 'levels.json'):
        (runtime / name).unlink(missing_ok=True)
    env = dict(os.environ, SOUS_CHEF_NATIVE_SIM=str(runtime), SOUS_CHEF_NATIVE_LEVEL=args.level,
               SOUS_CHEF_NAV_TRACE='1', SOUS_CHEF_NATIVE_FPS=str(args.fps))
    with (runtime / 'launcher.log').open('w') as log:
        process = subprocess.Popen([
            sys.executable, str(ROOT / 'scripts/launch.py'), '--game', str(args.game),
            '--doorstop-target-assembly', str(bep / 'core/BepInEx.Preloader.dll'),
            '-batchmode', '-nographics', '-logFile', str(runtime / 'player.log'),
        ], cwd=ROOT, env=env, stdin=subprocess.DEVNULL, stdout=log, stderr=log, start_new_session=True)
    (runtime / 'process.json').write_text(json.dumps({'pid': process.pid, 'level': args.level}))
    try:
        return wait_ready(runtime, timeout=args.timeout)
    except (Exception, KeyboardInterrupt):
        # Only this Popen child is stopped; never kill a separately started game.
        try:
            command(runtime, 'quit', timeout=5)
        except (OSError, RuntimeError, TimeoutError):
            if process.poll() is None:
                process.terminate()
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait()
        raise


def navigate_leg(client, target, args, trace):
    result = {'success': False}
    catalog_deadline = time.monotonic() + args.timeout
    while True:
        catalog = client.available_actions()
        if catalog.get('complete'):
            break
        if time.monotonic() >= catalog_deadline:
            raise RuntimeError('Action discovery did not finish: ' + str(catalog.get('reason')))
        time.sleep(.1)
    actions = catalog['actions']
    schema = next((a for a in actions if a['name'] == 'navigate_to'), None)
    options = [] if schema is None else schema['parameters']['destination_id']['options']
    matches = [o for o in options if target.casefold() in
               (str(o.get(k) or '').casefold() for k in ('label', 'foodName'))]
    if len(matches) != 1:
        raise RuntimeError(f'Expected one reachable target matching {target!r}, found {len(matches)}')
    result['target'] = matches[0]
    started = time.monotonic()
    client.navigate_to(matches[0]['id'])
    while time.monotonic() - started < args.timeout:
        status = client.status()
        trace.write(json.dumps(status) + '\n')
        trace.flush()
        nav = status['navigation']
        if nav['state'] != 'navigating':
            result['success'] = nav['reason'] == getattr(args, 'arrival', 'arrived')
            result['reason'] = nav['reason']
            result['arrival'] = nav.get('arrival')
            result['teleportsCompleted'] = nav.get('teleportsCompleted', 0)
            if result['success'] and result['teleportsCompleted'] < getattr(args, 'min_teleports', 0):
                result['success'] = False
                result['reason'] = 'insufficient_teleports'
            break
        time.sleep(.1)
    else:
        client.stop()
        result['reason'] = 'test_timeout'
    result['seconds'] = time.monotonic() - started
    return result


def navigate(args):
    # Import the same client used by the harness; do not steer or retry for it.
    sys.path.insert(0, str(ROOT))
    from clients.actions import ActionClient
    from clients.control import ControlError
    trace_dir = (args.trace_dir or args.runtime / ('runs-' + time.strftime('%Y%m%d-%H%M%S'))).resolve()
    trace_dir.mkdir(parents=True, exist_ok=True)
    if args.fps is not None:
        command(args.runtime, 'fps ' + str(args.fps))
    results = []
    for attempt in range(1, args.repeat + 1):
        generation = read_status(args.runtime).get('generation', 0) + 1
        command(args.runtime, 'reset')
        initial = wait_ready(args.runtime, generation, args.timeout)
        result = {'attempt': attempt, 'initial': initial, 'success': False}
        with (trace_dir / f'attempt-{attempt}.ndjson').open('w') as trace, ActionClient() as client:
            started_action = False
            try:
                if client.hello.get('sessionId') != initial.get('sessionId'):
                    raise RuntimeError('Action endpoint belongs to a different game session')
                result['legs'] = []
                for target in [args.target] + getattr(args, 'then', []):
                    started_action = True
                    leg = navigate_leg(client, target, args, trace)
                    result['legs'].append(leg)
                    result.update(leg)
                    if not leg['success']:
                        break
                result['totalTeleportsCompleted'] = sum(leg.get('teleportsCompleted', 0) for leg in result['legs'])
            except (RuntimeError, ControlError, OSError) as exc:
                result['success'] = False
                if started_action:
                    try:
                        client.stop()
                    except (ControlError, OSError):
                        pass
                result['reason'] = str(exc)
        results.append(result)
        print(f"Attempt {attempt}: {result.get('reason')}", file=sys.stderr, flush=True)
    report = {'results': results, 'passed': sum(r['success'] for r in results),
              'attempts': len(results), 'traceDirectory': str(trace_dir)}
    (trace_dir / 'report.json').write_text(json.dumps(report, indent=2) + '\n')
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--runtime', type=Path, default=DEFAULT_RUNTIME)
    parser.add_argument('--game', type=Path, default=DEFAULT_GAME)
    parser.add_argument('--timeout', type=float, default=90)
    sub = parser.add_subparsers(dest='operation', required=True)
    launch = sub.add_parser('start')
    launch.add_argument('--level', default='s_wizard_school_3_4')
    launch.add_argument('--fps', type=int, choices=range(10, 241), metavar='10..240', default=60)
    load = sub.add_parser('load')
    load.add_argument('level')
    run = sub.add_parser('navigate')
    run.add_argument('target', help='Exact food name or target label (IDs change on reset)')
    run.add_argument('--then', action='append', default=[], metavar='TARGET',
                     help='Navigate to another target after arrival, without resetting (repeatable)')
    run.add_argument('--repeat', type=int, choices=range(1, 101), metavar='1..100', default=1)
    run.add_argument('--fps', type=int, choices=range(10, 241), metavar='10..240')
    run.add_argument('--trace-dir', type=Path)
    run.add_argument('--arrival', choices=('arrived', 'positioned_no_action'), default='arrived',
                     help='Expected navigation outcome; empty stations can be positioned without a native action')
    run.add_argument('--min-teleports', type=int, choices=range(0, 101), default=0,
                     help='Require this many completed native portal transitions on every leg as well as arrival')
    fps = sub.add_parser('fps')
    fps.add_argument('fps', type=int, choices=range(10, 241), metavar='10..240')
    for name in ('status', 'reset', 'levels', 'stop'):
        sub.add_parser(name)
    args = parser.parse_args()
    args.runtime = args.runtime.resolve()
    args.game = args.game.resolve()
    try:
        if args.operation == 'start':
            result = start(args)
        elif args.operation == 'navigate':
            result = navigate(args)
        elif args.operation == 'status':
            result = read_status(args.runtime)
        elif args.operation == 'levels':
            result = json.loads((args.runtime / 'levels.json').read_text())
        elif args.operation == 'stop':
            result = command(args.runtime, 'quit', timeout=15)
        elif args.operation == 'fps':
            result = command(args.runtime, 'fps ' + str(args.fps))
        else:
            generation = read_status(args.runtime).get('generation', 0) + 1
            command(args.runtime, 'reset' if args.operation == 'reset' else 'load ' + args.level)
            result = wait_ready(args.runtime, generation, args.timeout)
        print(json.dumps(result, indent=2))
        if args.operation == 'navigate' and result['passed'] != result['attempts']:
            return 2
    except (OSError, RuntimeError, TimeoutError) as exc:
        parser.exit(1, str(exc) + '\n')


if __name__ == '__main__':
    sys.exit(main())
