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
    def test_assembly_preserves_explicit_ticket_and_resource_allocation(self):
        c = client(reply(1))
        c.assemble_dish('order_A', bases=['d1', 'd2'], components=['d3'], equipment=['d4', 'd5'])
        self.assertEqual(c.socket.sent, [b'task 1 assemble_dish 1 sb3JkZXJfQQ 2 d1 d2 1 d3 2 d4 d5\n'])
        for kwargs in ({'bases': []}, {'bases': ['d1'], 'components': ['d1']},
                       {'bases': ['d1'], 'components': 'd2'}, {'bases': ['d1'], 'equipment': False},
                       {'bases': ['d1'], 'repetitions': None}, {'bases': ['d1'], 'items': []}):
            with self.assertRaises(ValueError): c.run_task('assemble_dish', order_id='order_A', **kwargs)
        with self.assertRaises(ValueError): c.run_task('wash_dishes', bases=['d1'])
        self.assertEqual(len(c.socket.sent), 1)

    def test_deliver_completed_dishes_uses_standard_task_repetitions(self):
        c = client(reply(1), reply(2))
        c.run_task('deliver_completed_dishes'); c.run_task('deliver_completed_dishes', None)
        self.assertEqual(c.socket.sent, [b'task 1 deliver_completed_dishes 1\n', b'task 2 deliver_completed_dishes forever\n'])

    def test_task_wrapper_validates_and_encodes_finite_infinite_and_mixed_items(self):
        c = client(reply(1), reply(2), reply(3))
        c.run_task('wash_dishes'); c.run_task('monitor_equipment', None)
        c.run_task('prepare_near_player', 2, items=[{'ingredient': 'chicken', 'preparation': 'chopped', 'quantity': 2}, {'ingredient': 'pot', 'preparation': 'empty'}])
        self.assertEqual(c.socket.sent, [b'task 1 wash_dishes 1\n', b'task 2 monitor_equipment forever\n', b'task 3 prepare_near_player 2 1 sY2hpY2tlbg chopped 2 scG90 empty 1\n'])
        for count in (0, 11, True, 'forever', 1.5):
            with self.assertRaises(ValueError): c.run_task('wash_dishes', count)
        for items in ([], [{'ingredient': 'chicken', 'quantity': 11}], [{'ingredient': 'chicken', 'preparation': 'invented'}]):
            with self.assertRaises(ValueError): c.run_task('prepare_near_player', items=items)

    def test_logistics_actions_encode_exact_sources_destinations_and_selectors(self):
        c = client(*(reply(i) for i in range(1, 8)))
        c.move_item('d1', 'd2'); c.fetch_ingredient('tomato', 'd2'); c.get_plate('d3')
        c.get_utensil('mixing bowl', 'd4'); c.stage_item('d5', 'd6'); c.return_utensil('d7'); c.clear_station('d8', 'd9')
        self.assertEqual(c.socket.sent, [b'move_item 1 d1 d2\n', b'fetch_ingredient 2 sdG9tYXRv d2\n', b'get_plate 3 d3\n',
                                      b'get_utensil 4 sbWl4aW5nIGJvd2w d4\n', b'stage_item 5 d5 d6\n', b'return_utensil 6 d7\n', b'clear_station 7 d8 d9\n'])

    def test_logistics_arguments_cannot_inject_or_exceed_command_bounds(self):
        c = client()
        for value in ('', None, 'x' * 65, '\nstop r', '\x00', 'é' * 64):
            with self.assertRaises(ValueError): c.fetch_ingredient(value, 'd2')
        for call in (lambda: c.move_item('d1', 'd2\nstop r'), lambda: c.stage_item(None, 'd2'),
                     lambda: c.get_plate('x'), lambda: c.return_utensil('d１２')):
            with self.assertRaises(ValueError): call()
        self.assertEqual(c.socket.sent, [])

    def test_first_layer_actions_have_typed_wire_commands(self):
        c = client(*(reply(i) for i in range(1, 10)))
        c.pick_up('d1'); c.place_at('d2'); c.add_ingredient('d3')
        c.work_at('d4', 'one_plate_clean', 12.5); c.activate('d5', 'off')
        c.discard('d6'); c.wait_until('d7', 'cooked', 30)
        c.throw_to('d8'); c.throw_to_player(1)
        self.assertEqual(c.socket.sent, [
            b'pick_up 1 d1\n', b'place_at 2 d2\n', b'add_ingredient 3 d3\n',
            b'work_at 4 d4 one_plate_clean 12500\n', b'activate 5 d5 off\n',
            b'discard 6 d6\n', b'wait_until 7 d7 cooked 30000\n',
            b'throw_to 8 d8\n', b'throw_to_player 9 1\n'])

    def test_action_arguments_reject_injection_and_unbounded_waits(self):
        c = client()
        calls = [lambda: c.activate('d1', 'on\nstop r'), lambda: c.work_at('d1', 'forever'),
                 lambda: c.wait_until('d1', 'arbitrary_expression'), lambda: c.throw_to('d1 stop r')]
        calls += [lambda value=v: c.wait_until('d1', 'empty', value)
                  for v in (True, 0, -.1, 120.001, float('inf'), float('nan'), '60')]
        calls += [lambda value=v: c.throw_to_player(value) for v in (True, 2, 0, '1', 1.0)]
        for call in calls:
            with self.assertRaises(ValueError): call()
        self.assertEqual(c.socket.sent, [])

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
