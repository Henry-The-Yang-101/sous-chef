import io
import json
from pathlib import Path
import sys
import unittest
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'clients'))
from actions import ActionClient
from control import ControlError


class Socket:
    def __init__(self): self.sent = []
    def sendall(self, data): self.sent.append(data)


def client(*replies):
    result = ActionClient.__new__(ActionClient)
    result.socket = Socket()
    result.reader = io.BytesIO(b''.join((json.dumps(r) + '\n').encode() for r in replies))
    result.request_id = 0
    result.sequence = 0
    result.claimed = False
    return result


def reply(n, **fields):
    return dict(type='reply', id=str(n), ok=True, control={'ownedByYou': False}, **fields)


class ActionsTests(unittest.TestCase):
    def test_catalog_is_typed_and_navigation_sends_only_target(self):
        catalog = {'actions': [{'name': 'stop'}, {'name': 'navigate_to'}], 'destinations': [{'id': 'd7'}]}
        c = client(reply(1, availableActions=catalog), reply(2), reply(3))
        self.assertEqual(c.available_actions(), catalog)
        c.navigate_to('d7'); c.stop()
        self.assertEqual(c.socket.sent, [b'actions 1\n', b'navigate_to 2 d7\n', b'stop 3\n'])

    def test_invalid_destination_cannot_inject_commands(self):
        c = client()
        for value in ['d1\nstop r', 'd-1', '', None, 'd１２', 'd1 extra']:
            with self.assertRaises(ValueError): c.navigate_to(value)
        self.assertEqual(c.socket.sent, [])

    def test_unavailable_destination_is_not_retried(self):
        c = client({'type': 'reply', 'id': '1', 'ok': False, 'error': 'destination_unavailable'})
        with self.assertRaisesRegex(ControlError, 'destination_unavailable'): c.navigate_to('d7')
        self.assertEqual(c.socket.sent, [b'navigate_to 1 d7\n'])

    def test_large_kitchen_catalog_is_not_truncated_at_old_control_limit(self):
        catalog = {'destinations': [{'id': 'd' + str(i), 'label': 'Counter with a plate',
                                    'position': [i, 0, 0]} for i in range(1200)]}
        self.assertGreater(len(json.dumps(catalog)), 65536)
        c = client(reply(1, availableActions=catalog))
        self.assertEqual(c.available_actions(), catalog)

    def test_oversized_catalog_still_has_a_bounded_read(self):
        c = client(reply(1, availableActions={'destinations': ['x' * 100]}))
        c.MAX_REPLY_BYTES = 64
        with self.assertRaisesRegex(ControlError, 'invalid control reply'):
            c.available_actions()
