import copy
import http.client
import json
import threading
import unittest
from unittest import mock
from http.server import ThreadingHTTPServer
from urllib.parse import urlsplit

from clients.control import ControlError
from harness.server import Handler, HarnessBridge, HarnessError, main, serve
from harness.sharing import PhoneShare
from test_scheduler import plan, spec
from harness.scheduler import context_of
from clients.capabilities import registry, REGISTRY_HASH, REGISTRY_VERSION


class FakeBridge:
    def __init__(self):
        self.hello = {'sessionId': 'test-session'}
        self.destinations = [dict(id='d7', objectInstanceId=77, label='utensil_pot_01',
                                  kind='item', position=[1, 0, 2], routeDistance=3,
                                  directDistance=2)]
        self.control = dict(ownedByYou=False, available=True, navigation={'state': 'idle'})
        self.calls = []
        self.failure = None
        self.reject = None
        self.targets = None
        self.players = []
        self.items, self.ingredients, self.utensil_types, self.delivery_destinations, self.jobs = [], [], [], [], []
        self.job_options = None
        self.tasks = []
        self.display_options = {}

    def enable_queue(self):
        self.hello['capabilities'] = dict(version=REGISTRY_VERSION, registryHash=REGISTRY_HASH,
            tasks=[c['name'] for c in registry()['contracts'] if c['kind'] == 'task'],
            actions=[c['name'] for c in registry()['contracts'] if c['kind'] == 'action'])
        self.control.update(receiptProtocolVersion=1, kitchenEpoch='epoch', dispatchGeneration=0, chefInstanceId=8)

    def execute(self, request_id, epoch, generation, name, arguments, repetitions=1, version=None):
        self.run_task(name, repetitions, **arguments)
        receipt = dict(id=request_id, epoch=epoch, state='running', reason=None)
        self.last_response = {'control': copy.deepcopy(self.control), 'receipt': receipt}
        return receipt

    def available_actions(self):
        if self.failure:
            raise self.failure
        self.last_response = {'control': copy.deepcopy(self.control)}
        targets = self.targets if self.targets is not None else [dict(d, operations=['pick_up', 'work_at', 'wait_until']) for d in self.destinations]
        job_options = self.job_options
        if job_options is None:
            sources = {'fetch_ingredient': [i['id'] for i in self.ingredients], 'get_utensil': self.utensil_types,
                       'get_plate': [''], 'clear_station': [t['id'] for t in targets]}
            job_options = {name: [{'sourceId': source, 'destinationIds': [d['id'] for d in self.delivery_destinations]}
                                  for source in sources.get(name, [i['id'] for i in self.items])] for name in self.jobs}
        return {'displayOptions': copy.deepcopy(self.display_options), 'taskOptions': copy.deepcopy(self.tasks), 'destinations': copy.deepcopy(self.destinations), 'targets': copy.deepcopy(targets),
                'players': copy.deepcopy(self.players), 'navigation': self.control['navigation'],
                'items': copy.deepcopy(self.items), 'ingredients': copy.deepcopy(self.ingredients),
                'utensilTypes': self.utensil_types[:], 'deliveryDestinations': copy.deepcopy(self.delivery_destinations),
                'jobOptions': copy.deepcopy(job_options),
                'actions': [{'name': name} for name in self.jobs]}

    def _job(self, operation, *arguments):
        self.calls.append((operation, *arguments))
        self.control.update(ownedByYou=True, action={'state': 'running', 'operation': operation, 'phase': 'picking_up', 'targetId': arguments[-1]})
        return copy.deepcopy(self.control)

    def move_item(self, item, destination): return self._job('move_item', item, destination)
    def stage_item(self, item, destination): return self._job('stage_item', item, destination)
    def clear_station(self, station, destination): return self._job('clear_station', station, destination)
    def fetch_ingredient(self, ingredient, destination): return self._job('fetch_ingredient', ingredient, destination)
    def get_plate(self, destination): return self._job('get_plate', destination)
    def get_utensil(self, utensil_type, destination): return self._job('get_utensil', utensil_type, destination)
    def return_utensil(self, item): return self._job('return_utensil', item)

    def pick_up(self, target):
        self.calls.append(('pick_up', target))
        self.control.update(ownedByYou=True, action={'state': 'running', 'operation': 'pick_up'})
        return copy.deepcopy(self.control)

    def run_task(self, action, repetitions=1, **kwargs):
        self.calls.append(('task', action, repetitions, kwargs))
        self.control.update(ownedByYou=True, action={'state': 'running', 'operation': 'task'})
        return copy.deepcopy(self.control)

    def work_at(self, target, until='work_complete', timeout=60):
        self.calls.append(('work_at', target, until, timeout))
        self.control.update(ownedByYou=True, action={'state': 'running', 'operation': 'work_at'})
        return copy.deepcopy(self.control)

    def throw_to_player(self, player):
        self.calls.append(('throw_to_player', player))
        self.control.update(ownedByYou=True, action={'state': 'running', 'operation': 'throw_to_player'})
        return copy.deepcopy(self.control)

    def throw_to(self, target):
        self.calls.append(('throw_to', target))
        self.control.update(ownedByYou=True, action={'state': 'running', 'operation': 'throw_to'})
        return copy.deepcopy(self.control)

    def navigate_to(self, destination):
        self.calls.append(('navigate_to', destination))
        if self.reject:
            raise ControlError(self.reject)
        self.control.update(ownedByYou=True, navigation={'state': 'navigating', 'destinationId': destination})
        return copy.deepcopy(self.control)

    def status(self):
        return copy.deepcopy(self.control)

    def stop(self):
        self.calls.append(('stop',))
        self.control.update(ownedByYou=False, navigation={'state': 'idle', 'reason': 'stopped'})
        if 'action' in self.control:
            self.control['action'].update(state='cancelled', phase='idle', reason='stopped')
        return copy.deepcopy(self.control)

    def release(self):
        self.calls.append(('release',))
        self.control.update(ownedByYou=False, navigation={'state': 'idle', 'reason': 'released'})

    def close(self):
        self.calls.append(('close',))


class HarnessTests(unittest.TestCase):
    def setUp(self):
        self.now = 100.0
        self.native = FakeBridge()
        self.bridge = HarnessBridge(factory=lambda: self.native, clock=lambda: self.now)
        self.bridge.poll()
        self.addCleanup(self.bridge.close)

    def queue(self, task):
        self.native.enable_queue(); self.bridge.poll()
        result = self.bridge.accept_plan(plan(tasks=[task]), context_of(self.bridge.state()))
        self.bridge.scheduler.tick()
        return result

    def navigate(self):
        return self.bridge.navigate('tab-a', 'test-session', 'd7', 77)

    def test_live_catalog_replaces_removed_destinations(self):
        self.assertEqual(self.bridge.state()['catalog']['destinations'][0]['id'], 'd7')
        self.native.destinations.clear()
        self.bridge.poll()
        self.assertEqual(self.bridge.state()['catalog']['destinations'], [])

    def test_tasks_use_shared_queue_and_preserve_infinite_repeat(self):
        self.queue(spec('monitor_equipment', None))
        self.assertEqual(self.native.calls[-1], ('task', 'monitor_equipment', None, {}))
        with self.assertRaisesRegex(HarnessError, 'shared plan queue'):
            self.bridge.act('viewer', 'test-session', 'task', 0, 'task', {'action': 'wash_dishes'})

    def test_deliver_completed_dishes_uses_shared_queue(self):
        self.queue(spec('deliver_completed_dishes', 2))
        self.assertEqual(self.native.calls[-1], ('task', 'deliver_completed_dishes', 2, {}))

    def test_run_now_does_not_focus_game_when_autonomous_control_is_available(self):
        self.native.enable_queue()
        self.bridge.poll()
        self.bridge.focus = mock.Mock()
        result = self.bridge.accept_plan(plan(tasks=[spec('wash_dishes')], edit='replace_active'),
                                         context_of(self.bridge.state()), focus=True)
        self.assertEqual(result['queue']['pending'][0]['spec']['action'], 'wash_dishes')
        self.bridge.focus.assert_not_called()

    def test_assembly_forwards_explicit_ticket_and_resource_allocation(self):
        allocation = {'order_id': 'flow:One:7', 'bases': ['d1', 'd2'],
                      'components': ['d3'], 'equipment': ['d4', 'd5']}
        self.queue(spec('assemble_dish', arguments=allocation))
        self.assertEqual(self.native.calls[-1], ('task', 'assemble_dish', 1, allocation))

    def test_task_contract_errors_fail_before_focus_or_native_io(self):
        self.native.tasks = [{'name': 'wash_dishes'}]
        self.native.control.update(available=False, reason='game_unfocused')
        self.bridge.focus = mock.Mock()
        for arguments in ({'action': 'wash_dishes', 'items': []}, {'action': 'wash_dishes', 'player_id': 1},
                          {'action': 'wash_dishes', 'repetitions': True}, {'action': 'stop'},
                          {'action': 'assemble_dish', 'order_id': 'o', 'bases': ['d1'], 'components': ['d1']}):
            with self.subTest(arguments=arguments), self.assertRaises(HarnessError):
                self.bridge.act('viewer', 'test-session', 'task', 0, 'task', arguments)
        self.bridge.focus.assert_not_called()
        self.assertEqual(self.native.calls, [])

    def test_display_choices_do_not_authorize_tasks_or_supply_jobs(self):
        self.native.display_options = {
            'items': [{'id': 'd99', 'objectInstanceId': 999}],
            'ingredients': [{'id': 'tomato', 'label': 'Tomato'}],
            'deliveryDestinations': [{'id': 'd7', 'objectInstanceId': 77}],
            'taskOptions': [{'name': 'wash_dishes'}],
        }
        self.bridge.poll()
        self.assertEqual(self.bridge.state()['catalog']['displayOptions'], self.native.display_options)
        with self.assertRaisesRegex(HarnessError, 'shared plan queue'):
            self.bridge.act('viewer', 'test-session', 'task', 0, 'task', {'action': 'wash_dishes'})
        with self.assertRaises(HarnessError):
            self.bridge.act('viewer', 'test-session', 'd7', 77, 'fetch_ingredient', {'ingredient': 'tomato'})
        self.assertEqual(self.native.calls, [])

    def test_display_choices_follow_current_observations_without_global_items(self):
        self.native.display_options = {'ingredients': [{'id': 'tomato', 'label': 'Tomato'}]}
        self.bridge.poll()
        self.assertEqual(self.bridge.state()['catalog']['displayOptions']['ingredients'][0]['id'], 'tomato')
        self.native.display_options = {'ingredients': [{'id': 'rice', 'label': 'Rice'}]}
        self.bridge.poll()
        self.assertEqual(self.bridge.state()['catalog']['displayOptions']['ingredients'], [{'id': 'rice', 'label': 'Rice'}])

    def test_implemented_actions_remain_visible_when_paused_catalogue_hides_jobs(self):
        self.native.hello.update(bridgeVersion='0.6.0', actions=['navigate_to', 'move_item', 'stop'])
        self.native.control.update(available=False, reason='paused')
        self.bridge.poll()
        value = self.bridge.state()
        self.assertEqual(value['bridgeVersion'], '0.6.0')
        self.assertIn('move_item', value['supportedActions'])
        self.assertEqual(value['catalog']['actions'], [])
        self.native.failure = OSError('Disconnected')
        self.bridge.poll()
        self.assertIsNone(self.bridge.state()['supportedActions'])
        self.assertIsNone(self.bridge.state()['bridgeVersion'])

    def test_dispenser_metadata_and_distances_follow_live_catalog(self):
        self.native.destinations = [dict(id='d8', objectInstanceId=78, label='DispenserCrate 3 (2)',
                                        kind='ingredient_supply', foodName='Cheese_Raw',
                                        routeDistance=6.5, directDistance=4.2)]
        self.bridge.poll()
        item = self.bridge.state()['catalog']['destinations'][0]
        self.assertEqual((item['foodName'], item['routeDistance'], item['directDistance']),
                         ('Cheese_Raw', 6.5, 4.2))
        self.native.destinations[0].update(foodName='Tomato', routeDistance=3, directDistance=1)
        self.bridge.poll()
        self.assertEqual(self.bridge.state()['catalog']['destinations'][0]['foodName'], 'Tomato')

    def test_stale_state_hides_options_and_refuses_control(self):
        self.now += 2
        self.assertFalse(self.bridge.state()['fresh'])
        self.assertEqual(self.bridge.state()['catalog'], {})
        with self.assertRaisesRegex(HarnessError, 'unavailable'):
            self.navigate()
        self.assertEqual(self.native.calls, [])

    def test_click_revalidates_identity_and_reachability(self):
        with self.assertRaisesRegex(HarnessError, 'restarted'):
            self.bridge.navigate('tab-a', 'old-session', 'd7', 77)
        with self.assertRaisesRegex(HarnessError, 'no longer reachable'):
            self.bridge.navigate('tab-a', 'test-session', 'd7', 88)
        self.native.destinations.clear()
        with self.assertRaisesRegex(HarnessError, 'no longer reachable'):
            self.navigate()
        self.assertEqual(self.native.calls, [])

    def test_navigation_uses_one_persistent_connection_and_exact_id(self):
        value = self.navigate()
        self.bridge.poll()
        self.assertEqual(self.native.calls, [('navigate_to', 'd7')])
        self.assertTrue(value['viewerOwnsAction'])
        self.assertEqual(value['lastCommand']['target']['objectInstanceId'], 77)
        self.assertEqual(value['control']['navigation']['state'], 'navigating')

    def test_first_layer_actions_dispatch_once_with_exact_target(self):
        value = self.bridge.act('tab-a', 'test-session', 'd7', 77, 'pick_up')
        self.assertEqual(self.native.calls, [('pick_up', 'd7')])
        self.assertTrue(value['viewerOwnsAction'])
        self.assertEqual(value['lastCommand']['operation'], 'pick_up')
        self.bridge.poll()
        self.assertTrue(self.bridge.state('tab-a')['viewerOwnsAction'])

    def test_work_parameters_and_nonwalking_targets(self):
        self.native.targets = [dict(self.native.destinations[0], operations=['work_at'])]
        self.native.destinations.clear()
        self.bridge.act('tab-a', 'test-session', 'd7', 77, 'work_at', {'until': 'one_plate_clean', 'timeout': 12})
        self.assertEqual(self.native.calls, [('work_at', 'd7', 'one_plate_clean', 12)])

    def test_action_affordance_is_revalidated_before_dispatch(self):
        self.native.targets = [dict(self.native.destinations[0], operations=[])]
        with self.assertRaisesRegex(HarnessError, 'no longer available'):
            self.bridge.act('tab-a', 'test-session', 'd7', 77, 'pick_up')
        self.assertEqual(self.native.calls, [])

    def test_unknown_action_or_extra_arguments_cannot_call_client_methods(self):
        for operation, arguments in [('close', {}), ('pick_up', {'destination_id': 'd9'}), ('wait_until', {})]:
            with self.assertRaises(HarnessError):
                self.bridge.act('tab-a', 'test-session', 'd7', 77, operation, arguments)
        self.assertEqual(self.native.calls, [])

    def test_player_pass_validates_player_object_then_dispatches_logical_id(self):
        self.native.players = [dict(id='p1', playerId=1, objectInstanceId=123, operations=['throw_to_player'])]
        with self.assertRaises(HarnessError):
            self.bridge.act('tab-a', 'test-session', 'p1', 999, 'throw_to_player')
        self.bridge.act('tab-a', 'test-session', 'p1', 123, 'throw_to_player')
        self.assertEqual(self.native.calls, [('throw_to_player', 1)])

    def test_throw_uses_exact_nonwalking_place_and_no_source_item_argument(self):
        self.native.targets = [dict(self.native.destinations[0], operations=['throw_to'])]
        self.native.destinations = []
        self.bridge.act('tab-a', 'test-session', 'd7', 77, 'throw_to')
        self.assertEqual(self.native.calls, [('throw_to', 'd7')])

    def test_throw_removed_by_live_planner_is_not_dispatched_from_old_catalog(self):
        for operation in ('throw_to', 'throw_to_player'):
            with self.subTest(operation=operation):
                target = dict(id='p1' if operation == 'throw_to_player' else 'd9',
                              objectInstanceId=123, playerId=1, operations=[operation])
                if operation == 'throw_to_player':
                    self.native.players = [target]
                else:
                    self.native.targets = [target]
                self.bridge.poll()
                target['operations'] = []
                with self.assertRaisesRegex(HarnessError, 'no longer available'):
                    self.bridge.act('tab-a', 'test-session', target['id'], 123, operation)
        self.assertEqual(self.native.calls, [])

    def test_logistics_dispatch_validates_both_item_and_destination_identity(self):
        self.native.jobs = ['move_item']
        self.native.items = [dict(id='d8', objectInstanceId=88, label='Tomato')]
        self.native.delivery_destinations = self.native.destinations[:]
        with self.assertRaisesRegex(HarnessError, 'source item or station changed'):
            self.bridge.act('tab-a', 'test-session', 'd7', 77, 'move_item', {'item_id': 'd8', 'source_instance_id': 99})
        self.bridge.act('tab-a', 'test-session', 'd7', 77, 'move_item', {'item_id': 'd8', 'source_instance_id': 88})
        self.assertEqual(self.native.calls, [('move_item', 'd8', 'd7')])

    def test_fetch_jobs_use_only_current_ingredient_and_utensil_options(self):
        self.native.jobs = ['fetch_ingredient', 'get_utensil', 'get_plate']
        self.native.ingredients = [dict(id='i4', label='Tomato_Raw')]
        self.native.utensil_types = ['pan']
        self.native.delivery_destinations = self.native.destinations[:]
        with self.assertRaisesRegex(HarnessError, 'currently observed ingredient'):
            self.bridge.act('tab-a', 'test-session', 'd7', 77, 'fetch_ingredient', {'ingredient': 'invented'})
        self.bridge.act('tab-a', 'test-session', 'd7', 77, 'fetch_ingredient', {'ingredient': 'i4'})
        self.bridge.act('tab-a', 'test-session', 'd7', 77, 'get_utensil', {'utensil_type': 'pan'})
        self.bridge.act('tab-a', 'test-session', 'd7', 77, 'get_plate')
        self.assertEqual(self.native.calls, [('fetch_ingredient', 'i4', 'd7'), ('get_utensil', 'pan', 'd7'), ('get_plate', 'd7')])

    def test_return_and_clear_jobs_use_their_correct_source_catalogues(self):
        self.native.jobs = ['return_utensil', 'clear_station']
        self.native.items = [dict(id='d8', objectInstanceId=88, label='Pan')]
        self.native.delivery_destinations = [dict(id='d9', objectInstanceId=99, label='Counter')]
        self.bridge.act('tab-a', 'test-session', 'd8', 88, 'return_utensil')
        self.bridge.act('tab-a', 'test-session', 'd9', 99, 'clear_station', {'station_id': 'd7', 'source_instance_id': 77})
        self.assertEqual(self.native.calls, [('return_utensil', 'd8'), ('clear_station', 'd7', 'd9')])

    def test_rejected_command_is_not_retried(self):
        self.native.reject = 'planning_budget_exceeded'
        with self.assertRaisesRegex(HarnessError, 'planning_budget_exceeded'):
            self.navigate()
        self.bridge.poll()
        self.assertEqual(self.native.calls, [('navigate_to', 'd7')])

    def test_job_pair_is_rechecked_even_if_both_fields_still_exist(self):
        self.native.jobs = ['fetch_ingredient']
        self.native.ingredients = [dict(id='i4', label='Bun')]
        self.native.delivery_destinations = self.native.destinations[:]
        self.native.job_options = {'fetch_ingredient': [{'sourceId': 'i4', 'destinationIds': ['d7']}]}
        self.bridge.poll()
        self.native.job_options['fetch_ingredient'][0]['destinationIds'] = ['d9']
        with self.assertRaisesRegex(HarnessError, 'no longer form a valid job'):
            self.bridge.act('tab-a', 'test-session', 'd7', 77, 'fetch_ingredient', {'ingredient': 'i4'})
        self.assertEqual(self.native.calls, [])

    def test_missing_or_empty_job_options_fail_closed(self):
        self.native.jobs = ['get_plate']
        self.native.delivery_destinations = self.native.destinations[:]
        self.native.job_options = {}
        with self.assertRaisesRegex(HarnessError, 'no longer form a valid job'):
            self.bridge.act('tab-a', 'test-session', 'd7', 77, 'get_plate')
        self.assertEqual(self.native.calls, [])

    def test_browser_click_focuses_game_then_revalidates_exact_target(self):
        self.native.control.update(available=False, reason='game_unfocused')
        focused = []
        def focus():
            focused.append(True)
            self.native.control.update(available=True, reason=None)
        self.bridge.focus = focus
        self.navigate()
        self.assertEqual(focused, [True])
        self.assertEqual(self.native.calls, [('navigate_to', 'd7')])

    def test_target_removed_during_focus_cannot_start(self):
        self.native.control.update(available=False, reason='game_unfocused')
        def focus():
            self.native.control.update(available=True, reason=None)
            self.native.destinations.clear()
        self.bridge.focus = focus
        with self.assertRaisesRegex(HarnessError, 'no longer reachable'):
            self.navigate()
        self.assertEqual(self.native.calls, [])

    def test_disconnect_clears_catalog_and_reconnect_never_replays(self):
        self.navigate()
        self.native.failure = OSError('connection lost')
        self.bridge.poll()
        self.assertFalse(self.bridge.state()['connected'])
        self.assertEqual(self.bridge.state()['catalog'], {})
        self.native.failure = None
        self.bridge.poll()
        self.assertEqual(self.native.calls.count(('navigate_to', 'd7')), 1)

    def test_abandoned_browser_keeps_action_until_stop(self):
        self.navigate()
        self.now += 6
        self.bridge.state('another-tab')
        self.bridge.poll()
        self.assertNotIn(('release',), self.native.calls)
        self.assertNotIn(('stop',), self.native.calls)
        self.assertEqual(self.bridge.state()['control']['navigation']['state'], 'navigating')
        self.bridge.stop()
        self.assertIn(('stop',), self.native.calls)

    def test_browser_state_reads_preserve_action(self):
        self.navigate()
        self.now += 4
        self.bridge.state('tab-a')
        self.now += 4
        self.bridge.poll()
        self.assertNotIn(('release',), self.native.calls)

    def test_closing_another_tab_does_not_cancel_action(self):
        self.navigate()
        self.bridge.leave('tab-b')
        self.assertNotIn(('release',), self.native.calls)
        self.bridge.leave('tab-a')
        self.bridge.poll()
        self.assertNotIn(('release',), self.native.calls)
        self.assertEqual(self.bridge.state()['control']['navigation']['state'], 'navigating')

    def test_stop_interrupts_and_clears_old_catalog(self):
        self.navigate()
        value = self.bridge.stop()
        self.assertEqual(value['control']['navigation']['state'], 'idle')
        self.assertEqual(value['catalog'], {})
        self.assertIn(('stop',), self.native.calls)

    def test_shutdown_closes_owning_connection(self):
        self.navigate()
        self.bridge.close()
        self.assertIn(('close',), self.native.calls)


class HttpTests(unittest.TestCase):
    def setUp(self):
        self.native = FakeBridge()
        self.bridge = HarnessBridge(factory=lambda: self.native)
        self.bridge.poll()
        self.server = ThreadingHTTPServer(('127.0.0.1', 0), Handler)
        self.server.bridge = self.bridge
        self.server.token = 'test-token'
        self.server.sharing = PhoneShare(self.server.server_port, self.server.token,
                                        ['192.168.1.23'], 'https://test.trycloudflare.com')
        self.worker = threading.Thread(target=self.server.serve_forever, kwargs={'poll_interval': .01})
        self.worker.start()

    def tearDown(self):
        self.server.shutdown()
        self.server.server_close()
        self.worker.join()
        self.bridge.close()

    def request(self, method, path, data=None, headers=None):
        connection = http.client.HTTPConnection('127.0.0.1', self.server.server_port, timeout=2)
        connection.request(method, path, json.dumps(data) if data is not None else None, headers or {})
        response = connection.getresponse()
        body = response.read()
        code = response.status
        connection.close()
        return code, body

    def test_page_and_live_state_are_served(self):
        code, body = self.request('GET', '/')
        self.assertEqual(code, 200)
        self.assertIn(b'Action targets', body)
        self.assertIn(b'Kitchen map', body)
        self.assertIn(b'Game FPS', body)
        self.assertEqual(self.request('GET', '/visuals.js')[0], 200)
        code, body = self.request('GET', '/api/state')
        self.assertEqual(code, 200)
        self.assertEqual(json.loads(body)['catalog']['destinations'][0]['id'], 'd7')

    def test_capabilities_endpoint_is_read_only_and_uses_shared_registry(self):
        from clients.capabilities import REGISTRY_HASH
        code, body = self.request('GET', '/api/capabilities')
        self.assertEqual(code, 200)
        data = json.loads(body)
        self.assertEqual(data['registry']['registry_hash'], REGISTRY_HASH)
        self.assertEqual(len(data['state']['entries']), 28)
        self.assertFalse(data['state']['compatible'])  # Legacy bridge has no manifest.
        self.assertEqual(self.native.calls, [])
        bootstrap = json.loads(self.request('GET', '/api/bootstrap')[1])
        self.assertEqual(data['registry'], bootstrap['capabilityRegistry'])
        self.assertEqual(self.request('GET', '/api/capabilities', headers={'Origin': 'https://evil.example'})[0], 403)

    def test_plan_and_queue_endpoints_share_auth_revision_and_journal(self):
        self.native.enable_queue(); self.bridge.poll()
        body = dict(token='test-token', viewerId='tab', plan=plan(tasks=[spec(), spec()]), context=context_of(self.bridge.state()))
        self.assertEqual(self.request('POST', '/api/plan', body, {'Origin': 'https://evil.example'})[0], 403)
        self.assertEqual(self.request('POST', '/api/plan', dict(body, token='wrong'))[0], 403)
        code, raw = self.request('POST', '/api/plan', body)
        self.assertEqual(code, 200)
        result = json.loads(raw)
        self.assertEqual(len(result['queue']['pending']), 2)
        self.assertEqual(self.native.calls, [])  # HTTP acceptance never dispatches a draft.
        duplicate = json.loads(self.request('POST', '/api/plan', body)[1])
        self.assertEqual(duplicate['operationResult'], result['operationResult'])
        stale = dict(body, plan=dict(body['plan'], command_id='stale'))
        self.assertEqual(self.request('POST', '/api/plan', stale)[0], 409)
        edit = dict(operation_id='clear', expected_queue_revision=1, operation='clear_pending')
        self.assertEqual(self.request('POST', '/api/queue', dict(token='test-token', viewerId='tab', edit=edit))[0], 200)
        self.assertEqual(self.bridge.scheduler.snapshot()['pending'], [])
        for data in (None, [], {'unexpected': True}):
            self.assertIn(self.request('POST', '/api/plan', dict(body, plan=data))[0], (400, 409))
        self.assertEqual(self.request('POST', '/api/task', body)[0], 404)

    def test_stop_does_not_wait_for_normal_io_lock(self):
        self.bridge.io_lock.acquire()
        try:
            result = []
            worker = threading.Thread(target=lambda: result.append(self.bridge.stop()))
            worker.start(); worker.join(1)
            self.assertFalse(worker.is_alive(), 'Stop blocked on ordinary bridge I/O')
            self.assertEqual(result[0]['lastCommand']['message'], 'Stopped')
        finally:
            self.bridge.io_lock.release()

    def test_foreign_origin_host_and_missing_token_are_rejected(self):
        for headers in [{'Origin': 'https://example.com'}, {'Host': 'example.com'}]:
            self.assertEqual(self.request('GET', '/api/bootstrap', headers=headers)[0], 403)
        self.assertEqual(self.request('POST', '/api/stop', {'viewerId': 'x'})[0], 403)
        self.assertEqual(self.native.calls, [])

    def test_qr_sharing_info_contains_access_link_and_offline_svg(self):
        code, body = self.request('GET', '/api/share')
        self.assertEqual(code, 200)
        links = json.loads(body)['links']
        self.assertEqual(links[0]['url'], 'https://test.trycloudflare.com/#access=test-token')
        self.assertEqual(links[1]['url'], f'http://192.168.1.23:{self.server.server_port}/#access=test-token')
        self.assertIn('viewBox=', links[0]['qrSvg'])

    def test_phone_needs_qr_token_even_when_proxy_connects_from_loopback(self):
        for host in [f'192.168.1.23:{self.server.server_port}', 'test.trycloudflare.com']:
            headers = {'Host': host}
            self.assertEqual(self.request('GET', '/', headers=headers)[0], 200)
            for path in ['/api/bootstrap', '/api/state', '/api/share', '/api/capabilities']:
                self.assertEqual(self.request('GET', path, headers=headers)[0], 403)
                headers['X-Page-Token'] = 'test-token'
                code, body = self.request('GET', path, headers=headers)
                self.assertEqual(code, 200)
                headers.pop('X-Page-Token')
        self.assertEqual(self.native.calls, [])

    def test_phone_commands_and_ownership_use_same_live_bridge(self):
        headers = {'Host': 'test.trycloudflare.com', 'Origin': 'https://test.trycloudflare.com'}
        data = dict(token='test-token', viewerId='phone', sessionId='test-session',
                    destinationId='d7', objectInstanceId=77)
        self.assertEqual(self.request('POST', '/api/navigate', data, headers)[0], 200)
        code, body = self.request('GET', '/api/state', headers={**headers, 'X-Page-Token': 'test-token', 'X-Viewer-Id': 'phone'})
        self.assertEqual(code, 200)
        self.assertTrue(json.loads(body)['viewerOwnsAction'])
        self.assertEqual(self.request('POST', '/api/stop', data, headers)[0], 200)
        self.assertEqual(self.native.calls, [('navigate_to', 'd7'), ('stop',), ('close',)])

    def test_forwarded_host_does_not_bypass_origin_or_token_checks(self):
        headers = {'Host': 'test.trycloudflare.com', 'Origin': 'https://evil.example',
                   'X-Page-Token': 'test-token'}
        self.assertEqual(self.request('GET', '/api/state', headers=headers)[0], 403)
        self.assertEqual(self.request('POST', '/api/stop', dict(token='test-token', viewerId='x'), headers)[0], 403)
        headers = {'Host': 'unknown.trycloudflare.com', 'X-Page-Token': 'test-token'}
        self.assertEqual(self.request('GET', '/api/state', headers=headers)[0], 403)
        self.assertEqual(self.native.calls, [])

    def test_navigation_and_stop_round_trip(self):
        body = dict(token='test-token', viewerId='tab-a', sessionId='test-session', destinationId='d7', objectInstanceId=77)
        code, response = self.request('POST', '/api/navigate', body)
        self.assertEqual(code, 200)
        self.assertEqual(json.loads(response)['control']['navigation']['state'], 'navigating')
        code, response = self.request('POST', '/api/stop', body)
        self.assertEqual(code, 200)
        self.assertEqual(json.loads(response)['control']['navigation']['state'], 'idle')
        self.assertEqual(self.native.calls, [('navigate_to', 'd7'), ('stop',), ('close',)])

    def test_expired_destination_returns_conflict_without_movement(self):
        self.native.destinations.clear()
        body = dict(token='test-token', viewerId='tab-a', sessionId='test-session', destinationId='d7', objectInstanceId=77)
        self.assertEqual(self.request('POST', '/api/navigate', body)[0], 409)
        self.assertEqual(self.native.calls, [])


class SharingTests(unittest.TestCase):
    def test_tunnel_is_published_only_after_connection_and_removed_on_exit(self):
        sharing = PhoneShare(8765, 'token', ['192.168.1.23'])
        sharing.internet_pending = True
        process = mock.MagicMock()
        process.poll.return_value = None
        sharing.process = process

        def logs():
            yield 'INF Your quick Tunnel: https://connected.trycloudflare.com\n'
            self.assertTrue(sharing.describe()['internetPending'])
            self.assertEqual([link['label'] for link in sharing.links], ['Same Wi-Fi'])
            yield 'INF Registered tunnel connection connIndex=0\n'
            self.assertTrue(sharing.describe()['internetPending'])
            self.assertEqual([link['label'] for link in sharing.links], ['Same Wi-Fi'])
            with mock.patch.object(sharing, '_verify_tunnel') as verify:
                sharing._check_tunnel()
                verify.assert_called_once_with('https://connected.trycloudflare.com')
            self.assertFalse(sharing.describe()['internetPending'])
            self.assertEqual(sharing.links[0]['url'], 'https://connected.trycloudflare.com/#access=token')
            self.assertTrue(sharing.valid_origin('connected.trycloudflare.com', None))

        process.stdout.__iter__.side_effect = logs
        sharing._read_tunnel()
        self.assertTrue(sharing.describe()['internetPending'])
        self.assertEqual([link['label'] for link in sharing.links], ['Same Wi-Fi'])
        self.assertFalse(sharing.valid_origin('connected.trycloudflare.com', None))
        self.assertIn('stopped', sharing.status)
        process.stdout.close.assert_called_once()

    def test_tunnel_failure_before_connection_waits_for_recovery(self):
        sharing = PhoneShare(8765, 'token')
        sharing.internet_pending = True
        sharing.process = mock.MagicMock()
        sharing.process.stdout.__iter__.return_value = iter(['ERR Could not connect\n'])
        sharing._read_tunnel()
        self.assertTrue(sharing.describe()['internetPending'])
        self.assertEqual(sharing.links, [])
        self.assertIn('stopped', sharing.status)

    def connected_tunnel(self):
        sharing = PhoneShare(8765, 'token', ['192.168.1.23'])
        sharing.process = mock.Mock()
        sharing.process.poll.return_value = None
        sharing.tunnel_url = 'https://connected.trycloudflare.com'
        sharing.tunnel_verified = True
        sharing._add(sharing.tunnel_url, 'Internet')
        return sharing

    def test_live_process_with_broken_dns_hides_qr_and_requests_replacement(self):
        sharing = self.connected_tunnel()
        with mock.patch.object(sharing, '_verify_tunnel', side_effect=OSError('NXDOMAIN')), \
                mock.patch('builtins.print'):
            self.assertFalse(sharing._check_tunnel())
            self.assertTrue(sharing.describe()['internetPending'])
            self.assertEqual([link['label'] for link in sharing.links], ['Same Wi-Fi'])
            self.assertFalse(sharing.valid_origin('connected.trycloudflare.com', None))
            self.assertFalse(sharing._check_tunnel())
            self.assertTrue(sharing._check_tunnel())
        self.assertIsNone(sharing.process.poll())

    def test_transient_failure_recovers_same_link_and_resets_failure_count(self):
        sharing = self.connected_tunnel()
        with mock.patch.object(sharing, '_verify_tunnel', side_effect=[OSError('offline'), None]), \
                mock.patch('builtins.print'):
            sharing._check_tunnel()
            self.assertFalse(sharing._check_tunnel())
        self.assertEqual(sharing.links[0]['url'], sharing.tunnel_url + '/#access=token')
        self.assertEqual(sharing.tunnel_failures, 0)
        self.assertFalse(sharing.internet_pending)

    def test_unverified_candidate_requires_token_and_does_not_publish_qr(self):
        sharing = self.connected_tunnel()
        sharing.links[:] = [link for link in sharing.links if link['label'] != 'Internet']

        def probe(origin):
            self.assertTrue(sharing.valid_origin(urlsplit(origin).netloc, origin))
            self.assertFalse(sharing.is_local(urlsplit(origin).netloc, '127.0.0.1'))
            self.assertEqual([link['label'] for link in sharing.links], ['Same Wi-Fi'])

        with mock.patch.object(sharing, '_verify_tunnel', side_effect=probe):
            sharing._check_tunnel()
        self.assertEqual(sharing.links[0]['label'], 'Internet')

    def test_public_probe_checks_dns_and_exact_authenticated_session(self):
        sharing = PhoneShare(8765, 'token')
        response = mock.MagicMock()
        response.__enter__.return_value.read.return_value = b'{"token":"different-session"}'
        with mock.patch('harness.sharing.socket.getaddrinfo') as dns, \
                mock.patch('harness.sharing.urlopen', return_value=response) as fetch:
            with self.assertRaisesRegex(ValueError, 'this harness session'):
                sharing._verify_tunnel('https://connected.trycloudflare.com')
            dns.assert_called_once()
            request = fetch.call_args.args[0]
            self.assertEqual(request.full_url, 'https://connected.trycloudflare.com/api/bootstrap')
            self.assertEqual(request.get_header('X-page-token'), 'token')

    def test_monitor_replaces_failed_process_and_does_not_restart_after_close(self):
        sharing = self.connected_tunnel()
        sharing.health_changed.set()
        with mock.patch.object(sharing, '_check_tunnel', return_value=True), \
                mock.patch.object(sharing, '_stop_tunnel') as stop, \
                mock.patch.object(sharing, '_launch_tunnel', side_effect=sharing.halt.set) as launch:
            sharing._monitor_tunnel()
            stop.assert_called_once()
            launch.assert_called_once()
            launch.reset_mock()
            sharing._monitor_tunnel()
            launch.assert_not_called()

    def test_startup_without_a_connection_eventually_replaces_stalled_process(self):
        sharing = self.connected_tunnel()
        sharing.tunnel_url = None
        sharing.tunnel_started = 100
        with mock.patch('harness.sharing.time.monotonic', return_value=150):
            self.assertFalse(sharing._check_tunnel())
        with mock.patch('harness.sharing.time.monotonic', return_value=200), mock.patch('builtins.print'):
            for _ in range(sharing.FAILURE_LIMIT - 1):
                self.assertFalse(sharing._check_tunnel())
            self.assertTrue(sharing._check_tunnel())

    def test_new_hostname_gets_dns_propagation_grace_before_replacement(self):
        sharing = self.connected_tunnel()
        sharing.tunnel_verified = False
        sharing.tunnel_started = 100
        with mock.patch.object(sharing, '_verify_tunnel', side_effect=OSError('NXDOMAIN')), \
                mock.patch('harness.sharing.time.monotonic', return_value=150), mock.patch('builtins.print'):
            for _ in range(sharing.FAILURE_LIMIT + 1):
                self.assertFalse(sharing._check_tunnel())
        with mock.patch.object(sharing, '_verify_tunnel'):
            self.assertFalse(sharing._check_tunnel())
        self.assertTrue(sharing.tunnel_verified)

    def test_invalid_forwarded_urls_are_rejected(self):
        for url in ['file:///etc/passwd', 'https://user:pass@example.com',
                    'https://example.com/path', 'https://example.com/#secret']:
            with self.subTest(url=url), self.assertRaises(ValueError):
                PhoneShare(8765, 'token', public_url=url)

    def test_only_loopback_peer_and_local_host_are_trusted_without_token(self):
        sharing = PhoneShare(8765, 'token', ['192.168.1.23'])
        self.assertTrue(sharing.is_local('localhost:8765', '127.0.0.1'))
        self.assertFalse(sharing.is_local('localhost:8765', '192.168.1.24'))
        self.assertFalse(sharing.is_local('192.168.1.23:8765', '127.0.0.1'))
        self.assertFalse(sharing.authorized(None))
        self.assertFalse(sharing.authorized('wrong'))
        self.assertFalse(sharing.authorized('☃'))
        self.assertTrue(sharing.authorized('token'))


class StartupTests(unittest.TestCase):
    def run_server(self, options, failure=None):
        with mock.patch.multiple('harness.server', ThreadingHTTPServer=mock.DEFAULT,
                                 HarnessBridge=mock.DEFAULT, ContentsObserver=mock.DEFAULT,
                                 PhoneShare=mock.DEFAULT, lan_addresses=mock.DEFAULT) as mocks, \
                mock.patch('harness.server.signal.signal'), mock.patch('builtins.print'):
            server = mocks['ThreadingHTTPServer'].return_value
            server.server_port = 8765
            server.serve_forever.side_effect = KeyboardInterrupt
            sharing = mocks['PhoneShare'].return_value
            sharing.start_tunnel.side_effect = failure
            try:
                serve(**options)
            finally:
                sharing.close.assert_called_once()
                server.server_close.assert_called_once()
            return sharing, server

    def test_normal_startup_uses_cloudflare_and_explicit_modes_opt_out(self):
        for options, expected in [({}, True), ({'tunnel': True}, True),
                                  ({'tunnel': False}, False), ({'local_only': True}, False),
                                  ({'public_url': 'https://forwarded.example'}, False)]:
            with self.subTest(options=options):
                sharing, _ = self.run_server(options)
                self.assertEqual(sharing.start_tunnel.called, expected)

    def test_missing_cloudflared_keeps_default_server_available_with_explanation(self):
        sharing, server = self.run_server({}, RuntimeError('Install cloudflared first'))
        self.assertIn('Install cloudflared', sharing.status)
        server.serve_forever.assert_called_once()

    def test_explicit_tunnel_failure_is_reported(self):
        with self.assertRaisesRegex(RuntimeError, 'Install cloudflared'):
            self.run_server({'tunnel': True}, RuntimeError('Install cloudflared first'))

    def test_command_line_preserves_automatic_default_and_no_tunnel_option(self):
        for arguments, expected in [([], None), (['--tunnel'], True), (['--no-tunnel'], False)]:
            with self.subTest(arguments=arguments), mock.patch('harness.server.serve') as start:
                main(arguments)
                self.assertIs(start.call_args.args[3], expected)
