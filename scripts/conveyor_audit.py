#!/usr/bin/env python3
"""Verify native conveyor traversal through ordinary navigation requests.

Requires a running isolated native simulator. The mine fixture moves P1 out of
the spawn aisle before navigation. No inputs, geometry edits, belt-speed changes
or action retries are used during measured traversal.
"""
import argparse
import json
from pathlib import Path
from types import SimpleNamespace
import native_simulator as sim


def probe_directions(runtime, level):
    """Read-only production segment checks on the native belts in both directions."""
    pairs = ([(12, 0, 9.6, 12.4, 0, 9.6), (12, 0, 1.2, 11.6, 0, 1.2),
              (16.8, 0, 10.8, 16.8, 0, 11.2), (21.6, 0, 3.6, 21.6, 0, 3.2)]
             if level == 's_mine_4_3' else
             [(14.4, 0, -3.6, 14.8, 0, -3.6), (14.4, 0, -9.6, 14, 0, -9.6)])
    results = []
    for pair in pairs:
        row = {}
        for key, points in [('forward', pair), ('reverse', pair[3:] + pair[:3])]:
            sim.command(runtime, 'probe-walk ' + ' '.join(map(str, points)))
            row[key] = json.loads((runtime / 'probe.json').read_text())
        row['success'] = (row['forward']['clear'] and not row['reverse']['clear'] and
                          row['reverse']['failure']['reason'] == 'conveyor_opposes_walking')
        results.append(row)
    return {'level': level, 'success': all(row['success'] for row in results), 'pairs': results}


def conveyor_motion(trace):
    """Use observed native surface velocity, not the planner's conveyor model."""
    decisions = {}
    for line in trace.read_text().splitlines():
        for row in json.loads(line)['navigation'].get('executionTrace') or []:
            decisions[row['frame']] = row
    riding, upstream, grounds, directions = 0, [], set(), set()
    opposing_since, longest_opposing = None, 0
    for row in sorted(decisions.values(), key=lambda r: r['frame']):
        flow = row.get('surfaceVelocity')
        opposing = False
        if (row['phase'] in ('steer', 'steer_adjusted') and flow is not None and
                flow[0] ** 2 + flow[2] ** 2 > .2 ** 2):
            riding += 1
            grounds.add(row['groundId'])
            directions.add(('+' if flow[0] > 0 else '-') + 'X' if abs(flow[0]) >= abs(flow[2])
                           else ('+' if flow[2] > 0 else '-') + 'Z')
            dx, dz = (row['goal'][i] - row['position'][i] for i in (0, 2))
            length = (dx * dx + dz * dz) ** .5
            opposing = length > .01 and (dx * flow[0] + dz * flow[2]) / length < -.05
            if opposing:
                upstream.append(row)
        if opposing:
            if opposing_since is None:
                opposing_since = row['time']
        elif opposing_since is not None:
            longest_opposing = max(longest_opposing, row['time'] - opposing_since)
            opposing_since = None
    if opposing_since is not None:
        longest_opposing = max(longest_opposing, row['time'] - opposing_since)
    return {'conveyorDecisionFrames': riding, 'conveyorGroundIds': sorted(grounds),
            'conveyorDirections': sorted(directions),
            'upstreamDecisionFrames': len(upstream), 'upstreamDecisions': upstream,
            'longestUpstreamHeadingSeconds': longest_opposing}


def open_mine_aisle(runtime):
    sim.command(runtime, 'stage-player 1 7 0 5.9 0')
    return json.loads((runtime / 'staging.json').read_text())


def audit(runtime, output, repeat, rates):
    status = sim.read_status(runtime)
    if not status.get('running') or sim.control_port(runtime) == 24703:
        raise RuntimeError('Use a running isolated simulator with a separate port range')
    output.mkdir(parents=True, exist_ok=True)
    report = {'runtimeProcess': json.loads((runtime / 'process.json').read_text()),
              'directionProbes': [], 'cases': [], 'success': True}
    for level, target, returning in [('s_mine_4_3', 'utensil_frying_pan_01', 'Meat'),
                                     ('s_sushi_1_4', 'Cucumber', 'SushiRice')]:
        generation = sim.read_status(runtime)['generation'] + 1
        sim.command(runtime, 'load ' + level)
        sim.wait_ready(runtime, generation, 60)
        proof = probe_directions(runtime, level)
        report['directionProbes'].append(proof)
        report['success'] &= proof['success']
        for fps in rates:
            directory = output / f'{level}-{fps}'
            result = sim.navigate(SimpleNamespace(runtime=runtime, target=target, then=[returning],
                repeat=repeat, fps=fps, trace_dir=directory, timeout=60, min_teleports=0, arrival='arrived'),
                setup=open_mine_aisle if level == 's_mine_4_3' else None)
            for attempt in result['results']:
                motion = conveyor_motion(directory / f"attempt-{attempt['attempt']}.ndjson")
                attempt['conveyors'] = motion
                attempt['success'] = (attempt['success'] and motion['conveyorDecisionFrames'] >= 5 and
                                      len(motion['conveyorGroundIds']) >= 2 and
                                      motion['longestUpstreamHeadingSeconds'] <= .15)
            result['passed'] = sum(row['success'] for row in result['results'])
            result['level'], result['fps'] = level, fps
            report['cases'].append(result)
            report['success'] &= result['passed'] == result['attempts']
            (output / 'report.json').write_text(json.dumps(report, indent=2) + '\n')
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--runtime', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--repeat', type=int, default=3)
    parser.add_argument('--fps', type=int, nargs='+', default=[60])
    args = parser.parse_args()
    try:
        report = audit(args.runtime.resolve(), args.output.resolve(), args.repeat, args.fps)
    except (OSError, RuntimeError, TimeoutError) as error:
        parser.exit(1, str(error) + '\n')
    print(json.dumps({'success': report['success'], 'report': str(args.output.resolve() / 'report.json')}))
    return 0 if report['success'] else 2


if __name__ == '__main__':
    raise SystemExit(main())
