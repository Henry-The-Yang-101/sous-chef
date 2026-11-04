#!/usr/bin/env python3
"""Audit autonomous tasks in an explicitly selected isolated native simulator.

Dirty stacks/fire/burnt setup uses documented native initial-condition fixtures.
Measured execution uses only the production ActionClient and logical inputs.
"""
import argparse
import hashlib
import json
import math
from pathlib import Path
import sys
import time

import native_simulator as sim
from throw_audit import snapshot, fields, write

sys.path.insert(0, str(sim.ROOT))
from clients.actions import ActionClient
from clients.control import ControlError


class Audit:
    def __init__(self, args):
        self.args = args
        self.directory = args.output
        self.client = None

    def load(self, level):
        generation = sim.read_status(self.args.runtime)['generation'] + 1
        sim.command(self.args.runtime, 'load ' + level)
        sim.wait_ready(self.args.runtime, generation)
        self.client = ActionClient(self.args.port_base + 1, timeout=15)
        return self.catalog()

    def catalog(self):
        end = time.monotonic() + 30
        while time.monotonic() < end:
            catalog = self.client.available_actions()
            if catalog.get('complete'):
                return catalog
            time.sleep(.1)
        raise TimeoutError('Native walking catalogue did not complete')

    def observe(self, label, timeout=120, on_poll=None):
        request_id = self.client.last_response['control']['action']['requestId']
        end = time.monotonic() + timeout
        receipts = []
        with (self.directory / (label + '.ndjson')).open('w') as trace:
            while time.monotonic() < end:
                # Exercise the exact catalogue polling used by localhost, even
                # while the parent waits without a running child.
                self.client.available_actions()
                control = self.client.last_response['control']
                trace.write(json.dumps(control) + '\n')
                action = control['action']; receipts.append(action)
                if action['requestId'] != request_id:
                    raise AssertionError('Task request identity changed')
                if action['state'] != 'running':
                    write(self.directory / (label + '.json'), control)
                    if action['state'] != 'succeeded':
                        raise AssertionError(action)
                    return receipts
                if not control['ownedByYou']:
                    raise AssertionError('Task lost its lease')
                if on_poll:
                    on_poll(action)
                time.sleep(.1)
        self.client.stop()
        raise TimeoutError(label)

    def snap(self, name):
        return snapshot(self.args.port_base, self.client.hello['sessionId'], self.directory / (name + '.json'))

    def fixture(self, command):
        sim.command(self.args.runtime, 'task-fixture ' + command)
        record = json.loads((self.args.runtime / 'task-fixture.json').read_text())
        write(self.directory / ('fixture-' + str(time.time_ns()) + '.json'), record)
        return record

    def request_when_available(self, action, *args, **kwargs):
        # Geometry/client readiness can change between a catalogue and dispatch.
        # Retry only a rejected precondition: accepted task requests are never
        # replayed, and observe() checks their single identity through completion.
        deadline = time.monotonic() + 15
        rejected = 0
        while time.monotonic() < deadline:
            catalog = self.client.available_actions()
            if any(t['name'] == action for t in catalog.get('taskOptions', [])):
                try:
                    self.client.run_task(action, *args, **kwargs)
                    return rejected
                except ControlError as error:
                    if str(error) != 'task_not_available':
                        raise
                    rejected += 1
            time.sleep(.1)
        raise TimeoutError('Task did not become available: ' + action)

    def rice(self):
        catalog = self.catalog()
        ingredient = next(i['id'] for i in catalog['ingredients'] if i['label'] == 'SushiRice')
        row = next(r for r in catalog['jobOptions']['fetch_ingredient'] if r['sourceId'] == ingredient)
        cooker = next(t for t in catalog['targets'] if t['kind'] == 'cooking_station' and t['id'] in row['destinationIds'])
        self.client.fetch_ingredient(ingredient, cooker['id']); self.observe('rice-setup')
        state = self.snap('rice-in-pot')
        holder = next(o for o in state['objects'] if o['unityInstanceId'] == cooker['objectInstanceId'])
        ref = fields(holder, 'ServerAttachStation')['m_item']['objectId']
        pot = next(o for o in state['objects'] if o['id'] == ref)
        return cooker, pot

    def prepare(self):
        self.load('s_sushi_1_2')
        self.request_when_available('prepare_near_player', items=[
            {'ingredient': 'SushiRice', 'preparation': 'cooked'},
            {'ingredient': 'SushiFish', 'preparation': 'chopped'}])
        simultaneous = False

        def check_concurrency(action):
            nonlocal simultaneous
            if action['phase'] != 'work_at' or simultaneous:
                return
            observed = self.snap('concurrent-chopping-and-cooking')
            for item in observed['objects']:
                data = fields(item, 'ServerCookingHandler').get('m_ServerData', {}).get('fields', {})
                if data.get('CookingStateMessage.m_cookingState') == 'Progressing' and data.get('CookingStateMessage.m_cookingProgress', 0) > 0:
                    simultaneous = True

        receipts = self.observe('task', on_poll=check_concurrency)
        phases = [a['phase'] for a in receipts]
        assert phases.index('add_ingredient') < phases.index('work_at'), 'Cooking must start before chopping'
        assert simultaneous, 'Native cooking must still be progressing while P2 chops'
        after = self.snap('after')
        handoff = receipts[-1]['details']['handoff']
        assert any(o['unityInstanceId'] == handoff['itemInstanceId'] for o in after['objects'])
        return {'concurrentChoppingAfterCookingStarted': True, 'handoff': handoff}

    def take_ready(self):
        self.load('s_sushi_1_2'); cooker, pot = self.rice()
        self.request_when_available('take_ready'); receipts = self.observe('task')
        after = self.snap('after')
        original = next(o for o in after['objects'] if o['unityInstanceId'] == pot['unityInstanceId'])
        assert fields(original, 'ServerIngredientContainer')['m_contents'] == []
        assert any(a['phase'] == 'collect_contents' for a in receipts)
        return {'originalPotEmptied': pot['unityInstanceId'], 'servingTransferObserved': True}

    def repeated_heat(self):
        catalog = self.load('s_sushi_1_2')
        ingredient = next(i['id'] for i in catalog['ingredients'] if i['label'] == 'SushiRice')
        cookers = [t for t in catalog['targets'] if t['kind'] == 'cooking_station']
        for n, cooker in enumerate(cookers):
            self.client.fetch_ingredient(ingredient, cooker['id']); self.observe('setup-' + str(n))
        before = self.snap('before')
        original = {}
        for cooker in cookers:
            station = next(o for o in before['objects'] if o['unityInstanceId'] == cooker['objectInstanceId'])
            original[cooker['objectInstanceId']] = fields(station, 'ServerAttachStation')['m_item']['objectId']
        self.request_when_available('take_ready', None)
        request = self.client.last_response['control']['action']['requestId']
        end = time.monotonic() + 120
        with (self.directory / 'task.ndjson').open('w') as trace:
            while time.monotonic() < end:
                self.client.available_actions(); control = self.client.last_response['control']; action = control['action']
                trace.write(json.dumps(control) + '\n')
                if action['requestId'] != request or action['state'] != 'running':
                    self.snap('failure'); raise AssertionError(action)
                assert control['ownedByYou']
                if action['details']['completedRepetitions'] >= len(cookers):
                    break
                time.sleep(.1)
            else:
                self.client.stop(); raise TimeoutError('Repeated heat removal did not clear all three burners')
        after = self.snap('after')
        for instance, item in original.items():
            station = next(o for o in after['objects'] if o['unityInstanceId'] == instance)
            food = next(o for o in after['objects'] if o['id'] == item)
            assert not fields(food, 'ServerIngredientContainer')['m_contents'] or fields(station, 'ServerAttachStation').get('m_item', {}).get('objectId') != item
            data = fields(food, 'ServerCookingHandler').get('m_ServerData', {}).get('fields', {})
            assert data.get('CookingStateMessage.m_cookingState') != 'Ruined'
        plates = [o for o in after['objects'] if fields(o, 'ServerPlate') and fields(o, 'ServerIngredientContainer').get('m_contents')]
        assert len(plates) == len(cookers), 'All three rescued batches should be plated when clean plates are available'
        self.client.stop()
        return {'completedRepetitions': len(cookers), 'platedBatches': len(plates), 'singleRequest': request}

    def repeated_prepare(self):
        catalog = self.load('s_wizard_3_2')  # Story 3-3 (native Level15).
        cheese = next(i['id'] for i in catalog['preparationOptions'] if 'cheese' in i['label'].lower())
        self.request_when_available('prepare_near_player', 3, items=[{'ingredient': cheese, 'preparation': 'raw'}])
        deliveries = set()
        def record(action):
            handoff = action['details'].get('handoff')
            if handoff:
                deliveries.add(handoff['itemInstanceId'])
        self.observe('task', on_poll=record)
        self.snap('after')
        assert len(deliveries) == 3, 'Repeated preparation must deliver three distinct native ingredients'
        return {'distinctCheeseDeliveries': sorted(deliveries)}

    def fire(self):
        catalog = self.load('s_sushi_1_2')
        cooker = next(t for t in catalog['targets'] if t['kind'] == 'cooking_station')
        self.fixture('ignite ' + str(cooker['objectInstanceId']))
        self.request_when_available('put_out_fire'); receipts = self.observe('task')
        after = self.snap('after')
        burning = [o for o in after['objects'] if fields(o, 'ServerFlammable').get('m_onFire')]
        assert not burning
        assert any(a['phase'] == 'extinguishing' for a in receipts)
        return {'remainingFires': len(burning)}

    def burnt(self):
        self.load('s_sushi_1_2'); cooker, pot = self.rice()
        self.fixture('burnt ' + str(pot['unityInstanceId']))
        # Some stations ignite immediately once their native cook tick observes
        # ruined food. Clear that independently before testing disposal.
        options = [t['name'] for t in self.catalog()['taskOptions']]
        if 'put_out_fire' in options:
            self.request_when_available('put_out_fire'); self.observe('extinguish-setup')
        rejected = self.request_when_available('toss_burnt_food'); self.observe('task')
        after = self.snap('after')
        original = next(o for o in after['objects'] if o['unityInstanceId'] == pot['unityInstanceId'])
        holder = next(o for o in after['objects'] if o['unityInstanceId'] == cooker['objectInstanceId'])
        assert fields(original, 'ServerIngredientContainer')['m_contents'] == []
        assert fields(holder, 'ServerAttachStation')['m_item']['objectId'] == original['id']
        return {'exactPotReturnedToOriginalBurner': pot['unityInstanceId'], 'rejectedStartPreconditions': rejected}

    def wash(self):
        catalog = self.load('s_sushi_1_4')
        dirty = self.fixture('dirty 2')
        # Use a different reachable empty counter for a second native stack.
        counter = next(d for d in catalog['deliveryDestinations'] if d['kind'] == 'counter')
        other = self.fixture('dirty 1 ' + str(counter['objectInstanceId']))
        self.request_when_available('wash_dishes'); self.observe('task')
        after = self.snap('after')
        assert all(fields(o, 'ServerWashingStation').get('m_plateCount', 0) == 0 for o in after['objects'])
        assert not any(o['unityInstanceId'] in (dirty['stackInstanceId'], other['stackInstanceId']) for o in after['objects'])
        return {'originalDirtyStacksRemoved': 2, 'dirtyPlatesWashed': 3}

    def repetitions(self):
        self.load('s_sushi_1_4'); self.fixture('dirty 1')
        self.request_when_available('wash_dishes', 2)
        injected = False

        def next_batch(action):
            nonlocal injected
            if action['details']['completedRepetitions'] == 1 and not injected:
                self.fixture('dirty 1'); injected = True

        receipts = self.observe('task', on_poll=next_batch)
        assert receipts[-1]['details']['completedRepetitions'] == 2 and injected
        return {'completedRepetitions': 2}

    def monitor(self):
        self.load('s_sushi_1_2'); self.rice()
        self.request_when_available('monitor_equipment'); self.observe('task'); self.snap('after')
        self.request_when_available('monitor_equipment', None)
        request = self.client.last_response['control']['action']['requestId']
        for _ in range(20):
            self.client.available_actions(); control = self.client.last_response['control']
            assert control['ownedByYou'] and control['action']['state'] == 'running'
            assert control['action']['requestId'] == request
            time.sleep(.1)
        started = time.monotonic(); final = self.client.stop(); duration = time.monotonic() - started
        write(self.directory / 'stopped.json', final)
        assert final['action']['state'] == 'cancelled' and duration < .5
        return {'infiniteWaitRetained': True, 'stopSeconds': duration}

    def mixer(self):
        catalog = self.load('s_wizard_school_3_4')
        mixer = next(t for t in catalog['targets'] if t['kind'] != 'item' and 'mixed' in t.get('waitConditions', [])
                     and any(t['id'] in r['destinationIds'] for r in catalog['jobOptions']['fetch_ingredient']))
        for ingredient in ('Flour', 'Egg'):
            self.client.fetch_ingredient(ingredient, mixer['id']); self.observe('setup-' + ingredient)
        before = self.snap('before')
        holder = next(o for o in before['objects'] if o['unityInstanceId'] == mixer['objectInstanceId'])
        original = fields(holder, 'ServerAttachStation')['m_item']['objectId']
        self.request_when_available('monitor_equipment'); self.observe('task')
        after = self.snap('after')
        holder = next(o for o in after['objects'] if o['unityInstanceId'] == mixer['objectInstanceId'])
        bowl = next(o for o in after['objects'] if o['id'] == original)
        current = fields(holder, 'ServerAttachStation').get('m_item')
        assert not current or current['objectId'] != original or fields(bowl, 'ServerIngredientContainer')['m_contents'] == []
        return {'mixedContentsRemoved': True, 'originalBowl': bowl['unityInstanceId']}

    def utensil(self):
        self.load('s_sushi_1_2')
        before = self.snap('before')
        self.request_when_available('prepare_near_player', items=[{'ingredient': 'pot', 'preparation': 'empty'}])
        receipts = self.observe('task')
        identity = receipts[-1]['details']['handoff']['itemInstanceId']
        pot = next(o for o in before['objects'] if o['unityInstanceId'] == identity)
        assert fields(pot, 'ServerIngredientContainer')['m_contents'] == []
        self.snap('after')
        return {'nativeEmptyPotDelivered': identity}

    def dirty_delivery(self):
        self.load('MovingPlatform4')
        status = sim.read_status(self.args.runtime)
        p1 = next(p['position'] for p in status['players'] if p['name'] == 'Player 1')
        p2 = next(p['position'] for p in status['players'] if p['name'] == 'Player 2')
        # Explicit supported initial poses swap roles between the native islands.
        # P1 inputs remain untouched throughout measured task execution.
        buffer = None
        for n in range(12):
            point = [p1[0] + 1.6 * math.cos(n * math.pi / 6), p1[1], p1[2] + 1.6 * math.sin(n * math.pi / 6)]
            try:
                sim.command(self.args.runtime, 'stage-player 2 ' + ' '.join(map(str, (*point, 0))))
                buffer = point
                break
            except RuntimeError:
                continue
        if buffer is None:
            raise RuntimeError('No supported role-swap buffer pose')
        sim.command(self.args.runtime, 'stage-player 1 ' + ' '.join(map(str, (*p2, 0))))
        sim.command(self.args.runtime, 'stage-player 2 ' + ' '.join(map(str, (*p1, 0))))
        write(self.directory / 'role-swap.json', {'player1Before': p1, 'player2Before': p2, 'buffer': buffer,
              'after': sim.read_status(self.args.runtime)['players'], 'kind': 'explicit_supported_initial_poses'})
        catalog = self.catalog()
        counter = next(d for d in catalog['deliveryDestinations'] if d['kind'] == 'counter')
        dirty = self.fixture('dirty 2 ' + str(counter['objectInstanceId']))
        self.request_when_available('deliver_dirty_dishes')
        receipts = self.observe('task')
        after = self.snap('after')
        handoff = receipts[-1]['details']['handoff']
        assert handoff['itemInstanceId'] == dirty['stackInstanceId']
        assert any(o['unityInstanceId'] == dirty['stackInstanceId'] for o in after['objects'])
        if handoff['targetInstanceId'] is not None:
            target = next(o for o in after['objects'] if o['unityInstanceId'] == handoff['targetInstanceId'])
            cargo = fields(target, 'ServerAttachStation')['m_item']['objectId']
            assert next(o for o in after['objects'] if o['id'] == cargo)['unityInstanceId'] == dirty['stackInstanceId']
        return {'humanOnlySinkDelivery': True, 'exactDirtyStackDelivered': dirty['stackInstanceId'], 'handoff': handoff}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--runtime', type=Path, required=True)
    parser.add_argument('--port-base', type=int, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--case', action='append', choices=('prepare', 'take_ready', 'repeated_heat', 'repeated_prepare', 'fire', 'burnt', 'wash', 'repetitions', 'monitor', 'mixer', 'utensil', 'dirty_delivery'))
    args = parser.parse_args(); args.output.mkdir(parents=True, exist_ok=True)
    sim.wait_ready(args.runtime)
    runtime_dll = args.runtime / 'BepInEx/plugins/SousChef.Bridge.dll'
    report = {'bridgeSha256': hashlib.sha256(runtime_dll.read_bytes()).hexdigest(), 'cases': []}
    audit = Audit(args)
    for case in args.case or ['prepare', 'take_ready', 'fire', 'burnt', 'wash', 'repetitions', 'monitor', 'mixer', 'utensil', 'dirty_delivery']:
        audit.directory = args.output / case; audit.directory.mkdir(exist_ok=True)
        try:
            details = getattr(audit, case)()
            result = {'name': case, 'success': True, **details}
        except Exception as error:
            result = {'name': case, 'success': False, 'error': str(error)}
        finally:
            if audit.client:
                audit.client.close(); audit.client = None
        report['cases'].append(result); write(args.output / 'report.json', report)
        print(json.dumps(result), flush=True)
    return 0 if all(c['success'] for c in report['cases']) else 2


if __name__ == '__main__':
    raise SystemExit(main())
