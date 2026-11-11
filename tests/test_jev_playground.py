"""Historical wire validation and scripted decision regressions. Never uses HTTP.

Old responses are mock decisions for current control flow, NOT measurements of
the revised prompts. Production playground replay still rejects changed requests.
"""
import copy
import gzip
import hashlib
import json
from pathlib import Path
import unittest

from clients.capabilities import REGISTRY_HASH, contract
from harness.decision.compiler import Compiler, Clarification, build_context
from harness.decision.config import Settings
from harness.decision.prompts import TASK_BOUNDARIES, task_description
from harness.decision.protocol import DecisionError, validate_request, validate_response
from scripts.jev_evaluate import audit_case, score_outcome, semantic_result
from scripts.jev_playground import next_request
import test_decision as support

ROOT = Path(__file__).resolve().parents[1]
with gzip.open(ROOT / 'tests/fixtures/jev-playground-replay.json.gz', 'rt', encoding='utf-8') as file:
    REPLAY = json.load(file)
PREVIOUS_TRIALS = {trial['id']: trial for trial in REPLAY['previous_trials']}
TRIALS = {trial['id']: trial for trial in REPLAY['trials']}
CASES = {case['name']: case for case in REPLAY['sessions']}
RELEASE = [case for case in CASES.values() if case.get('variant') == 'nativerelease' and case['type'] == 'command']


def current_world(world):
    value = copy.deepcopy(world)
    value['capabilities']['registry_hash'] = REGISTRY_HASH
    return value


def session(case):
    result = {**{k: copy.deepcopy(case[k]) for k in ('name', 'text', 'world', 'queue', 'utterance_fields', 'clarifications') if k in case},
            'responses': [{k: copy.deepcopy(TRIALS[key][k]) for k in ('request', 'response')} for key in case['trial_ids']]}
    result['world'] = current_world(result['world'])
    return result


async def scripted_replay(recorded):
    """Rebuild current requests and inject historical choices as explicit mocks."""
    current = {**copy.deepcopy(recorded), 'responses': []}
    for item in recorded['responses']:
        pending = await next_request(current, settings=support.PLAYGROUND_SETTINGS)
        if pending['status'] != 'request':
            # Exact user confirmation can finish without consuming the old
            # verifier decision, as production replay also permits.
            return pending
        request = pending['request']
        response = copy.deepcopy(item['response'])
        if 'region_goal' in response['answers']:
            response['answers']['persistent_area'] = response['answers'].pop('region_goal')
        validate_response(request, response)
        current['responses'].append({'request': request, 'response': response})
    return await next_request(current, settings=support.PLAYGROUND_SETTINGS)


class PlaygroundReplayTests(unittest.IsolatedAsyncioTestCase):
    def test_current_and_historical_wire_observations_validate(self):
        for trial in [*TRIALS.values(), *REPLAY['previous_trials'], *REPLAY['historical_trials']]:
            with self.subTest(trial=trial['id']):
                validate_request(trial['request'])
                validate_response(trial['request'], trial['response'])

    async def test_production_replay_refuses_previous_prompt_responses(self):
        outcome = await next_request(session(RELEASE[0]))
        self.assertEqual(outcome['status'], 'rejected')
        self.assertIn('Prompt changed', outcome['message'])

    async def test_48_historical_nonmovement_sequences_preserve_control_flow(self):
        current = [c for c in CASES.values() if c.get('variant') in ('release', 'nativerelease') and c['type'] == 'command' and not c['name'].endswith(('_region', '_navigate'))]
        self.assertEqual(len(current), 48)
        task_descriptions = {name: task_description(contract(name)) for name in TASK_BOUNDARIES}
        for case in current:
            with self.subTest(case=case['name']):
                # These are scripted decisions, not fresh model observations.
                recorded = session(case)
                outcome = await scripted_replay(recorded)
                expected = case['outcome']
                if expected.get('field') == 'next_task':
                    # Keep the recorded choice keys and use current task labels.
                    expected = dict(expected, options={key: task_descriptions.get(key, label)
                        for key, label in expected['options'].items()})
                if case['name'] in ('release_holdout_sequential', 'nativerelease_holdout_sequential'):
                    # The recording's tentative "done" omitted serving. A
                    # focused completeness check must keep the draft on hold.
                    self.assertEqual(outcome['status'], 'request')
                    request = outcome['request']
                    self.assertEqual(set(request['questions']), {'missing_task'})
                    recorded['responses'].append({'request': request,
                                                  'response': support.response(request, {'missing_task': 1})})
                    blocked = await scripted_replay(recorded)
                    self.assertEqual(blocked, expected)
                    continue
                if case['name'] == 'release_holdout_preparation_missing':
                    expected = dict(expected, options={'raw': 'Raw Cucumber.', 'chopped': 'Chopped Cucumber.'})
                self.assertEqual(outcome, expected)
                self.assertEqual(score_outcome(case['expected'], outcome), case['score'])
                self.assertNotIn(case['score'], ('incomplete', 'incorrect_completion', 'unexpected_rejection'))

    async def test_historical_choices_preserve_every_executable_field(self):
        completed = set()
        blocked = set()
        for case in RELEASE:
            if case['cohort'] != 'holdout' or case['name'].endswith('_region'):
                continue
            outcome = await scripted_replay(session(case))
            name = case['name'].removeprefix('nativerelease_holdout_')
            if outcome['status'] == 'complete':
                self.assertEqual(semantic_result(outcome['result']), case['expected'])
                completed.add(name)
            elif case['expected']['kind'] in ('rejected', 'clarification'):
                self.assertIn(outcome['status'], ('clarification', 'rejected'))
                blocked.add(name)
        self.assertEqual(completed, {'cooked', 'correction', 'fire', 'monitor', 'pause', 'raw_player4', 'ready', 'repeat',
                                         'repeated_tasks', 'same_ingredient', 'serve', 'wash'})
        self.assertEqual(blocked, {'timed', 'concurrent', 'unavailable', 'out_of_range', 'speech_conflict',
                                  'preparation_missing', 'invalid_preparation'})

    async def test_complete_plan_confirmation_keeps_exact_fields_without_more_model_io(self):
        confirmations = 0
        for case in RELEASE:
            if case['outcome'].get('stage') != 'coverage':
                continue
            replay = session(case)
            pending = await scripted_replay(replay)
            replay['clarifications'] = [{'stage': pending['stage'], 'field': pending['field'],
                'key': 'confirm_plan', 'question': pending['question'], 'answer': pending['options']['confirm_plan']}]
            accepted = await scripted_replay(replay)
            self.assertEqual(accepted['status'], 'complete')
            self.assertEqual(semantic_result(accepted['result']), case['expected'])
            confirmations += 1
        self.assertEqual(confirmations, 1)

    async def test_verifier_gates_block_16_historical_negative_decisions(self):
        for settings, expected_controls in ((support.PLAYGROUND_SETTINGS, 4), (Settings(), 7)):
            with self.subTest(coverage_probability=settings.coverage_probability):
                wrong, accepted_controls = 0, 0
                for case in CASES.values():
                    if case['type'] != 'final_verifier':
                        continue
                    trial = TRIALS[case['trial_ids'][0]]
                    async def observed(request):
                        return validate_response(request, trial['response'])
                    compiler = Compiler({'id': case['name'], 'text': case['text'], **case['utterance_fields']},
                                        build_context(current_world(case['world']), case['queue']), observed, settings)
                    compiler.tasks = copy.deepcopy(case['proposed'].get('tasks', []))
                    blocked = False
                    try:
                        await compiler.coverage(case['proposed'])
                    except Clarification:
                        blocked = True
                    if not case['expected_match']:
                        self.assertTrue(blocked, case['name'])
                        wrong += 1
                    elif not blocked:
                        accepted_controls += 1
                self.assertEqual(wrong, 16)
                self.assertEqual(accepted_controls, expected_controls)

    def test_historical_dialogue_choices_exercise_service_control_flow(self):
        for case in REPLAY['previous_dialogues']:
            with self.subTest(case=case['name']):
                fixture = support.ServiceTests()
                fixture.setUp(settings=support.PLAYGROUND_SETTINGS)
                try:
                    fixture.t.script.steps = copy.deepcopy(case['initial_steps'])
                    fixture.service.submit({'id': 'original', 'text': case['original_utterance']})
                    fixture.wait_for({case['initial_status']})
                    recorded = PREVIOUS_TRIALS[case['trial_ids'][0]]
                    name = case['name'].removeprefix('dialogue_release_')
                    reply = copy.deepcopy(recorded['response'])
                    if name == 'preparation':
                        # The removed, invalid states had no probability mass.
                        probabilities = reply['answers']['answer']['probabilities']
                        for invalid in ('cooked', 'mixed', 'empty'):
                            self.assertEqual(probabilities.pop(invalid), 0)
                    requests = []
                    async def observed(request):
                        requests.append(copy.deepcopy(request))
                        if len(requests) == 1:
                            return reply
                        raise DecisionError('End of recorded dialogue probe')
                    fixture.t.script = observed
                    fixture.service.submit({'id': 'reply', 'text': case['text']})
                    result = fixture.wait_for({case['observed_service']['status']})
                    # Historical dialogue choices are mocks for the revised prompts.
                    # Subsequent compiler requests are not answered.
                    self.assertEqual(len(requests), len(case['observed_service']['requests']))
                    validate_response(requests[0], reply)
                    self.assertEqual(fixture.bridge.scheduler.snapshot()['pending'], [])
                    self.assertEqual(fixture.bridge.calls, [])
                    self.assertEqual(len(fixture.service.inbox), case['observed_service']['inbox_count'])
                    if name == 'correct':
                        self.assertEqual(requests[1]['state']['utterance']['text'],
                                         case['original_utterance'] + '\nUser correction: ' + case['text'])
                    elif name == 'preparation':
                        self.assertEqual(requests[1]['state']['compiler']['resolved_preparation'], 'raw')
                    elif name == 'unclear':
                        self.assertTrue(result['result']['message'].startswith('Restate'))
                finally:
                    fixture.doCleanups()

    def test_holdout_provenance_has_no_reused_responses(self):
        holdout = [case for case in CASES.values() if case.get('cohort') == 'holdout']
        self.assertEqual(len(holdout), 104)
        self.assertTrue(all(not case.get('reused_observations') for case in holdout))
        self.assertEqual(len({key for case in holdout for key in case['trial_ids']}), 445)
        path = 'tests/fixtures/jev-clarification-corpus.json'
        self.assertEqual(hashlib.sha256((ROOT/path).read_bytes()).hexdigest(), REPLAY['clarification_corpus_sha256'])

    def test_scoring_does_not_reward_abstention_or_partial_matches(self):
        case = CASES['nativerelease_holdout_raw_player4']
        outcome = copy.deepcopy(case['outcome'])
        for field, value in (('player_id', 1), ('items', [])):
            wrong = copy.deepcopy(outcome)
            wrong['result']['plan']['tasks'][0]['arguments'][field] = value
            self.assertEqual(score_outcome(case['expected'], wrong), 'incorrect_completion')
        self.assertEqual(score_outcome(case['expected'], {'status': 'clarification'}), 'clarification')
        self.assertEqual(score_outcome({'kind': 'blocked'}, {'status': 'clarification'}), 'safe_block')
        self.assertEqual(score_outcome({'kind': 'rejected'}, {'status': 'clarification'}), 'unnecessary_clarification')
        self.assertEqual(score_outcome({'kind': 'rejected'}, {'status': 'rejected'}), 'correct_rejection')
        self.assertEqual(score_outcome({'kind': 'clarification'}, {'status': 'rejected'}), 'incorrect_rejection')

    async def test_timed_washing_is_rejected_without_a_partial_plan(self):
        for name in ('release_holdout_timed', 'nativerelease_holdout_timed', 'timed_repeat_control'):
            case = CASES[name]
            outcome = await scripted_replay(session(case))
            self.assertEqual(outcome['status'], 'rejected')
            self.assertEqual(len(case['trial_ids']), 1)
            self.assertNotIn('result', outcome)

    def test_positive_corpus_matches_contracts_and_native_catalog_certainty(self):
        corpus = json.loads((ROOT/'tests/fixtures/jev-clarification-corpus.json').read_text())
        for case in corpus['development'] + corpus['holdout']:
            audit_case(case, current_world(corpus['worlds'][case.get('world', 'base')]), corpus['queues'][case.get('queue', 'empty')])
            self.assertTrue(case['audit'])
            for entry in case['audit']:
                self.assertTrue((ROOT/entry['source']).exists())
        for world in corpus['worlds'].values():
            for item in world['capabilities']['semantic_catalog']['displayOptions']['preparationOptions']:
                if not item.get('utensil'):
                    self.assertEqual(item['preparations']['cooked'], 'unknown')
                    self.assertEqual(item['preparations']['mixed'], 'unknown')

    def test_corpus_audit_rejects_impossible_positive_expectations(self):
        corpus = json.loads((ROOT/'tests/fixtures/jev-clarification-corpus.json').read_text())
        original = next(c for c in corpus['holdout'] if c['id'] == 'utensil')
        kitchen, queue = corpus['worlds']['alternate'], corpus['queues']['empty']
        mutations = [lambda a: a['items'][0].update(preparation='chopped'),
                     lambda a: a['items'][0].update(quantity=11),
                     lambda a: a['items'][0].update(ingredient='absent-item'),
                     lambda a: a.update(player_id=3)]
        for mutate in mutations:
            case = copy.deepcopy(original)
            mutate(case['expected']['tasks'][0]['arguments'])
            with self.assertRaises(ValueError): audit_case(case, kitchen, queue)
        case = copy.deepcopy(original)
        case['expected']['tasks'][0]['repeat'] = {'mode': 'finite', 'count': None}
        with self.assertRaises(ValueError): audit_case(case, kitchen, queue)


if __name__ == '__main__':
    unittest.main()
