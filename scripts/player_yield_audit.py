#!/usr/bin/env python3
"""Replay native player encounters with normal P2 navigation and scripted P1 axes."""
import argparse
from collections import Counter
import json
import math
from pathlib import Path
import sys
import time
from types import SimpleNamespace

import native_simulator as sim
from conveyor_audit import conveyor_motion

sys.path.insert(0, str(sim.ROOT))
from clients.actions import ActionClient


# P1 foot XZ, P2 foot XZ, P1 logical input X/Y and duration, expected phase.
CASES = {
    'head-on': ((13.9, -1.2), (17.0, -1.2), (1, 0, 1000), 'yield_step'),
    'crossing': ((15.7, -.1), (17.7, -1.2), (0, 1, 550), 'yield_wait'),
    'stationary': ((14.8, -1.2), (17.0, -1.2), (0, 0, 1600), 'yield_step'),
    'following': ((13.5, -1.2), (17.0, -1.2), (-1, 0, 750), None),
    'separate-lanes': ((14.5, -.35), (17.0, -1.55), (1, 0, 1000), None),
    'wall-side': ((8.4, -4.45), (8.4, -1.2), (0, -1, 650), 'yield_step'),
    'occupied-stance': ((8.4, -3.6), (17.0, -1.2), (0, 0, 2300), 'yield_wait'),
    'narrow-aisle': (None, None, (0, 1, 250, 1300), 'yield_wait'),
    'stationary-corner': ((10.2, 4.6), (8.4, 4.6), (0, 0, 3200), 'yield_step'),
}


def distance_xz(a, b):
    return math.hypot(b[0] - a[0], b[2] - a[2])


def audit_case(runtime, output, name):
    p1, p2, axes, expected = CASES[name]
    blocked = name == 'occupied-stance'
    mine = name == 'narrow-aisle'
    corner = name == 'stationary-corner'
    generation = sim.wait_ready(runtime)['generation'] + 1
    level = 's_mine_4_3' if mine else 's_sushi_1_2' if corner else 's_sushi_1_1'
    sim.command(runtime, 'load ' + level)
    ready = sim.wait_ready(runtime, generation)
    if not mine:
        for player, (x, z) in enumerate((p1, p2), 1):
            sim.command(runtime, f'stage-player {player} {x} 0 {z} 0')

    trace = output / 'navigation.ndjson'
    with ActionClient(sim.control_port(runtime), timeout=15) as client:
        if client.hello['sessionId'] != ready['sessionId']:
            raise RuntimeError('Connected to a different simulator')
        deadline = time.monotonic() + 15
        while not client.available_actions()['complete']:
            if time.monotonic() >= deadline:
                raise TimeoutError('Catalogue did not finish')
            time.sleep(.1)
        sim.command(runtime, 'walk-teammate ' + ' '.join(map(str, axes)))
        returning = None
        with trace.open('w') as out:
            target = 'utensil_frying_pan_01' if mine else 'SushiFish'
            result = sim.navigate_leg(client, target, SimpleNamespace(timeout=4 if corner else 3 if blocked else 15), out)
            if mine and result['success']:
                returning = sim.navigate_leg(client, 'Meat', SimpleNamespace(timeout=15), out)
        final = client.status()

    deadline = time.monotonic() + 2
    while not (runtime / 'teammate-walk.json').exists() and time.monotonic() < deadline:
        time.sleep(.025)
    fixture = json.loads((runtime / 'teammate-walk.json').read_text())
    (output / 'teammate-walk.json').write_text(json.dumps(fixture, indent=2) + '\n')
    if fixture['result'] != 'completed':
        raise RuntimeError('Teammate walk failed: ' + fixture['result'])
    samples = fixture['samples']
    decisions = {}
    for line in trace.read_text().splitlines():
        for row in json.loads(line)['navigation']['executionTrace']:
            decisions[row['frame']] = row
    phases = Counter(row['phase'] for row in decisions.values())
    # Ignore neutral frames between opposite sidesteps; input can stop briefly
    # at a wall without actually breaking the pacing loop.
    steps = [row['input'] for row in decisions.values() if row['phase'] == 'yield_step']
    reversals = sum(a[0] * b[0] + a[2] * b[2] < -.5 for a, b in zip(steps, steps[1:]))
    start, end = samples[0]['p1'], samples[-1]['p1']
    x, y, duration = axes[:3]
    progress = x * (end[0] - start[0]) - y * (end[2] - start[2])
    disturbance = (max(abs(x * (s['p1'][2] - start[2]) + y * (s['p1'][0] - start[0])) for s in samples)
                   if x or y else max(distance_xz(start, s['p1']) for s in samples))
    gap = min(distance_xz(s['p1'], s['p2']) for s in samples)
    saw_expected = phases[expected] > 0 if expected else not any(p.startswith('yield_') for p in phases)
    finished = (result['reason'] == 'test_timeout' and final['navigation']['reason'] == 'stopped'
                if blocked else result['success'])
    conveyors = conveyor_motion(trace) if mine else None
    wait_started = None
    if mine:
        first_walk = min((row['time'] for row in decisions.values() if row['phase'] == 'steer'), default=0)
        first_wait = min((row['time'] for row in decisions.values() if row['phase'] == 'yield_wait'), default=None)
        if first_wait is not None:
            wait_started = first_wait - first_walk
        finished &= (returning is not None and returning['success'] and
                     wait_started is not None and 0 <= wait_started < axes[3] / 1000 - .2 and
                     conveyors['conveyorDecisionFrames'] >= 5 and len(conveyors['conveyorGroundIds']) >= 2 and
                     conveyors['longestUpstreamHeadingSeconds'] <= .15)
    minimum_progress = .6 if mine else 6 * duration / 1000 - .45
    # Reject stops, loss of progress and lateral displacement in P1's input
    # path. At 20 FPS the input fixture can quantize a walk by one native frame.
    success = (finished and saw_expected and disturbance < .05 and gap >= .76
               and (not (x or y) or progress >= minimum_progress) and (not corner or reversals <= 1)
               and phases['stall_reroute'] == 0)
    return dict(name=name, success=success, result=result, phases=dict(phases),
                yieldReversals=reversals,
                player1Progress=progress, player1Disturbance=disturbance, minimumSeparation=gap,
                returning=returning, conveyors=conveyors, firstWaitSeconds=wait_started)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--runtime', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--fps', type=int, nargs='+', default=[60, 20])
    parser.add_argument('--repeat', type=int, default=1)
    parser.add_argument('--case', choices=CASES, action='append')
    parser.add_argument('--baseline', action='store_true', help='Record older behavior without enforcing yield expectations')
    args = parser.parse_args()
    if sim.control_port(args.runtime) == 24703 or not sim.read_status(args.runtime).get('running'):
        parser.error('Use a running isolated simulator')

    args.output.mkdir(parents=True, exist_ok=True)
    results = []
    report = dict(runtime=json.loads((args.runtime / 'process.json').read_text()), cases=results)
    for fps in args.fps:
        sim.command(args.runtime, f'fps {fps}')
        for name in args.case or CASES:
            for attempt in range(1, args.repeat + 1):
                directory = args.output / f'{fps}-{name}-{attempt}'
                directory.mkdir(exist_ok=True)
                try:
                    result = audit_case(args.runtime, directory, name)
                except (RuntimeError, TimeoutError, OSError) as exc:
                    result = dict(name=name, success=False, error=str(exc))
                result.update(fps=fps, attempt=attempt)
                results.append(result)
                (args.output / 'report.json').write_text(json.dumps(report, indent=2) + '\n')
                outcome = 'baseline recorded' if args.baseline else 'pass' if result['success'] else 'fail'
                print(f"{fps} FPS {name} #{attempt}: {outcome}", flush=True)
    return 0 if args.baseline or all(row['success'] for row in results) else 2


if __name__ == '__main__':
    sys.exit(main())
