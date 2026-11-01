#!/usr/bin/env python3
"""Dependency-free NDJSON client. No command channel or gameplay inputs."""
import argparse
import json
import socket
import time


class BridgeClient:
    def __init__(self, port=24702, timeout=5.0):
        self.socket = socket.create_connection(('127.0.0.1', port), timeout)
        self.reader = self.socket.makefile('rb')
        self.assets = {}
        self.snapshot = None
        self.session_id = None
        self.last_received = None
        self.last_sequence = None

    def close(self):
        self.reader.close()
        self.socket.close()

    def __enter__(self): return self
    def __exit__(self, *args): self.close()

    def messages(self):
        try:
            while True:
                line = self.reader.readline(64 * 1024 * 1024 + 1)
                if not line: raise EOFError('Bridge disconnected')
                if len(line) > 64 * 1024 * 1024 or not line.endswith(b'\n'):
                    raise ValueError('Bridge message exceeds 64 MiB limit')
                message = json.loads(line)
                kind = message.get('type')
                if kind == 'hello':
                    if message.get('protocolVersion') != 1: raise ValueError('Unsupported protocol')
                    self.session_id = message['sessionId']
                    self.assets.clear(); self.snapshot = None; self.last_sequence = None
                elif kind == 'asset':
                    self.assets[message['id']] = message
                elif kind in ('snapshot', 'status'):
                    if message.get('sessionId') != self.session_id: raise ValueError('Session changed without hello')
                    sequence = message['sequence']
                    if self.last_sequence is not None and sequence <= self.last_sequence:
                        raise ValueError('Non-monotonic sequence')
                    self.last_sequence = sequence
                    self.snapshot = message if kind == 'snapshot' and message.get('inRound') else None
                    self.last_received = time.monotonic()
                    if self.snapshot:
                        needed = {c['shape'][key]['assetId'] for o in message['objects'] for c in o['colliders']
                                  for key in ('mesh', 'terrain') if c.get('shape', {}).get(key)}
                        if not needed.issubset(self.assets): raise ValueError('Missing geometry assets')
                        self.assets = {k: self.assets[k] for k in needed}
                    else: self.assets.clear()
                yield message
        finally:
            self.snapshot = None

    def current(self, max_age=2.0):
        """Never return a kitchen after stream loss, status, or stale capture."""
        if self.last_received is None or time.monotonic() - self.last_received > max_age:
            return None
        return self.snapshot


def summary(message):
    if message['type'] != 'snapshot':
        return {k: v for k, v in message.items() if k != 'data'}
    objects = message['objects']
    return {'type': 'snapshot', 'sequence': message['sequence'], 'epoch': message['levelEpoch'],
            'objects': len(objects), 'colliders': sum(len(o['colliders']) for o in objects),
            'chefs': sum(any(c['type'] == 'PlayerControls' for c in o['components']) for o in objects),
            'role': message['role'], 'captureMs': message['diagnostics']['captureMilliseconds'],
            'missingFields': message['coverage']['missingFields'],
            'errors': len(message['coverage']['errors']) + message['coverage']['fieldErrors']}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--port', type=int, default=24702)
    parser.add_argument('--record', help='Save complete NDJSON including mesh assets')
    parser.add_argument('--frames', type=int, default=0, help='Stop after N snapshot/status messages')
    args = parser.parse_args()
    recording = open(args.record, 'w') if args.record else None
    try:
        with BridgeClient(args.port) as client:
            count = 0
            for message in client.messages():
                if recording: recording.write(json.dumps(message, separators=(',', ':')) + '\n')
                if message['type'] == 'asset': continue
                print(json.dumps(summary(message)), flush=True)
                if message['type'] in ('snapshot', 'status'):
                    count += 1
                    if args.frames and count >= args.frames: break
    except (OSError, EOFError, ValueError) as error:
        parser.exit(1, str(error) + '\n')
    finally:
        if recording: recording.close()
