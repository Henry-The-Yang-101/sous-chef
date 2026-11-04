#!/usr/bin/env python3
"""Exercise transport scheduling, pause and moving pickups in a native simulator.

Use an explicitly selected isolated runtime/port pair. The belt case stages only
P2's initial supported pose. The pipeline case seeds a post-dispatch checkpoint
with food already moving on a native belt; it does not test handoff selection or
placement. All measured chef execution uses ordinary ActionClient commands.
"""
import argparse
import json
import math
from pathlib import Path
import time

import native_simulator as sim
from task_audit import Audit
from throw_audit import carrier_items, write


def moving_food(catalog):
    belts = {t['id'] for t in catalog['targets'] if 'ConveyorStation' in t['label']}
    foods = [i for i in catalog['items'] if not i['supply'] and i['ingredientId']
             and i['sourceId'] in belts and i['routeDistance'] is not None]
    if not foods:
        raise AssertionError('No obtainable native belt cargo')
    return min(foods, key=lambda i: i['routeDistance'])


def cargo_motion(audit, instance):
    before = audit.snap('moving-before')
    time.sleep(.15)
    after = audit.snap('moving-after')
    item = next(o for o in before['objects'] if o['unityInstanceId'] == instance)
    moved = next(o for o in after['objects'] if o['id'] == item['id'])
    distance = math.dist(item['transform']['position'], moved['transform']['position'])
    assert distance > .02, 'Cargo must actually move in native physics'
    return item, distance


def belt(audit):
    catalog = audit.load('s_sushi_4_5')
    food = moving_food(catalog)
    approach = next(d['approachPosition'] for d in catalog['destinations'] if d['id'] == food['sourceId'])
    sim.command(audit.args.runtime, 'stage-player 2 ' + ' '.join(map(str, (*approach, 0))))
    item, distance = cargo_motion(audit, food['objectInstanceId'])
    catalog = audit.client.available_actions()
    target = next(t for t in catalog['targets'] if t['objectInstanceId'] == food['objectInstanceId'])
    audit.client.pick_up(target['id'])
    audit.observe('belt-pickup', timeout=70)
    carried = carrier_items(audit.snap('held'))
    assert len(carried) == 1 and carried[0]['id'] == item['id'], 'Must carry the exact original moving object'
    return {'exactItem': food['objectInstanceId'], 'displacement': distance, 'initialPoseFixture': approach}


def pipeline(audit):
    catalog = audit.load('s_sushi_4_5')
    food = moving_food(catalog)
    ingredient = next(i['id'] for i in catalog['ingredients'] if i['label'] == 'SushiFish')
    audit.request_when_available('prepare_near_player', items=[
        {'ingredient': ingredient, 'preparation': 'chopped', 'quantity': 2}])
    request = audit.client.last_response['control']['action']['requestId']
    audit.fixture('in-flight ' + str(food['objectInstanceId']) + ' 1')
    _, distance = cargo_motion(audit, food['objectInstanceId'])
    deadline = time.monotonic() + 40
    with (audit.directory / 'overlap.ndjson').open('w') as trace:
        while time.monotonic() < deadline:
            audit.client.available_actions()
            control = audit.client.last_response['control']
            action = control['action']
            trace.write(json.dumps(control) + '\n')
            assert action['requestId'] == request and action['state'] == 'running' and control['ownedByYou'], control
            if (action['details']['pendingDeliveries'] and action['phase'] == 'work_at'
                    and (action['details'].get('child') or {}).get('phase') == 'working'):
                write(audit.directory / 'overlap.json', control)
                audit.snap('concurrent-chopping')
                audit.client.stop()
                return {'request': request, 'checkpointSeeded': True,
                        'pendingDeliveries': action['details']['pendingDeliveries'], 'displacement': distance}
            time.sleep(.1)
    audit.client.stop()
    raise AssertionError('No native chopping/transport overlap')


def pause(audit):
    catalog = audit.load('s_sushi_1_2')
    ingredient = next(i['id'] for i in catalog['ingredients'] if i['label'] == 'SushiFish')
    audit.request_when_available('prepare_near_player', items=[{'ingredient': ingredient, 'preparation': 'chopped'}])
    request = audit.client.last_response['control']['action']['requestId']
    deadline = time.monotonic() + 30
    while time.monotonic() < deadline:
        audit.client.available_actions()
        action = audit.client.last_response['control']['action']
        if action['phase'] == 'work_at' and (action['details'].get('child') or {}).get('phase') == 'working':
            break
        time.sleep(.05)
    else:
        raise AssertionError('Did not reach native chopping')
    sim.command(audit.args.runtime, 'pause on')
    records = []
    for _ in range(25):
        audit.client.available_actions()
        control = audit.client.last_response['control']
        action = control['action']
        records.append(control)
        assert action['requestId'] == request and action['state'] == 'running', control
        assert control['ownedByYou'] and control['suspended'] and control['reason'] == 'paused', control
        time.sleep(.1)
    write(audit.directory / 'paused.json', records)
    sim.command(audit.args.runtime, 'input-focus off')
    sim.command(audit.args.runtime, 'pause off')
    time.sleep(.5)
    audit.client.available_actions()
    control = audit.client.last_response['control']
    assert control['ownedByYou'] and control['suspended'] and control['reason'] == 'game_unfocused', control
    write(audit.directory / 'focus-transition.json', control)
    sim.command(audit.args.runtime, 'input-focus on')
    audit.observe('resumed')
    assert audit.client.last_response['control']['action']['requestId'] == request
    audit.request_when_available('monitor_equipment', None)
    sim.command(audit.args.runtime, 'pause on')
    start = time.monotonic()
    control = audit.client.stop()
    elapsed = time.monotonic() - start
    assert control['action']['state'] == 'cancelled' and not control['ownedByYou'], control
    sim.command(audit.args.runtime, 'pause off')
    write(audit.directory / 'stopped.json', control)
    return {'sameRequest': request, 'pausedSeconds': 2.5, 'focusTransitionPreserved': True, 'stopSeconds': elapsed}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('case', choices=('belt', 'pipeline', 'pause'))
    parser.add_argument('--runtime', type=Path, required=True)
    parser.add_argument('--port-base', type=int, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    args.runtime = args.runtime.resolve()
    args.output.mkdir(parents=True, exist_ok=True)
    audit = Audit(args)
    try:
        result = {'belt': belt, 'pipeline': pipeline, 'pause': pause}[args.case](audit)
        result.update(case=args.case, success=True,
                      binary=json.loads((args.runtime / 'process.json').read_text())['bridgeSha256'])
        write(args.output / 'result.json', result)
        print(json.dumps(result))
    finally:
        if audit.client is not None:
            audit.client.close()
        # A failed assertion must not strand the isolated simulator's native gate.
        if sim.is_running(args.runtime):
            sim.command(args.runtime, 'pause off')
            sim.command(args.runtime, 'input-focus on')
