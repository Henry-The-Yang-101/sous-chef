"""Offline regression of actual Chrome playground observations. Never uses HTTP."""
import copy
import json
from pathlib import Path
import unittest

from harness.decision.compiler import Compiler, Clarification, build_context
from harness.decision.config import Settings
from harness.decision.protocol import DecisionError, validate_request, validate_response
from scripts.jev_playground import next_request, synthetic_world
import test_decision as support


EVIDENCE = json.loads((Path(__file__).resolve().parents[1] / 'docs/jev-playground-evaluation.json').read_text())
TRIALS = {trial['id']: trial for trial in EVIDENCE['trials']}


def session(case):
    return {**{k: copy.deepcopy(case[k]) for k in ('name', 'text', 'queue', 'utterance_fields', 'clarifications') if k in case},
            'responses': [{k: copy.deepcopy(TRIALS[key][k]) for k in ('request', 'response')} for key in case['trial_ids']]}


def tasks(result):
    return [(task['action'], task['arguments'], task['repeat']) for task in result['plan']['tasks']]


def finite(count):
    return {'mode': 'finite', 'count': count}


class PlaygroundReplayTests(unittest.IsolatedAsyncioTestCase):
    def test_actual_dialogue_responses_replay_through_the_service(self):
        expected = {'confirm': 'preview', 'cancel': 'cancelled', 'new': 'error', 'preparation': 'error',
                    'correct': 'clarification', 'separate': 'clarification', 'withdraw': 'cancelled', 'unclear': 'clarification'}
        for case in EVIDENCE['dialogue_probes']:
            name = case['name'].removeprefix('dialogue_')
            with self.subTest(case=name):
                fixture = support.ServiceTests()
                fixture.setUp()
                try:
                    fixture.t.script.steps = copy.deepcopy(case['initial_steps'])
                    fixture.service.submit({'id': 'original', 'text': case['original_utterance']})
                    fixture.wait_for({case['initial_status']})
                    recorded = TRIALS[case['trial_ids'][0]]
                    requests = []
                    async def observed(request):
                        requests.append(copy.deepcopy(request))
                        if len(requests) == 1:
                            return recorded['response']
                        # Only the dialogue decision was evaluated online; never
                        # fabricate provider evidence for subsequent compilation.
                        raise DecisionError('End of recorded dialogue probe')
                    fixture.t.script = observed
                    fixture.service.submit({'id': 'reply', 'text': case['text']})
                    result = fixture.wait_for({expected[name]})
                    self.assertEqual(requests[0], recorded['request'])
                    self.assertEqual(fixture.bridge.scheduler.snapshot()['pending'], [])
                    self.assertEqual(fixture.bridge.calls, [])
                    if name == 'new':
                        self.assertEqual(requests[1]['state']['utterance']['text'], case['text'])
                    elif name == 'preparation':
                        self.assertEqual(requests[1]['state']['compiler']['items'],
                                         [{'ingredient': 'carrot', 'preparation': 'chopped', 'quantity': 1}])
                    elif name in ('correct', 'separate', 'unclear'):
                        self.assertEqual(len(requests), 1)
                        self.assertTrue(result['result']['message'].startswith('Restate'))
                finally:
                    fixture.doCleanups()

    def test_all_recorded_wire_responses_match_their_exact_requests(self):
        for trial in TRIALS.values():
            with self.subTest(trial=trial['id']):
                validate_request(trial['request'])
                validate_response(trial['request'], trial['response'])

    async def test_final_cases_replay_with_exact_production_prompts(self):
        for case in EVIDENCE['frozen_holdout'] + EVIDENCE['final_tuning_cases']:
            with self.subTest(case=case['name']):
                # next_request refuses a replay if even one production request
                # differs. A missing recorded response returns "request".
                self.assertEqual(await next_request(session(case)), case['observed_outcome'])

    async def test_frozen_holdout_accepts_only_the_three_correct_complete_plans(self):
        expected = {
            'wash': [('wash_dishes', {}, finite(1))],
            'repeat': [('toss_burnt_food', {}, finite(2))],
            'unlimited': [('wash_dishes', {}, {'mode': 'until_cancelled'})],
        }
        blocked = {'concurrent': 'rejected', 'multi': 'clarification', 'negated': 'clarification',
                   'prepare': 'clarification', 'timed': 'clarification'}
        for case in EVIDENCE['frozen_holdout']:
            name = case['name'].removeprefix('holdout_final_')
            with self.subTest(case=name):
                result = await next_request(session(case))
                if name in expected:
                    self.assertEqual(result['status'], 'complete')
                    self.assertEqual(tasks(result['result']), expected[name])
                    self.assertEqual(result['result']['plan']['queue_edit'], 'append')
                else:
                    self.assertEqual(result['status'], blocked[name])
                    self.assertNotIn('result', result)

    async def test_complete_preparation_confirmation_preserves_exact_lists(self):
        expected = {
            'current_prepare': [('prepare_near_player', {'player_id': 1, 'items': [
                {'ingredient': 'carrot', 'preparation': 'chopped', 'quantity': 3}]}, finite(1))],
            'current_mixed': [('prepare_near_player', {'player_id': 1, 'items': [
                {'ingredient': 'onion', 'preparation': 'raw', 'quantity': 1},
                {'ingredient': 'carrot', 'preparation': 'chopped', 'quantity': 2}]}, finite(1)),
                ('wash_dishes', {}, finite(1))],
            'holdout_final_prepare': [('prepare_near_player', {'player_id': 3, 'items': [
                {'ingredient': 'onion', 'preparation': 'raw', 'quantity': 2}]}, finite(1))],
        }
        for case in EVIDENCE['frozen_holdout'] + EVIDENCE['final_tuning_cases']:
            if case['name'] not in expected:
                continue
            with self.subTest(case=case['name']):
                replay = session(case)
                pending = await next_request(replay)
                self.assertEqual(pending['status'], 'clarification')
                self.assertEqual(pending['stage'], 'coverage')
                # An explicit local button answer, not an invented provider
                # response. It binds only the displayed complete interpretation.
                replay['clarifications'] = [{'stage': pending['stage'], 'field': pending['field'],
                    'key': 'confirm_plan', 'question': pending['question'], 'answer': pending['options']['confirm_plan']}]
                accepted = await next_request(replay)
                self.assertEqual(accepted['status'], 'complete')
                self.assertEqual(tasks(accepted['result']), expected[case['name']])

    async def test_final_verifier_blocks_every_deliberately_wrong_plan(self):
        rejected = 0
        for case in EVIDENCE['final_verifier_probes']:
            trial = TRIALS[case['trial_ids'][0]]
            source = trial['request']['state']
            proposed = source['compiler']['proposed']
            async def ask(request):
                self.assertEqual(request, trial['request'])
                return trial['response']
            compiler = Compiler({'id': case['name'], 'text': case['text']},
                build_context(synthetic_world(), {'revision': 0, **source['scheduler']}), ask)
            compiler.tasks = copy.deepcopy(proposed.get('tasks', []))
            with self.subTest(case=case['name']):
                scores = trial['response']['answers']
                blocked = any(scores[field]['noul'] < Settings().coverage_probability for field in ('coverage', 'unambiguous'))
                if not case['expected_coverage']:
                    self.assertTrue(blocked)
                    rejected += 1
                if blocked:
                    with self.assertRaises(Clarification):
                        await compiler.coverage(proposed)
                else:
                    await compiler.coverage(proposed)
        self.assertEqual(rejected, 6)


if __name__ == '__main__':
    unittest.main()
