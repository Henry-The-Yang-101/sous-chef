#!/usr/bin/env python3
"""Audit task eligibility and recovery from held/caught cargo in isolated Unity.

Explicit native CarryItem fixtures reproduce a catch's authoritative carrier
state, without a human throw trajectory. All subsequent P2 actions use production
logical inputs; work progress, P2 poses and clocks are never forced.
"""
import argparse
import hashlib
import json
from pathlib import Path
import time

import native_simulator as sim
from task_audit import Audit
from throw_audit import carrier_items, fields, item_holders, write
from clients.actions import ActionClient


CASES = ('held_raw', 'held_chopped', 'held_wrong', 'raw_rejects_chopped', 'catch_current', 'catch_other', 'catch_working')


def load(audit, level):
    generation = sim.read_status(audit.args.runtime)['generation'] + 1
    sim.command(audit.args.runtime, 'load ' + level)
    sim.wait_ready(audit.args.runtime, generation)
    audit.client = ActionClient(audit.args.port_base + 1, timeout=15)
    deadline = time.monotonic() + 20
    while time.monotonic() < deadline:
        catalog = audit.client.available_actions()
        if catalog.get('preparationOptions') and catalog.get('targets'):
            return catalog
        time.sleep(.1)
    raise TimeoutError('Native task catalogue unavailable')


def run(audit, case):
    raft = case in ('held_raw', 'held_chopped', 'catch_other')
    catalog = load(audit, 's_rapids_4_2A' if raft else 's_sushi_1_2')
    food = 'Potato' if raft else 'SushiFish'
    wrong = 'Lettuce' if raft else 'SushiRice'
    sources = {t['foodName']: t['objectInstanceId'] for t in catalog['targets'] if t.get('foodName')}
    before = audit.snap('initial')
    bot = next(o for o in before['objects'] if o['name'] == 'Player 2' and fields(o, 'ServerPlayerAttachmentCarrier'))
    fixtures = []

    def hold(name, chopped=False):
        fixture = audit.fixture('ingredient ' + str(sources[name]) + ' ' + str(bot['unityInstanceId']) + (' chopped' if chopped else ''))
        fixtures.append(fixture)
        return fixture['itemInstanceId']

    caught = unrelated = None
    if case in ('held_raw', 'held_chopped', 'catch_working'):
        caught = hold(food, case == 'held_chopped')
    elif case == 'held_wrong':
        unrelated = hold(wrong)
    elif case == 'raw_rejects_chopped':
        unrelated = hold(food, True)
    elif case == 'catch_other':
        # P2's raft has no potato supply. Offer an obtainable source so the
        # first unit starts before a different requested ingredient is caught.
        point = next(p['position'] for p in sim.read_status(audit.args.runtime)['players'] if p['name'] == 'Player 2')
        fixtures.append(audit.fixture('loose-ingredient ' + str(sources[food]) + ' ' +
                                      ' '.join(map(str, (point[0] + 1.8, point[1] + .4, point[2])))))
    catalog = audit.client.available_actions()
    offered = [t['name'] for t in catalog.get('taskOptions', [])]
    assert 'prepare_near_player' in offered, 'Occupied hands must not hide preparation'
    write(audit.directory / 'held-catalog.json', catalog)
    items = [{'ingredient': food, 'preparation': 'raw' if case == 'raw_rejects_chopped' else 'chopped'}]
    if case == 'catch_other':
        items.append({'ingredient': 'NuggetChicken', 'preparation': 'chopped'})
    audit.request_when_available('prepare_near_player', items=items)
    request = audit.client.last_response['control']['action']['requestId']
    deadline = time.monotonic() + audit.args.timeout
    phases, work_items, handoffs = set(), [], {}
    injected = case not in ('catch_current', 'catch_other', 'catch_working')
    measured = time.monotonic()
    with (audit.directory / 'trace.ndjson').open('w') as trace:
        while time.monotonic() < deadline:
            audit.client.available_actions()
            control = audit.client.last_response['control']; trace.write(json.dumps(control) + '\n')
            action = control['action']; details = action['details']; phases.add(action['phase'])
            assert action['requestId'] == request, 'Recovery must retain one accepted request'
            if details.get('handoff'):
                handoff = details['handoff']; handoffs[handoff['itemInstanceId']] = handoff
            if action['state'] != 'running':
                assert action['state'] == 'succeeded', action
                break
            assert control['ownedByYou'], 'Recovery must retain the parent lease'
            child = details.get('child') or {}
            pickup = action['phase'] == 'pick_up' and child.get('phase') in ('navigating', 'recovering_navigation')
            working = action['phase'] == 'work_at' and child.get('phase') == 'working'
            if not injected and (working if case == 'catch_working' else pickup):
                if case == 'catch_working':
                    unrelated = hold(wrong)
                else:
                    caught = hold('NuggetChicken' if case == 'catch_other' else food)
                injected = True
                audit.snap('after-catch')
            if working and (not work_items or case == 'catch_working' and len(work_items) < 2):
                state = audit.snap('work-' + str(len(work_items)))
                for station in state['objects']:
                    if not fields(station, 'ServerWorkstation'):
                        continue
                    ref = fields(station, 'ServerAttachStation').get('m_item')
                    item = next((o for o in state['objects'] if ref and o['id'] == ref['objectId']), None)
                    if item is not None:
                        work_items.append(item['unityInstanceId'])
            time.sleep(.05)
        else:
            raise TimeoutError(case + ': ' + json.dumps(action))
    after = audit.snap('after')
    write(audit.directory / 'final-control.json', control)
    assert injected, 'Catch checkpoint was not reached'
    assert details['completedRepetitions'] == 1
    assert not carrier_items(after), 'Completed preparation must leave empty hands'
    if case == 'held_chopped':
        assert 'work_at' not in phases and caught in handoffs, 'Already-chopped held cargo must be delivered unchanged'
    elif case == 'raw_rejects_chopped':
        assert 'work_at' not in phases and unrelated not in handoffs, 'A raw request must not deliver chopped cargo'
    elif case != 'held_wrong':
        assert caught in work_items, 'The exact caught raw ingredient must reach a native chopping board'
    cargo_fate = None
    if unrelated is not None:
        item = next(o for o in after['objects'] if o['unityInstanceId'] == unrelated)
        holders = item_holders(after, item)
        cargo_fate = 'counter' if any(h['component'] == 'ServerAttachStation' for h in holders) else None
        if case == 'catch_working' and cargo_fate is None:
            # Releasing the interrupted native work button can itself detach a
            # catch before parking begins. Require that it survived and remains
            # obtainable, rather than mistaking empty hands for a storage proof.
            available = audit.client.available_actions()
            write(audit.directory / 'released-item-catalog.json', available)
            assert any(row['objectInstanceId'] == unrelated and row.get('routeDistance') is not None
                       for row in available.get('items', [])), 'Released catch must remain obtainable'
            assert fields(item, 'ServerPhysicalAttachment').get('m_isHeld') is False
            cargo_fate = 'collectible_loose'
        assert cargo_fate is not None, 'Unrelated cargo must survive on a counter'
    return {'requestId': request, 'seconds': time.monotonic() - measured, 'caughtItem': caught, 'parkedItem': unrelated,
            'cargoFate': cargo_fate, 'phases': sorted(phases), 'workedItems': work_items, 'handoffs': list(handoffs.values()), 'fixtures': fixtures}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--runtime', type=Path, required=True)
    parser.add_argument('--port-base', type=int, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--case', action='append', choices=CASES)
    parser.add_argument('--timeout', type=float, default=120)
    args = parser.parse_args(); args.runtime = args.runtime.resolve()
    process = json.loads((args.runtime / 'process.json').read_text())
    if args.port_base == 24702 or args.port_base != process['portBase'] or not sim.is_running(args.runtime):
        parser.error('Use the matching isolated native runtime and non-default ports')
    binary = args.runtime / 'BepInEx/plugins/SousChef.Bridge.dll'
    if hashlib.sha256(binary.read_bytes()).hexdigest() != process['bridgeSha256']:
        parser.error('Runtime DLL differs from its launch manifest')
    sim.wait_ready(args.runtime)
    results = []; args.output.mkdir(parents=True, exist_ok=True)
    for case in args.case or CASES:
        audit = Audit(args); audit.directory = args.output / case; audit.directory.mkdir(parents=True, exist_ok=True)
        try:
            result = dict(run(audit, case), case=case, success=True)
        except Exception as error:
            result = {'case': case, 'success': False, 'error': str(error)}
            if audit.client:
                try:
                    audit.snap('failure'); audit.client.stop()
                except Exception as cleanup:
                    result['cleanupError'] = str(cleanup)
        finally:
            if audit.client:
                audit.client.close()
        results.append(result); print(json.dumps(result), flush=True)
        write(args.output / 'report.json', {'bridgeSha256': process['bridgeSha256'], 'passed': sum(r['success'] for r in results),
                                           'attempts': len(results), 'results': results})
    return 0 if all(r['success'] for r in results) else 2


if __name__ == '__main__':
    raise SystemExit(main())
