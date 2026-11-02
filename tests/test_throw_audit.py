"""Protect the native audit against false success from stale receipts/identity."""
import json
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'scripts'))
import throw_audit as audit


class ThrowAuditTests(unittest.TestCase):
    def test_rejection_does_not_reuse_previous_successful_receipt(self):
        class Client:
            request_id = 40

            def throw_to(self, _):
                self.request_id += 1
                raise audit.ControlError('hands_empty')

            def status(self):
                self.request_id += 1
                return {'action': {'requestId': '39', 'state': 'succeeded',
                                   'phase': 'idle', 'reason': 'throw_received'},
                        'navigation': {'state': 'idle'}, 'ownedByYou': False}

        with tempfile.TemporaryDirectory() as directory:
            result = audit.execute(Client(), 'throw_to', 'd1', Path(directory) / 'trace', 1)
        self.assertEqual(result['outcome'], 'hands_empty')
        self.assertIsNone(result['receipt'])
        self.assertFalse(result['ownedAfter'])

    def test_holder_confirmation_requires_exact_object_reference(self):
        def station(name, item_id):
            return {'name': name, 'unityInstanceId': 1, 'components': [
                {'type': 'ServerAttachStation', 'adapters': {'ServerAttachStation': {
                    'fields': {'m_item': {'objectId': item_id}}}}}]}
        snapshot = {'objects': [station('intended station', 'other-cheese'),
                                station('wrong station', 'exact-cargo')]}
        holders = audit.item_holders(snapshot, {'id': 'exact-cargo'})
        self.assertEqual([holder['name'] for holder in holders], ['wrong station'])
        self.assertEqual(audit.item_holders(snapshot, None), [])

    def test_fixture_names_are_unique_and_keep_functional_success_expectations(self):
        cases = json.loads((Path(__file__).parent / 'actions/native-throw-cases.json').read_text())
        self.assertEqual(len({case['name'] for case in cases}), len(cases))
        ordinary = [case for case in cases if case['name'].startswith('cheese-')]
        self.assertEqual(len(ordinary), 4)
        self.assertTrue(all(case['expected'] == 'throw_received' for case in ordinary))
        # A known implementation failure must not turn the intended operation
        # into a passing regression by changing its expectation to that error.


if __name__ == '__main__':
    unittest.main()
