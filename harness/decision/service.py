"""One bounded inbox and async worker; preview/clarification are never queue entries."""
import asyncio
from collections import deque
import copy
import hashlib
import json
import threading
import time
import uuid

from clients.capabilities import REGISTRY_HASH, ValidationError
from harness.plans import identity, validate_plan
from harness.scheduler import context_of
from .compiler import Compiler, Clarification, Unsupported, build_context, choice
from .prompts import examples
from .config import MODEL, Settings, load_settings
from .protocol import DecisionError
from .transport import DecisionClient, HttpTransport
from .usage import round_key


class InterpreterService:
    def __init__(self, bridge, gate, settings=None, transport=None, usage=None):
        self.bridge, self.gate = bridge, gate
        self.configuration_error = None
        try:
            self.settings = settings or load_settings()
        except (OSError, ValueError):
            self.settings = Settings()
            self.configuration_error = 'Jev configuration could not be loaded. Check the API key format and .env file permissions.'
        self.configured = bool(self.settings.api_key) or transport is not None
        self.transport = transport or HttpTransport(self.settings.api_key)
        self.client = DecisionClient(self.transport, gate, self.settings, usage)
        self.mode = 'preview'
        self.inbox, self.seen = deque(), {}
        self.active = self.pending = self.result = self.future = None
        self.revision = 0
        self.status = 'idle'
        self.retain_until = time.monotonic() + 600
        self.loop = asyncio.new_event_loop()
        self.worker = threading.Thread(target=self._work, name='jev-interpreter', daemon=True)
        self.closed = False
        gate.listeners.append(self.invalidate)
        self.worker.start()

    def _work(self):
        asyncio.set_event_loop(self.loop)
        self.loop.run_forever()

    def invalidate(self):
        # Invoked under gate.lock, including for microphone failures and Stop.
        self.revision += 1
        self.inbox.clear()
        self.pending = self.active = self.result = None
        self.status = 'idle'

    def _schedule(self, coroutine):
        future = asyncio.run_coroutine_threadsafe(coroutine, self.loop)
        self.future = future
        self.gate.inflight.add(future)
        def finished(completed):
            with self.gate.lock:
                self.gate.inflight.discard(completed)
        future.add_done_callback(finished)

    def submit(self, command):
        with self.gate.lock:
            self.gate.check(self.gate.generation)
            if not self.configured:
                raise DecisionError('Add JEV_API_KEY to .env and restart the harness')
            if not isinstance(command, dict) or not isinstance(command.get('text'), str) or not 1 <= len(command['text'].strip()) <= 4000:
                raise DecisionError('Enter a nonempty command of at most 4,000 characters')
            command = copy.deepcopy(command)
            command['id'] = identity(command.get('id', uuid.uuid4().hex), 'command_id')
            command['text'] = command['text'].strip()
            # Speech generations are authoritative, even if a helper result arrives late.
            if command.get('speech_generation', self.gate.generation) != self.gate.generation:
                raise DecisionError('Speech command expired')
            fingerprint = hashlib.sha256(json.dumps(command, sort_keys=True).encode()).digest()
            prior = self.seen.get(command['id'])
            if prior:
                if prior[0] != fingerprint: raise DecisionError('Command ID was reused for different text')
                return
            if len(self.inbox) >= 3:
                raise DecisionError('Command inbox is full; finish or cancel the current interpretation')
            self.retain_until = time.monotonic() + 600
            self.seen[command['id']] = (fingerprint, time.monotonic())
            if self.pending and self.pending['kind'] == 'clarification':
                if self.status == 'interpreting_reply':
                    raise DecisionError('Already interpreting a clarification reply')
                self.status = 'interpreting_reply'
                self._schedule(self._reply(command, self.pending))
            else:
                self.inbox.append(command)
                if self.pending and self.pending['kind'] == 'preview' and self.status != 'interpreting_followup':
                    self.status = 'interpreting_followup'
                    self._schedule(self._preview_followup(self.pending))
                else:
                    self._advance()

    def _advance(self):
        if self.active or self.pending or not self.inbox or self.gate.muted or self.closed:
            return
        command = self.inbox.popleft()
        try:
            world, queue = self.bridge.observation(), self.bridge.scheduler.snapshot()
            native = context_of(world)
            if not native: raise DecisionError('Waiting for a fresh kitchen and Player 2')
            context = build_context(world, queue)
            generation = self.gate.generation
            budget = dict(deadline=time.monotonic() + self.settings.compile_seconds,
                          requests=0, input_tokens=0, output_tokens=0, last_ms=None)
            async def ask(request):
                return await self._ask(active, request)
            compiler = Compiler(command, context, ask, self.settings)
            active = {'compiler': compiler, 'generation': generation, 'context': native,
                      'round': round_key(world['sessionId'], world['control']),
                      'revision': queue['revision'], 'commandGeneration': queue.get('commandGeneration', 0),
                      'budget': budget, 'id': command['id']}
            self.active, self.result, self.status = active, None, 'interpreting'
            self.revision += 1
            self._schedule(self._compile(active))
        except (ValueError, KeyError) as error:
            self.status, self.result = 'error', {'message': str(error)}
            self.inbox.clear()

    def _current(self, active, *, deadline=True):
        self.gate.check(active['generation'])
        if active is not self.active:
            raise DecisionError('This interpretation was discarded')
        world, queue = self.bridge.observation(), self.bridge.scheduler.snapshot()
        if (context_of(world) != active['context'] or round_key(world['sessionId'], world['control']) != active['round']
                or queue['revision'] != active['revision']
                or queue.get('commandGeneration', 0) != active['commandGeneration']
                or world.get('capabilities', {}).get('registry_hash') != REGISTRY_HASH):
            raise DecisionError('Kitchen or queue changed; submit the command again')
        if deadline and time.monotonic() > active['budget']['deadline']:
            raise DecisionError('Interpretation expired; submit the command again')
        return world

    async def _ask(self, active, request):
        self._current(active)
        return await self.client.ask(request, active['generation'], active['budget'],
                                     lambda: self._current(active), round_key=active['round'])

    async def _compile(self, active):
        try:
            outcome = await active['compiler'].compile()
            while await self._arrivals(active):
                with self.gate.lock:
                    self._current(active)
                    if any(c['id'] not in active.get('independent_followups', set()) for c in self.inbox):
                        continue  # A follow-up arrived between classification and adoption.
                    self.result = dict(outcome, metrics=self._metrics(active))
                    self.revision += 1
                    if outcome['kind'] == 'no_change':
                        self.status, self.active = 'no_change', None
                        self._advance()
                    elif self.mode == 'automatic':
                        self._accept(active, outcome)
                    else:
                        self.status = 'preview'
                        self.pending = {'kind': 'preview', 'id': uuid.uuid4().hex, 'active': active,
                                        'outcome': outcome, 'expires': time.monotonic() + self.settings.preview_seconds}
                    break
        except Clarification as clarification:
            self._clarify(active, clarification)
        except asyncio.CancelledError:
            raise
        except Exception as error:
            with self.gate.lock:
                if self.active is not active: return
                self.status = 'unsupported' if isinstance(error, Unsupported) else 'error'
                self.result = {'message': str(error) if isinstance(error, ValueError) else 'Interpretation failed; no work was accepted',
                               'metrics': self._metrics(active)}
                self.pending = self.active = None
                # Later independent utterances retain FIFO order and get fresh state.
                self.revision += 1
                self._advance()

    def _clarify(self, active, clarification):
        with self.gate.lock:
            if self.active is not active: return
            self.status = 'clarification'
            self.pending = {'kind': 'clarification', 'id': uuid.uuid4().hex, 'active': active,
                            'clarification': clarification, 'expires': time.monotonic() + self.settings.clarification_seconds}
            self.result = {'message': clarification.message, 'metrics': self._metrics(active)}
            self.revision += 1

    async def _arrivals(self, active):
        """Classify buffered follow-ups before the unfinished draft can be accepted.

        Separate commands stay FIFO. Corrections revise the unfinished command;
        a model never gets to retroactively undo an accepted task.
        """
        inspected = active.setdefault('independent_followups', set())
        while True:
            with self.gate.lock:
                self._current(active)
                incoming = next((c for c in self.inbox if c['id'] not in inspected), None)
                if incoming is None: return True
                incoming = copy.deepcopy(incoming)
            options = {'separate': 'An independent new command to append after this one, including then/after that.',
                       'correction': 'Explicitly revises or replaces the unfinished original command.',
                       'cancel': 'Cancels only the unfinished original command.',
                       'ambiguous': 'Cannot tell whether this revises the original or adds independent work.'}
            request = {'model': MODEL, 'state': {'unfinished_command': active['compiler'].command['text'],
                                               'new_utterance': incoming['text'],
                                               **({'unfinished_interpretation': active['compiler'].describe(active['compiler'].tasks)}
                                                  if active['compiler'].tasks else {})},
                       'questions': {'relationship': choice('Classify how the new utterance relates to the unfinished command. '
                           'No work from the unfinished command has been accepted yet. Do not treat an explicit correction as appended work.' + examples('relationship'), options)}}
            response = await self._ask(active, request)
            answer = response['answers']['relationship']
            key = answer['choice']
            if answer['probabilities'][key] < self.settings.critical_probability:
                key = 'ambiguous'
            with self.gate.lock:
                self._current(active)
                if key == 'separate':
                    inspected.add(incoming['id'])
                    continue
                self.inbox = deque(c for c in self.inbox if c['id'] != incoming['id'])
                if key == 'ambiguous':
                    # Do not accept either interpretation of an ambiguous follow-up.
                    self.inbox.clear()
                    raise Clarification('Restate the complete corrected command; neither unfinished command was accepted.', 'followup', 'relationship', {})
                if key == 'correction':
                    incoming['text'] = active['compiler'].command['text'] + '\nUser correction: ' + incoming['text']
                    if len(incoming['text']) > 4000:
                        raise DecisionError('Corrected command is too long; restate it')
                    self.inbox.appendleft(incoming)
                self.active = self.pending = None
                self.status, self.result = 'cancelled', None
                self._advance()
                return False

    async def _preview_followup(self, pending):
        active = pending['active']
        try:
            active['budget']['deadline'] = time.monotonic() + self.settings.compile_seconds
            if await self._arrivals(active):
                with self.gate.lock:
                    if self.pending is pending: self.status = 'preview'
        except Clarification as clarification:
            self._clarify(active, clarification)
        except Exception as error:
            with self.gate.lock:
                if self.active is active:
                    self.pending = self.active = None
                    self.status, self.result = 'error', {'message': str(error) if isinstance(error, ValueError) else 'Follow-up interpretation failed; restate the command'}

    def _answer(self, pending, key):
        active = pending['active']
        self._current(active, deadline=False)
        if pending is not self.pending or time.monotonic() >= pending['expires']:
            raise DecisionError('Clarification expired; submit the complete command again')
        active['compiler'].resolve(pending['clarification'], key)
        # User thinking time is separate from the bounded model-time budget.
        active['budget']['deadline'] = time.monotonic() + self.settings.compile_seconds
        self.pending, self.status = None, 'interpreting'
        self.revision += 1
        self._schedule(self._compile(active))

    async def _reply(self, command, pending):
        active = pending['active']
        clarification = pending['clarification']
        options = {'answer': 'Answers only the pending question using one of its choices.',
                   'cancel': 'Cancels this unfinished command, without changing accepted game work.',
                   'new_command': 'A separate complete instruction or a correction of the whole unfinished command.',
                   'ambiguous': 'Unrelated speech or a reply that does not resolve the question.'}
        values = clarification.options | {'ambiguous': 'No unique supplied answer fits the reply.'}
        try:
            if not clarification.options:
                # A restatement gets fresh state, not a yes/no interpretation of an absent option.
                with self.gate.lock:
                    self._current(active, deadline=False)
                    self.pending = self.active = None
                    self.inbox.appendleft(command)
                    self._advance()
                return
            request = {'model': MODEL, 'state': {'original_utterance': active['compiler'].command['text'],
                'pending_question': clarification.message, 'allowed_answers': clarification.options, 'reply': command['text']},
                'questions': {'route': choice('Does the reply answer the pending question, cancel it, or give a new complete command?', options),
                              'answer': choice('If the reply answers the pending question, which allowed answer does it select? Read only the reply and pending question.' + examples('reply_answer'), values)}}
            # Reply has its own per-request deadline but consumes the original request count.
            active['budget']['deadline'] = time.monotonic() + self.settings.compile_seconds
            response = await self._ask(active, request)
            with self.gate.lock:
                self._current(active)
                if self.pending is not pending or time.monotonic() >= pending['expires']:
                    raise DecisionError('Clarification expired')
                route = response['answers']['route']
                key = route['choice']
                if route['probabilities'][key] < self.settings.choice_probability:
                    key = 'ambiguous'
                if key == 'answer':
                    answer = response['answers']['answer']
                    if answer['choice'] in clarification.options and answer['probabilities'][answer['choice']] >= self.settings.critical_probability:
                        self._answer(pending, answer['choice'])
                        return
                elif key in ('cancel', 'new_command'):
                    self.pending = self.active = None
                    self.status = 'cancelled'
                    if key == 'new_command': self.inbox.appendleft(command)
                    self._advance()
                    return
                self.status = 'clarification'
        except (ValueError, OSError, TimeoutError):
            with self.gate.lock:
                if self.pending is pending: self.status = 'clarification'

    def _accept(self, active, outcome):
        # Queue-only Stop needs the same compare-and-swap as all other edits;
        # hold only local locks through validation + the journal transaction.
        with self.bridge.scheduler.lock:
            return self._accept_locked(active, outcome)

    def _accept_locked(self, active, outcome):
        world = self._current(active, deadline=False)
        if outcome['kind'] == 'plan':
            raw = outcome['plan']
            validate_plan(raw, capabilities=world['capabilities'])
            self._exact_bindings(active['compiler'].context['semantic'], world['capabilities']['semantic_catalog'], raw)
            receipt = self.bridge.scheduler.accept(raw, active['context'])
        else:
            edit = outcome['edit']
            if edit['operation'] == 'stop_all':
                # The scheduler's cancellation barrier uses the normal independent
                # native cancel path; invalidate every other unfinished command now.
                self.gate.invalidate()
                receipt = self.bridge.scheduler.stop()
            else:
                receipt = self.bridge.scheduler.edit(edit)
        self.status = 'accepted'
        self.result = dict(outcome, receipt=receipt, metrics=self._metrics(active))
        self.active = self.pending = None
        self.revision += 1
        self._advance()

    @staticmethod
    def _exact_bindings(before, after, plan):
        def targets(semantic):
            return {t['id']: t.get('objectInstanceId') for t in semantic.get('targets', []) + semantic.get('displayOptions', {}).get('items', []) if 'id' in t}
        old, new = targets(before), targets(after)
        def movement(semantic):
            value = semantic.get('movement') or {}
            entries = value.get('points', []) + value.get('platforms', []) + value.get('stations', [])
            return {c['id']: (value.get('epoch'), c.get('binding'),
                             c.get('objectInstanceId') if c.get('binding') == 'station'
                             else (c.get('supportId'), c.get('supportLocalPosition'))) for c in entries}
        old_movement, new_movement = movement(before), movement(after)
        old_orders, new_orders = ({order['id']: order for order in semantic.get('assemblyOrders', [])}
                                  for semantic in (before, after))
        for task in plan['tasks']:
            arguments = task['arguments']
            if task['action'] == 'navigate_to' and before.get('movement'):
                reference = arguments['destination_id']
                if reference not in old_movement or old_movement[reference] != new_movement.get(reference):
                    raise DecisionError('The movement destination changed or belongs to another kitchen; submit again')
            if task['action'] == 'assemble_dish':
                ident = arguments['order_id']
                original, current = old_orders.get(ident), new_orders.get(ident)
                if not original or not current or current.get('recipeId') != original.get('recipeId'):
                    raise DecisionError('The selected order is no longer available. Submit the command again.')
                allocation = current.get('allocation') or {}
                if any(ident not in allocation.get(group, ()) or ident not in old or old[ident] != new.get(ident)
                       for group in ('bases', 'components', 'equipment') for ident in arguments.get(group, ())):
                    raise DecisionError('The allocated assembly resources changed. Submit the command again.')
            # Ingredient and player semantics may be late bound; exact native
            # destination IDs must retain their observed object identity.
            for value in arguments.values():
                if isinstance(value, str) and value in old and (value not in new or new[value] != old[value]):
                    raise DecisionError('An exact target changed; submit the command again')

    @staticmethod
    def _metrics(active):
        return {k: v for k,v in active['budget'].items() if k != 'deadline'}

    def control(self, request):
        if not isinstance(request, dict): raise DecisionError('Missing interpreter control')
        operation = request.get('operation')
        with self.gate.lock:
            if request.get('generation') != self.gate.generation:
                raise DecisionError('Voice state changed; refresh the controls')
            if operation == 'mode':
                if self.active or self.inbox: raise DecisionError('Finish or discard the current interpretation first')
                if request.get('mode') not in ('preview', 'automatic'): raise DecisionError('Unknown interpreter mode')
                self.mode = request['mode']
                self.revision += 1
                return
            self.gate.check(self.gate.generation)
            if operation == 'submit':
                self.submit({'id': request.get('command_id', uuid.uuid4().hex), 'text': request.get('text')})
                return
            pending = self.pending
            if not pending or request.get('id') != pending['id']:
                raise DecisionError('This preview or clarification is no longer current')
            if time.monotonic() >= pending['expires']:
                self.pending = self.active = None
                self.status = 'expired'
                raise DecisionError('This preview or clarification expired')
            if operation == 'discard':
                if self.future: self.future.cancel()
                self.pending = self.active = None
                self.status, self.result = 'cancelled', None
                self.revision += 1
                self._advance()
            elif operation == 'answer' and pending['kind'] == 'clarification':
                self._answer(pending, request.get('key'))
            elif operation == 'accept' and pending['kind'] == 'preview':
                if self.status == 'interpreting_followup':
                    raise DecisionError('Wait for the follow-up command to be resolved')
                try:
                    self._accept(pending['active'], pending['outcome'])
                except DecisionError as error:
                    self.pending = self.active = None
                    self.status, self.result = 'expired', {'message': str(error)}
                    self.revision += 1
                    self._advance()
                    raise
            else:
                raise DecisionError('Unsupported interpreter control')

    def snapshot(self):
        with self.gate.lock:
            now = time.monotonic()
            if now >= self.retain_until and not self.active:
                self.result = None
            self.seen = {k:v for k,v in self.seen.items() if now-v[1] < 600}
            pending = self.pending
            if pending:
                try:
                    self._current(pending['active'], deadline=False)
                    if now >= pending['expires']: raise DecisionError('Interpretation expired')
                except ValueError as error:
                    self.pending = self.active = None
                    self.status, self.result = 'expired', {'message': str(error)}
                    self.revision += 1
                    self._advance()
                    pending = self.pending
            visible = None
            if pending:
                visible = {'id': pending['id'], 'kind': pending['kind'], 'remainingSeconds': max(0, round(pending['expires']-now))}
                if pending['kind'] == 'clarification':
                    visible.update(question=pending['clarification'].message, options=pending['clarification'].options)
            return {'revision': self.revision, 'generation': self.gate.generation, 'configured': self.configured,
                    'configurationError': self.configuration_error,
                    'model': MODEL, 'mode': self.mode, 'status': self.status, 'inbox': len(self.inbox),
                    'pending': visible, 'result': copy.deepcopy(self.result),
                    'metrics': self._metrics(self.active) if self.active else None}

    def close(self):
        with self.gate.lock:
            if self.closed:
                return
            self.closed = True
            if self.future: self.future.cancel()
            self.invalidate()
            self.gate.listeners.remove(self.invalidate)
        try:
            async def shutdown():
                tasks = [task for task in asyncio.all_tasks() if task is not asyncio.current_task()]
                for task in tasks: task.cancel()
                await asyncio.gather(*tasks, return_exceptions=True)
                await self.transport.close()
            asyncio.run_coroutine_threadsafe(shutdown(), self.loop).result(timeout=2)
        except Exception:
            pass
        self.loop.call_soon_threadsafe(self.loop.stop)
        self.worker.join(timeout=2)
        if not self.worker.is_alive(): self.loop.close()
