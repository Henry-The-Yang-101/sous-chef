"""Use the plugin's focused keyboard shortcut as another voice-turn owner."""
import json
import socket
import threading


class GamePushToTalk:
    def __init__(self, voice, endpoint):
        self.voice = voice
        self.endpoint = endpoint
        self.owner = object()
        self.halt = threading.Event()
        self.thread = None
        self.key = None
        self.active = None

    def start(self):
        self.thread = threading.Thread(target=self._run, name='game-voice-input', daemon=True)
        self.thread.start()

    def close(self):
        self.halt.set()
        if self.thread:
            self.thread.join(timeout=2)

    @staticmethod
    def _read(reader):
        line = reader.readline(1025)
        if not line.endswith(b'\n') or len(line) > 1024:
            raise ValueError('Incomplete voice input frame')
        message = json.loads(line)
        if not isinstance(message, dict):
            raise ValueError('Invalid voice input frame')
        return message

    def _run(self):
        while not self.halt.is_set():
            endpoint = self.endpoint()
            if endpoint:
                try:
                    self._listen(endpoint)
                except (OSError, ValueError):
                    pass
                finally:
                    self.key = None
                    self._finish('cancel')
            self.halt.wait(.25)

    def _listen(self, endpoint):
        session, port = endpoint
        with socket.create_connection(('127.0.0.1', port), timeout=1) as sock, sock.makefile('rb') as reader:
            hello = self._read(reader)
            if (hello.get('type'), hello.get('protocolVersion'), hello.get('sessionId'), hello.get('stream')) != (
                    'hello', 1, session, 'voice'):
                raise ValueError('Unexpected game voice stream')
            last_turn = None
            while not self.halt.is_set():
                frame = self._read(reader)
                if self.halt.is_set() or self.endpoint() != endpoint:
                    return
                if frame.get('type') == 'gap':
                    continue  # Each frame describes the entire current turn.
                turn, phase, key = frame.get('turn'), frame.get('phase'), frame.get('key')
                if (frame.get('type') != 'pushToTalk' or type(turn) is not int or not 0 <= turn < 2**63
                        or (last_turn is not None and turn < last_turn) or not isinstance(key, str)
                        or phase not in ('idle', 'held', 'released', 'cancelled')):
                    raise ValueError('Invalid push-to-talk state')
                self.key = key
                # The first frame can describe a hold from before the connection.
                new_press = last_turn is not None and turn > last_turn and phase == 'held'
                self._observe(f'game:{session}:{turn}', phase, new_press)
                last_turn = turn

    def _observe(self, identity, phase, new_press):
        if self.active:
            turn_id, generation = self.active
            if identity != turn_id or phase != 'held':
                self._finish('finalize' if identity == turn_id and phase == 'released' else 'cancel')
            elif not self._command('heartbeat', turn_id, generation):
                self.active = None  # Mute, Stop, or a service reset already ended it.
        if new_press:
            with self.voice.lock:
                generation = self.voice.gate.generation
                if self._command('begin', identity, generation):
                    self.active = identity, generation

    def _finish(self, operation):
        if self.active:
            turn_id, generation = self.active
            self.active = None
            self._command(operation, turn_id, generation)

    def _command(self, operation, identity, generation):
        try:
            self.voice.command({'operation': operation, 'turnId': identity, 'generation': generation}, self.owner)
            return True
        except ValueError:
            # An unarmed microphone or a turn owned by a browser rejects the key.
            return False
