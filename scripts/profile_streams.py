#!/usr/bin/env python3
"""Read both observation streams concurrently; never send gameplay controls."""
import argparse
import json
from pathlib import Path
import statistics
import sys
import threading
import time

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'clients'))
from bridge import BridgeClient


def percentile(values, fraction):
    ordered = sorted(values)
    return ordered[min(len(ordered) - 1, int((len(ordered) - 1) * fraction))] if ordered else None


def measure(port, seconds):
    captures, clocks, arrivals, sizes, discovery = [], [], [], [], []
    gaps = 0
    start = time.monotonic()
    with BridgeClient(port, timeout=3) as client:
        for message in client.messages():
            if message['type'] == 'gap':
                gaps += message['beforeSequence'] - message['afterSequence'] - 1
            if message['type'] in ('snapshot', 'gameplay'):
                captures.append(message['diagnostics']['captureMilliseconds'])
                clocks.append(message['clock']['realtime'])
                arrivals.append(time.monotonic())
                sizes.append(len(json.dumps(message, separators=(',', ':')).encode()))
                discovery.append(message.get('coverage', {}).get('discoveryAgeSeconds', 0))
            if time.monotonic() - start >= seconds:
                break
    intervals = [b - a for a, b in zip(clocks, clocks[1:]) if b >= a]
    return {'port': port, 'samples': len(captures), 'skippedSequences': gaps,
            'observedCaptureHz': (len(clocks) - 1) / (clocks[-1] - clocks[0]) if len(clocks) > 1 and clocks[-1] > clocks[0] else None,
            'receivedHz': (len(arrivals) - 1) / (arrivals[-1] - arrivals[0]) if len(arrivals) > 1 else None,
            'captureMsMedian': statistics.median(captures) if captures else None,
            'captureMsP95': percentile(captures, .95), 'captureMsMax': max(captures) if captures else None,
            'sampleIntervalSecondsP95': percentile(intervals, .95),
            'sampleBytesMedian': statistics.median(sizes) if sizes else None,
            'discoveryAgeSecondsMax': max(discovery) if discovery else None}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--seconds', type=float, default=15)
    parser.add_argument('--full-port', type=int, default=24702)
    parser.add_argument('--gameplay-port', type=int, default=24704)
    args = parser.parse_args()
    if args.seconds <= 0: parser.error('--seconds must be positive')
    results = {}

    def run(name, port):
        try: results[name] = measure(port, args.seconds)
        except (OSError, EOFError, ValueError) as error: results[name] = {'error': str(error)}

    workers = [threading.Thread(target=run, args=(name, port)) for name, port in
               [('full', args.full_port), ('gameplay', args.gameplay_port)]]
    for worker in workers: worker.start()
    for worker in workers: worker.join()
    print(json.dumps(results, indent=2))
    return int(any('error' in value or not value.get('samples') for value in results.values()))


if __name__ == '__main__': sys.exit(main())
