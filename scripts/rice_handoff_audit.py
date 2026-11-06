#!/usr/bin/env python3
"""Native rice refill and efficient, spaced handoffs. Use an isolated runtime."""
import argparse
import hashlib
import json
import math
import time
from pathlib import Path

import native_simulator as sim
from held_item_audit import load
from task_audit import Audit
from throw_audit import fields, write


def rice(audit, loose_only=False):
    catalog = load(audit, 's_sushi_1_3')
    before = audit.snap('initial')
    supply = next(t['objectInstanceId'] for t in catalog['targets'] if t.get('foodName') == 'SushiRice')
    empty = {o['unityInstanceId'] for o in before['objects'] if fields(o, 'ServerAttachStation') and
             fields(o, 'ServerAttachStation').get('m_item') is None}
    audit.request_when_available('prepare_near_player', None, items=[dict(ingredient='SushiRice', preparation='cooked')])
    request = audit.client.last_response['control']['action']['requestId']
    supplied = []; filled = None; first_delivery = False
    end = time.monotonic() + audit.args.timeout
    with (audit.directory / 'trace.ndjson').open('w') as trace:
        while time.monotonic() < end:
            control = audit.client.status(); action = control['action']; details = action['details']; child = details.get('child') or {}
            trace.write(json.dumps(action) + '\n')
            assert action['state'] == 'running' and action['requestId'] == request and control['ownedByYou']
            lane = details['preparation'][0]
            if not supplied and lane['processing'] == 1 and child.get('operation') == 'add_ingredient':
                # Supply a counter portion and a loose portion as P2 returns
                # with the second batch, near the first pot's completion time.
                bot = next(p['position'] for p in sim.read_status(audit.args.runtime)['players'] if p['name'] == 'Player 2')
                counter = min((t for t in catalog['targets'] if t['kind'] == 'counter' and
                               t['objectInstanceId'] in empty and 'standard' in t['label']),
                              key=lambda t: math.dist(t['position'], bot))
                if not loose_only:
                    supplied.append(audit.fixture(f"ingredient {supply} {counter['objectInstanceId']}"))
                supplied.append(audit.fixture(f"loose-ingredient {supply} {bot[0]-.7} {bot[1]+.4} {bot[2]}"))
                audit.snap('stock-arrived')
            if lane['delivered']:
                first_delivery = True
            if lane['processing'] == 3 and not first_delivery:
                frame = audit.snap('three-pots-filled')
                pots = [o for o in frame['objects'] if fields(o, 'ServerCookingHandler') and
                        fields(o, 'ServerIngredientContainer').get('m_contents')]
                assert len(pots) == 3 and all(fields(o, 'ServerCookingHandler')['m_ServerData']['fields']['CookingStateMessage.m_cookingState'] != 'Ruined' for o in pots)
                # A native add destroys/transfers the exact raw food. At least
                # one supplied portion must be consumed by this refill.
                present = {o['unityInstanceId'] for o in frame['objects'] if o.get('activeInHierarchy')}
                consumed = [f['itemInstanceId'] for f in supplied if f['itemInstanceId'] not in present]
                assert consumed, 'third pot came from another dispenser fetch despite supplied rice'
                filled = dict(pots=[o['unityInstanceId'] for o in pots], suppliedRiceConsumed=consumed)
            if first_delivery:
                assert filled, 'waited for existing rice before filling the spare pot with supplied stock'
                break
            time.sleep(.08)
        else:
            raise TimeoutError(f'rice refill: {action}')
    audit.snap('after')
    return dict(request=request, supplied=supplied, **filled)


def handoff(audit):
    catalog = load(audit, 's_sushi_1_3')
    before = audit.snap('initial')
    bot = next(o for o in before['objects'] if o['name'] == 'Player 2')
    human = next(o for o in before['objects'] if o['name'] == 'Player 1')
    supply = next(t['objectInstanceId'] for t in catalog['targets'] if t.get('foodName') == 'Cucumber')
    fixture = audit.fixture(f"ingredient {supply} {bot['unityInstanceId']} chopped")
    audit.request_when_available('prepare_near_player', 1, items=[dict(ingredient='Cucumber', preparation='chopped')])
    request = audit.client.last_response['control']['action']['requestId']
    end = time.monotonic() + audit.args.timeout
    choices = []; phases = set()
    with (audit.directory / 'trace.ndjson').open('w') as trace:
        while time.monotonic() < end:
            control = audit.client.status(); action = control['action']; details = action['details']; child = details.get('child') or {}
            trace.write(json.dumps(action) + '\n')
            assert action['requestId'] == request
            choice = details.get('handoff')
            if choice and (not choices or choice != choices[-1]):
                choices.append(choice)
            phases.add(child.get('phase'))
            if action['state'] != 'running':
                assert action['state'] == 'succeeded', action
                break
            time.sleep(.08)
        else:
            raise TimeoutError(f'handoff: {action}')
    after = audit.snap('after')
    item = next(o for o in after['objects'] if o['unityInstanceId'] == fixture['itemInstanceId'])
    separation = math.dist([item['transform']['position'][i] for i in (0, 2)],
                           [human['transform']['position'][i] for i in (0, 2)])
    assert 'throw_hold' in phases and any(c['mode'].startswith('throw') for c in choices), choices
    assert 2.7 <= separation <= 4.5, f'expected delivery near the three-tile ring: {separation}'
    assert details['preparation'][0]['lastDeliveredInstanceId'] == fixture['itemInstanceId']
    return dict(request=request, exactHandoff=fixture['itemInstanceId'], separation=separation, choices=choices)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--runtime', required=True, type=Path)
    parser.add_argument('--port-base', required=True, type=int)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--case', choices=('rice', 'rice_floor', 'handoff'), action='append')
    parser.add_argument('--timeout', type=float, default=100)
    args = parser.parse_args()
    manifest = json.loads((args.runtime / 'process.json').read_text())
    digest = hashlib.sha256((args.runtime / 'BepInEx/plugins/SousChef.Bridge.dll').read_bytes()).hexdigest()
    if args.port_base == 24702 or args.port_base != manifest['portBase'] or digest != manifest['bridgeSha256'] or not sim.is_running(args.runtime):
        parser.error('use the matching isolated simulator')
    results = []
    for case in args.case or ('rice', 'rice_floor', 'handoff'):
        audit = Audit(args); audit.directory = args.output / case; audit.directory.mkdir(parents=True, exist_ok=True)
        try:
            row = dict(case=case, success=True, **(handoff(audit) if case == 'handoff' else rice(audit, case == 'rice_floor')))
        except Exception as error:
            audit.snap('failure')
            row = dict(case=case, success=False, error=repr(error))
        finally:
            if audit.client:
                audit.client.stop(); audit.client.close()
        results.append(row); print(json.dumps(row), flush=True)
        write(args.output / 'report.json', dict(bridgeSha256=digest, results=results))
    return 0 if all(r['success'] for r in results) else 2


if __name__ == '__main__':
    raise SystemExit(main())
