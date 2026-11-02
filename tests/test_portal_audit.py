"""Coverage and isolation invariants for the native portal audit."""
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'scripts'))
import portal_audit as audit


class PortalAuditTests(unittest.TestCase):
    def test_wrong_running_port_range_is_rejected_before_loading(self):
        with tempfile.TemporaryDirectory() as temp:
            with patch.object(sys, 'argv', ['portal_audit', '--runtime', temp,
                    '--output', temp, '--port-base', '25712']), \
                 patch.object(audit.sim, 'read_status', return_value={'running': True}), \
                 patch.object(audit.sim, 'control_port', return_value=24703), \
                 patch.object(audit.sim, 'command') as command, \
                 patch('sys.stderr'):
                with self.assertRaises(SystemExit) as stopped:
                    audit.main()
                self.assertEqual(stopped.exception.code, 2)
                command.assert_not_called()

    def test_disappearing_dormant_portal_is_not_reported_as_no_portals(self):
        initial = {'generation': 1, 'inRound': True, 'portals': [
            {'id': 123, 'playerSender': True, 'enabled': False, 'exitObjectId': 456}]}
        after = dict(initial, portals=[])
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            (root / 'process.json').write_text(json.dumps({'portBase': 25712}))
            with patch.object(audit.sim, 'read_status', side_effect=[initial, initial, after]), \
                 patch.object(audit.sim, 'wait_ready', return_value=initial), \
                 patch.object(audit.sim, 'command'), patch.object(audit.time, 'sleep'), \
                 patch.object(audit, 'ActionClient') as client:
                result = audit.audit_level(root, {'name': 'kitchen', 'dlc': -1}, root)
            self.assertFalse(result['success'])
            self.assertEqual(result['reason'], 'discovered_portals_disappeared')
            self.assertEqual(result['missingPortals'], [123])
            client.assert_not_called()
