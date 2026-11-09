"""Harness-owned speech lifecycle. Never holds a game/scheduler lock."""
from collections import deque
import copy
import json
import os
from pathlib import Path
import queue
import subprocess
import sys
import threading
import time
import uuid

from .assembler import CommandAssembler
from .gate import DispatchGate, FakeTransport


DEFAULT_SETTINGS = {'locale': 'en-US', 'deviceId': '', 'mode': 'push_to_talk',
                    'pauseMs': 800, 'maxCommandMs': 30000, 'fastResults': False,
                    'energyThreshold': 0.015, 'inspection': 'local'}


def settings_from(value):
    if not isinstance(value, dict) or value.keys() - DEFAULT_SETTINGS.keys():
        raise ValueError('Unknown voice setting')
    result = dict(DEFAULT_SETTINGS, **value)
    for key in ('locale', 'deviceId'):
        if not isinstance(result[key], str) or len(result[key]) > 256:
            raise ValueError('Invalid ' + key)
    if not result['locale'] or result['mode'] not in ('push_to_talk', 'automatic') or result['inspection'] not in ('local', 'fake', 'jev'):
        raise ValueError('Invalid voice mode or locale')
    if type(result['fastResults']) is not bool:
        raise ValueError('fastResults must be boolean')
    for key, low, high in [('pauseMs', 200, 3000), ('maxCommandMs', 1000, 60000), ('energyThreshold', .001, .5)]:
        if type(result[key]) not in (int, float) or not low <= result[key] <= high:
            raise ValueError(f'{key} must be between {low} and {high}')
    return result


class VoiceService:
    RETENTION_SECONDS = 600
    FINALIZE_SECONDS = 15

    def __init__(self):
        self.gate = DispatchGate()
        self.instance_id = uuid.uuid4().hex
        self.revision = 0
        self.lock = self.gate.lock
        self.assembler = CommandAssembler()
        self.settings = dict(DEFAULT_SETTINGS)
        self.status = 'muted'
        self.error = None
        self.meter = None
        self.input_device = None
        self.devices, self.locales = [], []
        self.history = deque(maxlen=1)
        self.fake = FakeTransport()
        self.fake_result = None
        self.on_command = None
        self.process = None
        self.outbound = queue.Queue(maxsize=32)
        self.halt = threading.Event()
        self.launching = False
        self.mute_since = None
        self.capture_stopped = True
        self.last_event = time.monotonic()
        self.finalizing = {}
        self.ptt_owner = None
        self.ptt_id = None
        self.ptt_heartbeat = None
        self.worker = threading.Thread(target=self._supervise, name='voice-service', daemon=True)
        self.worker.start()

    def _reset(self, muted=None):
        self.revision += 1
        generation = self.gate.invalidate(muted)
        self.assembler.reset(generation)
        self.finalizing.clear()
        self.history.clear()
        self.fake_result = None
        self.meter = None
        self.input_device = None
        self.ptt_owner = self.ptt_id = self.ptt_heartbeat = None
        while True:
            try:
                self.outbound.get_nowait()
            except queue.Empty:
                break
        return generation

    def _send(self, kind, **fields):
        try:
            self.outbound.put_nowait(dict(protocol_version=1, type=kind,
                                         speech_generation=self.gate.generation, **fields))
        except queue.Full:
            self._failure('Too many microphone controls; unfinished commands discarded')
            raise ValueError(self.error) from None

    def _failure(self, message):
        # Device/protocol/process failures close the gate and discard every draft.
        self._reset(True)
        self.status, self.error = 'error', message
        if self.process and self.process.poll() is None:
            self.process.kill()
        else:
            self.capture_stopped = True

    def command(self, request, viewer):
        if not isinstance(request, dict):
            raise ValueError('Missing voice control')
        operation = request.get('operation')
        with self.lock:
            if operation != 'mute' and request.get('generation') != self.gate.generation:
                raise ValueError('Voice state changed; use the current controls')
            self.revision += 1
            if operation == 'mute':
                self._reset(True)
                running = self.process and self.process.poll() is None
                self.status = 'muting' if running else 'muted'
                self.capture_stopped = not running
                self.mute_since = time.monotonic() if running else None
                if running:
                    self._send('mute')
            elif operation == 'configure':
                if not self.gate.muted or not self.capture_stopped:
                    raise ValueError('Mute voice before changing microphone settings')
                self.settings = settings_from(request.get('settings'))
                self._reset(True)
                if self.process and self.process.poll() is None:
                    self._send('configure', settings=self.settings)
            elif operation in ('arm', 'devices'):
                if operation == 'arm':
                    if not self.gate.muted or not self.capture_stopped:
                        raise ValueError('Voice is already armed or still muting')
                    self._reset(False)
                    self.capture_stopped = False
                    self.error, self.status = None, 'preparing'
                self._ensure_helper()
                self._send('arm' if operation == 'arm' else 'devices', settings=self.settings)
            elif operation == 'arm_text':
                if not self.gate.muted or not self.capture_stopped:
                    raise ValueError('Mute voice before enabling typed commands')
                self._reset(False)
                self.error, self.status = None, 'typed_ready'
            elif operation == 'begin':
                if self.gate.muted or self.settings['mode'] != 'push_to_talk' or self.status != 'listening' or self.ptt_owner:
                    raise ValueError('Microphone is not ready for a new push-to-talk turn')
                identity = request.get('turnId')
                if not isinstance(identity, str) or not 1 <= len(identity) <= 80:
                    raise ValueError('Missing push-to-talk identity')
                self.ptt_owner, self.ptt_id, self.ptt_heartbeat = viewer, identity, time.monotonic()
                self._send('begin', turn_id=identity)
            elif operation in ('finalize', 'cancel', 'heartbeat'):
                if (self.ptt_owner != viewer or self.ptt_id != request.get('turnId') or self.gate.muted):
                    raise ValueError('This push-to-talk turn is no longer active for this input')
                if operation == 'heartbeat':
                    self.ptt_heartbeat = time.monotonic()
                elif operation == 'cancel':
                    self.invalidate()
                else:
                    self._send('finalize', turn_id=self.ptt_id)
                    self.ptt_owner = self.ptt_id = self.ptt_heartbeat = None
            elif operation == 'clear':
                self.history.clear()
                self.fake_result = None
            else:
                raise ValueError('Unknown voice operation')
            return self.snapshot()

    def invalidate(self):
        """Stop invalidates interpretation/capture without changing mute preference."""
        with self.lock:
            self.revision += 1
            muted = self.gate.muted
            self._reset()
            if self.process and self.process.poll() is None:
                self._send('mute' if muted else 'reset', settings=self.settings)
                self.status = 'muting' if muted else 'preparing'
                self.capture_stopped = False
                self.mute_since = time.monotonic() if muted else None
            elif not muted and self.status != 'typed_ready':
                self.status = 'preparing'
                self._ensure_helper()
                self._send('arm', settings=self.settings)

    def snapshot(self):
        with self.lock:
            now = time.monotonic()
            while self.history and now - self.history[0]['retainedAt'] > self.RETENTION_SECONDS:
                self.history.popleft()
            if not self.history:
                self.fake_result = None
            return {'muted': self.gate.muted, 'muteAcknowledged': self.gate.muted and self.capture_stopped,
                    'instanceId': self.instance_id, 'revision': self.revision,
                    'generation': self.gate.generation, 'status': self.status, 'error': self.error,
                    'settings': copy.deepcopy(self.settings), 'devices': copy.deepcopy(self.devices),
                    'audioLevel': copy.deepcopy(self.meter), 'inputDevice': self.input_device,
                    'locales': list(self.locales), 'drafts': self.assembler.preview(),
                    'commands': [{k: copy.deepcopy(v) for k, v in item.items() if k != 'retainedAt'} for item in self.history],
                    'fakeResult': copy.deepcopy(self.fake_result), 'transport': self.settings['inspection'],
                    'pttActive': self.ptt_owner is not None}

    def _ensure_helper(self):
        if self.launching or (self.process and self.process.poll() is None):
            return
        self.launching = True
        threading.Thread(target=self._launch, name='voice-launcher', daemon=True).start()

    def _launch(self):
        try:
            launcher = Path(__file__).resolve().parents[2] / 'scripts' / 'apple_speech_service.py'
            # stderr remains diagnostics, never JSON. No raw audio or transcript log.
            process = subprocess.Popen([sys.executable, str(launcher)], stdin=subprocess.PIPE,
                                       stdout=subprocess.PIPE, text=True, bufsize=1)
            os.set_blocking(process.stdin.fileno(), False)
            with self.lock:
                if self.halt.is_set():
                    process.kill()
                self.process = process
                self.last_event = time.monotonic()
            while line := process.stdout.readline(131073):
                if len(line) > 131072:
                    raise ValueError('Speech event exceeds protocol limit')
                self._event(json.loads(line))
            process.wait()
            with self.lock:
                self.capture_stopped = True
                if not self.halt.is_set() and self.status not in ('muted', 'muting', 'error'):
                    self._failure('Speech helper exited; arm voice to retry')
                elif self.status == 'muting':
                    self.status, self.mute_since = 'muted', None
        except (OSError, ValueError, KeyError, TypeError) as error:
            with self.lock:
                self._failure('Speech helper: ' + str(error))
        finally:
            if 'process' in locals():
                if process.poll() is None:
                    process.kill()
                process.wait()
                process.stdin.close()
                process.stdout.close()
            with self.lock:
                self.launching = False
                self.capture_stopped = True
                self.revision += 1

    def _event(self, event):
        with self.lock:
            if not isinstance(event, dict):
                raise ValueError('Invalid speech event')
            if event.get('speech_generation') != self.gate.generation:
                return
            self.last_event = time.monotonic()
            self.revision += 1
            command = self.assembler.consume(event)
            kind = event.get('type')
            if kind == 'ready':
                self.devices, self.locales = event.get('devices', []), event.get('locales', [])
            elif kind in ('error', 'device_changed'):
                self._failure(event.get('message', 'Microphone changed; arm voice again'))
                return
            elif kind == 'status':
                status = event.get('status')
                if status == 'muted' and self.gate.muted:
                    self.status, self.mute_since = 'muted', None
                    self.capture_stopped = True
                elif not self.gate.muted and status in ('preparing', 'downloading', 'listening', 'finalizing'):
                    self.status = status
                    if status == 'listening':
                        self.input_device = event.get('device_name')
            elif kind == 'audio_level' and not self.gate.muted:
                self.meter = {key: event.get(key) for key in ('rms', 'speech_detected', 'audio_end_ms')}
            elif kind == 'turn_boundary':
                self.finalizing[event['turn_id']] = time.monotonic()
            if command is not None and not self.gate.muted:
                boundary_at = self.finalizing.pop(event['turn_id'], None)
                command['timing'] = {'cutoffToCommitMs': round((time.monotonic() - boundary_at) * 1000) if boundary_at else None,
                                     'helperFinalizationMs': event.get('finalization_ms'),
                                     'speechToCutoffMs': (command['audio_range_ms'][1] - command['last_speech_ms'])
                                     if command.get('last_speech_ms') is not None else None}
                command.update(retainedAt=time.monotonic(), committedAt=time.time())
                self.history.append(command)
                self.fake_result = None
                if not command.get('empty') and self.settings['inspection'] == 'fake':
                    generation = self.gate.generation
                    future = self.gate.dispatch(generation, self.fake, command)
                    # The initial transport is immediate and local. A real adapter
                    # waits on its own worker, then adopts through the same fence.
                    self.gate.adopt(generation, future, lambda result: setattr(self, 'fake_result', result))
                elif not command.get('empty') and self.settings['inspection'] == 'jev' and self.on_command:
                    try:
                        self.on_command(command)
                    except ValueError as error:
                        self.error = str(error)

    def _supervise(self):
        pending_message = None
        while not self.halt.wait(.05):
            with self.lock:
                now = time.monotonic()
                process = self.process
                if self.mute_since and now - self.mute_since > 2:
                    if process and process.poll() is None:
                        process.kill()
                    # Acknowledgment follows process exit, never just a timeout.
                if self.ptt_heartbeat and now - self.ptt_heartbeat > 3:
                    self.invalidate()  # Lost input source: discard the unfinished turn.
                    self.error = 'Push-to-talk connection lost; unfinished audio discarded'
                if self.finalizing and now - min(self.finalizing.values()) > self.FINALIZE_SECONDS:
                    self._failure('Speech finalization timed out; command discarded')
                if not self.gate.muted and self.status == 'listening' and now - self.last_event > 5:
                    self._failure('Microphone stopped producing audio; command discarded')
                if not process or process.poll() is not None:
                    if not self.launching and not self.outbound.empty():
                        self._ensure_helper()
                    continue
                if pending_message is None:
                    try:
                        pending_message = self.outbound.get_nowait()
                    except queue.Empty:
                        continue
                message = pending_message
                if message['speech_generation'] != self.gate.generation:
                    pending_message = None
                    continue
            # A bounded atomic nonblocking pipe write keeps the mute watchdog
            # responsive even if a helper stops reading its control pipe.
            try:
                payload = (json.dumps(message) + '\n').encode()
                if len(payload) > os.fpathconf(process.stdin.fileno(), 'PC_PIPE_BUF'):
                    raise ValueError('Speech control exceeds the atomic pipe limit')
                os.write(process.stdin.fileno(), payload)
                pending_message = None
            except BlockingIOError:
                continue
            except (OSError, ValueError):
                pending_message = None
                with self.lock:
                    self._failure('Speech helper connection lost')

    def close(self):
        with self.lock:
            self._reset(True)
            self.halt.set()
            if self.process and self.process.poll() is None:
                self.process.terminate()
        self.worker.join(timeout=1)
        if self.process:
            try:
                self.process.wait(timeout=2)
            except subprocess.TimeoutExpired:
                self.process.kill()
