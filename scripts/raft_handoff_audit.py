#!/usr/bin/env python3
"""Record native raft handoffs, repeated preparation, transitions and frame costs.

Supply fixtures create native food on local counters before the request. Optional
initial poses use the simulator's checked staging command. P2 then uses ordinary
production logical inputs; no chef/cargo/progress writes occur during execution.
"""
import argparse
import hashlib
import json
import math
from pathlib import Path
import statistics
import sys
import threading
import time

import native_simulator as sim
from task_audit import Audit
from throw_audit import fields, write
from clients.actions import ActionClient
from clients.bridge import BridgeClient
from clients.control import ControlError


class Samples:
    def __init__(self, port, directory):
        self.client = BridgeClient(port, timeout=15)
        self.directory = directory
        self.records = []
        self.stop = False
        self.error = None
        self.skipped = 0
        self.thread = threading.Thread(target=self.read, daemon=True)
        self.thread.start()

    def read(self):
        try:
            with (self.directory / 'samples.ndjson').open('w') as output:
                for state in self.client.messages():
                    if self.stop:
                        return
                    if state['type'] == 'gap':
                        self.skipped += state['beforeSequence'] - state['afterSequence'] - 1
                    if state['type'] != 'gameplay':
                        continue
                    bot = next((o for o in state['objects'] if o['name'] == 'Player 2'), None)
                    work = []
                    for o in state['objects']:
                        w = fields(o, 'ServerWorkableItem')
                        if w:
                            work.append({'id': o['id'], 'progress': w.get('m_progress'), 'subProgress': w.get('m_subProgress'),
                                         'onWorkstation': w.get('m_onWorkstation')})
                    record = {'time': state['clock']['realtime'], 'frame': state['clock']['renderFrame'],
                              'sequence': state['sequence'], 'coalesced': state['diagnostics']['coalescedCaptures'],
                              'captureMs': state['diagnostics']['captureMilliseconds'],
                              'bot': None if bot is None else {'position': bot['transform']['position'],
                                  'respawning': fields(bot, 'PlayerControls').get('m_bRespawning'),
                                  'ground': fields(bot, 'GroundCast').get('m_groundCollider'),
                                  'interaction': fields(bot, 'ClientPlayerControlsImpl_Default').get('m_sessionInteraction')},
                              'work': work,
                              'boards': [fields(o, 'ServerAttachStation').get('m_item', {}).get('objectId')
                                         for o in state['objects'] if fields(o, 'ServerWorkstation') and
                                         fields(o, 'ServerAttachStation').get('m_item')]}
                    self.records.append(record)
                    output.write(json.dumps(record) + '\n')
        except Exception as error:
            if not self.stop:
                self.error = str(error)

    def close(self):
        self.stop = True
        try:
            self.client.socket.shutdown(2)
        except OSError:
            pass
        self.thread.join(5)
        self.client.close()

    def summary(self):
        gaps = [1000 * (b['time'] - a['time']) / (b['frame'] - a['frame'])
                for a, b in zip(self.records, self.records[1:]) if b['frame'] > a['frame']]
        ordered = sorted(gaps)
        captures = sorted(r['captureMs'] for r in self.records)
        return {'samples': len(self.records), 'error': self.error, 'skippedSequences': self.skipped,
                'observedSeconds': self.records[-1]['time'] - self.records[0]['time'] if len(self.records) > 1 else 0,
                'approximateFps': None if len(self.records) < 2 else
                    (self.records[-1]['frame'] - self.records[0]['frame']) / (self.records[-1]['time'] - self.records[0]['time']),
                'sampledFrameMsMedian': statistics.median(gaps) if gaps else None,
                'sampledFrameMsP95': ordered[int(.95 * (len(ordered) - 1))] if ordered else None,
                'sampledFrameMsMax': max(gaps) if gaps else None,
                'captureMsMedian': statistics.median(captures) if captures else None,
                'captureMsP95': captures[int(.95 * (len(captures) - 1))] if captures else None,
                'captureMsMax': max(captures) if captures else None,
                'respawnObserved': any(r['bot'] and r['bot']['respawning'] for r in self.records),
                'lowestBotY': min((r['bot']['position'][1] for r in self.records if r['bot']), default=None)}


def load(audit):
    generation = sim.command(audit.args.runtime, 'fps ' + str(audit.args.fps))['generation'] + 1
    sim.command(audit.args.runtime, 'load ' + audit.args.level)
    ready = sim.wait_ready(audit.args.runtime, generation)
    audit.client = ActionClient(audit.args.port_base + 1, timeout=15)
    assert audit.client.hello['sessionId'] == ready['sessionId']
    deadline = time.monotonic() + 20
    while time.monotonic() < deadline:
        catalog = audit.client.available_actions()
        if catalog.get('preparationOptions') and any('standard' in d['label'] for d in catalog['destinations']):
            return catalog
        time.sleep(.1)
    raise TimeoutError('No local counter catalogue')


def run(audit):
    args = audit.args
    catalog = load(audit)
    before = audit.snap('initial')
    bot = next(o for o in before['objects'] if o['name'] == 'Player 2')
    human = next(o for o in before['objects'] if o['name'] == 'Player 1')
    if args.human_pose:
        sim.command(args.runtime, 'stage-player 1 ' + ' '.join(map(str, args.human_pose)))
        before = audit.snap('staged-human')
    elif args.case == 'far':
        ref = fields(human, 'GroundCast')['m_groundCollider']['objectId']
        floor = next(o for o in before['objects'] if o['id'] == ref)
        bounds = next(c['worldBounds'] for c in floor['colliders'] if not c['isTrigger'])
        center, size = bounds['center'], bounds['size']
        poses = [(center[0] + x * (size[0] * .5 - 1), 0, center[2] + z * (size[2] * .5 - 1), 180)
                 for x in (-1, -.5, 0, .5, 1) for z in (-1, -.5, 0, .5, 1)]
        poses.sort(key=lambda p: -math.dist(p[:3], bot['transform']['position']))
        for pose in poses:
            try:
                sim.command(args.runtime, 'stage-player 1 ' + ' '.join(map(str, pose)))
                break
            except RuntimeError as error:
                if 'supported clear' not in str(error):
                    raise
        else:
            raise AssertionError('No far supported human fixture pose')
        before = audit.snap('staged-human')
    supplies = {t['foodName']: t for t in catalog['targets'] if t['kind'] == 'ingredient_supply'}
    empty = {o['unityInstanceId'] for o in before['objects'] if fields(o, 'ServerAttachStation') and
             fields(o, 'ServerAttachStation').get('m_item') is None}
    local = sorted((t for t in catalog['targets'] if t['kind'] == 'counter' and 'standard' in t['label']
                    and t['objectInstanceId'] in empty
                    and math.dist(t['position'], bot['transform']['position']) < 6),
                   key=lambda t: math.dist(t['position'], bot['transform']['position']))
    names = ['Potato', 'NuggetChicken'] * args.cycles if args.case == 'repeat' else ['Potato']
    if len(local) < len(names):
        raise AssertionError('Not enough local fixture counters')
    fixtures = []
    for name, counter in zip(names, local):
        fixtures.append(audit.fixture('ingredient ' + str(supplies[name]['objectInstanceId']) + ' ' + str(counter['objectInstanceId']) +
                                      (' chopped' if args.case in ('far', 'safety') else '')))
    write(audit.directory / 'catalog.json', audit.client.available_actions())
    samples = Samples(args.port_base + 2, audit.directory)
    handoffs, transitions, landings = {}, [], {}
    end = time.monotonic() + args.timeout
    started = time.monotonic()
    try:
        items = [{'ingredient': name, 'preparation': 'chopped'} for name in ('Potato', 'NuggetChicken')] if args.case == 'repeat' else [
            {'ingredient': 'Potato', 'preparation': 'chopped'}]
        audit.request_when_available('prepare_near_player', None if args.case == 'repeat' else 1, items=items)
        request = audit.client.last_response['control']['action']['requestId']
        last = None
        with (audit.directory / 'control.ndjson').open('w') as output:
            while time.monotonic() < end:
                begin = time.monotonic()
                audit.client.available_actions()
                control = audit.client.last_response['control']; action = control['action']; d = action['details']; child = d.get('child') or {}
                output.write(json.dumps(control) + '\n'); output.flush()
                assert action['requestId'] == request
                if action['state'] == 'running':
                    assert control['ownedByYou']
                if d.get('handoff'):
                    handoffs[d['handoff']['itemInstanceId']] = d['handoff']
                    details = child.get('details') or {}
                    details = details.get('result') or details
                    details = details.get('throw') or details
                    if details.get('landingSupportInstanceId') is not None:
                        landings[d['handoff']['itemInstanceId']] = {k: details.get(k) for k in (
                            'landingSupportInstanceId', 'landingPosition', 'landingDistance', 'releasePosition', 'walkDistance')}
                native_time = samples.records[-1]['time'] if samples.records else action['started']
                row = {'time': native_time, 'elapsed': time.monotonic() - started,
                       'state': action['state'], 'phase': action['phase'], 'childPhase': child.get('phase'),
                       'childOperation': child.get('operation'), 'childTarget': child.get('targetId'),
                       'childStarted': child.get('started'), 'repetitions': d['completedRepetitions'],
                       'steps': d['completedSteps'], 'waiting': d.get('waitingReason'), 'rttMs': 1000 * (time.monotonic() - begin)}
                key = (row['state'], row['phase'], row['childPhase'], row['repetitions'])
                if key != last:
                    transitions.append(row); print(json.dumps(row), flush=True); last = key
                if action['state'] != 'running' or args.case == 'repeat' and d['completedRepetitions'] >= args.cycles:
                    break
                if samples.records and samples.records[-1]['bot'] and samples.records[-1]['bot']['respawning']:
                    break
                time.sleep(max(0, .2 - (time.monotonic() - begin)))
        final = audit.client.status()
        if final['action']['state'] not in ('running', 'succeeded'):
            write(audit.directory / 'failed-control.json', final)
            raise AssertionError(final['action'])
        final_snapshot = audit.snap('final')
        audit.client.stop()
    finally:
        samples.close()
    result = {'case': args.case, 'level': args.level, 'requestedFps': args.fps, 'action': final['action'],
              'fixtures': fixtures, 'handoffs': list(handoffs.values()), 'landings': list(landings.values()), 'transitions': transitions,
              'performance': samples.summary(), 'elapsed': time.monotonic() - started,
              'bridgeSha256': hashlib.sha256((args.runtime / 'BepInEx/plugins/SousChef.Bridge.dll').read_bytes()).hexdigest()}
    placed_work = {}
    started_work = set()
    actual_gaps = []
    for sample in samples.records:
        for workable in sample['work']:
            if workable['id'] in started_work:
                continue
            if workable['id'] in sample['boards']:
                placed_work.setdefault(workable['id'], sample['time'])
            if (workable['progress'] or workable['subProgress']) and workable['id'] in placed_work:
                actual_gaps.append(sample['time'] - placed_work.pop(workable['id']))
                started_work.add(workable['id'])
    result['placedToFirstChopSeconds'] = [g for g in actual_gaps if g >= 0]
    delivered = [o for o in final_snapshot['objects'] if o['unityInstanceId'] in handoffs and o.get('activeInHierarchy')]
    human = next(o for o in final_snapshot['objects'] if o['name'] == 'Player 1')
    ground_id = fields(human, 'GroundCast')['m_groundCollider']['objectId']
    floor = next(o for o in final_snapshot['objects'] if o['id'] == ground_id)
    bounds = next(c['worldBounds'] for c in floor['colliders'] if not c['isTrigger'])
    center, size = bounds['center'], bounds['size']
    result['deliveredItems'] = [{'instanceId': o['unityInstanceId'], 'name': o['name'], 'position': o['transform']['position']}
                                for o in delivered]
    result['allCargoOnReceivingFloor'] = len(delivered) == len(names) and all(
        abs(o['transform']['position'][0] - center[0]) < size[0] / 2 and
        abs(o['transform']['position'][2] - center[2]) < size[2] / 2 and
        abs(o['transform']['position'][1] - center[1] - size[1] / 2) < .25 for o in delivered)
    result['success'] = (not result['performance']['respawnObserved'] and result['performance']['lowestBotY'] > -.5 and
                         samples.error is None and result['allCargoOnReceivingFloor']) and (
        final['action']['details']['completedRepetitions'] >= args.cycles if args.case == 'repeat' else final['action']['state'] == 'succeeded')
    write(audit.directory / 'result.json', result)
    return result


def thrown(audit):
    """Infinite chopped potato/chicken while the human keeps supplying raw food.

    Two raw ingredients are in play; each completed P2 handoff supplies the next,
    whichever of potato and chicken has been supplied least. The native spawn fixture stands in for the
    human's throw landing on P2's floor (`loose`), a standard counter (`counter`),
    a free board (`board`), or every board at once before the request (`full`).
    """
    args = audit.args
    catalog = load(audit)
    supplies = {t['foodName']: t for t in catalog['targets'] if t['kind'] == 'ingredient_supply'}
    names, fixtures, supplied = ['Potato', 'NuggetChicken'], [], {'Potato': 0, 'NuggetChicken': 0}

    def supply(n, name=None, boards_only=False):
        name = name or min(names, key=lambda m: (supplied[m], names.index(m)))
        state = audit.snap('supply-' + str(len(fixtures)))
        bot = next(o for o in state['objects'] if o['name'] == 'Player 2')
        floor = next(o for o in state['objects'] if o['id'] == fields(bot, 'GroundCast')['m_groundCollider']['objectId'])
        bounds = next(c['worldBounds'] for c in floor['colliders'] if not c['isTrigger'])
        source, command = supplies[name]['objectInstanceId'], None
        if args.layout == 'loose':
            p, c = bot['transform']['position'], bounds['center']
            point = (p[0] + (c[0] - p[0]) * .5 + (.8 if n % 2 else -.8), .4, p[2] + (c[2] - p[2]) * .5)
            command = 'loose-ingredient %d %.3f %.3f %.3f' % (source, *point)
        empty = {o['unityInstanceId']: o['transform']['position'] for o in state['objects'] if fields(o, 'ServerAttachStation') and
                 fields(o, 'ServerAttachStation').get('m_item') is None and not fields(o, 'ServerConveyorStation')}
        local = lambda t: t['objectInstanceId'] in empty and all(
            abs(empty[t['objectInstanceId']][a] - bounds['center'][a]) <= bounds['size'][a] / 2 + 1 for a in (0, 2))
        for boards in () if command else (True,) if boards_only else (True, False) if args.layout in ('board', 'full') and n else (False,):
            holders = sorted((t for t in audit.client.available_actions()['targets'] if local(t) and
                              (t['kind'] == 'workstation' if boards else t['kind'] == 'counter' and 'standard' in t['label'])),
                             key=lambda t: math.dist(t['position'], bot['transform']['position']))
            if holders:
                command = 'ingredient %d %d' % (source, holders[0]['objectInstanceId']); break
        if command is None:
            if boards_only:
                return None
            raise AssertionError('No local holder for supplied ' + name)
        supplied[name] += 1
        return audit.fixture(command)

    fixtures += [supply(0), supply(1)]
    while args.layout == 'full' and fixtures[-1] is not None:
        fixtures.append(supply(len(fixtures), 'NuggetChicken', boards_only=True))
    fixtures = [f for f in fixtures if f]
    samples = Samples(args.port_base + 2, audit.directory)
    handoffs, transitions, waits = [], [], {}
    started = time.monotonic(); end = started + args.timeout; waiting = None
    try:
        audit.request_when_available('prepare_near_player', None, items=[{'ingredient': n, 'preparation': 'chopped'} for n in names])
        request = audit.client.last_response['control']['action']['requestId']
        last = None
        with (audit.directory / 'control.ndjson').open('w') as output:
            while time.monotonic() < end:
                begin = time.monotonic()
                audit.client.available_actions()
                control = audit.client.last_response['control']; action = control['action']; d = action['details']
                output.write(json.dumps(control) + '\n'); output.flush()
                assert action['requestId'] == request
                if action['state'] != 'running':
                    break
                assert control['ownedByYou']
                key = (action['phase'], d['completedRepetitions'])
                if key != last:
                    transitions.append({'elapsed': begin - started, 'phase': action['phase'], 'repetitions': d['completedRepetitions']}); last = key
                # Longest stationary wait per step, excluding active child actions.
                # A handoff step without a started child is still choosing a pass.
                phase = action['phase'] or ''
                reason = None if d.get('child') else 'waiting_for_player_handoff' if phase == 'giving_to_player' else \
                    phase if phase.startswith('waiting_for') else None
                if reason != (waiting or (None,))[0]:
                    if waiting: waits[waiting[0]] = max(waits.get(waiting[0], 0), begin - waiting[1])
                    waiting = (reason, begin) if reason else None
                if d.get('handoff') and d['handoff']['itemInstanceId'] not in handoffs and action['phase'] != 'giving_to_player':
                    handoffs.append(d['handoff']['itemInstanceId'])
                    if len(fixtures) < 2 * args.cycles:
                        fixtures.append(supply(len(fixtures)))
                if d['completedRepetitions'] >= args.cycles:
                    break
                if samples.records and samples.records[-1]['bot'] and samples.records[-1]['bot']['respawning']:
                    break
                time.sleep(max(0, .2 - (time.monotonic() - begin)))
        if waiting:
            waits[waiting[0]] = max(waits.get(waiting[0], 0), time.monotonic() - waiting[1])
        final = audit.client.status()
        # A finished round has no further in-round snapshot to wait for.
        final_snapshot = audit.snap('final') if sim.read_status(args.runtime).get('inRound') else {'objects': []}
        audit.client.stop()
    finally:
        samples.close()
    alive = {o['unityInstanceId'] for o in final_snapshot['objects'] if o.get('activeInHierarchy')}
    result = {'case': 'thrown', 'layout': args.layout, 'level': args.level, 'requestedFps': args.fps, 'action': final['action'],
              'fixtures': fixtures, 'handoffs': handoffs, 'survivingHandoffs': len([h for h in handoffs if h in alive]),
              'longestWaits': waits, 'transitions': transitions, 'performance': samples.summary(), 'elapsed': time.monotonic() - started,
              'bridgeSha256': hashlib.sha256((args.runtime / 'BepInEx/plugins/SousChef.Bridge.dll').read_bytes()).hexdigest()}
    result['success'] = (final['action']['state'] == 'running' and final['action']['details']['completedRepetitions'] >= args.cycles and
                         len(set(handoffs)) >= 2 * args.cycles and result['survivingHandoffs'] >= 2 * args.cycles and
                         not result['performance']['respawnObserved'] and samples.error is None)
    write(audit.directory / 'result.json', result)
    return result


def edge(audit):
    catalog = load(audit)
    # Freeze only initial fixture selection/staging. Measured execution below
    # resumes the native drifting floor, chef controllers and round clock.
    sim.command(audit.args.runtime, 'pause on')
    state = audit.snap('initial')
    bot = next(o for o in state['objects'] if o['name'] == 'Player 2')
    human = next(o for o in state['objects'] if o['name'] == 'Player 1')
    floor_id = fields(bot, 'GroundCast')['m_groundCollider']['objectId']
    floor = next(o for o in state['objects'] if o['id'] == floor_id)
    bounds = next(c['worldBounds'] for c in floor['colliders'] if not c['isTrigger'])
    center, size = bounds['center'], bounds['size']
    selected = None
    probes = []
    observed_after = state['clock']['realtime']
    # The probe search itself takes time. Refresh the moving floor before each
    # candidate so a setup goal does not drift off its rounded-contact boundary.
    with BridgeClient(audit.args.port_base + 2, timeout=15) as bridge:
        stream = bridge.messages()
        for axis, sign in ((2, -1), (0, 1), (0, -1), (2, 1)):
            tangent = 2 if axis == 0 else 0
            for fraction in (0, -.15, .15, -.3, .3, -.45, .45, -.6, .6, -.75, .75):
                for sample in stream:
                    if sample['type'] == 'gameplay' and sample['clock']['realtime'] > observed_after:
                        floor = next(o for o in sample['objects'] if o['id'] == floor_id)
                        bounds = next(c['worldBounds'] for c in floor['colliders'] if not c['isTrigger'])
                        center, size = bounds['center'], bounds['size']
                        break
                start = [center[0], 0, center[2]]; point = start.copy()
                start[axis] += sign * (size[axis] * .5 - .5)
                point[axis] += sign * (size[axis] * .5 + .30)
                start[tangent] += fraction * size[tangent] * .5; point[tangent] = start[tangent]
                observed_after = sim.command(audit.args.runtime, 'probe-walk ' + ' '.join(map(str, (*start, *point))))['realtime']
                probe = json.loads((audit.args.runtime / 'probe.json').read_text())
                probes.append(probe)
                if probe['start'] and probe['end'] and probe['clear']:
                    selected = (start, point, probe, axis, sign, fraction); break
            if selected:
                break
    write(audit.directory / 'edge-probes.json', probes)
    if selected is None:
        raise AssertionError('No native rounded-contact edge fixture')
    start, point, probe, axis, sign, fraction = selected
    write(audit.directory / 'edge-probe.json', probe)
    pose = (*start, 180)
    sim.command(audit.args.runtime, 'stage-player 2 ' + ' '.join(map(str, pose)))
    source = next(t for t in catalog['targets'] if t.get('foodName') == 'Potato')
    supplied = audit.fixture('ingredient ' + str(source['objectInstanceId']) + ' ' + str(bot['unityInstanceId']) + ' chopped')
    state = audit.snap('safe-initial-pose')
    floor = next(o for o in state['objects'] if o['id'] == floor_id)
    bounds = next(c['worldBounds'] for c in floor['colliders'] if not c['isTrigger'])
    point = [bounds['center'][0], 0, bounds['center'][2]]
    point[axis] += sign * (bounds['size'][axis] * .5 + .30)
    tangent = 2 if axis == 0 else 0
    point[tangent] += fraction * bounds['size'][tangent] * .5
    fixture = audit.fixture('stance ' + str(human['unityInstanceId']) + ' ' + ' '.join(map(str, point)))
    samples = Samples(audit.args.port_base + 2, audit.directory)
    rejected = None
    try:
        sim.command(audit.args.runtime, 'pause off')
        try:
            audit.client.navigate_to(fixture['destination'])
        except ControlError as error:
            rejected = str(error)
        end = time.monotonic() + 8
        with (audit.directory / 'control.ndjson').open('w') as output:
            while time.monotonic() < end:
                control = audit.client.status(); output.write(json.dumps(control) + '\n')
                if samples.records and samples.records[-1]['bot'] and samples.records[-1]['bot']['respawning']:
                    break
                time.sleep(.1)
        audit.snap('final')
        audit.client.stop()
    finally:
        samples.close()
    navigation = dict(control['navigation'])
    navigation['executionTrace'] = [r for r in navigation.get('executionTrace', []) if r['time'] >= fixture['realtime']]
    result = {'case': 'edge', 'level': audit.args.level, 'initialPose': pose, 'point': point, 'fixture': fixture,
              'food': supplied, 'rejected': rejected, 'navigation': navigation, 'performance': samples.summary(),
              'bridgeSha256': hashlib.sha256((audit.args.runtime / 'BepInEx/plugins/SousChef.Bridge.dll').read_bytes()).hexdigest()}
    result['steeringObserved'] = any(any(abs(v) > 0 for v in r['input']) for r in navigation['executionTrace'])
    performance = result['performance']
    result['success'] = (rejected == 'unsafe_release_stance' and not result['steeringObserved'] and
                         performance['samples'] >= 50 and performance['observedSeconds'] >= 7 and
                         performance['error'] is None and not performance['respawnObserved'] and performance['lowestBotY'] > -.5)
    write(audit.directory / 'result.json', result)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('case', choices=('repeat', 'far', 'safety', 'latency', 'edge', 'thrown'))
    parser.add_argument('--layout', choices=('board', 'full', 'counter', 'loose'), default='board',
                        help='Where the human supplies raw food for the thrown case')
    parser.add_argument('--runtime', type=Path, required=True)
    parser.add_argument('--port-base', type=int, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--level', help='Native scene; defaults to 4-2 for edge, otherwise 2-1')
    parser.add_argument('--fps', type=int, default=60)
    parser.add_argument('--cycles', type=int, default=3)
    parser.add_argument('--timeout', type=float, default=100)
    parser.add_argument('--human-pose', type=float, nargs=4)
    args = parser.parse_args(); args.runtime = args.runtime.resolve(); args.output.mkdir(parents=True, exist_ok=True)
    args.level = args.level or ('s_rapids_4_2A' if args.case == 'edge' else 's_rapids_2_1')
    if args.cycles < 1 or args.fps < 1 or not math.isfinite(args.timeout) or args.timeout <= 0:
        parser.error('Cycles, FPS and timeout must be positive')
    process = json.loads((args.runtime / 'process.json').read_text())
    if args.port_base == 24702 or process['portBase'] != args.port_base:
        parser.error('Use the matching isolated native ports')
    if hashlib.sha256((args.runtime / 'BepInEx/plugins/SousChef.Bridge.dll').read_bytes()).hexdigest() != process['bridgeSha256']:
        parser.error('Runtime DLL differs from its launch manifest')
    if sim.read_status(args.runtime).get('generation', 0) == 0:
        sim.wait_ready(args.runtime)
    audit = Audit(args)
    try:
        result = edge(audit) if args.case == 'edge' else thrown(audit) if args.case == 'thrown' else run(audit)
        print(json.dumps({k: v for k, v in result.items() if k not in ('transitions', 'action')}), flush=True)
        return 0 if result['success'] else 2
    finally:
        if audit.client:
            try:
                audit.client.stop()
            finally:
                audit.client.close()


if __name__ == '__main__':
    raise SystemExit(main())
