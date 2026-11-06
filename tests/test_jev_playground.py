"""Offline replay of real Chrome playground observations. Never uses HTTP."""
import copy
import hashlib
import json
from pathlib import Path
import unittest

from harness.decision.compiler import Compiler, Clarification, build_context
from harness.decision.config import Settings
from harness.decision.protocol import DecisionError, validate_request, validate_response
from scripts.jev_evaluate import score_outcome, semantic_result
from scripts.jev_playground import next_request
import test_decision as support

ROOT = Path(__file__).resolve().parents[1]
EVIDENCE = json.loads((ROOT / 'docs/jev-playground-round2.json').read_text())
TRIALS = {trial['id']: trial for trial in EVIDENCE['trials']}
CASES = {case['name']: case for case in EVIDENCE['sessions']}
RELEASE = [case for case in CASES.values() if case.get('variant') == 'release' and case['type'] == 'command']


def session(case):
    return {**{k: copy.deepcopy(case[k]) for k in ('name', 'text', 'world', 'queue', 'utterance_fields', 'clarifications') if k in case},
            'responses': [{k: copy.deepcopy(TRIALS[key][k]) for k in ('request', 'response')} for key in case['trial_ids']]}


class PlaygroundReplayTests(unittest.IsolatedAsyncioTestCase):
    def test_current_and_historical_wire_observations_validate(self):
        historical = json.loads((ROOT / 'docs/jev-playground-evaluation.json').read_text())
        for trial in list(TRIALS.values()) + historical['trials']:
            with self.subTest(trial=trial['id']):
                validate_request(trial['request'])
                validate_response(trial['request'], trial['response'])

    async def test_all_41_final_command_cases_use_exact_production_requests(self):
        self.assertEqual(len(RELEASE), 41)
        for case in RELEASE:
            with self.subTest(case=case['name']):
                # Any changed prompt fails; a missing observation returns request.
                outcome = await next_request(session(case))
                self.assertEqual(outcome, case['outcome'])
                self.assertEqual(score_outcome(case['expected'], outcome), case['score'])
                self.assertNotIn(case['score'], ('incomplete', 'incorrect_completion', 'unexpected_rejection'))

    async def test_heldout_completions_match_every_executable_field(self):
        completed = set()
        blocked = set()
        for case in RELEASE:
            if case['cohort'] != 'holdout':
                continue
            outcome = await next_request(session(case))
            name = case['name'].removeprefix('release_holdout_')
            if outcome['status'] == 'complete':
                self.assertEqual(semantic_result(outcome['result']), case['expected'])
                completed.add(name)
            elif case['expected']['kind'] == 'blocked':
                self.assertIn(outcome['status'], ('clarification', 'rejected'))
                blocked.add(name)
        self.assertEqual(completed, {'burnt', 'dirty', 'monitor', 'negation', 'pause', 'raw_player4', 'wash'})
        self.assertEqual(blocked, {'timed', 'concurrent', 'unavailable', 'out_of_range', 'speech_conflict',
                                  'preparation_missing', 'unresolved', 'infinite_middle'})

    async def test_complete_plan_confirmation_keeps_exact_fields_without_more_model_io(self):
        confirmations = 0
        for case in RELEASE:
            if case['outcome'].get('stage') != 'coverage':
                continue
            replay = session(case)
            pending = await next_request(replay)
            replay['clarifications'] = [{'stage': pending['stage'], 'field': pending['field'],
                'key': 'confirm_plan', 'question': pending['question'], 'answer': pending['options']['confirm_plan']}]
            accepted = await next_request(replay)
            self.assertEqual(accepted['status'], 'complete')
            self.assertEqual(semantic_result(accepted['result']), case['expected'])
            confirmations += 1
        self.assertEqual(confirmations, 6)

    async def test_current_verifier_blocks_all_16_wrong_or_ambiguous_proposals(self):
        wrong, accepted_controls = 0, 0
        for case in CASES.values():
            if case['type'] != 'final_verifier':
                continue
            trial = TRIALS[case['trial_ids'][0]]
            async def observed(request):
                self.assertEqual(request, trial['request'])
                return trial['response']
            compiler = Compiler({'id': case['name'], 'text': case['text'], **case['utterance_fields']},
                                build_context(case['world'], case['queue']), observed)
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
        self.assertEqual(accepted_controls, 3)  # Four correct controls still ask.

    async def test_repeated_decisions_preserve_observed_gate_outcomes(self):
        repeats = 0
        for case in CASES.values():
            if case['type'] != 'repeat':
                continue
            original = CASES[case['original_session']]
            replay = session(original)
            index = case['original_index']
            trial = TRIALS[case['trial_ids'][0]]
            self.assertEqual(replay['responses'][index]['request'], trial['request'])
            replay['responses'][index] = {k: trial[k] for k in ('request', 'response')}
            self.assertEqual(await next_request(replay), original['outcome'])
            repeats += 1
        self.assertEqual(repeats, 16)  # Two new runs of eight exact requests.

    def test_current_dialogue_observations_replay_through_service(self):
        for case in CASES.values():
            if case['type'] != 'final_dialogue':
                continue
            with self.subTest(case=case['name']):
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
                        raise DecisionError('End of recorded dialogue probe')
                    fixture.t.script = observed
                    fixture.service.submit({'id': 'reply', 'text': case['text']})
                    result = fixture.wait_for({case['observed_service']['status']})
                    self.assertEqual(requests, case['observed_service']['requests'])
                    self.assertEqual(requests[0], recorded['request'])
                    self.assertEqual(fixture.bridge.scheduler.snapshot()['pending'], [])
                    self.assertEqual(fixture.bridge.calls, [])
                    self.assertEqual(len(fixture.service.inbox), case['observed_service']['inbox_count'])
                    name = case['name'].removeprefix('dialogue_release_')
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
        self.assertEqual(len(holdout), 58)
        self.assertTrue(all(not case.get('reused_observations') for case in holdout))
        self.assertEqual(len({key for case in holdout for key in case['trial_ids']}), 157)
        path = 'tests/fixtures/jev-generalization-corpus.json'
        self.assertEqual(hashlib.sha256((ROOT/path).read_bytes()).hexdigest(), EVIDENCE['freeze']['source_sha256'][path])

    def test_scoring_does_not_reward_abstention_or_partial_matches(self):
        case = CASES['release_holdout_raw_player4']
        outcome = copy.deepcopy(case['outcome'])
        for field, value in (('player_id', 1), ('items', [])):
            wrong = copy.deepcopy(outcome)
            wrong['result']['plan']['tasks'][0]['arguments'][field] = value
            self.assertEqual(score_outcome(case['expected'], wrong), 'incorrect_completion')
        self.assertEqual(score_outcome(case['expected'], {'status': 'clarification'}), 'clarification')
        self.assertEqual(score_outcome({'kind': 'blocked'}, {'status': 'clarification'}), 'safe_block')


if __name__ == '__main__':
    unittest.main()
