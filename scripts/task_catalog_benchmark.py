#!/usr/bin/env python3
"""Measure a selected native runtime's idle task catalogue without issuing input."""
import argparse
import json
from pathlib import Path
import statistics
import sys
import time

import native_simulator as sim

sys.path.insert(0, str(sim.ROOT))
from clients.actions import ActionClient


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--runtime', type=Path, required=True)
    parser.add_argument('--port-base', type=int, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--polls', type=int, default=50)
    args = parser.parse_args()
    if args.polls < 20:
        parser.error('Use at least 20 polls (the first ten warm up the catalogue)')
    sim.wait_ready(args.runtime)
    samples = []
    client = ActionClient(args.port_base + 1, timeout=10)
    try:
        for _ in range(args.polls):
            start = time.monotonic()
            client.available_actions()
            elapsed = time.monotonic() - start
            control = client.last_response['control']
            if control.get('claimed') or control.get('action', {}).get('state') == 'running':
                raise RuntimeError('Benchmark requires idle Player 2; it never cancels another action')
            native = sim.read_status(args.runtime)
            nav = control['navigation']
            samples.append(dict(at=time.monotonic(), frame=native['frame'], rttMs=elapsed * 1000,
                                revision=nav['catalogRevision'], backend=nav['diagnostics']['searchBackend']))
            time.sleep(max(0, .2 - elapsed))
    finally:
        client.close()
    measured = samples[10:]
    report = dict(bridgeVersion=client.hello.get('bridgeVersion'), samples=samples,
                  medianRttMs=statistics.median(s['rttMs'] for s in measured),
                  approximateFrameRate=(measured[-1]['frame'] - measured[0]['frame']) / (measured[-1]['at'] - measured[0]['at']),
                  navigationRefreshesPerPoll=(measured[-1]['revision'] - measured[0]['revision']) / (len(measured) - 1))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2) + '\n')
    print(json.dumps({k: v for k, v in report.items() if k != 'samples'}))


if __name__ == '__main__':
    main()
