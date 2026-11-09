import json
import socket
import time
import unittest
from unittest.mock import patch

from harness.voice.game_input import GamePushToTalk
from harness.voice.service import VoiceService


class GameVoiceInputTests(unittest.TestCase):
    def setUp(self):
        self.voice = VoiceService()
        self.addCleanup(self.voice.close)
        self.send = self.enterContext(patch.object(self.voice, '_send'))
        self.enterContext(patch.object(self.voice, '_ensure_helper'))
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
