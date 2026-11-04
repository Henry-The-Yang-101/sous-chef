#!/usr/bin/env python3
"""Run story 4-2 preparation regressions in an isolated native simulator.

Supplied-food cases create native raw/chopped food on the floor. The complete
cooked-pair case also stages P1's initial supported pose on P2's raft to isolate
preparation from the level's time-dependent transport. Measured P2 actions use
production logical inputs, with no pose/progress writes or request retries.
"""
import argparse
import hashlib
import json
from pathlib import Path
import time

import native_simulator as sim
from task_audit import Audit
from throw_audit import fields, write
from clients.actions import ActionClient


CASES = ('repeat_raw', 'chopped_pair', 'cooked_floor', 'cooked_pair')


def load(audit):
    generation = sim.read_status(audit.args.runtime)['generation'] + 1
    sim.command(audit.args.runtime, 'load s_rapids_4_2A')
    ready = sim.wait_ready(audit.args.runtime, generation)
    audit.client = ActionClient(audit.args.port_base + 1, timeout=15)
    assert audit.client.hello['sessionId'] == ready['sessionId']
    deadline = time.monotonic() + 20
    while time.monotonic() < deadline:
        catalog = audit.client.available_actions()
        if catalog.get('preparationOptions') and catalog.get('targets'):
            write(audit.directory / 'initial-catalog.json', catalog)
            return catalog
        time.sleep(.1)
    raise TimeoutError('Native preparation catalogue unavailable')


def supply(audit, catalog, name, chopped=False, offset=0):
    source = next(t for t in catalog['targets'] if t.get('foodName') == name)
    bot = next(p['position'] for p in sim.read_status(audit.args.runtime)['players'] if p['name'] == 'Player 2')
    position = (bot[0] + .9, bot[1] + .4, bot[2] + offset)
    return audit.fixture('loose-ingredient ' + str(source['objectInstanceId']) + ' ' +
                         ' '.join(map(str, position)) + (' chopped' if chopped else ''))


def cooking_items(snapshot):
    result = []
    for item in snapshot['objects']:
        state = fields(item, 'ServerCookingHandler').get('m_ServerData', {}).get('fields', {})
        if state.get('CookingStateMessage.m_cookingState') == 'Progressing' and fields(item, 'ServerIngredientContainer').get('m_contents'):
            result.append(item['unityInstanceId'])
    return result


def cooked_deliveries(snapshot, handoffs):
    evidence = []
    for handoff in handoffs:
        item = next(o for o in snapshot['objects'] if o['unityInstanceId'] == handoff['itemInstanceId'])
        contents = fields(item, 'ServerIngredientContainer').get('m_contents')
        state = fields(item, 'ServerCookingHandler').get('m_ServerData', {}).get('fields', {}).get('CookingStateMessage.m_cookingState')
        plated = any(node.get('fields', {}).get('CookedCompositeAssembledNode.m_progress') == 'Cooked' for node in contents or [])
        assert contents and (plated or state in ('Completed', 'OverDoing')), 'Delivered food must be cooked and unruined'
        evidence.append({'itemInstanceId': item['unityInstanceId'], 'plated': plated, 'cookingState': state})
    return evidence


def run(audit, case):
    catalog = load(audit)
    initial = []
    if case == 'repeat_raw':
        items = [{'ingredient': name, 'preparation': 'raw'} for name in ('Lettuce', 'Tomato')]
        repetitions = None
    elif case == 'chopped_pair':
        initial = [supply(audit, catalog, 'NuggetChicken', offset=-.65), supply(audit, catalog, 'Potato', offset=.65)]
        items = [{'ingredient': name, 'preparation': 'chopped'} for name in ('NuggetChicken', 'Potato')]
        repetitions = 1
    else:
        if case == 'cooked_pair':
            deadline = time.monotonic() + 15
            while True:
                catalog = audit.client.available_actions()
                stance = next((d['approachPosition'] for d in catalog['destinations']
                               if d.get('foodName') == 'Lettuce'), None)
                if stance:
                    break
                if time.monotonic() >= deadline:
                    raise TimeoutError('No certified initial human stance')
                time.sleep(.1)
            pose = (*stance, 0)
            sim.command(audit.args.runtime, 'stage-player 1 ' + ' '.join(map(str, pose)))
            initial.append({'initialHumanPose': pose})
        initial.append(supply(audit, catalog, 'Potato', chopped=True))
        items = [{'ingredient': name, 'preparation': 'cooked'} for name in ('NuggetChicken', 'Potato')]
        repetitions = 1
    audit.snap('before')
    audit.request_when_available('prepare_near_player', repetitions, items=items)
    request = audit.client.last_response['control']['action']['requestId']
    deadline = time.monotonic() + audit.args.timeout
    handoffs, phases, cookers, last = {}, set(), [], None
    with (audit.directory / 'trace.ndjson').open('w') as trace:
        while time.monotonic() < deadline:
            audit.client.available_actions()
            control = audit.client.last_response['control']
            trace.write(json.dumps(control) + '\n')
            action = control['action']; details = action['details']
            assert action['requestId'] == request, 'One accepted request must survive the whole case'
            phases.add(action['phase'])
            if details.get('handoff'):
                handoff = details['handoff']
                handoffs[handoff['itemInstanceId']] = handoff
            summary = (action['state'], action['phase'], details['completedRepetitions'])
            if summary != last:
                print(json.dumps({'case': case, 'state': summary}), flush=True)
                last = summary
            if action['state'] != 'running':
                assert action['state'] == 'succeeded', action
                break
            assert control['ownedByYou'], 'Request must retain its lease'
            if case.startswith('cooked') and not cookers and action['phase'] == 'waiting_for_prepared_ingredients':
                cookers = cooking_items(audit.snap('potato-cooking-before-chicken-arrives'))
                assert cookers, 'Available chopped potato must cook while earlier requested chicken is absent'
                if case == 'cooked_floor':
                    audit.client.stop()
                    break
                initial.append(supply(audit, catalog, 'NuggetChicken', offset=-.65))
            if case == 'repeat_raw' and details['completedRepetitions'] >= 2:
                audit.client.stop()
                break
            time.sleep(.1)
        else:
            write(audit.directory / 'timeout.json', control)
            audit.snap('timeout-snapshot')
            raise TimeoutError(case)
    write(audit.directory / 'final.json', control)
    after = audit.snap('after')
    cooked = []
    if case == 'repeat_raw':
        assert details['completedRepetitions'] >= 2 and len(handoffs) >= 4, 'Two repetitions must hand off four distinct items'
    elif case == 'chopped_pair':
        assert details['completedRepetitions'] == 1 and len(handoffs) == 2 and 'work_at' in phases
    elif case == 'cooked_pair':
        assert details['completedRepetitions'] == 1 and len(handoffs) == 2 and 'work_at' in phases and 'collect_contents' in phases
        cooked = cooked_deliveries(after, handoffs.values())
    else:
        assert cookers and 'work_at' not in phases, 'Supplied chopped potato must bypass chopping'
    return {'requestId': request, 'completedRepetitions': details['completedRepetitions'], 'handoffs': list(handoffs.values()),
            'phases': sorted(phases), 'cookingBeforeChickenArrived': cookers, 'cookedDeliveries': cooked, 'fixtures': initial}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--runtime', type=Path, required=True)
    parser.add_argument('--port-base', type=int, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--case', action='append', choices=CASES)
    parser.add_argument('--timeout', type=float, default=200)
    args = parser.parse_args()
    args.runtime = args.runtime.resolve()
    process = json.loads((args.runtime / 'process.json').read_text())
    if args.port_base == 24702 or args.port_base != process['portBase'] or not sim.is_running(args.runtime):
        parser.error('Use the matching isolated native runtime and non-default ports')
    binary = args.runtime / 'BepInEx/plugins/SousChef.Bridge.dll'
    if hashlib.sha256(binary.read_bytes()).hexdigest() != process['bridgeSha256']:
        parser.error('Runtime DLL differs from its launch manifest')
    sim.wait_ready(args.runtime)
    results = []
    args.output.mkdir(parents=True, exist_ok=True)
    for case in CASES:
        if args.case and case not in args.case:
            continue
        audit = Audit(args); audit.directory = args.output / case; audit.directory.mkdir(parents=True, exist_ok=True)
        try:
            result = dict(run(audit, case), case=case, success=True)
        except Exception as error:
            result = {'case': case, 'success': False, 'error': str(error)}
            if audit.client:
                try:
                    audit.snap('failure-snapshot'); audit.client.stop()
                except Exception as cleanup:
                    result['cleanupError'] = str(cleanup)
        finally:
            if audit.client:
                audit.client.close()
        results.append(result)
        print(json.dumps(result), flush=True)
        write(args.output / 'report.json', {'bridgeSha256': process['bridgeSha256'], 'passed': sum(r['success'] for r in results),
                                           'attempts': len(results), 'results': results})
    return 0 if results and all(r['success'] for r in results) else 2


if __name__ == '__main__':
    raise SystemExit(main())
