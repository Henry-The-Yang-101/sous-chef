#!/usr/bin/env python3
"""Precise native checks for preparation dispatch, exact handoffs and cooking overlap.

Fixtures supply ingredients where a human would, including a catch checkpoint;
all walking, work, equipment progress and handoffs use the production executor.
"""
import argparse
import hashlib
import json
import math
import time
from pathlib import Path

import native_simulator as sim
from held_item_audit import load
from preparation_audit import cooked_deliveries, cooking_items
from task_audit import Audit
from throw_audit import fields, write

READY_CASES = ('ready_before_chop', 'held_raw_ready', 'ready_during_placement')
CASES = ('catch_backlog', 'skip_resume', 'ordered', 'cooking_overlap', 'blocked_handoff') + READY_CASES


def run(audit, case):
    level = 's_rapids_2_1' if case == 'catch_backlog' else 's_rapids_4_2A'
    cat = load(audit, level)
    supplies = {t['foodName']: t['objectInstanceId'] for t in cat['targets'] if t.get('foodName')}
    before = audit.snap('initial')
    bot = next(o for o in before['objects'] if o['name'] == 'Player 2')
    empty = {o['unityInstanceId'] for o in before['objects'] if fields(o, 'ServerAttachStation') and
             fields(o, 'ServerAttachStation').get('m_item') is None}
    counters = sorted((t for t in cat['targets'] if t['objectInstanceId'] in empty and t['kind'] == 'counter'
                       and 'standard' in t['label'] and math.dist(t['position'], bot['transform']['position']) < 6),
                      key=lambda t: math.dist(t['position'], bot['transform']['position']))
    fixtures = []
    def supply(food, index, chopped=False):
        holder = bot['unityInstanceId'] if index is None else counters[index]['objectInstanceId']
        f = audit.fixture(f"ingredient {supplies[food]} {holder}" + (' chopped' if chopped else ''))
        fixtures.append(f)
        return f['itemInstanceId']

    if case == 'cooking_overlap':
        deadline = time.monotonic() + 20
        while time.monotonic() < deadline:
            stance = next((d['approachPosition'] for d in audit.client.available_actions()['destinations']
                           if d.get('foodName') == 'Lettuce'), None)
            if stance:
                sim.command(audit.args.runtime, 'stage-player 1 ' + ' '.join(map(str, (*stance, 0))))
                break
        else:
            raise AssertionError('no same-raft stance for nonthrowable cookware')
        human = next(o for o in before['objects'] if o['name'] == 'Player 1')
        fryers = [o for o in before['objects'] if fields(o, 'ServerCookingStation') and fields(o, 'ServerAttachStation').get('m_item')]
        assert len(fryers) == 2
        fixtures.append(audit.fixture(f"borrow-cookware {fryers[0]['unityInstanceId']} {human['unityInstanceId']}"))
        raw = [supply('NuggetChicken', i) for i in range(2)]
        names = ['NuggetChicken']; states = ['cooked']; repetitions = None
    elif case == 'blocked_handoff':
        names = ['deepfatfryer', 'Lettuce']; states = ['empty', 'raw']; repetitions = 1
    elif case in ('skip_resume', 'ordered'):
        if case == 'ordered':
            supply('NuggetChicken', 0); supply('NuggetChicken', 1)
        names = ['Lettuce', 'NuggetChicken', 'Tomato']; states = ['raw'] * 3; repetitions = 2
    else:
        # Chicken is absent while the raft already holds two finished potatoes.
        # Interrupt pickup of one with a chopped chicken directly in P2's hands.
        potatoes = [supply('Potato', i, True) for i in range(2)]
        names = ['Potato', 'NuggetChicken']; states = ['chopped'] * 2; repetitions = None

    start_clock = sim.read_status(audit.args.runtime)
    audit.request_when_available('prepare_near_player', repetitions,
                                 items=[dict(ingredient=n, preparation=s) for n, s in zip(names, states)])
    request = audit.client.last_response['control']['action']['requestId']
    end = time.monotonic() + audit.args.timeout
    totals = [0] * len(names); sequence = []; receipts = []; cooked = []; overlap = None; caught = None; resupplied = False; parked = None; joined = False
    with (audit.directory / 'trace.ndjson').open('w') as trace:
        while time.monotonic() < end:
            control = audit.client.status()
            action = control['action']; d = action['details']; child = d.get('child') or {}
            trace.write(json.dumps(action) + '\n')
            assert action['requestId'] == request and (action['state'] != 'running' or control['ownedByYou'])
            counts = [r['delivered'] for r in d['preparation'] or []]
            for i, count in enumerate(counts):
                if count != totals[i]:
                    assert count == totals[i] + 1, 'polling missed a delivery'
                    item = d['preparation'][i]['lastDeliveredInstanceId']
                    receipts.append(item); sequence.append(names[i])
                    if case == 'cooking_overlap':
                        cooked.extend(cooked_deliveries(audit.snap(f'confirmed-{count}'), [dict(itemInstanceId=item)]))
            if counts:
                totals = counts
            if case == 'blocked_handoff' and counts:
                parked = parked or d['preparation'][0]['parkedHandoff']
                if counts == [0, 1] and parked and not joined:
                    # P1 joins P2 after the throwable food arrives; the same
                    # parked basket must now finish the original finite request.
                    for dx, dz in ((1.2, 0), (-1.2, 0), (0, 1.2), (0, -1.2), (1, 1), (-1, -1)):
                        pos = next(p['position'] for p in sim.read_status(audit.args.runtime)['players'] if p['name'] == 'Player 2')
                        try:
                            sim.command(audit.args.runtime, f'stage-player 1 {pos[0]+dx} {pos[1]} {pos[2]+dz} 0')
                            joined = True
                            break
                        except RuntimeError as err:
                            if 'supported clear' not in str(err):
                                raise
                    assert joined, 'no supported receiving stance'
            if case == 'catch_backlog' and caught is None and child.get('operation') == 'pick_up' and child.get('phase') == 'navigating':
                caught = supply('NuggetChicken', None, True)
            if case == 'skip_resume' and totals == [2, 0, 2] and not resupplied:
                assert sequence == ['Lettuce', 'Tomato'] * 2, sequence
                # Both A and C reached their finite caps before B was supplied.
                supply('NuggetChicken', 0); supply('NuggetChicken', 1); resupplied = True
            if case == 'cooking_overlap' and overlap is None and d.get('preparation') and d['preparation'][0]['processing'] and child.get('phase') == 'working':
                frame = audit.snap('second-chicken-chopping')
                progressing = cooking_items(frame)
                on_board = [o for o in frame['objects'] if o['unityInstanceId'] in raw and fields(o, 'ServerWorkableItem').get('m_onWorkstation')]
                if progressing and on_board:
                    overlap = dict(cookingContainers=progressing, choppingRaw=on_board[0]['unityInstanceId'])
            if action['state'] != 'running' or (case == 'catch_backlog' and totals == [2, 1]) or (case == 'cooking_overlap' and totals == [2]):
                break
            time.sleep(.09)
        else:
            raise TimeoutError(f'{case}: counts={totals}, last={action}')
    finish_clock = sim.read_status(audit.args.runtime)
    fps = (finish_clock['frame'] - start_clock['frame']) / (finish_clock['realtime'] - start_clock['realtime'])
    audit.snap('after')
    if case == 'catch_backlog':
        assert action['state'] == 'running' and caught and totals == [2, 1] and set(receipts) == set(potatoes + [caught])
        assert sequence[0] == 'NuggetChicken', 'The catch must interrupt, then both exact potatoes must be rediscovered'
    elif case == 'skip_resume':
        assert resupplied and action['state'] == 'succeeded' and totals == [2, 2, 2] and len(set(receipts)) == 6
    elif case == 'blocked_handoff':
        assert joined and parked in receipts and sequence == names[::-1] and action['state'] == 'succeeded'
    elif case == 'ordered':
        assert action['state'] == 'succeeded' and sequence == names * 2 and len(set(receipts)) == 6
    else:
        assert action['state'] == 'running' and totals == [2] and overlap and len(set(receipts)) == 2
        assert len(cooked) == 2
    assert d['completedRepetitions'] == min(totals)
    return dict(request=request, averageHeadlessFps=round(fps, 2), worker=control.get('worker'), sequence=sequence, exactHandoffs=receipts, overlap=overlap, cooked=cooked, fixtures=fixtures)


def ready_priority(audit, case):
    cat = load(audit, 's_rapids_2_1')
    before = audit.snap('initial')
    bot = next(o for o in before['objects'] if o['name'] == 'Player 2')
    supplies = {t['foodName']: t['objectInstanceId'] for t in cat['targets'] if t.get('foodName')}
    empty = {o['unityInstanceId'] for o in before['objects'] if fields(o, 'ServerAttachStation') and
             fields(o, 'ServerAttachStation').get('m_item') is None}
    counters = sorted((t for t in cat['targets'] if t['objectInstanceId'] in empty and t['kind'] == 'counter'
                       and 'standard' in t['label'] and math.dist(t['position'], bot['transform']['position']) < 6),
                      key=lambda t: math.dist(t['position'], bot['transform']['position']))
    board = min((o for o in before['objects'] if fields(o, 'ServerWorkstation') and o['unityInstanceId'] in empty),
                key=lambda o: math.dist(o['transform']['position'], bot['transform']['position']))
    assert len(counters) >= 3
    fixtures = []
    def supply(name, holder, chopped=False):
        row = audit.fixture(f"ingredient {supplies[name]} {holder}" + (' chopped' if chopped else ''))
        fixtures.append(row)
        return row['itemInstanceId']
    holder = (bot['unityInstanceId'] if case == 'held_raw_ready' else
              counters[0]['objectInstanceId'] if case == 'ready_during_placement' else board['unityInstanceId'])
    raw = supply('Potato', holder)
    ready = []
    def stock_ready():
        ready.extend(supply('NuggetChicken', c['objectInstanceId'], True) for c in counters[1:3])
    if case != 'ready_during_placement':
        stock_ready()
    audit.request_when_available('prepare_near_player', None, items=[
        dict(ingredient=name, preparation='chopped') for name in ('Potato', 'NuggetChicken')])
    request = audit.client.last_response['control']['action']['requestId']
    totals = [0, 0]; sequence = []; receipts = []; work = False
    end = time.monotonic() + audit.args.timeout
    with (audit.directory / 'trace.ndjson').open('w') as trace:
        while time.monotonic() < end:
            control = audit.client.status(); action = control['action']; details = action['details']; child = details.get('child') or {}
            trace.write(json.dumps(action) + '\n')
            assert action['state'] == 'running' and action['requestId'] == request and control['ownedByYou'], action
            counts = [r['delivered'] for r in details['preparation'] or []]
            for i, count in enumerate(counts):
                if count != totals[i]:
                    assert count == totals[i] + 1
                    sequence.append(('Potato', 'NuggetChicken')[i]); receipts.append(details['preparation'][i]['lastDeliveredInstanceId'])
            if counts:
                totals = counts
            if case == 'ready_during_placement' and not ready and child.get('operation') == 'place_at' and child.get('phase') == 'navigating':
                stock_ready(); audit.snap('ready-arrived-before-chop')
            if action['phase'] == 'work_at':
                assert ready and totals[1] == 2, 'P2 started chopping while requested ready chicken was waiting'
                if not work:
                    audit.snap('chopping-after-ready-handoffs'); work = True
            if totals == [1, 2]:
                break
            time.sleep(.05)
        else:
            raise TimeoutError(f'{case}: {action}')
    audit.snap('after')
    assert sequence == ['NuggetChicken', 'NuggetChicken', 'Potato'] and set(receipts[:2]) == set(ready)
    assert work and details['completedRepetitions'] == 1
    return dict(request=request, sequence=sequence, exactHandoffs=receipts, rawPotato=raw,
                readyChicken=ready, choppedOnlyAfterReadyDelivered=True, fixtures=fixtures)


def waiting_frames(audit):
    """Same workload on old and new binaries; no full/gameplay stream subscription."""
    # P2 has local salad dispensers, so preparation is eligible on both builds.
    # Chicken and potato remain on the other raft for the entire timed window.
    load(audit, 's_rapids_4_2A')
    items = [dict(ingredient=n, preparation='chopped') for n in ('Potato', 'NuggetChicken')]
    audit.request_when_available('prepare_near_player', None, items=items)
    request = audit.client.last_response['control']['action']['requestId']
    time.sleep(2)
    windows = []
    for _ in range(2):
        start = sim.read_status(audit.args.runtime)
        for _ in range(50):
            begin = time.monotonic()
            c = audit.client.status()
            assert c['action']['requestId'] == request and c['ownedByYou']
            assert c['action']['phase'] == 'waiting_for_ingredient' and c['worker']['state'] == 'ready'
            time.sleep(max(0, .2 - (time.monotonic() - begin)))
        end = sim.read_status(audit.args.runtime)
        windows.append(dict(frames=end['frame'] - start['frame'], seconds=end['realtime'] - start['realtime']))
    return dict(windows=windows, averageHeadlessFps=sum(w['frames'] for w in windows) / sum(w['seconds'] for w in windows))


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--runtime', type=Path, required=True)
    p.add_argument('--port-base', type=int, required=True)
    p.add_argument('--output', type=Path, required=True)
    p.add_argument('--case', choices=CASES + ('waiting_frames',), action='append')
    p.add_argument('--timeout', type=float, default=160)
    args = p.parse_args()
    manifest = json.loads((args.runtime / 'process.json').read_text())
    digest = hashlib.sha256((args.runtime / 'BepInEx/plugins/SousChef.Bridge.dll').read_bytes()).hexdigest()
    if args.port_base != manifest['portBase'] or args.port_base == 24702 or digest != manifest['bridgeSha256'] or not sim.is_running(args.runtime):
        p.error('use the matching live isolated simulator')
    results = []
    for case in args.case or CASES:
        audit = Audit(args); audit.directory = args.output / case; audit.directory.mkdir(exist_ok=True, parents=True)
        try:
            result = waiting_frames(audit) if case == 'waiting_frames' else ready_priority(audit, case) if case in READY_CASES else run(audit, case)
            row = dict(case=case, success=True, **result)
        except Exception as exc:
            row = dict(case=case, success=False, error=repr(exc))
        finally:
            if audit.client:
                audit.client.stop(); audit.client.close()
        results.append(row)
        print(json.dumps(row), flush=True)
        write(args.output / 'report.json', dict(bridgeSha256=digest, results=results))
    return 0 if all(r['success'] for r in results) else 2


if __name__ == '__main__':
    raise SystemExit(main())
