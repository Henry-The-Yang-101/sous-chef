#!/usr/bin/env python3
"""Verify Milestone 1 catalog observation in an isolated native kitchen.

Uses an unlimited monitor assignment in an empty kitchen. Pause and focus are
native simulator fixtures; catalog discovery itself must never drive input.
"""
import argparse
import hashlib
import json
import math
from pathlib import Path
import statistics
import sys
import time

import native_simulator as sim
sys.path.insert(0, str(sim.ROOT))
from clients.actions import ActionClient
from clients.capabilities import ACTION_NAMES, TASK_NAMES, REGISTRY_HASH
from harness.capabilities import snapshot


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--runtime', type=Path, required=True)
    parser.add_argument('--port-base', type=int, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    process = json.loads((args.runtime / 'process.json').read_text())
    if args.port_base == 24702 or args.port_base != process['portBase']:
        parser.error('Use matching isolated runtime ports')
    binary = args.runtime / 'BepInEx/plugins/SousChef.Bridge.dll'
    digest = hashlib.sha256(binary.read_bytes()).hexdigest()
    if digest != process['bridgeSha256']:
        parser.error('Runtime binary differs from launch manifest')
    sim.wait_ready(args.runtime)
    args.output.mkdir(parents=True, exist_ok=True)
    client = ActionClient(args.port_base + 1, timeout=15)
    owned = False
    samples = []

    def observe(label, request=None, suspended=False):
        durations = []
        first = sim.read_status(args.runtime)
        for _ in range(20):
            start = time.monotonic()
            catalog = client.available_actions()
            durations.append((time.monotonic() - start) * 1000)
            control = client.last_response['control']
            manifest = catalog['capabilities']
            assert manifest['registryHash'] == REGISTRY_HASH, manifest
            assert set(manifest['tasks']) == set(TASK_NAMES), manifest
            assert set(manifest['actions']) == set(ACTION_NAMES) | {'task'}, manifest
            semantic = catalog['semanticCatalog']
            assert not semantic.get('error') and semantic['targets'] and semantic['assemblyOrders'], semantic
            assert semantic['players'] and semantic['displayOptions']['preparationOptions'], semantic
            view = snapshot(dict(fresh=True, catalog=catalog, control=control))
            assert all(c['support'] == 'implemented' for c in view['entries']), view
            if request:
                assert control['action']['requestId'] == request and control['action']['state'] == 'running', control
                assert control['claimed'] and control['suspended'] == suspended, control
                assert not catalog.get('taskOptions'), catalog.get('taskOptions')
                assert all(c['start_eligibility']['state'] == 'blocked' for c in view['entries'] if c['name'] != 'stop')
            time.sleep(max(0, .2 - (time.monotonic() - start)))
        last = sim.read_status(args.runtime)
        elapsed = last['realtime'] - first['realtime']
        first_positions = {p['name']: p['position'] for p in first['players']}
        last_positions = {p['name']: p['position'] for p in last['players']}
        assert first_positions.keys() == last_positions.keys(), 'Player assignment changed'
        drift = max(math.dist(position, last_positions[name]) for name, position in first_positions.items())
        (args.output / (label + '-poses.json')).write_text(json.dumps(dict(first=first_positions, last=last_positions, drift=drift), indent=2) + '\n')
        if request:
            assert drift <= .0001, 'Catalog polling moved a chef in the idle monitoring fixture'
        record = dict(label=label, polls=len(durations), medianRttMs=statistics.median(durations),
                      maxRttMs=max(durations), frameRate=(last['frame'] - first['frame']) / elapsed,
                      task=control.get('action'), suspended=control['suspended'],
                      maximumChefDisplacement=drift,
                      tickets=len(semantic['assemblyOrders']), targets=len(semantic['targets']))
        samples.append(record)
        (args.output / (label + '.json')).write_text(json.dumps(dict(control=control, catalog=catalog), indent=2) + '\n')

    try:
        initial = client.status()
        if initial.get('claimed'):
            parser.error('Runtime must be idle; refusing to replace existing work')
        observe('idle')
        catalog = client.available_actions()
        assert any(t['name'] == 'monitor_equipment' for t in catalog['taskOptions']), 'Use a kitchen with accessible empty cooking equipment'
        receipt = client.run_task('monitor_equipment', None)
        request = receipt['action']['requestId']; owned = True
        time.sleep(.5)
        observe('busy', request)
        sim.command(args.runtime, 'pause on')
        observe('paused', request, True)
        sim.command(args.runtime, 'pause off')
        sim.command(args.runtime, 'input-focus off')
        observe('unfocused', request, True)
        client.close(); client = ActionClient(args.port_base + 1, timeout=15)
        observe('reconnected', request, True)
        sim.command(args.runtime, 'input-focus on')
        observe('resumed', request)
        stopped = client.stop(); owned = False
        assert stopped['action']['state'] == 'cancelled' and not stopped['claimed'], stopped
        result = dict(success=True, registryHash=REGISTRY_HASH, binarySha256=digest,
                      level=sim.read_status(args.runtime)['scene'], samples=samples,
                      boundary='Empty monitoring fixture, not task-completion or universal FPS certification.')
        (args.output / 'result.json').write_text(json.dumps(result, indent=2) + '\n')
        print(json.dumps(result))
    finally:
        if owned:
            client.stop()
        client.close()
        if sim.is_running(args.runtime):
            sim.command(args.runtime, 'pause off')
            sim.command(args.runtime, 'input-focus on')


if __name__ == '__main__':
    main()
