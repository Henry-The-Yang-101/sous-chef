import json
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'scripts'))
from conveyor_audit import conveyor_motion


class ConveyorAuditTests(unittest.TestCase):
    def measure(self, windows):
        with tempfile.TemporaryDirectory() as temporary:
            trace = Path(temporary) / 'trace.ndjson'
            trace.write_text(''.join(json.dumps({'navigation': {'executionTrace': rows}}) + '\n'
                                     for rows in windows))
            return conveyor_motion(trace)

    def decision(self):
        return dict(frame=1, time=1, phase='steer', groundId=23, surfaceVelocity=[5, 0, 0],
                    position=[0, 0, 0], goal=[1, 0, 0])

    def test_repeated_windows_do_not_inflate_conveyor_coverage(self):
        row = self.decision()
        result = self.measure([[row], [row]])
        self.assertEqual(result['conveyorDecisionFrames'], 1)
        self.assertEqual(result['conveyorGroundIds'], [23])
        self.assertEqual(result['upstreamDecisionFrames'], 0)

    def test_native_upstream_heading_is_a_failure(self):
        row = self.decision()
        row['goal'] = [-1, 0, 0]
        result = self.measure([[row]])
        self.assertEqual(result['upstreamDecisionFrames'], 1)

    def test_calm_floor_and_neutral_alignment_do_not_cover_a_conveyor(self):
        calm, aligning = self.decision(), self.decision()
        calm['surfaceVelocity'] = [0, 0, 0]
        aligning['phase'] = 'aligning'
        self.assertEqual(self.measure([[calm], [aligning]])['conveyorDecisionFrames'], 0)

    def test_final_decision_in_a_frame_replaces_proposed_heading(self):
        proposed, final = self.decision(), self.decision()
        proposed['goal'] = [-1, 0, 0]
        final['goal'] = [0, 0, 1]
        final['phase'] = 'steer_adjusted'
        result = self.measure([[proposed, final]])
        self.assertEqual(result['conveyorDecisionFrames'], 1)
        self.assertEqual(result['upstreamDecisionFrames'], 0)

    def test_sustained_upstream_attempt_cannot_hide_behind_successful_arrival(self):
        rows = []
        for frame in range(4):
            row = self.decision()
            row.update(frame=frame, time=1 + .1 * frame, goal=[-1, 0, 0] if frame < 3 else [1, 0, 0])
            rows.append(row)
        result = self.measure([rows])
        self.assertGreater(result['longestUpstreamHeadingSeconds'], .15)


if __name__ == '__main__':
    unittest.main()
