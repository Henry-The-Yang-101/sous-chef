import json
import socket
import time
import unittest
from unittest.mock import patch

from harness.voice.game_input import GamePushToTalk
from harness.voice.service import VoiceService


class _VoiceTestCase(unittest.TestCase):
    def setUp(self):
        self.voice = VoiceService()
        self.addCleanup(self.voice.close)
        self.send = self.enterContext(patch.object(self.voice, '_send'))
        self.enterContext(patch.object(self.voice, '_ensure_helper'))


class GameVoiceInputTests(_VoiceTestCase):
    def setUp(self):
        super().setUp()
        self.listener = self.enterContext(socket.socket())
        self.listener.bind(('127.0.0.1', 0))
        self.listener.listen()
        self.listener.settimeout(3)
        self.endpoint = 'fixture', self.listener.getsockname()[1]
        self.source = GamePushToTalk(self.voice, lambda: self.endpoint)
        self.source.start()
        self.addCleanup(self.source.close)

    def connect(self):
        game, _ = self.listener.accept()
        self.addCleanup(game.close)
        self.write(game, type='hello', protocolVersion=1, sessionId='fixture', stream='voice')
        return game

    @staticmethod
    def write(game, **frame):
        game.sendall((json.dumps(frame) + '\n').encode())

    def frame(self, game, turn, phase, key='F8'):
        self.write(game, type='pushToTalk', turn=turn, phase=phase, key=key)

    def wait_for(self, predicate):
        deadline = time.monotonic() + 2
        while not predicate():
            if time.monotonic() >= deadline:
                self.fail('Voice stream did not reach the expected state')
            time.sleep(.01)

    def ready(self):
        with self.voice.lock:
            self.voice.gate.invalidate(False)
            self.voice.status = 'listening'

    def test_game_press_and_release_share_browser_turn_ownership(self):
        self.ready()
        game = self.connect()
        self.frame(game, 0, 'idle', 'F8')
        self.wait_for(lambda: self.source.key == 'F8')
        self.frame(game, 1, 'held')
        self.wait_for(lambda: self.voice.snapshot()['pttActive'])
        with self.assertRaisesRegex(ValueError, 'not ready'):
            self.voice.command({'operation': 'begin', 'generation': self.voice.gate.generation,
                                'turnId': 'browser'}, 'browser')
        self.frame(game, 1, 'released')
        self.wait_for(lambda: not self.voice.snapshot()['pttActive'])
        self.assertEqual([call.args[0] for call in self.send.call_args_list], ['begin', 'finalize'])
        self.assertEqual(self.send.call_args_list[0].kwargs, self.send.call_args_list[1].kwargs)

    def test_disconnect_and_mute_discard_holds_without_replaying_them(self):
        self.ready()
        game = self.connect()
        self.frame(game, 1, 'held')  # Press predates this connection.
        self.frame(game, 1, 'released')
        self.frame(game, 2, 'held')
        self.wait_for(lambda: self.voice.ptt_id == 'game:fixture:2')
        game.close()
        self.wait_for(lambda: self.voice.status == 'preparing' and self.source.key is None)
        self.assertFalse(self.voice.snapshot()['pttActive'])

        self.ready()
        game = self.connect()
        self.frame(game, 2, 'held')
        self.frame(game, 2, 'cancelled')
        self.frame(game, 3, 'held')
        self.wait_for(lambda: self.voice.ptt_id == 'game:fixture:3')
        self.voice.command({'operation': 'mute'}, 'browser')
        self.frame(game, 3, 'released', 'F9')
        self.wait_for(lambda: self.source.key == 'F9' and self.source.active is None)
        self.assertTrue(self.voice.snapshot()['muted'])
        self.assertEqual([call.args[0] for call in self.send.call_args_list], ['begin', 'arm', 'begin'])


class VoiceServiceTests(_VoiceTestCase):
    def test_local_inspection_only_keeps_the_latest_finalized_turn(self):
        self.voice.command({'operation': 'arm_text', 'generation': self.voice.gate.generation}, 'browser')
        generation = self.voice.gate.generation
        self.voice.settings['inspection'] = 'fake'
        for index, utterance in enumerate(('Wash dishes', 'Bring me rice')):
            def emit(kind, sequence, **fields):
                self.voice._event(dict(protocol_version=1, speech_generation=generation,
                                       type=kind, event_sequence=index * 4 + sequence,
                                       turn_id=f'turn-{index}', **fields))
            start, end = index * 1000, (index + 1) * 1000
            emit('turn_started', 1, start_ms=start)
            emit('transcript_update', 2, audio_range_ms=[start, end], text=utterance,
                 is_final=True, finalized_before_ms=end)
            emit('turn_boundary', 3, cutoff_ms=end)
            emit('finalize_completed', 4, cutoff_ms=end, results_drained=True,
                 finalized_before_ms=end, last_result_sequence=index * 4 + 2)
        state = self.voice.snapshot()
        self.assertEqual([c['text'] for c in state['commands']], ['Bring me rice'])
        self.assertEqual(state['fakeResult']['commandId'], state['commands'][0]['id'])
        self.voice.command({'operation': 'mute'}, 'browser')
        self.assertEqual(self.voice.snapshot()['commands'], [])
