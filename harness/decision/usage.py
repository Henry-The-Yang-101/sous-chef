"""Per-round Jev input usage, independent of command lifetime."""
from collections import deque
from dataclasses import dataclass
from pathlib import Path
import threading

from .protocol import DecisionError


def round_key(session, control):
    # Older bridges only expose the kitchen epoch, which can also change on
    # controller reassignment. Prefer the native round ID when present.
    if control.get('reason') == 'outside_local_round':
        return None
    ident = control.get('roundId', control.get('kitchenEpoch'))
    return (session, ident) if session and ident is not None else None


@dataclass
class _Round:
    key: tuple
    input_tokens: int = 0
    pending: int = 0


class RoundUsage:
    def __init__(self, path=None):
        self.path = Path(path) if path is not None else None
        self.lock = threading.Lock()
        self.current = None
        self.ended = deque()
        self.closed = False
        self.saved = None
        self.total = 0
        self.error = None
        self._load()

    def _load(self):
        try:
            rows = self.path.read_bytes().splitlines() if self.path else []
            if any(not row.isdigit() or int(row) == 0 for row in rows):
                raise ValueError('Expected one positive token count per line')
            saved = [int(row) for row in rows]
        except FileNotFoundError:
            saved = []
        except (OSError, ValueError) as error:
            self.error = f'Could not read Jev usage history: {error}'
            return
        self.saved, self.total, self.error = saved, sum(saved), None

    def observe(self, key):
        with self.lock:
            if self.closed:
                return
            if key != (self.current.key if self.current else None):
                if self.current:
                    self.ended.append(self.current)
                self.current = _Round(key) if key is not None else None
            self._flush()

    def begin(self, key):
        with self.lock:
            if self.closed or not self.current or self.current.key != key:
                raise DecisionError('The round changed before the Jev request was sent')
            round_usage = self.current
            round_usage.pending += 1

        def finish(input_tokens):
            with self.lock:
                round_usage.input_tokens += input_tokens
                round_usage.pending -= 1
                self._flush()

        return finish

    def _flush(self):
        if self.saved is None:
            self._load()
        # A response may arrive after the final bell. Finish its original row
        # before saving a later round, even if that later round already ended.
        while self.ended and not self.ended[0].pending:
            tokens = self.ended[0].input_tokens
            if tokens:
                if self.saved is None:
                    return
                try:
                    if self.path:
                        self.path.parent.mkdir(parents=True, exist_ok=True)
                        with self.path.open('a+b') as history:
                            if history.tell():
                                history.seek(-1, 2)
                                if history.read(1) != b'\n':
                                    history.write(b'\n')
                            history.write(f'{tokens}\n'.encode('ascii'))
                except OSError as error:
                    self.error = f'Could not save Jev usage history: {error}'
                    return  # Keep the unsaved round for the next flush.
                self.saved.append(tokens)
                self.total += tokens
                self.error = None
            self.ended.popleft()

    def snapshot(self):
        with self.lock:
            return {'currentInputTokens': self.current.input_tokens if self.current else 0,
                    'savedRounds': self.saved.copy() if self.saved is not None else [],
                    'savedInputTokens': self.total if self.saved is not None else None,
                    'error': self.error}

    def close(self):
        with self.lock:
            self.closed = True
            if self.current:
                self.ended.append(self.current)
                self.current = None
            self._flush()
            return self.error
