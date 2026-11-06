import asyncio
import copy
from dataclasses import replace
import json
from pathlib import Path
import tempfile
import threading
import time
import unittest
from unittest.mock import patch

from clients.capabilities import REGISTRY_HASH, registry
from harness.decision.compiler import Compiler, Clarification, Unsupported, build_context, number_candidates
from harness.decision.config import MODEL, Settings, load_settings
from harness.decision.protocol import DecisionError, validate_request, validate_response
from harness.decision.service import InterpreterService
from harness.decision.transport import DecisionClient, HttpTransport, ProviderError, retry_seconds
from harness.scheduler import Scheduler, context_of
from harness.voice.gate import DispatchGate
from test_scheduler import Native, plan, spec


def world():
    result = Native().state()
    result['capabilities']['semantic_catalog'] = {
        'players': [{'playerId': 1}, {'playerId': 3}],
        'targets': [{'id': 'd1', 'objectInstanceId': 20, 'label': 'Sink', 'possibleOperations': ['navigate_to', 'work_at']}],
        'displayOptions': {'preparationOptions': [
            {'id': 'carrot', 'label': 'Carrot', 'preparations': {'raw': 'applicable', 'chopped': 'applicable', 'empty': 'not_applicable'}},
            {'id': 'onion', 'label': 'Onion', 'preparations': {'raw': 'applicable', 'chopped': 'applicable'}},
            {'id': 'rice', 'label': 'Rice', 'preparations': {'raw': 'applicable', 'cooked': 'applicable'}},
        ]}}
    return result


def response(request, selections=None):
    selections = selections or {}
    answers = {}
    for key, question in request['questions'].items():
        if question['type'] == 'noul':
            default = 0.0 if key in ('concurrent_duties', 'external_deadline', 'region_goal') else 1.0
            answers[key] = {'type': 'noul', 'noul': selections.get(key, default)}
        else:
            default = {'route': 'task_plan', 'queue_edit': 'append', 'simple': 'staged',
                       'repeat': 'count_1', 'recipient': 'player_1', 'coverage': 'equivalent'}.get(key, next(iter(question['criteria'])))
            value = selections.get(key, default)
            if key == 'coverage' and isinstance(value, (int, float)):
                value = ('equivalent', value)
            probability = 1.0
            if isinstance(value, tuple): value, probability = value
            others = max(1, len(question['criteria']) - 1)
            answers[key] = {'type': 'choice', 'choice': value, 'confidence': probability,
                'probabilities': {option: probability if option == value else (1-probability)/others for option in question['criteria']}}
    return {'model': MODEL, 'answers': answers, 'usage': {'input_tokens': 100, 'output_tokens': 10}}


class Script:
    def __init__(self, steps): self.steps, self.requests = list(steps), []
    async def __call__(self, request):
        self.requests.append(copy.deepcopy(request))
        if not self.steps: raise AssertionError('Unexpected model request: ' + str(request['questions'].keys()))
        step = self.steps.pop(0)
        if isinstance(step, BaseException): raise step
        return response(request, step)


def compiler(text, steps, context=None):
    script = Script(steps)
    queue = {'revision': 0, 'active': None, 'pending': []}
    value = Compiler({'id': 'command', 'text': text}, context or build_context(world(), queue), script)
    return value, script


class CompilerTests(unittest.IsolatedAsyncioTestCase):
    async def test_simple_candidate_covers_whole_command(self):
        c, s = compiler('Wash dishes', [{'simple': 'wash_dishes_once'}, {}])
        result = await c.compile()
        self.assertEqual(result['plan']['tasks'], [spec()])
        self.assertEqual(len(s.requests), 2)
        self.assertEqual(result['plan']['queue_edit'], 'append')

    async def test_repeated_tasks_are_occurrences_not_a_set(self):
        c, _ = compiler('Wash, take ready food, then wash again', [
            {'simple_coverage': 0}, {'next_task': 'wash_dishes'}, {},
            {'next_task': 'take_ready'}, {}, {'next_task': 'wash_dishes'}, {}, {'next_task': 'done'}, {}])
        result = await c.compile()
        self.assertEqual([t['action'] for t in result['plan']['tasks']], ['wash_dishes', 'take_ready', 'wash_dishes'])

    async def test_repeated_ingredient_different_preparation_and_corrected_quantity(self):
        c, script = compiler('Give me one raw carrot and two, actually three chopped carrots', [
            {'simple_coverage': 0}, {'next_task': 'prepare_near_player'},
            {'ingredient': 'ingredient_carrot'}, {'preparation': 'raw'}, {'quantity': 'count_1'},
            {'ingredient': 'ingredient_carrot'}, {'preparation': 'chopped'}, {'quantity': 'count_3'},
            {'ingredient': 'done'}, {}, {}, {'next_task': 'done'}, {}])
        items = (await c.compile())['plan']['tasks'][0]['arguments']['items']
        self.assertEqual(items, [{'ingredient': 'carrot', 'preparation': 'raw', 'quantity': 1},
                                 {'ingredient': 'carrot', 'preparation': 'chopped', 'quantity': 3}])
        quantities = [request for request in script.requests if 'quantity' in request['questions']]
        self.assertEqual([request['state']['compiler']['resolved_preparation'] for request in quantities], ['raw', 'chopped'])
        self.assertTrue(all(set(request['questions']) == {'quantity'} for request in quantities))

    async def test_clarification_preserves_draft_and_cached_decisions(self):
        c, s = compiler('Give me two chopped carrots and one onion, then wash', [
            {'simple_coverage': 0}, {'next_task': 'prepare_near_player'},
            {'ingredient': 'ingredient_carrot'}, {'preparation': 'chopped'}, {'quantity': 'count_2'},
            {'ingredient': 'ingredient_onion'}, {'preparation': 'unspecified'}, {'quantity': 'count_1'},
            {'ingredient': 'done'}, {}, {}, {'next_task': 'wash_dishes'}, {}, {'next_task': 'done'}, {}])
        with self.assertRaises(Clarification) as caught: await c.compile()
        self.assertEqual(c.tasks, [])
        self.assertEqual(len(s.requests), 7)
        c.resolve(caught.exception, 'chopped')
        result = await c.compile()
        self.assertEqual(len(s.requests), 15)
        self.assertEqual(result['plan']['tasks'][0]['arguments']['items'][1]['preparation'], 'chopped')
        self.assertEqual(s.requests[-1]['state']['compiler']['clarifications'][0]['answer'], 'Chopped Onion.')

    async def test_unlimited_tail_is_finite_compilation(self):
        c, s = compiler('Wash then keep monitoring equipment', [{'simple_coverage': 0},
            {'next_task': 'wash_dishes'}, {}, {'next_task': 'monitor_equipment'}, {'repeat': 'until_cancelled'},
            {'next_task': 'done'}, {}])
        result = await c.compile()
        self.assertEqual(result['plan']['tasks'][-1]['repeat'], {'mode': 'until_cancelled'})
        self.assertEqual(len(s.requests), 7)

    async def test_unlimited_before_later_work_is_not_accepted(self):
        c, _ = compiler('Keep washing then take ready food', [{'simple_coverage': 0},
            {'next_task': 'wash_dishes'}, {'repeat': 'until_cancelled'}, {'next_task': 'take_ready'}])
        with self.assertRaisesRegex(Clarification, 'Unlimited'): await c.compile()

    async def test_fifth_occurrence_allows_done_sixth_rejected(self):
        for next_task in ('done', 'wash_dishes'):
            steps = [{'simple_coverage': 0}] + [{'next_task': 'wash_dishes'}, {}] * 5 + [{'next_task': next_task}, {}]
            c, _ = compiler('Wash five separate times', steps)
            if next_task == 'done': self.assertEqual(len((await c.compile())['plan']['tasks']), 5)
            else:
                with self.assertRaises(Clarification): await c.compile()

    async def test_low_confidence_done_and_failed_coverage_never_truncate(self):
        for final in ({'next_task': ('done', .84)}, {'next_task': 'done'}):
            c, _ = compiler('Wash and more work', [{'simple_coverage': 0}, {'next_task': 'wash_dishes'}, {}, final, {'coverage': 'different'}])
            with self.assertRaises(Clarification): await c.compile()

    async def test_missing_simple_candidate_falls_back_to_full_registry(self):
        c, _ = compiler('Pass the held item to player one', [{'simple_coverage': 0}, {'next_task': 'native_action'}, {'action':'throw_to_player'},
            {'player_id': 'value_0'}, {}, {'next_task': 'done'}, {}])
        self.assertEqual((await c.compile())['plan']['tasks'][0]['action'], 'throw_to_player')

    async def test_out_of_range_not_clamped_and_impossible_preparation_not_substituted(self):
        c, _ = compiler('Wash twelve batches', [{'simple_coverage': 0}, {'next_task': 'wash_dishes'}, {'repeat': 'out_of_range'}])
        with self.assertRaises(Unsupported): await c.compile()
        c, _ = compiler('Give me an empty carrot', [{'simple_coverage': 0}, {'next_task': 'prepare_near_player'},
            {'ingredient': 'ingredient_carrot'}, {'preparation': 'empty'}, {'quantity': 'count_1'}])
        with self.assertRaises(Unsupported): await c.compile()

    async def test_assembly_and_unimplemented_work_have_no_partial_result(self):
        c, _ = compiler('Wash then make a pizza', [{'simple_coverage': 0}, {'next_task': 'wash_dishes'}, {}, {'next_task': 'assemble_dish'}])
        with self.assertRaisesRegex(Unsupported, 'allocation'): await c.compile()
        c, _ = compiler('Wash while monitoring for thirty seconds', [{'concurrent_duties': 1}])
        with self.assertRaises(Unsupported): await c.compile()

    async def test_queue_only_remove_binds_local_identity(self):
        ctx = build_context(world(), {'revision': 4, 'pending': [{'id': 'queued', 'spec': spec(), 'state': 'pending'}]})
        c, s = compiler('Remove the first pending task', [{'route': 'queue_only', 'queue_edit': 'remove_pending'},
            {'target': 'pending_1'}, {}], ctx)
        result = await c.compile()
        self.assertEqual(result['edit']['task_id'], 'queued')
        self.assertEqual(result['edit']['expected_queue_revision'], 4)
        self.assertNotIn('next_task', str(s.requests))
        self.assertIn('Remove pending task 1: Wash dirty dishes', result['message'])
        self.assertEqual(s.requests[0]['state']['scheduler']['pending_task_count'], 1)
        self.assertNotIn('pending', s.requests[0]['state']['scheduler'])
        self.assertEqual(s.requests[1]['state']['scheduler']['pending'][0]['id'], 'queued')

    async def test_constraint_vetoes_do_not_replace_whole_plan_verification(self):
        for field in ('concurrent_duties', 'external_deadline', 'region_goal'):
            with self.subTest(field=field):
                c, script = compiler('A request with an unsupported constraint', [{field: .85}])
                with self.assertRaises(Unsupported): await c.compile()
                self.assertEqual(len(script.requests), 1)
        c, script = compiler('Wash dishes for a minute', [
            {'simple': 'wash_dishes_once', 'external_deadline': .8}, {'coverage': 'different'}])
        with self.assertRaises(Clarification): await c.compile()
        self.assertEqual(len(script.requests), 2)

    async def test_native_verification_includes_the_selected_target_meaning(self):
        c, script = compiler('Go stand at the sink', [{'simple_coverage': 0}, {'next_task': 'native_action'},
            {'action': 'navigate_to'}, {'destination_id': 'value_0'}, {}, {'next_task': 'done'}, {}])
        result = await c.compile()
        self.assertEqual(result['plan']['after_plan'], 'pause_pending')
        self.assertEqual(script.requests[-1]['state']['world']['targets'][0]['label'], 'Sink')

    async def test_uncertain_removal_confirmation_names_the_exact_pending_task(self):
        ctx = build_context(world(), {'revision': 4, 'pending': [
            {'id': 'first', 'spec': spec(), 'state': 'pending'},
            {'id': 'second', 'spec': spec('take_ready'), 'state': 'pending'}]})
        c, _ = compiler('Remove the second pending task', [{'route': 'queue_only', 'queue_edit': 'remove_pending'},
            {'target': 'pending_2'}, {'coverage': .6}], ctx)
        with self.assertRaises(Clarification) as caught: await c.compile()
        self.assertIn('Remove pending task 2: Take ready food off heat', caught.exception.options['confirm_plan'])
        c.resolve(caught.exception, 'confirm_plan')
        self.assertEqual((await c.compile())['edit']['task_id'], 'second')

    async def test_replacement_requires_explicit_scope(self):
        c, _ = compiler('Do washing instead', [{'simple': 'wash_dishes_once', 'queue_edit': 'replace_all', 'deletion': .1}])
        with self.assertRaisesRegex(Clarification, 'Confirm'): await c.compile()

    async def test_negated_and_unrelated_commands_do_nothing(self):
        c, s = compiler("Don't stop washing", [{'route': 'no_change'}])
        self.assertEqual((await c.compile())['kind'], 'no_change')
        self.assertEqual(len(s.requests), 1)

    async def test_full_plan_confirmation_is_exact_and_does_not_repeat_model_io(self):
        c, s = compiler('Wash dishes', [{'simple':'wash_dishes_once'}, {'coverage':.6}])
        with self.assertRaises(Clarification) as caught: await c.compile()
        self.assertEqual(list(caught.exception.options), ['confirm_plan'])
        c.resolve(caught.exception, 'confirm_plan')
        self.assertEqual((await c.compile())['kind'], 'plan')
        self.assertEqual(len(s.requests), 2)
        self.assertNotIn('command_id', s.requests[-1]['state']['compiler']['proposed'])

    async def test_ten_items_can_finish_but_eleventh_is_not_truncated(self):
        for ending in ('done','ingredient_carrot'):
            steps = [{'simple_coverage':0},{'next_task':'prepare_near_player'}]
            steps += [{'ingredient':'ingredient_carrot'},{'preparation':'chopped'},{'quantity':'count_1'}]*10
            steps += [{'ingredient':ending},{},{},{'next_task':'done'},{}]
            c,_ = compiler('Give me ten separately described carrot entries',steps)
            if ending=='done': self.assertEqual(len((await c.compile())['plan']['tasks'][0]['arguments']['items']),10)
            else:
                with self.assertRaises(Clarification): await c.compile()


class ProtocolTests(unittest.TestCase):
    def setUp(self):
        self.request = {'model': MODEL, 'state': {'text': 'wash'}, 'questions': {
            'x': {'type': 'choice', 'instructions': 'Choose', 'criteria': {'a': 'a', 'b': 'b'}},
            'yes': {'type': 'noul', 'instructions': 'Yes?'}}}

    def test_malformed_or_drifting_responses_fail_closed(self):
        good = response(self.request, {'x': 'a'})
        for mutate in [lambda r:r.update(model='jev-latest'), lambda r:r['answers'].pop('yes'),
                       lambda r:r.pop('usage'), lambda r:r['answers']['x'].update(choice='evil'),
                       lambda r:r['answers']['x'].update(confidence=float('nan')),
                       lambda r:r['answers']['yes'].update(noul=True),
                       lambda r:r['answers']['x']['probabilities'].pop('b'),
                       lambda r:r['answers']['x']['probabilities'].update(b=.5),
                       lambda r:r['answers']['x'].update(choice='b')]:
            value = copy.deepcopy(good); mutate(value)
            with self.assertRaises(DecisionError): validate_response(self.request, value)

    def test_context_and_primitive_limits(self):
        validate_request(self.request)
        for request in [dict(self.request, model='jev-latest'), dict(self.request, state='x'*25000),
                        dict(self.request, assets={}), dict(self.request, questions={'x': {'type':'box','instructions':'Find'}})]:
            with self.assertRaises(DecisionError): validate_request(request)

    def test_env_is_literal_private_and_environment_wins(self):
        with tempfile.TemporaryDirectory() as d:
            path = Path(d)/'.env'; path.write_text("JEV_API_KEY='literal$(echo-no)'\nIGNORED=x\n")
            self.assertEqual(load_settings(path, {}).api_key, 'literal$(echo-no)')
            self.assertEqual(load_settings(path, {'JEV_API_KEY': 'environment'}).api_key, 'environment')
            self.assertEqual(load_settings(path, {'TYPESAFE_API_KEY': 'environment-alias'}).api_key, 'environment-alias')
            self.assertNotIn('literal', repr(load_settings(path, {})))
        self.assertEqual(number_candidates('twenty-one carrots, actually 3; twice'), {21:['twenty-one'], 3:['3'], 2:['twice']})
        self.assertEqual(number_candidates('give player one three carrots'), {1:['one'], 3:['three']})
        self.assertEqual(number_candidates('one hundred and two, two thousand five, minus three'),
                         {102:['one hundred and two'], 2005:['two thousand five'], -3:['minus three']})


class Transport:
    def __init__(self, steps=()):
        self.script = Script(steps)
        self.sent = 0
        self.before_send = self.after_send = None
    async def exchange(self, request, send):
        if self.before_send: await self.before_send.wait()
        send(lambda: setattr(self, 'sent', self.sent + 1))
        if self.after_send: await self.after_send.wait()
        return await self.script(request)
    async def close(self): pass


class TransportTests(unittest.IsolatedAsyncioTestCase):
    async def test_stale_command_is_checked_at_actual_write(self):
        gate = DispatchGate(); generation = gate.invalidate(False)
        t = Transport([{}]); t.before_send=asyncio.Event()
        p=ProtocolTests();p.setUp();current=True
        def check():
            if not current: raise DecisionError('Queue changed')
        budget=dict(deadline=time.monotonic()+2,requests=0,input_tokens=0,output_tokens=0)
        task=asyncio.create_task(DecisionClient(t,gate,Settings()).ask(p.request,generation,budget,check))
        await asyncio.sleep(.01);current=False;t.before_send.set()
        with self.assertRaisesRegex(DecisionError,'Queue changed'):await task
        self.assertEqual(t.sent,0)

    async def test_http_framing_and_keepalive_without_network(self):
        p=ProtocolTests();p.setUp();body=json.dumps(response(p.request)).encode()
        class Writer:
            def __init__(self):self.writes=[];self.closed=False
            def write(self,value):self.writes.append(value)
            def is_closing(self):return self.closed
            async def drain(self):pass
            def close(self):self.closed=True
            async def wait_closed(self):pass
        reader=asyncio.StreamReader();writer=Writer()
        reader.feed_data(b'HTTP/1.1 200 OK\r\nContent-Length: '+str(len(body)).encode()+b'\r\n\r\n'+body)
        reader.feed_data(b'HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n'+f'{len(body):x}'.encode()+b'\r\n'+body+b'\r\n0\r\n\r\n')
        with patch('asyncio.open_connection',return_value=(reader,writer)) as connect:
            transport=HttpTransport('test-only-not-a-key')
            for _ in range(2):
                result=await transport.exchange(p.request,lambda send:send())
                validate_response(p.request,result)
            self.assertEqual(connect.call_count,1)
            self.assertEqual(len(writer.writes),2)
            self.assertTrue(all(b'Danger-Zone' not in payload for payload in writer.writes))
            await transport.close()

    async def test_transient_retry_succeeds_and_counts_attempts(self):
        gate=DispatchGate();generation=gate.invalidate(False)
        t=Transport([ProviderError(503),{}]);p=ProtocolTests();p.setUp()
        budget=dict(deadline=time.monotonic()+2,requests=0,input_tokens=0,output_tokens=0)
        await DecisionClient(t,gate,Settings()).ask(p.request,generation,budget)
        self.assertEqual(t.sent,2);self.assertEqual(budget['requests'],2)
        self.assertEqual(budget['input_tokens'],100)
    async def test_mute_before_actual_send_blocks_deferred_transport(self):
        gate = DispatchGate(); generation = gate.invalidate(False)
        t = Transport([{}]); t.before_send = asyncio.Event()
        client = DecisionClient(t, gate, Settings())
        budget = dict(deadline=time.monotonic()+2, requests=0, input_tokens=0, output_tokens=0)
        request = ProtocolTests(); request.setUp()
        task = asyncio.create_task(client.ask(request.request, generation, budget))
        await asyncio.sleep(.01); gate.invalidate(True); t.before_send.set()
        with self.assertRaises(ValueError): await task
        self.assertEqual(t.sent, 0)

    async def test_mute_during_backoff_prevents_retry(self):
        gate = DispatchGate(); generation = gate.invalidate(False)
        t = Transport([ProviderError(429), {}])
        client = DecisionClient(t, gate, Settings())
        p = ProtocolTests(); p.setUp()
        budget = dict(deadline=time.monotonic()+2, requests=0, input_tokens=0, output_tokens=0)
        task = asyncio.create_task(client.ask(p.request, generation, budget))
        await asyncio.sleep(.04); gate.invalidate(True)
        with self.assertRaises(ValueError): await task
        self.assertEqual(t.sent, 1)

    async def test_auth_not_retried_and_budget_bounded(self):
        for error, settings, expected in [(ProviderError(401), Settings(), 1),
                                         (ProviderError(429, 1000), Settings(), 1),
                                         (ProviderError(503), replace(Settings(), retries=0), 1)]:
            gate = DispatchGate(); generation = gate.invalidate(False)
            t = Transport([error]); p = ProtocolTests(); p.setUp()
            budget = dict(deadline=time.monotonic()+2, requests=0, input_tokens=0, output_tokens=0)
            with self.assertRaises(DecisionError): await DecisionClient(t,gate,settings).ask(p.request,generation,budget)
            self.assertEqual(t.sent, expected)


class Bridge(Native):
    def __init__(self):
        super().__init__(); self.world = world(); self.scheduler = Scheduler(self)
    def observation(self): return copy.deepcopy(self.world)


class ServiceTests(unittest.TestCase):
    def setUp(self):
        self.bridge = Bridge(); self.gate = DispatchGate()
        self.t = Transport([{'simple': 'wash_dishes_once'}, {}])
        self.service = InterpreterService(self.bridge, self.gate, transport=self.t)
        self.gate.invalidate(False)
        self.addCleanup(self.bridge.scheduler.close)
        self.addCleanup(self.service.close)

    def wait_for(self, statuses):
        end = time.monotonic()+2
        while time.monotonic() < end:
            snapshot = self.service.snapshot()
            if snapshot['status'] in statuses: return snapshot
            time.sleep(.005)
        self.fail('Interpreter did not settle: '+str(snapshot))

    def control(self, operation, **fields):
        return self.service.control({'operation':operation, 'generation':self.gate.generation, **fields})

    def test_preview_cannot_execute_until_explicit_acceptance_and_no_auto_focus(self):
        self.service.submit({'id':'test', 'text':'Wash'})
        state = self.wait_for({'preview'})
        self.assertEqual(self.bridge.scheduler.snapshot()['pending'], [])
        self.control('accept', id=state['pending']['id'])
        self.assertEqual(len(self.bridge.scheduler.snapshot()['pending']), 1)
        self.assertEqual(self.bridge.calls, [])

    def test_manual_edit_and_round_change_defeat_preview(self):
        for mutation in ('queue', 'epoch'):
            self.t.script.steps = [{'simple':'wash_dishes_once'}, {}]
            self.service.submit({'id':mutation, 'text':'Wash'})
            state = self.wait_for({'preview'})
            if mutation == 'queue':
                self.bridge.scheduler.edit({'operation_id':'edit', 'expected_queue_revision':0, 'operation':'pause_pending'})
            else: self.bridge.world['control']['kitchenEpoch'] = 'new'
            with self.assertRaises(ValueError): self.control('accept', id=state['pending']['id'])
            self.assertEqual(self.service.snapshot()['status'], 'expired')
            self.assertEqual(self.bridge.scheduler.snapshot()['pending'], [])

    def test_mute_invalidates_preview_and_typed_input_cannot_bypass_it(self):
        self.service.submit({'id':'test','text':'Wash'})
        self.wait_for({'preview'}); self.gate.invalidate(True)
        self.assertIsNone(self.service.snapshot()['pending'])
        with self.assertRaises(ValueError): self.service.submit({'text':'Wash'})
        self.assertEqual(self.bridge.scheduler.snapshot()['pending'], [])

    def test_inbox_order_dedup_and_capacity(self):
        self.service.submit({'id':'one','text':'Wash'}); self.wait_for({'preview'})
        self.t.script.steps = [{'relationship':'separate'}] * 3
        for i in range(3): self.service.submit({'id':f'next{i}','text':'Wash'})
        self.service.submit({'id':'one','text':'Wash'})
        with self.assertRaises(ValueError): self.service.submit({'id':'overflow','text':'Wash'})
        with self.assertRaises(ValueError): self.service.submit({'id':'one','text':'Different'})
        self.assertEqual([c['id'] for c in self.service.inbox], ['next0','next1','next2'])

    def test_automatic_acceptance_is_opt_in_and_existing_unlimited_vetoes_append(self):
        self.bridge.scheduler.accept(plan(tasks=[spec(count=None)]), context_of(self.bridge.state()))
        self.control('mode', mode='automatic')
        self.service.submit({'id':'test','text':'Wash'})
        state = self.wait_for({'clarification'})
        self.assertIn('Unlimited', state['result']['message'])
        self.assertEqual(len(self.bridge.scheduler.snapshot()['pending']), 1)

    def test_preview_expiration(self):
        self.service.submit({'id':'test','text':'Wash'}); self.wait_for({'preview'})
        self.service.pending['expires'] = time.monotonic()-1
        self.assertEqual(self.service.snapshot()['status'], 'expired')
        self.assertEqual(self.bridge.scheduler.snapshot()['pending'], [])

    def test_exact_target_identity_not_frame_motion(self):
        before = world()['capabilities']['semantic_catalog']; after = copy.deepcopy(before)
        raw = plan(tasks=[dict(spec('navigate_to', arguments={'destination_id':'d1'}), kind='native_action')])
        after['targets'][0]['position'] = [2,0,3]
        InterpreterService._exact_bindings(before,after,raw)
        after['targets'][0]['objectInstanceId'] += 1
        with self.assertRaises(DecisionError): InterpreterService._exact_bindings(before,after,raw)

    def test_late_response_after_mute_never_accepts(self):
        self.t.after_send=asyncio.Event();self.control('mode',mode='automatic')
        self.service.submit({'id':'late','text':'Wash'})
        end=time.monotonic()+1
        while not self.t.sent and time.monotonic()<end:time.sleep(.005)
        self.gate.invalidate(True)
        self.service.loop.call_soon_threadsafe(self.t.after_send.set)
        time.sleep(.02)
        self.assertEqual(self.bridge.scheduler.snapshot()['pending'],[])
        self.assertEqual(self.t.sent,1)

    def test_queue_change_while_connecting_prevents_transmission(self):
        self.t.before_send=asyncio.Event()
        self.service.submit({'id':'stale','text':'Wash'})
        self.bridge.scheduler.edit({'operation_id':'manual','expected_queue_revision':0,'operation':'pause_pending'})
        self.service.loop.call_soon_threadsafe(self.t.before_send.set)
        self.wait_for({'error'})
        self.assertEqual(self.t.sent,0)

    def test_clarification_button_resumes_only_current_draft(self):
        self.t.script.steps=[{'simple':'wash_dishes_once'},{'coverage':.6}]
        self.service.submit({'id':'clarify','text':'Wash'})
        state=self.wait_for({'clarification'})
        self.assertEqual(self.bridge.scheduler.snapshot()['pending'],[])
        self.control('answer',id=state['pending']['id'],key='confirm_plan')
        preview=self.wait_for({'preview'})
        self.assertEqual(self.t.sent,2)
        self.control('accept',id=preview['pending']['id'])
        self.assertEqual(len(self.bridge.scheduler.snapshot()['pending']),1)

    def test_spoken_reply_can_cancel_an_unfinished_command(self):
        self.t.script.steps=[{'simple':'wash_dishes_once'},{'coverage':.6},{'route':'cancel','answer':'ambiguous'}]
        self.service.submit({'id':'one','text':'Wash'})
        self.wait_for({'clarification'})
        self.service.submit({'id':'reply','text':'Cancel this unfinished command'})
        self.wait_for({'cancelled'})
        self.assertEqual(self.bridge.scheduler.snapshot()['pending'],[])

    def test_followup_correction_replaces_unfinished_preview(self):
        self.service.submit({'id':'one','text':'Wash'})
        self.wait_for({'preview'})
        self.t.script.steps=[{'relationship':'correction'},{'simple':'take_ready_once'},{}]
        self.service.submit({'id':'correction','text':'Actually take ready food instead'})
        state=self.wait_for({'preview'})
        self.assertEqual(state['result']['plan']['tasks'][0]['action'],'take_ready')
        self.assertEqual(self.bridge.scheduler.snapshot()['pending'],[])

    def test_invalid_configuration_does_not_disable_manual_queue(self):
        with patch('harness.decision.service.load_settings',side_effect=ValueError('bad config')):
            service=InterpreterService(self.bridge,DispatchGate())
        self.addCleanup(service.close)
        self.assertFalse(service.snapshot()['configured'])
        self.assertIn('configuration',service.snapshot()['configurationError'])
        self.bridge.scheduler.accept(plan(),context_of(self.bridge.state()))
        self.assertEqual(len(self.bridge.scheduler.snapshot()['pending']),1)


if __name__ == '__main__': unittest.main()
