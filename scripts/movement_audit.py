#!/usr/bin/env python3
"""Record grounded discovery and movement in an explicitly isolated Unity player.

Selections here are geometric fixture labels, NOT a language-understanding test.
No player poses, cargo, progress or routes are written during measured execution.
"""
import argparse
import json
from pathlib import Path
import statistics
import sys
import time
import uuid

import native_simulator as sim
sys.path.insert(0, str(sim.ROOT))
from clients.actions import ActionClient
from harness.semantic_map import spatial_context


def run(args):
    generation = sim.read_status(args.runtime)['generation'] + 1
    sim.command(args.runtime, 'load ' + args.level)
    sim.wait_ready(args.runtime, generation)
    args.output.mkdir(parents=True, exist_ok=True)
    client = ActionClient(args.port_base + 1, timeout=15)
    rows, durations, map_slices = [], [], []
    discovery_started=time.monotonic()
    try:
        deadline = time.monotonic() + 25
        while time.monotonic() < deadline:
            start = time.monotonic(); catalog = client.available_actions(); durations.append((time.monotonic()-start)*1000)
            semantic = catalog.get('semanticCatalog', {})
            map_slices.append(semantic.get('movement',{}).get('coverage',{}).get('sliceMilliseconds',0))
            scene = spatial_context(semantic)
            if semantic.get('movement', {}).get('coverage', {}).get('samplingComplete') and scene['candidates']:
                break
            time.sleep(.1)
        else: raise TimeoutError('No completed sampled map sweep')
        discovery_ms=(time.monotonic()-discovery_started)*1000
        candidates = scene['candidates']
        if args.selection == 'left-board':
            target = min((p for p in candidates if p.get('kind') == 'workstation'), key=lambda p:p['screen']['right'])
        elif args.selection == 'top-left':
            target = min((p for p in candidates if p['binding'] == 'floor'), key=lambda p:p['screen']['right']+1-p['screen']['up'])
        else:
            supports = {s['id'] for s in scene['supports'] if (2 in s.get('occupants', [])) == (args.selection == 'occupied-platform')}
            target = next(p for p in candidates if p['binding'] == 'platform' and p['supportId'] in supports)
        (args.output/'context.json').write_text(json.dumps({'native': semantic, 'compact': scene, 'target': target},indent=2)+'\n')
        control=client.status(); request=uuid.uuid4().hex
        receipt=client.execute(request,control['kitchenEpoch'],control['dispatchGeneration'],'navigate_to',{'destination_id':target['id']})
        if receipt['state']=='rejected': raise RuntimeError(receipt['reason'])
        deadline = time.monotonic() + args.seconds
        while time.monotonic() < deadline:
            start=time.monotonic(); catalog=client.available_actions(); durations.append((time.monotonic()-start)*1000)
            map_slices.append(catalog.get('semanticCatalog',{}).get('movement',{}).get('coverage',{}).get('sliceMilliseconds',0))
            control=client.last_response['control']; rows.append(control)
            nav=control['navigation']
            if nav['state']=='idle': break
            time.sleep(.1)
        if nav['state']!='idle':
            client.stop()
            result='waiting_cancelled' if any(r['navigation'].get('waitingReason') for r in rows) else 'timeout'
        else: result=nav['reason']
        receipt=client.receipt(request)
        summary={'receipt':receipt,'level':args.level,'selection':args.selection,'target':target,'result':result,
                 'arrival':nav.get('arrival'),'samples':len(rows),
                 'waitingSamples':sum(r['navigation'].get('waitingReason') is not None for r in rows),
                 'bindingStable':all(r['navigation'].get('movementReference') in (target['id'],None) for r in rows),
                 'sampledPoints':semantic['movement']['coverage']['pointCount'],
                 'floorCandidates':sum(c['binding']=='floor' for c in candidates),
                 'camera':scene['orientation'],'catalogRoundtripMsMedian':statistics.median(durations),
                 'discoveryMs':discovery_ms,'mapSliceMsMedian':statistics.median(map_slices),'mapSliceMsMax':max(map_slices),
                 'scope':'Native Unity/PhysX, headless. Geometry selection is deterministic, no Jev calls.'}
        (args.output/'trace.json').write_text(json.dumps(rows,indent=2)+'\n')
        (args.output/'summary.json').write_text(json.dumps(summary,indent=2)+'\n')
        print(json.dumps(summary,indent=2))
        if result not in ('arrived','positioned_no_action') and not (args.allow_wait and result=='waiting_cancelled'):
            raise RuntimeError('Movement did not arrive: '+result)
        return summary
    finally:
        client.stop(); client.close()


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--runtime',type=Path,required=True)
    parser.add_argument('--port-base',type=int,required=True)
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--level',required=True)
    parser.add_argument('--selection',choices=('top-left','left-board','occupied-platform','other-platform'),required=True)
    parser.add_argument('--seconds',type=float,default=45)
    parser.add_argument('--allow-wait',action='store_true')
    run(parser.parse_args())
