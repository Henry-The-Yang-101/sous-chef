#!/usr/bin/env python3
"""Run throw cases against an already started, isolated native simulator.

Cases are JSON; setup uses ordinary P2 actions/input, optionally with an explicit
supported chef pose fixture. Ownership, physics parameters, receiver input and
game clocks are never modified by this runner.
Full snapshots provide item identity/holder evidence independent of receipts.
"""
import argparse
from contextlib import ExitStack
import hashlib
import json
from pathlib import Path
import sys
import socket
import time

import native_simulator as sim

sys.path.insert(0, str(sim.ROOT))
from clients.actions import ActionClient, ControlError
from clients.bridge import BridgeClient


def write(path, value):
    path.write_text(json.dumps(value, indent=2) + '\n')


def fields(obj, component):
    return next((c.get('adapters', {}).get(component, {}).get('fields', {})
                 for c in obj.get('components', []) if c['type'] == component), {})


def snapshot(port, session, path):
    first_sequence = None
    with BridgeClient(port, timeout=10) as bridge:
        for message in bridge.messages():
            if bridge.session_id != session:
                raise RuntimeError('Snapshot belongs to another native session')
            if message['type'] == 'snapshot' and message.get('inRound'):
                # A newly connected stream can replay its last cached capture.
                # Wait for the next capture so an after-action observation cannot
                # accidentally describe the pre-action holder relationship.
                if first_sequence is None:
                    first_sequence = message['sequence']
                    continue
                write(path, message)
                return message
    raise RuntimeError('No live native snapshot')


def carrier_items(snap, name='Player 2'):
    chef = next(o for o in snap['objects'] if o['name'] == name
                and fields(o, 'ServerPlayerAttachmentCarrier'))
    refs = fields(chef, 'ServerPlayerAttachmentCarrier')['m_carriedObjects']
    ids = {r['objectId'] for r in refs if r}
    return [o for o in snap['objects'] if o['id'] in ids]


def item_holders(snap, item):
    if item is None:
        return []
    result = []
    for obj in snap['objects']:
        for component, key in (('ServerAttachStation', 'm_item'),
                               ('ServerPlayerAttachmentCarrier', 'm_carriedObjects')):
            value = fields(obj, component).get(key)
            refs = value if isinstance(value, list) else [value]
            if any(isinstance(ref, dict) and ref.get('objectId') == item['id'] for ref in refs):
                result.append({'name': obj['name'], 'instanceId': obj['unityInstanceId'],
                               'component': component})
    return result


def catalog(client, timeout):
    deadline = time.monotonic() + timeout
    while True:
        result = client.available_actions()
        if result.get('complete'):
            return result
        if time.monotonic() >= deadline:
            raise RuntimeError('Discovery incomplete: ' + str(result.get('reason')))
        time.sleep(.1)


def resolve(options, selector):
    def matches(option):
        if isinstance(selector, dict):
            return all(option.get(key) == value for key, value in selector.items())
        return selector in (option['id'], option['label'], option.get('foodName'))
    found = [o for o in options if matches(o)]
    if len(found) != 1:
        raise RuntimeError(f'Expected one target for {selector!r}, found {len(found)}')
    return found[0]


def throw_option(client, operation, selector, timeout):
    """Wait for a definitive preview, rather than treating pending as a refusal."""
    deadline = time.monotonic() + timeout
    while True:
        available = catalog(client, timeout)
        if operation == 'throw_to_player':
            row = resolve(available['players'], {'playerId': selector})
        else:
            row = resolve(available['targets'], selector)
        if row.get('throw', {}).get('state') != 'pending':
            return available, row
        if time.monotonic() >= deadline:
            raise RuntimeError('Throw option preview did not finish: ' + str(selector))
        time.sleep(.1)


def execute(client, operation, argument, trace, timeout, cancel=None, cancel_mode='stop'):
    started = time.monotonic()
    result = {'operation': operation, 'argument': argument, 'phases': []}
    try:
        control = getattr(client, operation)(argument)
    except (ControlError, ValueError) as error:
        result['requestError'] = str(error)
        control = client.status()
    # A validation rejection can leave a previous receipt in status. Never
    # associate that receipt with the current request just because it is terminal.
    request_id = str(client.request_id - 1 if 'requestError' in result else client.request_id)
    with trace.open('w') as recording, ExitStack() as observers:
        while True:
            state = control['navigation'] if operation == 'navigate_to' else control['action']
            recording.write(json.dumps({'seconds': time.monotonic() - started, 'control': control}) + '\n')
            recording.flush()
            if state.get('phase') not in result['phases']:
                result['phases'].append(state.get('phase'))
            if 'requestError' in result or state['state'] != ('navigating' if operation == 'navigate_to' else 'running'):
                break
            if cancel and not result.get('cancelledAt') and state.get('phase') == cancel:
                result['cancelledAt'] = cancel
                if cancel_mode == 'disconnect':
                    port = client.socket.getpeername()[1]
                    # Abrupt transport loss; close() normally sends release and
                    # would therefore test a different cancellation path.
                    client.claimed = False
                    client.socket.shutdown(socket.SHUT_RDWR)
                    client.reader.close()
                    client.socket.close()
                    client = observers.enter_context(ActionClient(port, timeout=10))
                else:
                    client.stop()
            if time.monotonic() - started > timeout:
                result['testTimeout'] = True
                client.stop()
            time.sleep(.025)
            control = client.status()
            if result.get('testTimeout'):
                break
    result.update(seconds=time.monotonic() - started, navigation=control['navigation'])
    result['receipt'] = control['action'] if control['action'].get('requestId') == request_id else None
    result['outcome'] = result.get('requestError') or (
        control['navigation']['reason'] if operation == 'navigate_to' else (result['receipt'] or {}).get('reason'))
    result['ownedAfter'] = control['ownedByYou']
    return result


def run_case(args, case, index):
    directory = args.output / f'{index:02d}-{case["name"]}'
    directory.mkdir(parents=True, exist_ok=True)
    result = {'case': case, 'directory': str(directory), 'setup': []}
    try:
        generation = sim.read_status(args.runtime)['generation'] + 1
        sim.command(args.runtime, 'fps ' + str(case.get('fps', 60)))
        sim.command(args.runtime, 'load ' + case['level'])
        initial = sim.wait_ready(args.runtime, generation, args.timeout)
        result['initialNativeStatus'] = initial
        time.sleep(.5)
        with ActionClient(sim.control_port(args.runtime), timeout=10) as client:
            if client.hello['sessionId'] != initial['sessionId']:
                raise RuntimeError('Control belongs to another native session')
            for i, step in enumerate(case.get('setup', [])):
                if step['operation'] == 'stage_player':
                    command = 'stage-player ' + ' '.join(str(v) for v in
                              [step['playerId'], *step['position'], step['yaw']])
                    sim.command(args.runtime, command)
                    time.sleep(.25)
                    result['setup'].append(dict(step, evidence=json.loads((args.runtime / 'staging.json').read_text())))
                    continue
                if step['operation'] == 'input':
                    client.claim()
                    client.hold(step['seconds'], step.get('x', 0), step.get('y', 0), step.get('buttons', ()))
                    client.hold(.15)
                    if client.status()['ownedByYou']:
                        client.release()
                    result['setup'].append(step)
                    continue
                if step['operation'] == 'throw_to_player':
                    argument = step['target']
                else:
                    options = catalog(client, args.timeout)['targets']
                    argument = resolve(options, step['target'])['id']
                receipt = execute(client, step['operation'], argument,
                                  directory / f'setup-{i}.ndjson', args.timeout)
                result['setup'].append(receipt)
                ok = receipt['outcome'] in ('arrived', 'positioned_no_action') if step['operation'] == 'navigate_to' else (
                    (receipt['receipt'] or {}).get('state') == 'succeeded')
                if not ok and not step.get('allowFailure'):
                    raise RuntimeError('Setup failed: ' + str(receipt['outcome']))
            available = catalog(client, args.timeout)
            if 'expectOffered' in case:
                available, row = throw_option(client, case['operation'], case['target'], args.timeout)
                operation = case['operation']
                key = 'player_id' if operation == 'throw_to_player' else 'target_id'
                choices = next((action['parameters'][key]['options'] for action in available['actions']
                                if action['name'] == operation), [])
                result['option'] = {'offered': operation in row['operations'],
                                    'inParameterEnum': any(choice['id'] == row['id'] for choice in choices),
                                    'preview': row.get('throw')}
            write(directory / 'catalog.json', available)
            before = snapshot(args.snapshot_port, initial['sessionId'], directory / 'before.json')
            if case.get('probeCurrent'):
                row = resolve(available['players'], {'playerId': case['target']}) if case['operation'] == 'throw_to_player' else resolve(available['targets'], case['target'])
                sim.command(args.runtime, 'probe-throw ' + str(row['objectInstanceId']))
                result['currentPoseThrow'] = json.loads((args.runtime / 'throw-probe.json').read_text())
            cargo = carrier_items(before)
            cargo = cargo[0] if cargo else None
            result['cargoBefore'] = None if cargo is None else {
                'instanceId': cargo['unityInstanceId'], 'name': cargo['name'],
                'colliders': cargo['colliders'], 'transform': cargo['transform']}
            operation = case['operation']
            target = None
            if operation in ('throw_to_player', 'native_throw'):
                argument = case['target']
            else:
                target = resolve(available['targets'], case['target'])
                argument = target['id']
                result['target'] = target
            if operation == 'native_throw':
                # Native input baseline is independent of planner success: a
                # successful pass would otherwise consume the comparison cargo.
                client.claim()
                client.hold(.15, buttons=('use',))
                client.hold(.04)
                write(directory / 'input-status.json', client.status())
            else:
                result['action'] = execute(client, operation, argument, directory / 'action.ndjson',
                                           args.timeout, case.get('cancelAt'), case.get('cancelMode', 'stop'))
            after = snapshot(args.snapshot_port, initial['sessionId'], directory / 'after.json')
            post = next((o for o in after['objects'] if cargo and o['unityInstanceId'] == cargo['unityInstanceId']), None)
            result['holdersAfter'] = item_holders(after, post)
            result['cargoRetained'] = cargo is None or any(
                o['unityInstanceId'] == cargo['unityInstanceId'] for o in carrier_items(after))
            if operation == 'native_throw':
                result['nativeThrowFields'] = fields(post, 'ServerThrowableItem') if post else {}
                launched = bool(cargo and post and not result['cargoRetained']
                                and result['nativeThrowFields'].get('m_flying') is True)
                result['action'] = {'outcome': 'native_throw_observed' if launched else 'native_throw_not_observed'}
            result['nativeAfter'] = sim.read_status(args.runtime)
            expected = case['expected']
            if isinstance(expected, str):
                expected = [expected]
            result['matchesExpectation'] = result['action']['outcome'] in expected
            if 'expectCurrentFeasible' in case:
                result['matchesExpectation'] &= result.get('currentPoseThrow', {}).get('feasible') == case['expectCurrentFeasible']
            if 'expectOffered' in case:
                result['matchesExpectation'] &= all(result['option'][key] == case['expectOffered']
                                                     for key in ('offered', 'inParameterEnum'))
            receipt = result['action'].get('receipt') or {}
            if receipt.get('state') == 'succeeded':
                # Counter handoffs deliberately name the player as the command
                # target; the receipt names the actual counter in details.
                details = receipt.get('details') or {}
                counter = details.get('counterInstanceId')
                destination = target['objectInstanceId'] if target else next(
                    (p['objectInstanceId'] for p in available['players'] if p['playerId'] == argument), None)
                result['receiptIndependentlyConfirmed'] = bool(cargo and any(
                    holder['instanceId'] in (destination, counter) for holder in result['holdersAfter']))
                result['matchesExpectation'] &= result['receiptIndependentlyConfirmed']
            elif case.get('expectRetained', True):
                result['matchesExpectation'] &= result['cargoRetained']
            if 'expectArc' in case:
                details = receipt.get('details') or {}
                plan = details.get('result', details).get('throw') or {}
                checks = {}
                for key, value in case['expectArc'].items():
                    if key == 'minimumDrop':
                        checks[key] = plan.get('releaseHeight', -1e9) - plan.get('arrivalHeight', 1e9) >= value
                    elif key == 'minimumReleaseFoot':
                        checks[key] = plan.get('releasePosition', [0, -1e9, 0])[1] >= value
                    elif key == 'minimumFlightSeconds':
                        checks[key] = plan.get('flightSeconds', -1) >= value
                    elif key == 'minimumRise':
                        checks[key] = plan.get('arrivalHeight', -1e9) - plan.get('releaseHeight', 1e9) >= value
                    elif key == 'minimumClimb':
                        actor = next(o for o in before['objects'] if o['name'] == 'Player 2' and fields(o, 'ServerPlayerAttachmentCarrier'))
                        checks[key] = plan.get('releasePosition', [0, -1e9, 0])[1] - actor['transform']['position'][1] >= value
                    else:
                        raise ValueError('Unknown arc check: ' + key)
                result['arcChecks'] = checks
                result['matchesExpectation'] &= all(checks.values())
    except (OSError, RuntimeError, TimeoutError, ValueError, StopIteration) as error:
        result.update(setupError=str(error), matchesExpectation=False)
    write(directory / 'report.json', result)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--runtime', required=True, type=Path)
    parser.add_argument('--cases', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--timeout', type=float, default=35)
    args = parser.parse_args()
    args.runtime = args.runtime.resolve()
    args.output = args.output.resolve()
    if args.runtime == sim.DEFAULT_RUNTIME.resolve() or not sim.is_running(args.runtime):
        parser.error('Use a running isolated simulator runtime')
    args.snapshot_port = sim.control_port(args.runtime) - 1
    args.output.mkdir(parents=True, exist_ok=False)
    process = json.loads((args.runtime / 'process.json').read_text())
    dll = args.runtime / 'BepInEx/plugins/SousChef.Bridge.dll'
    report = {'process': process, 'loadedBridgeSha256': hashlib.sha256(dll.read_bytes()).hexdigest(), 'results': []}
    for index, case in enumerate(json.loads(args.cases.read_text()), 1):
        result = run_case(args, case, index)
        report['results'].append(result)
        write(args.output / 'report.json', report)
        print(json.dumps({'name': case['name'], 'outcome': result.get('action', {}).get('outcome'),
                          'setupError': result.get('setupError'), 'matchesExpectation': result['matchesExpectation']}), flush=True)
    return 0 if all(r['matchesExpectation'] for r in report['results']) else 2


if __name__ == '__main__':
    raise SystemExit(main())
