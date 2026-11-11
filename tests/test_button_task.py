import copy
import unittest

from clients.capabilities import ValidationError, wire_command
from harness.capabilities import assess_arguments
from harness.decision.compiler import Clarification, Unsupported, build_context
from harness.decision.protocol import DecisionError
from harness.decision.service import InterpreterService
from harness.semantic_map import interaction_context
from test_decision import compiler, world
from test_semantic_map import scene


def kitchen():
    value = world()
    semantic = value['capabilities']['semantic_catalog']
    semantic['movement'] = scene()
    semantic['targets'] = []
    for i, station in enumerate(semantic['movement']['stations']):
        station.update(label='Button', kind='interaction', routeStatus='verified')
        semantic['targets'].append(dict(station, possibleOperations=['activate'], activationStates=['trigger', 'on', 'off']))
    return value


def context(value):
    return build_context(value, {'revision': 0, 'active': None, 'pending': []})


class ButtonCompilerTests(unittest.IsolatedAsyncioTestCase):
    async def test_task_then_reachable_target_then_state_and_full_verification(self):
        value = kitchen()
        c, script = compiler('Push the bottom button', [
            {'simple_coverage': 0}, {'next_task': 'press_button'}, {'target_id': 'd1'},
            {'desired_state': 'unspecified'}, {}, {'next_task': 'done'}, {}], context(value))
        result = await c.compile()
        task = result['plan']['tasks'][0]
        self.assertEqual(task['action'], 'press_button')
        self.assertEqual(task['arguments'], {'target_id': 'd1', 'desired_state': 'trigger'})
        self.assertEqual(task['repeat'], {'mode': 'finite', 'count': 1})
        prompt = script.requests[2]
        self.assertEqual(prompt['state']['compiler']['action'], 'press_button')
        controls = prompt['state']['compiler']['controls']
        by_id = {t['id']: t for t in controls['candidates']}
        self.assertGreater(by_id['d1']['screen']['y'], by_id['d2']['screen']['y'])
        self.assertIn('TOP (0) to BOTTOM (1)', controls['coordinate_meaning'])
        self.assertEqual(set(prompt['questions']['target_id']['criteria']), {'d1', 'd2', 'ambiguous', 'unsupported'})
        self.assertEqual(script.requests[-1]['state']['world']['controls'], controls)
        self.assertIn('(x=0, y=1)', result['message'])

    async def test_unreachable_copy_remains_a_landmark_but_cannot_be_selected(self):
        value = kitchen()
        value['capabilities']['semantic_catalog']['movement']['stations'][0]['routeStatus'] = 'unknown'
        c, script = compiler('Push the bottom button', [{'target_id': 'unsupported'}], context(value))
        with self.assertRaises(Unsupported):
            await c.arguments('press_button', 1)
        prompt = script.requests[0]
        self.assertNotIn('d1', prompt['questions']['target_id']['criteria'])
        self.assertIn('d2', prompt['questions']['target_id']['criteria'])
        self.assertEqual(len(prompt['state']['compiler']['controls']['candidates']), 2)

    async def test_no_verified_route_does_not_invent_or_offer_a_target(self):
        value = kitchen()
        for station in value['capabilities']['semantic_catalog']['movement']['stations']:
            station['routeStatus'] = 'unknown'
        c, script = compiler('Press a button', [], context(value))
        with self.assertRaises(Unsupported): await c.arguments('press_button', 1)
        self.assertEqual(script.requests, [])

    async def test_ambiguity_asks_for_an_exact_control_and_retains_spatial_labels(self):
        c, _ = compiler('Press the button', [{'target_id': 'ambiguous'}], context(kitchen()))
        with self.assertRaises(Clarification) as raised: await c.arguments('press_button', 1)
        self.assertEqual(set(raised.exception.options), {'d1', 'd2'})
        self.assertIn('y=1', raised.exception.options['d1'])

    async def test_toggle_state_choices_are_bound_to_selected_control(self):
        value = kitchen()
        value['capabilities']['semantic_catalog']['targets'][0]['activationStates'] = ['trigger']
        c, script = compiler('Turn on the bottom button', [{'target_id': 'd1'}, {'desired_state': 'unsupported'}], context(value))
        with self.assertRaises(Unsupported): await c.arguments('press_button', 1)
        self.assertEqual(set(script.requests[-1]['questions']['desired_state']['criteria']),
                         {'value_0', 'unspecified', 'ambiguous', 'unsupported'})
        c, _ = compiler('Turn off the top switch', [{'target_id': 'd2'}, {'desired_state': 'value_1'}], context(value))
        self.assertEqual(await c.arguments('press_button', 1), {'target_id': 'd2', 'desired_state': 'off'})


class ButtonContractTests(unittest.TestCase):
    def test_wire_defaults_and_exact_target_validation(self):
        self.assertEqual(wire_command('press_button', {'target_id': 'd1'}), ('task', ('press_button', 1, 'd1', 'trigger')))
        for args in ({}, {'target_id': 'bottom'}, {'target_id': 'd1', 'desired_state': 'toggle'}):
            with self.assertRaises(ValidationError): wire_command('press_button', args)
        for count in (None, 2):
            with self.assertRaises(ValidationError): wire_command('press_button', {'target_id': 'd1'}, count)

    def test_projected_controls_reuse_camera_orientation_and_filter_workstations(self):
        semantic = kitchen()['capabilities']['semantic_catalog']
        semantic['targets'][1]['possibleOperations'] = ['work_at']
        semantic['movement']['orientation'].update(right=[-1, 0, 0], up=[0, 0, -1])
        controls = interaction_context(semantic, 'activate')
        self.assertEqual([t['id'] for t in controls['candidates']], ['d1'])
        self.assertEqual(controls['candidates'][0]['screen'], {'x': 1, 'y': 0})
        semantic['movement']['orientation']['known'] = False
        self.assertNotIn('screen', interaction_context(semantic, 'activate')['candidates'][0])

    def test_preacceptance_rejects_rebound_or_missing_target_but_allows_motion(self):
        before = kitchen()['capabilities']['semantic_catalog']
        plan = {'tasks': [{'action': 'press_button', 'arguments': {'target_id': 'd1', 'desired_state': 'trigger'}}]}
        after = copy.deepcopy(before)
        after['targets'][0]['position'] = [10, 0, 10]
        InterpreterService._exact_bindings(before, after, plan)
        after['targets'][0]['objectInstanceId'] = 999
        with self.assertRaises(DecisionError): InterpreterService._exact_bindings(before, after, plan)
        after['targets'].pop(0)
        with self.assertRaises(DecisionError): InterpreterService._exact_bindings(before, after, plan)
        self.assertEqual(assess_arguments('press_button', {'target_id': 'd1'}, after)['state'], 'not_applicable')


if __name__ == '__main__':
    unittest.main()
