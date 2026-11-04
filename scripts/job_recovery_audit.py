#!/usr/bin/env python3
"""Exercise job recovery with real pickups/deliveries in an isolated native player.

The explicit simulator fixture briefly hides an empty delivery station or its
colliders after a real pickup. All actions, movement and outcomes remain native.
"""
import argparse
import json
from pathlib import Path
import sys
import time

import native_simulator as sim
from throw_audit import carrier_items, fields, snapshot, write

sys.path.insert(0, str(sim.ROOT))
from clients.actions import ActionClient


CASES = [
    ('baseline', 60, None, 0, 0, False),
    ('handoff-target-250ms', 60, 'target', 250, 0, False),
    ('delivery-target-450ms', 60, 'target', 450, 150, False),
    ('delivery-route-250ms', 60, 'route', 250, 150, False),
    ('delivery-route-450ms', 60, 'route', 450, 150, False),
    ('persistent-target-900ms', 60, 'target', 900, 150, False),
    ('persistent-route-900ms', 60, 'route', 900, 150, False),
    ('stop-during-recovery', 60, 'target', 450, 150, True),
    ('baseline-20fps', 20, None, 0, 0, False),
    ('handoff-target-250ms-20fps', 20, 'target', 250, 0, False),
    ('delivery-route-250ms-20fps', 20, 'route', 250, 150, False),
    ('persistent-target-900ms-20fps', 20, 'target', 900, 150, False),
]


def fixture(runtime):
    try:
        return json.loads((runtime / 'interruption.json').read_text())
    except (FileNotFoundError, json.JSONDecodeError):
        return {}


def run_case(args, case):
    name, fps, kind, duration, delay, stop = case
    directory = args.output / name
    directory.mkdir(parents=True, exist_ok=True)
    generation = sim.read_status(args.runtime)['generation'] + 1
    sim.command(args.runtime, 'fps ' + str(fps))
    sim.command(args.runtime, 'load s_wizard_5_5')
    ready = sim.wait_ready(args.runtime, generation)
    (args.runtime / 'interruption.json').unlink(missing_ok=True)
    result = {'name': name, 'fps': fps, 'success': False}
    with ActionClient(args.port_base + 1, timeout=15) as client:
        if client.hello['sessionId'] != ready['sessionId']:
            raise RuntimeError('Connected to a different native runtime')
        deadline = time.monotonic() + 60
        while True:
            catalog = client.available_actions()
            if catalog['complete']:
                break
            if time.monotonic() >= deadline:
                raise TimeoutError('Job catalogue did not finish')
            time.sleep(.1)
        ingredient = next(i for i in catalog['ingredients'] if i['label'] == 'Cheese_Raw')
        row = next(r for r in catalog['jobOptions']['fetch_ingredient'] if r['sourceId'] == ingredient['id'])
        # A long, flat delivery leg leaves room to interrupt in-flight navigation.
        target = next(d for d in catalog['deliveryDestinations'] if d['id'] in row['destinationIds']
                      and abs(d['position'][0] - 22.8) < .1 and abs(d['position'][2] + 2.4) < .1)
        write(directory / 'catalog.json', catalog)
        if kind:
            sim.command(args.runtime, f"interrupt-target {target['objectInstanceId']} {duration} {kind} {delay}")
        initial = client.fetch_ingredient(ingredient['id'], target['id'])
        request_id = initial['action']['requestId']
        recovered, stopped, owned, receipts = False, False, True, []
        deadline = time.monotonic() + 60
        with (directory / 'trace.ndjson').open('w') as trace:
            while time.monotonic() < deadline:
                control = client.status()
                trace.write(json.dumps(control) + '\n')
                action = control['action']
                receipts.append(action)
                child = (action.get('details') or {}).get('child') or {}
                parent_recovery = (action.get('details') or {}).get('recovery') or {}
                child_recovery = child.get('recovery') or {}
                recovered |= parent_recovery.get('count', 0) > 0 or child_recovery.get('count', 0) > 0
                if action['state'] != 'running':
                    break
                owned &= control['ownedByYou']
                if stop and recovered and not stopped:
                    before = time.monotonic()
                    control = client.stop()
                    result['stopSeconds'] = time.monotonic() - before
                    stopped = True
                time.sleep(.02)
            else:
                raise TimeoutError('Job did not terminate')
        final = client.status()['action']
        if kind:
            while fixture(args.runtime).get('phase') != 'restored' and time.monotonic() < deadline:
                time.sleep(.02)
            write(directory / 'fixture.json', fixture(args.runtime))
        after = snapshot(args.port_base, ready['sessionId'], directory / 'snapshot.json')
        carried = carrier_items(after)
        station = next(o for o in after['objects'] if o['unityInstanceId'] == target['objectInstanceId'])
        ref = fields(station, 'ServerAttachStation').get('m_item')
        occupant = next((o for o in after['objects'] if ref and o['id'] == ref.get('objectId')), None)
        cargo_id = ((final.get('details') or {}).get('cargo') or {}).get('objectInstanceId')
        expected = 'cancelled' if stop else 'succeeded'
        exact_delivered = occupant is not None and occupant['unityInstanceId'] == cargo_id and not carried
        cargo_retained = len(carried) == 1 and carried[0]['unityInstanceId'] == cargo_id and occupant is None
        result.update(state=final['state'], reason=final['reason'], recoveryObserved=recovered,
                      ownershipRetained=owned, sameRequest=all(a['requestId'] == request_id for a in receipts),
                      exactDelivered=exact_delivered, cargoRetained=cargo_retained,
                      deliveredCount=final['details']['deliveredCount'], action=final,
                      fixture=fixture(args.runtime) if kind else None)
        result['success'] = (final['state'] == expected and owned and result['sameRequest']
            and (not kind or recovered and fixture(args.runtime).get('phase') == 'restored')
            and (exact_delivered and result['deliveredCount'] == 1 if expected == 'succeeded'
                 else cargo_retained and result['deliveredCount'] == 0)
            and (not stop or stopped and result['stopSeconds'] < .5))
    write(directory / 'result.json', result)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--runtime', type=Path, required=True)
    parser.add_argument('--port-base', type=int, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--case', action='append', help='Run only these named cases (repeatable)')
    args = parser.parse_args()
    args.runtime = args.runtime.resolve()
    args.output.mkdir(parents=True, exist_ok=True)
    process = json.loads((args.runtime / 'process.json').read_text())
    if args.port_base == 24702 or process['portBase'] != args.port_base or not sim.is_running(args.runtime):
        parser.error('Use the matching isolated runtime and non-default port range')
    results = []
    for case in CASES:
        if args.case and case[0] not in args.case:
            continue
        try:
            result = run_case(args, case)
        except Exception as error:
            result = {'name': case[0], 'success': False, 'error': str(error)}
        results.append(result)
        print(json.dumps({k: v for k, v in result.items() if k not in ('action', 'fixture')}), flush=True)
        write(args.output / 'report.json', {'bridgeSha256': process['bridgeSha256'],
              'passed': sum(r['success'] for r in results), 'attempts': len(results), 'results': results})
    return 0 if results and all(r['success'] for r in results) else 2


if __name__ == '__main__':
    sys.exit(main())
