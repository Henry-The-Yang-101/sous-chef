#!/usr/bin/env python3
"""Make an exact order in an isolated native kitchen without ingredient fixtures."""
import argparse
import hashlib
import json
import time
from pathlib import Path

from assembly_sweep import verify_plate
from task_audit import Audit
from throw_audit import write


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--runtime', type=Path, required=True)
    parser.add_argument('--port-base', type=int, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--level', required=True)
    parser.add_argument('--recipe')
    parser.add_argument('--timeout', type=float, default=180)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=False)
    audit = Audit(args)
    try:
        catalog = audit.load(args.level)
        write(args.output / 'catalog-before.json', catalog)
        audit.snap('before')
        order = next((order for order in catalog['assemblyOrders'] if not args.recipe or order['recipe'] == args.recipe), None)
        if order is None:
            raise RuntimeError('Requested recipe is not among this round’s initial tickets: ' +
                               ', '.join(order['recipe'] for order in catalog['assemblyOrders']))
        audit.client.assemble_dish(order['id'])
        started = time.monotonic()
        last = None

        def progress(action):
            nonlocal last
            detail = action['details']['assembly']
            state = (action['phase'], detail['verifiedAdditions'], detail['waitingReason'])
            if state != last:
                print(json.dumps({'seconds': round(time.monotonic() - started, 1), 'state': state}), flush=True)
                last = state

        receipts = audit.observe('execution', timeout=args.timeout, on_poll=progress)
        result = {'order': order, 'result': receipts[-1], 'seconds': time.monotonic() - started,
                  'dllSha256': hashlib.sha256((args.runtime / 'BepInEx/plugins/SousChef.Bridge.dll').read_bytes()).hexdigest()}
        result['plateProof'] = verify_plate(result, audit.snap('after'))
        write(args.output / 'report.json', result)
        print(json.dumps({'succeeded': True, 'recipe': order['recipe'], 'seconds': result['seconds'], 'plateProof': result['plateProof']}))
    finally:
        if audit.client:
            if audit.client.status()['action']['state'] == 'running':
                audit.client.stop()
            audit.client.close()


if __name__ == '__main__':
    main()
