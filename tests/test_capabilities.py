import json
import unittest

from clients.capabilities import (ACTION_NAMES, TASK_NAMES, REGISTRY_HASH, REGISTRY_VERSION,
                                 ValidationError, contract, registry, validate_arguments, wire_command)
from harness.capabilities import assess_arguments, snapshot
from harness.plans import NativeAction, validate_plan
from scripts.generate_capabilities import generate, ROOT


def task(name='wash_dishes', arguments=None, repeat=None):
    return dict(kind='native_task', action=name, capability_version=1,
                arguments=arguments or {}, repeat=repeat or {'mode': 'finite', 'count': 1})


def plan(*tasks):
    return dict(schema_version='sous-chef.plan.v1', capability_version=1, command_id='command_42',
                expected_queue_revision=0, queue_edit='append', tasks=list(tasks or [task()]),
                after_plan='continue_pending', on_failure='pause_pending')


def state(**overrides):
    result = dict(fresh=True, connected=True, control={'available': True}, catalog={
        'capabilities': {'version': REGISTRY_VERSION, 'registryHash': REGISTRY_HASH,
                         'tasks': list(TASK_NAMES), 'actions': list(ACTION_NAMES)},
        'taskOptions': [{'name': 'wash_dishes'}],
        'taskApplicability': {'wash_dishes': {'state': 'applicable', 'reason': 'native_start_preconditions_observed'}},
        'semanticCatalog': {'players': [{'playerId': 1}], 'displayOptions': {'preparationOptions': [
            {'id': 'carrot', 'preparations': {'raw': 'applicable', 'chopped': 'applicable', 'empty': 'not_applicable', 'cooked': 'unknown'}},
            {'id': 'plate', 'preparations': {'empty': 'applicable', 'chopped': 'not_applicable'}},
        ]}},
    })
    result.update(overrides)
    return result


class RegistryTests(unittest.TestCase):
    def test_every_adapter_agrees_with_shared_native_parser_fixtures(self):
        cases = json.loads((ROOT / 'tests/fixtures/capability-wire.json').read_text())
        self.assertEqual({case['name'] for case in cases}, set(TASK_NAMES + ACTION_NAMES))
        for case in cases:
            with self.subTest(name=case['name']):
                operation, tokens = wire_command(case['name'], case['arguments'], case.get('repetitions', 1))
                self.assertEqual(' '.join(map(str, (operation, 'probe', *tokens))), case['wire'])

    def test_generated_native_constants_are_current(self):
        self.assertEqual((ROOT / 'src/Core/CapabilityRegistry.cs').read_text(), generate())

    def test_complete_registry_has_no_speculative_executable_features(self):
        data = registry()
        self.assertEqual(len(TASK_NAMES), 9)
        self.assertEqual(len(ACTION_NAMES), 19)
        self.assertTrue(set(TASK_NAMES).isdisjoint(ACTION_NAMES))
        for spec in data['contracts']:
            for key in ('version', 'description', 'parameters', 'repeat', 'candidate_sources',
                        'start_preconditions', 'completion_evidence', 'cancellation', 'checkpoint_resume'):
                self.assertIn(key, spec)
            self.assertFalse(spec['checkpoint_resume'])
        for name in data['not_implemented'] + ['input', '__dict__', 'claim']:
            with self.assertRaises(ValidationError): contract(name)
        data['contracts'].clear()
        self.assertEqual(len(registry()['contracts']), 28)

    def test_strict_fields_defaults_and_quantities(self):
        value = validate_arguments('prepare_near_player', {'items': [{'ingredient': 'carrot'}]})
        self.assertEqual(value, {'items': [{'ingredient': 'carrot', 'preparation': 'raw', 'quantity': 1}], 'player_id': 1})
        for args in ({'items': []}, {'items': [{'ingredient': 'carrot', 'quantity': True}]},
                     {'items': [{'ingredient': 'carrot', 'quantity': 11}]},
                     {'items': [{'ingredient': 'carrot', 'extra': 2}]},
                     {'items': [{'ingredient': 'carrot'}], 'player_id': 2},
                     {'items': [{'ingredient': 'carrot'}], 'player_id': 1.0},
                     {'items': [{'ingredient': 'carrot'}] * 11}, {'items': None}):
            with self.subTest(args=args), self.assertRaises(ValidationError): validate_arguments('prepare_near_player', args)
        for name in TASK_NAMES:
            if name not in ('prepare_near_player', 'assemble_dish'):
                for field in ('items', 'player_id', 'order_id', 'bases', 'deadline', 'region_id'):
                    with self.subTest(name=name, field=field), self.assertRaises(ValidationError): validate_arguments(name, {field: None})

    def test_exact_allocation_bounds_disjointness_and_wire_budget(self):
        valid = dict(order_id='flow:One:7', bases=['d1'])
        self.assertEqual(validate_arguments('assemble_dish', valid)['components'], [])
        for change in ({'bases': []}, {'bases': ['d1'] * 2}, {'components': ['d1']},
                       {'equipment': ['d１２']}, {'components': ['d2'] * 25}, {'items': []}):
            with self.subTest(change=change), self.assertRaises(ValidationError): wire_command('assemble_dish', {**valid, **change})
        resources = ['d' + str(i).zfill(31) for i in range(72)]
        with self.assertRaisesRegex(ValidationError, 'wire limit'):
            wire_command('assemble_dish', dict(order_id='o', bases=resources[:24], components=resources[24:48], equipment=resources[48:]))
        for repeat in (None, 2, True):
            with self.assertRaises(ValidationError): wire_command('assemble_dish', valid, repeat)

    def test_unicode_and_native_byte_limits(self):
        # UTF-16 and UTF-8 bounds both matter to the native selector parser.
        for selector in ('😀' * 33, 'é' * 49, '\ud800', 'x\x00', '\n', ''):
            with self.assertRaises(ValidationError): wire_command('fetch_ingredient', {'ingredient': selector, 'destination': 'd1'})
        for bad in (False, '60', float('nan'), float('inf'), .09, 121, 10 ** 1000):
            with self.assertRaises(ValidationError): wire_command('work_at', {'target_id': 'd1', 'timeout': bad})
        with self.assertRaises(ValidationError): wire_command('move_item', {'item': 'd1', 'destination': 'd1'})


class PlanTests(unittest.TestCase):
    def test_repeated_occurrences_and_finite_products_are_preserved(self):
        prep = task('prepare_near_player', {'items': [
            {'ingredient': 'carrot', 'preparation': 'raw', 'quantity': 1},
            {'ingredient': 'carrot', 'preparation': 'chopped', 'quantity': 2},
        ]}, {'mode': 'finite', 'count': 3})
        raw = plan(task(), prep, task())
        result = validate_plan(raw)
        raw['tasks'][1]['arguments']['items'].clear()
        self.assertEqual(len(result.tasks), 3)
        self.assertEqual([item['quantity'] * result.tasks[1].repeat.count for item in result.tasks[1].arguments['items']], [3, 6])
        result.tasks[1].arguments['items'].clear()
        self.assertEqual(len(result.tasks[1].arguments['items']), 2)

    def test_unlimited_only_at_tail_and_never_on_assembly_or_actions(self):
        forever = {'mode': 'until_cancelled'}
        self.assertIsNone(validate_plan(plan(task(), task(repeat=forever))).tasks[-1].repeat.count)
        for raw in (plan(task(repeat=forever), task()), plan(task('assemble_dish', {'order_id': 'o', 'bases': ['d1']}, forever))):
            with self.assertRaises(ValidationError): validate_plan(raw)
        with self.assertRaises(ValidationError): validate_plan(plan(), existing_unlimited=True)
        move = {**task('navigate_to', {'destination_id': 'd3'}), 'kind': 'native_action'}
        self.assertIsInstance(validate_plan(plan(move)).tasks[0], NativeAction)
        move['repeat'] = forever
        with self.assertRaises(ValidationError): validate_plan(plan(move))

    def test_whole_plan_rejects_schema_union_fields_counts_versions_and_controls(self):
        invalid = [plan(*([task()] * 6)), plan({**task(), 'kind': 'move_to_region'}),
                   plan(task('move_to_region')), plan({**task('stop'), 'kind': 'native_action'}),
                   plan({**task(), 'capability_version': 2}), plan({**task(), 'capability_version': None}),
                   plan(task(repeat={'mode': 'finite', 'count': None})), plan(task(repeat={'mode': 'finite', 'count': True})),
                   plan(task(repeat={'mode': 'until_cancelled', 'count': 1})),
                   plan(task(), task('wash_dishes', {'region': 'bottom'}))]
        invalid += [{**plan(), **change} for change in ({'schema_version': 'v2'}, {'capability_version': True},
                    {'tasks': []}, {'expected_queue_revision': -1}, {'expected_queue_revision': True},
                    {'queue_edit': 'run_arbitrary_code'}, {'on_failure': 'skip'}, {'extra': 1})]
        for raw in invalid:
            with self.subTest(raw=raw), self.assertRaises(ValidationError): validate_plan(raw)

    def test_busy_and_suspended_are_supported_but_not_startable(self):
        for control in ({'available': True, 'action': {'state': 'running'}},
                        {'available': False, 'reason': 'paused', 'suspended': True}):
            view = snapshot(state(control=control))
            wash = next(c for c in view['entries'] if c['name'] == 'wash_dishes')
            self.assertEqual(wash['support'], 'implemented')
            self.assertEqual(wash['start_eligibility']['state'], 'blocked')
            self.assertEqual(wash['applicability']['state'], 'applicable')
            self.assertTrue(view['semantic_catalog'])
            validate_plan(plan(), capabilities=view)  # Future queue planning may happen while busy.

    def test_old_bridge_disconnect_staleness_and_version_changes_fail_closed(self):
        cases = [state(fresh=False), state(catalog={})]
        wrong = state(); wrong['catalog']['capabilities']['registryHash'] = 'old'; cases.append(wrong)
        for raw in cases:
            view = snapshot(raw)
            self.assertFalse(view['semantic_catalog'])
            with self.assertRaises(ValidationError): validate_plan(plan(), capabilities=view)

    def test_preparation_contradictions_rejected_unknown_not_fabricated(self):
        view = snapshot(state())
        semantic = view['semantic_catalog']
        for ingredient, preparation, expected in [('carrot', 'chopped', 'applicable'), ('carrot', 'empty', 'not_applicable'),
                                                   ('plate', 'chopped', 'not_applicable'), ('carrot', 'cooked', 'unknown')]:
            args = {'items': [{'ingredient': ingredient, 'preparation': preparation}]}
            self.assertEqual(assess_arguments('prepare_near_player', args, semantic)['state'], expected)
            if expected == 'not_applicable':
                with self.assertRaises(ValidationError): validate_plan(plan(task('prepare_near_player', args)), capabilities=view)

    def test_exact_allocation_never_rebinds_absent_ticket_or_resource(self):
        args = {'order_id': 'old-ticket', 'bases': ['d1'], 'components': ['d2']}
        semantic = {'assemblyOrders': [{'id': 'new-ticket'}], 'displayOptions': {'items': [{'id': 'd1', 'plate': True}]},
                    'targets': [{'id': 'd2', 'kind': 'ingredient_supply'}]}
        result = assess_arguments('assemble_dish', args, semantic)
        self.assertEqual(result['state'], 'not_applicable')
        semantic['targets'].clear()
        self.assertEqual(assess_arguments('assemble_dish', args, semantic)['state'], 'unknown')
        self.assertEqual(args['order_id'], 'old-ticket')
