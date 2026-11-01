import io
import json
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'clients'))
from control import ControlClient, ControlError


class Socket:
    def __init__(self): self.sent = []; self.closed = False
    def sendall(self, value): self.sent.append(value)
    def close(self): self.closed = True


def client_for(*responses):
    client = ControlClient.__new__(ControlClient)
    client.socket = Socket()
    client.reader = io.BytesIO(b''.join((json.dumps(r) + '\n').encode() for r in responses))
    client.request_id = 0; client.sequence = 0; client.claimed = False
    return client


def reply(ident, owned=True):
    return {'id': str(ident), 'ok': True, 'control': {'ownedByYou': owned}}


class ControlClientTests(unittest.TestCase):
    def test_claim_input_neutral_release(self):
        client = client_for(reply(1), reply(2), reply(3), reply(4, False))
        client.claim(); client.set_input(x=0.5, buttons=['use', 'dash']); client.set_input(); client.release()
        self.assertEqual(client.socket.sent, [b'claim 1\n', b'input 2 1 300 0.5 0.0 6\n',
                                             b'input 3 2 300 0.0 0.0 0\n', b'release 4\n'])
        self.assertFalse(client.claimed)

    def test_wrong_reply_or_rejection_not_silently_accepted(self):
        with self.assertRaisesRegex(ControlError, 'ID mismatch'): client_for(reply(9)).claim()
        with self.assertRaisesRegex(ControlError, 'player2_absent'):
            client_for({'id': '1', 'ok': False, 'error': 'player2_absent'}).claim()

    def test_invalid_input_never_sent(self):
        client = client_for()
        for args in [{'x': float('nan')}, {'y': float('inf')}, {'x': 2}, {'buttons': ['pause']}, {'ttl_ms': 1001}]:
            with self.assertRaises(ValueError): client.set_input(**args)
        self.assertEqual(client.socket.sent, [])

    def test_close_releases_owned_input(self):
        client = client_for(reply(1, False)); client.claimed = True
        client.close()
        self.assertEqual(client.socket.sent, [b'release 1\n'])
        self.assertTrue(client.socket.closed)

    def test_disconnected_client_still_closes_socket(self):
        client = client_for(); client.claimed = True
        client.close()
        self.assertTrue(client.socket.closed)


if __name__ == '__main__': unittest.main()
