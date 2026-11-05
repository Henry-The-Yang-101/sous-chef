#!/usr/bin/env python3
"""Exercise dish monitoring/serving with native Unity inputs in an isolated runtime."""
import argparse
import hashlib
import json
from pathlib import Path
import time

from task_audit import Audit
import native_simulator as sim
from throw_audit import carrier_items, fields, write


def successful_deliveries(snapshot):
    for obj in snapshot['objects']:
        flow = fields(obj, 'ServerCampaignFlowController')
        if flow:
            monitor = flow['m_teamMonitor']['fields']
            score = monitor['ServerTeamMonitor.m_score']['fields']
            return score['TeamMonitor+TeamScoreStats.TotalSuccessfulDeliveries']
    raise AssertionError('Native server score was not observed')


class DishAudit(Audit):
    def run(self, mode):
        catalog = self.load('s_sushi_1_1')
        before = self.snap('before')
        plates = sorted((i for i in catalog['items'] if i['cleanPlate'] and i['routeDistance'] is not None),
                        key=lambda i: i['routeDistance'])
        fish = next(i for i in catalog['items'] if i['supply'] and i['ingredientName'] == 'SushiFish')
        prawn = next(i for i in catalog['items'] if i['supply'] and i['ingredientName'] == 'SushiPrawn')
        selected = plates[:2] if mode == 'repetitions' else plates[:1]
        cargo = None
        if mode not in ('wait', 'stop'):
            for plate in selected:
                self.fixture(f"plate-food {plate['objectInstanceId']} {fish['sourceInstanceId']}")
        if mode in ('held', 'repetitions', 'drop_plate'):
            pickup = plates[1] if mode == 'drop_plate' else selected[0]
            self.client.pick_up(pickup['sourceId']); self.observe('pickup-setup')
            if mode == 'drop_plate':
                cargo = pickup['objectInstanceId']
        elif mode in ('counter', 'drop'):
            chef = next(o for o in before['objects'] if o['name'] == 'Player 2')
            result = self.fixture(f"ingredient {prawn['sourceInstanceId']} {chef['unityInstanceId']}")
            cargo = result['itemInstanceId']

        filled = []
        if mode in ('drop', 'drop_plate'):
            # Occupy every currently reachable storage counter; leave boards,
            # serving windows, dirty returns and existing plates untouched.
            catalog = self.catalog()
            state = self.snap('before-blocking-counters')
            by_instance = {o['unityInstanceId']: o for o in state['objects']}
            for destination in catalog['deliveryDestinations']:
                obj = by_instance[destination['objectInstanceId']]
                if destination['kind'] != 'counter' or fields(obj, 'ServerPlateStation'):
                    continue
                station = fields(obj, 'ServerAttachStation')
                if station and station['m_item'] is None:
                    self.fixture(f"ingredient {prawn['sourceInstanceId']} {destination['objectInstanceId']}")
                    filled.append(destination['objectInstanceId'])
            assert filled, 'Floor fallback must have explicit occupied-counter conditions'

        repetitions = None if mode == 'stop' else 2 if mode == 'repetitions' else 1
        self.request_when_available('deliver_dishes', repetitions)
        began = time.monotonic()
        supplied = False

        def supply_plate(action):
            nonlocal supplied
            if supplied or time.monotonic() - began < 1:
                return
            assert action['phase'] == 'waiting_for_plated_dish', action
            assert action['details']['completedRepetitions'] == 0
            self.fixture(f"plate-food {selected[0]['objectInstanceId']} {fish['sourceInstanceId']}")
            supplied = True

        if mode == 'stop':
            request = self.client.last_response['control']['action']['requestId']
            for _ in range(10):
                self.client.available_actions()
                control = self.client.last_response['control']
                assert control['ownedByYou'] and control['action']['requestId'] == request
                assert control['action']['state'] == 'running'
                time.sleep(.1)
            self.client.stop()
            self.client.available_actions()
            control = self.client.last_response['control']; write(self.directory / 'stopped.json', control)
            assert control['action']['state'] == 'cancelled' and not control['ownedByYou']
            assert successful_deliveries(self.snap('after')) == successful_deliveries(before)
            return {'idleMonitorCancelled': True, 'requestId': request}

        receipts = self.observe('deliver', timeout=90, on_poll=supply_plate if mode == 'wait' else None)
        after = self.snap('after')
        count = 2 if mode == 'repetitions' else 1
        assert successful_deliveries(after) - successful_deliveries(before) == count
        assert receipts[-1]['details']['completedRepetitions'] == count
        assert not carrier_items(after), 'P2 must finish serving with empty hands'
        child_operations = [r['details']['child']['operation'] for r in receipts if r['details'].get('child')]
        if mode == 'held':
            assert child_operations and set(child_operations) == {'place_at'}, child_operations
        if mode == 'wait':
            assert supplied and any(r['phase'] == 'waiting_for_plated_dish' for r in receipts)
        result = {'served': count, 'plateInstanceIds': [p['objectInstanceId'] for p in selected],
                  'requestId': receipts[-1]['requestId']}
        if cargo is not None:
            item = next(o for o in after['objects'] if o['unityInstanceId'] == cargo)
            holders = [o for o in after['objects'] if
                       (fields(o, 'ServerAttachStation').get('m_item') or {}).get('objectId') == item['id']]
            if mode in ('drop', 'drop_plate'):
                assert not holders and item['activeInHierarchy'], 'Exact original cargo must survive the floor drop'
                assert any((r['details'].get('child') or {}).get('phase') == 'positioning_ground_drop' or
                           r['phase'] == 'dropping_held_item' for r in receipts)
                result.update(cargoDropped=cargo, occupiedCounters=filled)
            else:
                assert len(holders) == 1, 'Exact original cargo must remain parked on a counter'
                result.update(cargoParked=cargo, counterInstanceId=holders[0]['unityInstanceId'])
        return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--runtime', type=Path, required=True)
    parser.add_argument('--port-base', type=int, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--case', action='append', choices=('held', 'counter', 'wait', 'drop', 'drop_plate', 'repetitions', 'stop'))
    args = parser.parse_args(); args.output.mkdir(parents=True, exist_ok=True)
    sim.wait_ready(args.runtime)
    report = {'bridgeSha256': hashlib.sha256((args.runtime / 'BepInEx/plugins/SousChef.Bridge.dll').read_bytes()).hexdigest(), 'cases': []}
    audit = DishAudit(args)
    for case in args.case or ('held', 'counter', 'wait', 'drop', 'drop_plate', 'repetitions', 'stop'):
        audit.directory = args.output / case; audit.directory.mkdir(exist_ok=True)
        try:
            result = {'name': case, 'success': True, **audit.run(case)}
        except Exception as error:
            result = {'name': case, 'success': False, 'error': str(error)}
        finally:
            if audit.client:
                audit.client.close(); audit.client = None
        report['cases'].append(result); write(args.output / 'report.json', report)
        print(json.dumps(result), flush=True)
    return 0 if all(case['success'] for case in report['cases']) else 2


if __name__ == '__main__':
    raise SystemExit(main())
