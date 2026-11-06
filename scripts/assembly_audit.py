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


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--runtime', type=Path, required=True)
    parser.add_argument('--port-base', type=int, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args(); args.output.mkdir(parents=True, exist_ok=True)
    audit = AssemblyAudit(args)
    try:
        result = audit.prepared()
        result['dllSha256'] = hashlib.sha256(Path('artifacts/plugin/SousChef.Bridge.dll').read_bytes()).hexdigest()
        write(args.output / 'report.json', result)
        print(json.dumps(result, indent=2))
    finally:
        if audit.client:
            if audit.client.status()['action']['state'] == 'running': audit.client.stop()
            audit.client.close()


if __name__ == '__main__': main()
