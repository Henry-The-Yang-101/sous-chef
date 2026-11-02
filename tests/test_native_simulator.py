"""Lifecycle regressions for the native player CLI; no game required."""
import json
from pathlib import Path
import sys
import tempfile
import unittest
from types import SimpleNamespace
from unittest.mock import patch

SCRIPTS = Path(__file__).resolve().parents[1] / 'scripts'
sys.path.insert(0, str(SCRIPTS))
import native_simulator as sim


class NativeSimulatorTests(unittest.TestCase):
    def test_reset_waits_for_new_ready_generation(self):
        states = [
            {'generation': 1, 'inRound': True},
            {'generation': 2, 'inRound': False},
            {'generation': 2, 'inRound': True},
        ]
        with patch.object(sim, 'read_status', side_effect=states) as read, \
             patch.object(sim, 'is_running', return_value=True), patch.object(sim.time, 'sleep'):
            result = sim.wait_ready(Path('/unused'), generation=2)
        self.assertEqual(read.call_count, 3)
        self.assertEqual(result, states[-1])

    def test_dead_process_cannot_report_stale_ready(self):
        with patch.object(sim, 'read_status', return_value={'generation': 9, 'inRound': True}), \
             patch.object(sim, 'is_running', return_value=False):
            with self.assertRaisesRegex(RuntimeError, 'exited'):
                sim.wait_ready(Path('/unused'))

    def test_command_ignores_previous_ack_and_error(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            seen = []
            def status(_):
                request = (root / 'command').read_text().split(' ', 1)
                seen.append(request)
                if len(seen) == 1:
                    return {'lastCommand': 'old', 'commandError': 'old error'}
                return {'lastCommand': request[0], 'commandError': None}
            with patch.object(sim, 'is_running', return_value=True), \
                 patch.object(sim, 'read_status', side_effect=status), patch.object(sim.time, 'sleep'):
                sim.command(root, 'reset')
            self.assertEqual(len(seen), 2)
            self.assertEqual(seen[0][1], 'reset\n')

    def test_command_rejection_is_not_retried(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            def status(_):
                ident = (root / 'command').read_text().split(' ', 1)[0]
                return {'lastCommand': ident, 'commandError': 'Unknown kitchen'}
            with patch.object(sim, 'is_running', return_value=True), \
                 patch.object(sim, 'read_status', side_effect=status) as read:
                with self.assertRaisesRegex(RuntimeError, 'Unknown kitchen'):
                    sim.command(root, 'load bogus')
            self.assertEqual(read.call_count, 1)

    def test_pid_reuse_does_not_allow_another_process(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            (root / 'process.json').write_text(json.dumps({'pid': 42}))
            with patch.object(sim.subprocess, 'run') as process:
                process.return_value.returncode = 0
                process.return_value.stdout = '/Applications/SomeOtherApp\n'
                self.assertFalse(sim.is_running(root))
                process.return_value.stdout = f'Overcooked2 -logFile {root}/player.log\n'
                self.assertTrue(sim.is_running(root))

    def test_navigation_waits_for_catalog_and_does_not_retry_failure(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            args = SimpleNamespace(runtime=root, trace_dir=root / 'traces', fps=None,
                                   repeat=1, target='Chocolate', timeout=2)
            catalogue = {'complete': True, 'actions': [{'name': 'navigate_to', 'parameters': {
                'destination_id': {'options': [{'id': 'd42', 'foodName': 'Chocolate'}]}}}]}
            with patch('clients.actions.ActionClient', autospec=True) as factory, \
                 patch.object(sim, 'read_status', return_value={'generation': 1}), \
                 patch.object(sim, 'wait_ready', return_value={'sessionId': 'sim'}), \
                 patch.object(sim, 'command'), patch.object(sim.time, 'sleep'):
                client = factory.return_value.__enter__.return_value
                client.hello = {'sessionId': 'sim'}
                client.available_actions.side_effect = [{'complete': False}, catalogue]
                client.status.return_value = {'navigation': {'state': 'idle', 'reason': 'no_safe_start'}}
                report = sim.navigate(args)
                client.navigate_to.assert_called_once_with('d42')
                self.assertEqual(client.available_actions.call_count, 2)
            self.assertEqual(report['passed'], 0)
            self.assertEqual(report['results'][0]['reason'], 'no_safe_start')

    def test_portal_validation_rejects_walking_only_arrival(self):
        for completed, passed in ((0, 0), (1, 0), (2, 1)):
            with self.subTest(completed=completed), tempfile.TemporaryDirectory() as temp:
                root = Path(temp)
                args = SimpleNamespace(runtime=root, trace_dir=root / 'traces', fps=None,
                                       repeat=1, target='Pan', timeout=2, min_teleports=2)
                catalogue = {'complete': True, 'actions': [{'name': 'navigate_to', 'parameters': {
                    'destination_id': {'options': [{'id': 'd42', 'label': 'Pan'}]}}}]}
                with patch('clients.actions.ActionClient', autospec=True) as factory, \
                     patch.object(sim, 'read_status', return_value={'generation': 1}), \
                     patch.object(sim, 'wait_ready', return_value={'sessionId': 'sim'}), \
                     patch.object(sim, 'command'), patch.object(sim.time, 'sleep'):
                    client = factory.return_value.__enter__.return_value
                    client.hello = {'sessionId': 'sim'}
                    client.available_actions.return_value = catalogue
                    client.status.return_value = {'navigation': {'state': 'idle', 'reason': 'arrived',
                                                                 'teleportsCompleted': completed}}
                    report = sim.navigate(args)
                    client.navigate_to.assert_called_once_with('d42')
                self.assertEqual(report['passed'], passed)
                self.assertEqual(report['results'][0]['teleportsCompleted'], completed)
                self.assertEqual(report['results'][0]['reason'], 'arrived' if passed else 'insufficient_teleports')

    def test_return_leg_uses_same_round_and_failure_is_not_hidden(self):
        for fail_second in (False, True):
            with self.subTest(fail_second=fail_second), tempfile.TemporaryDirectory() as temp:
                root = Path(temp)
                args = SimpleNamespace(runtime=root, trace_dir=root / 'traces', fps=None,
                                       repeat=1, target='Pan', timeout=2, min_teleports=2,
                                       **{'then': ['Meat']})
                def catalogue(label, ident):
                    return {'complete': True, 'actions': [{'name': 'navigate_to', 'parameters': {
                        'destination_id': {'options': [{'id': ident, 'label': label}]}}}]}
                with patch('clients.actions.ActionClient', autospec=True) as factory, \
                     patch.object(sim, 'read_status', return_value={'generation': 1}), \
                     patch.object(sim, 'wait_ready', return_value={'sessionId': 'sim'}), \
                     patch.object(sim, 'command') as command, patch.object(sim.time, 'sleep'):
                    client = factory.return_value.__enter__.return_value
                    client.hello = {'sessionId': 'sim'}
                    client.available_actions.side_effect = [catalogue('Pan', 'd1'),
                        RuntimeError('round ended') if fail_second else catalogue('Meat', 'd2')]
                    client.status.return_value = {'navigation': {'state': 'idle', 'reason': 'arrived',
                                                                 'teleportsCompleted': 2}}
                    report = sim.navigate(args)
                    command.assert_called_once_with(root, 'reset')
                    self.assertEqual(client.navigate_to.call_count, 1 if fail_second else 2)
                self.assertEqual(report['passed'], 0 if fail_second else 1)
                self.assertEqual(len(report['results'][0]['legs']), 1 if fail_second else 2)
                if not fail_second:
                    self.assertEqual(report['results'][0]['totalTeleportsCompleted'], 4)

    def test_failed_start_terminates_only_its_own_child(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            output = root / 'build'
            output.mkdir()
            (output / 'SousChef.Bridge.dll').write_bytes(b'fixture')
            args = SimpleNamespace(runtime=root / 'runtime', game=root / 'game.app',
                                   level='invalid', fps=60, timeout=1)
            (args.runtime / 'BepInEx').mkdir(parents=True)
            with patch.object(sim, 'build', return_value=output), \
                 patch.object(sim.shutil, 'copytree'), \
                 patch.object(sim.subprocess, 'run') as query, \
                 patch.object(sim.subprocess, 'Popen') as spawn, \
                 patch.object(sim, 'wait_ready', side_effect=RuntimeError('invalid level')), \
                 patch.object(sim, 'command', side_effect=TimeoutError('not responding')):
                query.return_value.returncode = 1
                child = spawn.return_value
                child.pid = 42
                child.poll.return_value = None
                with self.assertRaisesRegex(RuntimeError, 'invalid level'):
                    sim.start(args)
                child.terminate.assert_called_once()
                child.wait.assert_called_once()
                child.kill.assert_not_called()

    def test_pending_command_is_not_overwritten(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            (root / 'command').write_text('pending reset\n')
            with patch.object(sim, 'is_running', return_value=True):
                with self.assertRaisesRegex(RuntimeError, 'pending'):
                    sim.command(root, 'quit')
            self.assertEqual((root / 'command').read_text(), 'pending reset\n')


if __name__ == '__main__':
    unittest.main()
