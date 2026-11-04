#!/usr/bin/env python3
"""Native story 2-1 handoff and floor-clutter regressions in an isolated runtime.

Fixtures supply raw food to P2's separated chopping raft, or set initial local
chef poses/hands. Measured chopping, walking and delivery use production logical
inputs. Never run this against the user's normal game ports.
"""
import argparse
import json
import math
from pathlib import Path
import time

import native_simulator as sim
from task_audit import Audit
from throw_audit import carrier_items, fields, write
from clients.actions import ActionClient
from clients.control import ControlError


def wait_catalog(audit, predicate, timeout=15):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        catalog = audit.client.available_actions()
        if predicate(catalog):
            return catalog
        time.sleep(.1)
    raise TimeoutError('Required partial catalogue did not become available')


def load(audit):
    generation = sim.read_status(audit.args.runtime)['generation'] + 1
    sim.command(audit.args.runtime, 'load s_rapids_2_1')
    sim.wait_ready(audit.args.runtime, generation)
    audit.client = ActionClient(audit.args.port_base + 1, timeout=15)
    # This kitchen moves continuously. Usable partial routes are sufficient;
    # demanding globally complete discovery would reproduce the original wait.
    return wait_catalog(audit, lambda c: c.get('preparationOptions') and
                        any('standard' in d['label'] for d in c['destinations']))


def food(audit, supply, holder):
    return audit.fixture('ingredient ' + str(supply['objectInstanceId']) + ' ' + str(holder))['itemInstanceId']


def preparation(audit):
    catalog = load(audit)
    supplies = {t['foodName']: t for t in catalog['targets'] if t['kind'] == 'ingredient_supply'}
    counters = [d for d in catalog['destinations'] if 'standard' in d['label']]
    if audit.args.case == 'counter':
        pose = counters[-1]['approachPosition']
        sim.command(audit.args.runtime, 'stage-player 1 ' + ' '.join(map(str, (*pose, 180))))
    elif audit.args.case == 'busy':
        native = audit.snap('initial-human')
        human = next(o for o in native['objects'] if o['name'] == 'Player 1')
        food(audit, supplies['Potato'], human['unityInstanceId'])
        # Facing away and occupied hands each veto a native catch. Floor delivery
        # must therefore succeed rather than waiting for the human to cooperate.
        pose = human['transform']['position']
        sim.command(audit.args.runtime, 'stage-player 1 ' + ' '.join(map(str, (*pose, 180))))
    names = ['NuggetChicken', 'Potato', 'NuggetChicken'] if audit.args.case == 'mixed' else ['NuggetChicken']
    if audit.args.case == 'mixed':
        bot = next(p['position'] for p in sim.read_status(audit.args.runtime)['players'] if p['name'] == 'Player 2')
        # Raw supplies can be given to local raft counters before every distant
        # counter has a published route in the moving kitchen's partial graph.
        counters = sorted((t for t in catalog['targets'] if t['kind'] == 'counter' and 'standard' in t['label']
                           and math.dist(bot, t['position']) < 4), key=lambda t: math.dist(bot, t['position']))
    if len(counters) < len(names):
        raise AssertionError('Not enough obtainable initial counters for the supplied-food fixture')
    for name, counter in zip(names, counters):
        food(audit, supplies[name], counter['objectInstanceId'])
    before = audit.snap('before')
    selectors = {i['label']: i['id'] for i in catalog['preparationOptions']}
    items = [{'ingredient': selectors[name], 'preparation': 'chopped'} for name in names]
    audit.request_when_available('prepare_near_player', items=items)
    captured_wait = False
    collected = False
    handoff_order = {}
    blocked_at = None

    def inspect_wait(action):
        nonlocal captured_wait, collected, blocked_at
        if action['details'].get('handoff'):
            handoff = action['details']['handoff']
            handoff_order[handoff['itemInstanceId']] = handoff
        if (audit.args.case == 'mixed' and not collected and action['phase'] == 'waiting_for_player_handoff'
                and action['details']['completedSteps'] >= 22 and len(handoff_order) >= 2):
            if blocked_at is None:
                blocked_at = time.monotonic()
            if time.monotonic() - blocked_at >= 1:
                audit.snap('crowded-ground')
                human = next(o for o in before['objects'] if o['name'] == 'Player 1')
                audit.fixture('collect ' + str(next(iter(handoff_order))) + ' ' + str(human['unityInstanceId']))
                collected = True
        if not captured_wait and action['phase'] == 'waiting_for_ingredient' and action['details']['completedSteps'] >= 16:
            captured_wait = True
            audit.snap('waiting-for-supply')
            audit.fixture('sources all')
            write(audit.directory / 'waiting-catalog.json', audit.client.available_actions())

    try:
        receipts = audit.observe('task', timeout=110, on_poll=inspect_wait)
    except Exception:
        audit.snap('failure')
        raise
    after = audit.snap('after')
    assert not carrier_items(after), 'P2 must finish with empty hands'
    handoffs = {a['details']['handoff']['itemInstanceId']: a['details']['handoff']
                for a in receipts if a['details'].get('handoff')}
    assert len(handoffs) == len(names), handoffs
    assert any(a['phase'] == 'work_at' for a in receipts), 'Native chopping must occur'
    if audit.args.case == 'counter':
        assert all(h['mode'] in ('counter_near_player', 'throw_near_player') for h in handoffs.values()), handoffs
    if audit.args.case == 'busy':
        assert any(h['mode'] == 'throw_ground_near_player' for h in handoffs.values()), handoffs
        assert carrier_items(before, 'Player 1')[0]['id'] == carrier_items(after, 'Player 1')[0]['id']
    native_ids = {o['unityInstanceId'] for o in after['objects'] if o.get('activeInHierarchy')}
    assert set(handoffs) <= native_ids, 'Every delivered native food must still exist'
    return {'handoffs': list(handoffs.values()), 'singleRequest': receipts[-1]['requestId'],
            'nativeChopping': True, 'p2EmptyHands': True, 'resumedAfterHumanCollection': collected}


def clutter(audit):
    catalog = load(audit)
    sim.command(audit.args.runtime, 'stage-player 2 21.6 0 1.2 180')
    catalog = wait_catalog(audit, lambda c: all(any(d['id'] == t['id'] for d in c['destinations'])
                        for t in c.get('targets', []) if t['kind'] == 'ingredient_supply') and c.get('preparationOptions'))
    source = next(t for t in catalog['targets'] if t.get('foodName') == 'Potato')
    goal = next(d for d in catalog['destinations'] if d['id'] == source['id'])
    start = next(p['position'] for p in sim.read_status(audit.args.runtime)['players'] if p['name'] == 'Player 2')
    end = goal['approachPosition']

    def probe(label):
        sim.command(audit.args.runtime, 'probe-walk ' + ' '.join(map(str, (*start, *end))))
        result = json.loads((audit.args.runtime / 'probe.json').read_text())
        write(audit.directory / (label + '.json'), result)
        assert result['start'] and result['end'] and result['clear'], result

    probe('clean-path')
    loose = []
    for fraction in (.25, .5, .75):
        point = [start[n] + (end[n] - start[n]) * fraction for n in range(3)]
        point[1] += .1
        loose.append(audit.fixture('loose-ingredient ' + str(source['objectInstanceId']) + ' ' +
                                  ' '.join(map(str, point)))['itemInstanceId'])
    time.sleep(.6)
    before = audit.snap('loose-before')
    assert all(any(o['unityInstanceId'] == item and fields(o, 'ServerPhysicalAttachment') for o in before['objects']) for item in loose)
    probe('cluttered-path')
    audit.client.navigate_to(source['id'])
    deadline = time.monotonic() + 30
    with (audit.directory / 'walk.ndjson').open('w') as trace:
        while time.monotonic() < deadline:
            audit.client.available_actions()
            control = audit.client.last_response['control']
            trace.write(json.dumps(control) + '\n')
            if control['navigation']['state'] == 'idle':
                assert control['navigation']['reason'] == 'arrived', control
                break
            time.sleep(.1)
        else:
            raise TimeoutError('Native walking through loose food did not arrive')
    after = audit.snap('loose-after')
    assert not carrier_items(after), 'Walking must not pick the clutter up'
    return {'looseIngredients': loose, 'samePathClearBeforeAndAfter': True, 'nativeWalkingArrived': True,
            'walkDistance': math.dist(start, next(o['transform']['position'] for o in after['objects'] if o['name'] == 'Player 2'))}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('case', choices=('default', 'mixed', 'busy', 'counter', 'clutter'))
    parser.add_argument('--runtime', type=Path, required=True)
    parser.add_argument('--port-base', type=int, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    args.runtime = args.runtime.resolve()
    args.output.mkdir(parents=True, exist_ok=True)
    audit = Audit(args)
    try:
        result = clutter(audit) if args.case == 'clutter' else preparation(audit)
        result.update(case=args.case, success=True, level='s_rapids_2_1',
                      binary=json.loads((args.runtime / 'process.json').read_text())['bridgeSha256'])
        write(args.output / 'result.json', result)
        print(json.dumps(result))
    finally:
        if audit.client is not None:
            try:
                audit.client.stop()
            except (ControlError, OSError):
                pass
            finally:
                audit.client.close()
