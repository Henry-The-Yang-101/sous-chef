"""Run the dependency-free production JavaScript form regressions."""
from pathlib import Path
import shutil
import subprocess
import unittest


class HarnessWebTests(unittest.TestCase):
    @unittest.skipUnless(shutil.which('node'), 'Node.js is required for web regressions')
    def test_live_form_choices_and_run_availability(self):
        result = subprocess.run(['node', '--test', str(Path(__file__).with_name('harness_web.test.js'))],
                                capture_output=True, text=True, timeout=20)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
