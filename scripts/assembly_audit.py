#!/usr/bin/env python3
"""Replay exact-order assembly with prepared native ingredients and ordinary P2 input."""
import argparse
import hashlib
import json
from pathlib import Path
import time

from task_audit import Audit
from throw_audit import fields, write
import native_simulator as sim


class AssemblyAudit(Audit):
    def prepared(self):
        catalog = self.load('s_sushi_1_1')
        plates = sorted((i for i in catalog['items'] if i['cleanPlate'] and i['routeDistance'] is not None), key=lambda i: i['routeDistance'])
        fish = next(i for i in catalog['items'] if i['supply'] and i['ingredientName'] == 'SushiFish')
        state = self.snap('before')
        objects = {o['unityInstanceId']: o for o in state['objects']}
        counter = next(d for d in catalog['deliveryDestinations'] if d['kind'] == 'counter' and
                       fields(objects[d['objectInstanceId']], 'ServerAttachStation').get('m_item') is None)
        prepared = self.fixture(f"ingredient {fish['sourceInstanceId']} {counter['objectInstanceId']} chopped")
        catalog = self.catalog()
        source = next(i for i in catalog['items'] if i['objectInstanceId'] == prepared['itemInstanceId'])
        order = next(o for o in catalog['assemblyOrders'] if o['recipe'] == 'Sushi_PlainFish')
        self.client.assemble_dish(order['id'], bases=[i['id'] for i in plates[:2]], components=[source['id']])
        receipts = self.observe('prepared', timeout=75)
        details = receipts[-1]['details']['assembly']
        assert details['compositionVerified'] and details['verifiedAdditions'] == 1, details
        after = self.snap('after')
        assert details['baseInstanceId'] in {i['objectInstanceId'] for i in plates[:2]}
        return {'order': order, 'allocation': {'bases': plates[:2], 'source': source}, 'result': details,
                'requestId': receipts[-1]['requestId'], 'snapshot': str(self.directory / 'after.json')}

    def cooked(self):
        catalog = self.load('s_sushi_1_2')
        plates = sorted((i for i in catalog['items'] if i['cleanPlate'] and i['routeDistance'] is not None), key=lambda i: i['routeDistance'])
        pot = min((i for i in catalog['items'] if i['utensilType'] == 'pot' and i['routeDistance'] is not None), key=lambda i: i['routeDistance'])
        state = self.snap('cooked-before'); objects = {o['unityInstanceId']: o for o in state['objects']}
        counters = [d for d in catalog['deliveryDestinations'] if d['kind'] == 'counter' and
                    fields(objects[d['objectInstanceId']], 'ServerAttachStation').get('m_item') is None and
                    not any(c['type'] in ('ServerPlateStation', 'ServerConveyorStation') for c in objects[d['objectInstanceId']]['components'])]
        supplied = []
        for name, counter in zip(('SushiRice', 'SushiFish', 'Seaweed'), counters):
            source = next(i for i in catalog['items'] if i['supply'] and i['ingredientName'] == name)
            supplied.append(self.fixture(f"ingredient {source['sourceInstanceId']} {counter['objectInstanceId']} chopped")['itemInstanceId'])
            time.sleep(.3)
        assert len(supplied) == 3
        catalog = self.catalog(); components = [i['id'] for i in catalog['items'] if i['objectInstanceId'] in supplied]
        order = next(o for o in catalog['assemblyOrders'] if o['recipe'] == 'Sushi_Fish')
        allocation = dict(order_id=order['id'], bases=[p['id'] for p in plates[:2]], components=components, equipment=[pot['id'], pot['sourceId']])
        write(self.directory / 'cooked-allocation.json', allocation)
        self.client.assemble_dish(**allocation)
        receipts = self.observe('cooked', timeout=110)
        details = receipts[-1]['details']['assembly']
        assert details['compositionVerified'] and details['verifiedAdditions'] == 4, details
        after = self.snap('cooked-after')
        assert details['baseInstanceId'] in {i['objectInstanceId'] for i in plates[:2]}
        return {'order': order, 'allocation': allocation, 'result': details, 'requestId': receipts[-1]['requestId']}

    def mixed(self):
        catalog = self.load('s_wizard_school_3_4')
        plates = sorted((i for i in catalog['items'] if i['cleanPlate'] and i['routeDistance'] is not None), key=lambda i: i['routeDistance'])
        equipment = []
        for kind in ('bowl', 'pan'):
            vessel = min((i for i in catalog['items'] if i['utensilType'] == kind and i['routeDistance'] is not None), key=lambda i: i['routeDistance'])
            equipment.extend((vessel['id'], vessel['sourceId']))
        state = self.snap('mixed-before'); objects = {o['unityInstanceId']: o for o in state['objects']}
        counters = [d for d in catalog['deliveryDestinations'] if d['kind'] == 'counter' and
                    fields(objects[d['objectInstanceId']], 'ServerAttachStation').get('m_item') is None and
                    not any(c['type'] in ('ServerPlateStation', 'ServerConveyorStation') for c in objects[d['objectInstanceId']]['components'])]
        supplied = []
        for name, counter in zip(('Flour', 'Egg'), counters):
            source = next(i for i in catalog['items'] if i['supply'] and i['ingredientName'] == name)
            supplied.append(self.fixture(f"ingredient {source['sourceInstanceId']} {counter['objectInstanceId']} chopped")['itemInstanceId'])
            time.sleep(.3)
        assert len(supplied) == 2
        catalog = self.catalog(); components = [i['id'] for i in catalog['items'] if i['objectInstanceId'] in supplied]
        order = next(o for o in catalog['assemblyOrders'] if o['recipe'] == 'Pancake_Plain')
        allocation = dict(order_id=order['id'], bases=[p['id'] for p in plates[:2]], components=components, equipment=equipment)
        write(self.directory / 'mixed-allocation.json', allocation)
        self.client.assemble_dish(**allocation)
        receipts = self.observe('mixed', timeout=150)
        details = receipts[-1]['details']['assembly']
        assert details['compositionVerified'] and details['verifiedAdditions'] == 4, details
        self.snap('mixed-after')
        return {'order': order, 'allocation': allocation, 'result': details, 'requestId': receipts[-1]['requestId']}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--runtime', type=Path, required=True)
    parser.add_argument('--port-base', type=int, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--case', choices=('prepared', 'cooked', 'mixed'), default='prepared')
    args = parser.parse_args(); args.output.mkdir(parents=True, exist_ok=True)
    audit = AssemblyAudit(args)
    try:
        result = getattr(audit, args.case)()
        result['dllSha256'] = hashlib.sha256((args.runtime / 'BepInEx/plugins/SousChef.Bridge.dll').read_bytes()).hexdigest()
        write(args.output / 'report.json', result)
        print(json.dumps(result, indent=2))
    finally:
        if audit.client:
            if audit.client.status()['action']['state'] == 'running': audit.client.stop()
            audit.client.close()


if __name__ == '__main__': main()
