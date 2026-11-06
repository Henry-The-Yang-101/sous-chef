"""One serialization point for mute, all model attempts, and result adoption.

Transport.start MUST be nonblocking and start the attempt synchronously (no
deferred retry/dispatch). It returns a cancellable Future. Network waits belong
outside this lock. M4's real adapter must use this same contract for every stage.
"""
from concurrent.futures import Future
import threading


class DispatchGate:
    def __init__(self):
        self.lock = threading.RLock()
        self.generation = 0
        self.muted = True
        self.inflight = set()
        self.listeners = []

    def check(self, generation):
        with self.lock:
            if self.muted or generation != self.generation:
                raise ValueError('Voice / Jev is off or this command expired')

    def run(self, generation, action):
        """Serialize a nonblocking network write or local result adoption with mute."""
        with self.lock:
            self.check(generation)
            return action()

    def invalidate(self, muted=None):
        with self.lock:
            self.generation += 1
            if muted is not None:
                self.muted = muted
            for future in tuple(self.inflight):
                future.cancel()
            self.inflight.clear()
            for listener in self.listeners:
                listener()
            return self.generation

    def dispatch(self, generation, transport, request):
        with self.lock:
            if self.muted or generation != self.generation:
                raise ValueError('Voice / Jev is off or this command expired')
            future = transport.start(request)
            self.inflight.add(future)
            return future

    def adopt(self, generation, future, accept):
        with self.lock:
            self.inflight.discard(future)
            if self.muted or generation != self.generation or future.cancelled():
                return False
            accept(future.result())
            return True


class FakeTransport:
    """Deterministic local echo only: no network, plans, or native side effects."""
    def start(self, request):
        future = Future()
        future.set_result({'transport': 'fake', 'commandId': request['id'],
                           'message': 'Local fake transport received: ' + request['text']})
        return future
