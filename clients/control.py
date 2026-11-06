#!/usr/bin/env python3
"""Direct control of an existing local Player 2; no keyboard injection."""
import argparse
import json
import math
import re
import socket
import time


class ControlError(RuntimeError):
    pass


class ControlClient:
    BUTTONS = {'pickup': 1, 'use': 2, 'dash': 4}
    MAX_REPLY_BYTES = 4 * 1024 * 1024  # Includes bounded kitchen action catalogues.

    def __init__(self, port=24703, timeout=3.0):
        self.socket = socket.create_connection(('127.0.0.1', port), timeout)
        self.reader = self.socket.makefile('rb')
        self.request_id = 0
        self.sequence = 0
        self.claimed = False
        try:
            self.hello = self._read()
            if self.hello.get('controlProtocolVersion') != 1 or self.hello.get('player') != 2:
                raise ControlError('Unsupported control endpoint')
        except BaseException:
            self.reader.close(); self.socket.close(); raise

    def _read(self):
        line = self.reader.readline(self.MAX_REPLY_BYTES + 1)
        if not line or len(line) > self.MAX_REPLY_BYTES or not line.endswith(b'\n'):
            raise ControlError('Disconnected or invalid control reply')
        return json.loads(line)

    def _request(self, operation, *args, request_id=None):
        self.request_id += 1
        ident = str(self.request_id) if request_id is None else request_id
        if not isinstance(ident, str) or not re.fullmatch(r"[A-Za-z0-9_-]{1,32}", ident):
            raise ValueError("Invalid native request ID")
        line = ' '.join(map(str, (operation, ident, *args))) + '\n'
        self.socket.sendall(line.encode('ascii'))
        response = self._read()
        if response.get('id') != ident: raise ControlError('Control response ID mismatch')
        if not response.get('ok'): raise ControlError(response.get('error', 'Command failed'))
        self.last_response = response
        self.claimed = response['control']['ownedByYou']
        return response['control']

    def status(self): return self._request('status')

    def claim(self):
        result = self._request('claim')
        self.sequence = 0
        return result

    def set_input(self, x=0, y=0, buttons=(), ttl_ms=300):
        if not all(math.isfinite(v) and abs(v) <= 1 for v in (x, y)):
            raise ValueError('Axes must be finite and within [-1, 1]')
        if not isinstance(ttl_ms, int) or not 50 <= ttl_ms <= 1000:
            raise ValueError('ttl_ms must be an integer in 50..1000')
        mask = 0
        for button in buttons:
            if button not in self.BUTTONS: raise ValueError('Unknown button: ' + button)
            mask |= self.BUTTONS[button]
        self.sequence += 1
        return self._request('input', self.sequence, ttl_ms, float(x), float(y), mask)

    def hold(self, seconds, x=0, y=0, buttons=()):
        """Renew a held input; send neutral afterwards, preserving ownership."""
        if not math.isfinite(seconds) or not 0 <= seconds <= 3600:
            raise ValueError('seconds must be finite and in 0..3600')
        deadline = time.monotonic() + seconds
        try:
            while time.monotonic() < deadline:
                self.set_input(x, y, buttons)
                time.sleep(min(0.1, max(0, deadline - time.monotonic())))
        finally:
            self.set_input()

    def release(self):
        return self._request('release')

    def close(self):
        try:
            if self.claimed:
                try: self.release()
                except (OSError, ControlError): pass
        finally:
            self.reader.close(); self.socket.close()

    def __enter__(self): return self
    def __exit__(self, *args): self.close()


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--port', type=int, default=24703)
    parser.add_argument('--seconds', type=float, default=0, help='0 queries status; positive claims P2 for a timed input')
    parser.add_argument('--x', type=float, default=0)
    parser.add_argument('--y', type=float, default=0)
    parser.add_argument('--pickup', action='store_true')
    parser.add_argument('--use', action='store_true', help='Work/interact or aim/throw according to game context')
    parser.add_argument('--dash', action='store_true')
    args = parser.parse_args()
    try:
        with ControlClient(args.port) as client:
            print(json.dumps(client.status()))
            if args.seconds:
                print(json.dumps(client.claim()))
                client.hold(args.seconds, args.x, args.y,
                            [name for name in ControlClient.BUTTONS if getattr(args, name)])
                print(json.dumps(client.release()))
    except (OSError, ControlError, ValueError) as error:
        parser.exit(1, str(error) + '\n')
