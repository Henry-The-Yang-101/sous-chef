"""Single-writer queue transactions; native I/O never runs under the state lock.

Only explicit native success advances a sequence. Dispatch intent is committed
before send. Unknown delivery is reconciled by read-only receipt lookup, never
replayed. The bridge adapter supplies fresh observations and fenced native I/O.
"""
import copy
import json
import threading
import uuid

from clients.capabilities import REGISTRY_VERSION, ValidationError, registry
from clients.control import ControlError
from harness.plan_store import PlanStore
from harness.plans import NativeAction, closed, identity, validate_plan


class QueueConflict(ValueError):
    pass


def unique_id():
    return uuid.uuid4().hex


def context_of(world):
    control = world.get('control', {})
    if (not world.get('fresh') or control.get('receiptProtocolVersion') != 1
            or not control.get('kitchenEpoch') or control.get('chefInstanceId') is None):
        return None
    return {'session': world['sessionId'], 'epoch': control['kitchenEpoch'],
            'chef': control['chefInstanceId'], 'fence': control['dispatchGeneration']}


def same_kitchen(a, b):
    return a and b and all(a[k] == b[k] for k in ('session', 'epoch', 'chef'))


def task_spec(task):
    return {'kind': 'native_action' if isinstance(task, NativeAction) else 'native_task',
            'action': task.action, 'arguments': task.arguments, 'capability_version': task.capability_version,
            'repeat': {'mode': task.repeat.mode, **({'count': task.repeat.count} if task.repeat.count is not None else {})}}


class Scheduler:
    CAPACITY = 10
    RETRYABLE = {'task_not_available', 'action_in_progress', 'already_claimed', 'game_unfocused',
                 'paused', 'input_suppressed', 'player2_not_ready'}

    def __init__(self, bridge, path=':memory:'):
        self.bridge = bridge
        self.lock = threading.RLock()
        self.worker_lock = threading.Lock()
        self.fault = None
        self.store = PlanStore(path)
        self.data = self.store.load() or dict(version=1, revision=0, commandGeneration=0, context=None, active=None,
            pending=[], paused=False, reason=None, barrier=None, outcomes=[], lastOperation=None)
        self.data.setdefault('commandGeneration', 0)
        if self.data['version'] != 1:
            raise RuntimeError('Unsupported queue journal version')
        if self.data['active'] and self.data['active']['requestId']:
            self.data['active']['state'] = 'reconciling'
            self.data['active']['reason'] = 'Checking native receipt after harness restart'
            self.store.save(self.data)

    def snapshot(self):
        with self.lock:
            return dict(copy.deepcopy(self.data), capacity=self.CAPACITY, error=self.fault)

    def world(self):
        # Avoid UI/voice locks while holding the scheduler lock. Test adapters
        # and existing native-only users may expose just state().
        return getattr(self.bridge, 'observation', self.bridge.state)()

    def _save(self, value, operation=None):
        # Commit first: storage failures cannot leave an unjournaled accepted task.
        if value != self.data or operation:
            try:
                self.store.save(value, operation)
            except Exception as error:
                self.freeze('Queue journal unavailable: ' + str(error))
                raise
            self.data = value

    def freeze(self, reason):
        with self.lock:
            self.fault = reason

    def _revision(self, expected):
        if self.fault:
            raise QueueConflict(self.fault)
        if type(expected) is not int or expected != self.data['revision']:
            raise QueueConflict('Queue changed; review the current queue and try again')

    def _current(self, world, expected_context):
        current = context_of(world)
        if (current is None or expected_context != current or
                world.get('control', {}).get('reason') in ('outside_local_round', 'player2_absent_or_ambiguous', 'player2_not_local')):
            raise QueueConflict('Kitchen or control changed; refresh the queue')
        if self.data['context'] is not None and self.data['context'] != current:
            raise QueueConflict('Kitchen is being reconciled; try again after the next observation')
        return current

    @staticmethod
    def _validate_order(active, pending, outcomes):
        if len(pending) > Scheduler.CAPACITY:
            raise ValidationError('Queue capacity is ten pending tasks')
        ordered = ([active] if active else []) + pending
        if any(t['spec']['repeat']['mode'] == 'until_cancelled' for t in ordered[:-1]):
            raise ValidationError('Unlimited work must be at the end of the whole queue')
        seen = {t['id'] for t in outcomes if t['state'] == 'succeeded'}
        for entry in ordered:
            if not set(entry['dependsOn']) <= seen:
                raise QueueConflict('This edit breaks a task dependency; replace the dependent plan instead')
            seen.add(entry['id'])

    def accept(self, raw, expected_context):
        # The same immutable plan API will be used by the future voice compiler.
        payload = json.dumps({'plan': raw, 'context': expected_context}, sort_keys=True)
        if not isinstance(raw, dict):
            raise ValidationError('Expected a typed plan')
        ident = identity(raw.get('command_id'), 'command_id')
        with self.lock:
            previous = self.store.operation(ident, payload)
            if previous is not None:
                return previous
            self._revision(raw.get('expected_queue_revision'))
            world = self.world()
            current = self._current(world, expected_context)
            plan = validate_plan(raw, capabilities=world['capabilities'])
            if self.data['barrier']:
                raise QueueConflict('Native cancellation is still being reconciled')
            value = copy.deepcopy(self.data)
            value['context'] = current
            entries = []
            for task in plan.tasks:
                entries.append(dict(id=unique_id(), commandId=plan.command_id, spec=task_spec(task),
                    dependsOn=[entries[-1]['id']] if entries else [], state='pending', reason=None,
                    requestId=None, after='continue_pending'))
            entries[-1]['after'] = plan.after_plan
            active, pending = value['active'], value['pending']
            removed = []
            if plan.queue_edit in ('replace_pending', 'replace_all'):
                removed = pending
                pending = []
            if plan.queue_edit in ('replace_active', 'replace_all'):
                # The old root's dependents must be explicitly replaced as well.
                self._validate_order(None, entries + pending, value['outcomes'])
                if active:
                    removed.append(active)
                    active = None
                value['barrier'] = self._barrier(current)
                value['paused'], value['reason'] = False, None
            pending = pending + entries if plan.queue_edit == 'append' else entries + pending
            self._validate_order(active, pending, value['outcomes'])
            for entry in removed:
                self._outcome(value, entry, 'cancelled', 'Replaced by an explicit queue edit')
            value['active'], value['pending'] = active, pending
            # An explicit new plan may follow Stop, but ordinary Add does not
            # silently resume a queue paused by a failure or user choice.
            value['revision'] += 1
            result = {'operationId': ident, 'revision': value['revision'], 'taskIds': [e['id'] for e in entries]}
            value['lastOperation'] = dict(result, message='Plan accepted: ' + plan.queue_edit.replace('_', ' '))
            self._save(value, (ident, payload, result))
            return result

    @staticmethod
    def _barrier(context):
        return {'id': unique_id(), 'context': copy.deepcopy(context), 'state': 'pending'}

    @staticmethod
    def _outcome(value, entry, state, reason):
        outcome = dict(copy.deepcopy(entry), state=state, reason=reason)
        value['outcomes'].append(outcome)
        retained = ([value['active']] if value['active'] else []) + value['pending']
        needed = {dep for t in retained for dep in t['dependsOn']}
        value['outcomes'] = [t for i, t in enumerate(value['outcomes']) if i >= len(value['outcomes']) - 50 or t['id'] in needed]

    def edit(self, request):
        fields = ('operation_id', 'expected_queue_revision', 'operation', 'task_id', 'task_ids')
        closed(request, fields, fields[:3], 'queue edit')
        ident = identity(request['operation_id'], 'operation_id')
        payload = json.dumps(request, sort_keys=True)
        with self.lock:
            previous = self.store.operation(ident, payload)
            if previous is not None:
                return previous
            self._revision(request['expected_queue_revision'])
            value = copy.deepcopy(self.data)
            op = request['operation']
            if op not in ('clear_pending', 'remove_pending', 'reorder_pending', 'pause_pending', 'resume_pending', 'skip_active'):
                raise ValidationError('Unsupported queue edit')
            if set(request) - set(fields[:3]) != ({'task_id'} if op == 'remove_pending' else {'task_ids'} if op == 'reorder_pending' else set()):
                raise ValidationError('Unsupported fields for this queue edit')
            if op == 'clear_pending':
                for entry in value['pending']:
                    self._outcome(value, entry, 'cancelled', 'Pending queue cleared')
                value['pending'] = []
            elif op == 'remove_pending':
                ident_to_remove = identity(request['task_id'], 'task_id')
                entry = next((t for t in value['pending'] if t['id'] == ident_to_remove), None)
                if entry is None:
                    raise QueueConflict('That pending task is no longer present')
                value['pending'].remove(entry)
                self._validate_order(value['active'], value['pending'], value['outcomes'])
                self._outcome(value, entry, 'cancelled', 'Removed from queue')
            elif op == 'reorder_pending':
                ids = request['task_ids']
                if not isinstance(ids, list) or any(not isinstance(i, str) for i in ids) or len(ids) != len(value['pending']) or set(ids) != {t['id'] for t in value['pending']}:
                    raise QueueConflict('Reorder must name every pending task exactly once')
                by_id = {t['id']: t for t in value['pending']}
                value['pending'] = [by_id[i] for i in ids]
                self._validate_order(value['active'], value['pending'], value['outcomes'])
            elif op == 'pause_pending':
                value['paused'], value['reason'] = True, 'Dispatch paused by user'
            elif op == 'resume_pending':
                world = self.world()
                self._current(world, value['context'])
                if value['barrier'] or (value['active'] and value['active']['state'] == 'reconciling'):
                    raise QueueConflict('Resolve the unknown native outcome before resuming')
                self._validate_order(value['active'], value['pending'], value['outcomes'])
                value['paused'], value['reason'] = False, None
            elif op == 'skip_active':
                if value['barrier']:
                    raise QueueConflict('Cancellation is already pending')
                if not value['active']:
                    raise QueueConflict('There is no active task to skip')
                self._validate_order(None, value['pending'], value['outcomes'])
                self._outcome(value, value['active'], 'cancelled', 'Explicitly skipped; partial effects remain')
                value['active'] = None
                value['barrier'] = self._barrier(value['context'])
                value['paused'], value['reason'] = False, None
            value['revision'] += 1
            result = {'operationId': ident, 'revision': value['revision']}
            value['lastOperation'] = dict(result, message=op.replace('_', ' ').capitalize())
            self._save(value, (ident, payload, result))
            return result

    def stop(self):
        """Durably invalidate pending/late work before priority native Stop I/O."""
        with self.lock:
            value = copy.deepcopy(self.data)
            for entry in ([value['active']] if value['active'] else []) + value['pending']:
                self._outcome(value, entry, 'cancelled', 'Stopped; partial physical effects remain')
            value['active'], value['pending'] = None, []
            value['revision'] += 1
            value['commandGeneration'] += 1
            value['paused'], value['reason'] = False, 'Stopping native work'
            value['barrier'] = self._barrier(value['context'] or context_of(self.world()))
            value['lastOperation'] = {'message': 'Stop requested', 'revision': value['revision']}
            self._save(value)
            return copy.deepcopy(value['barrier'])

    def confirm_stop(self, barrier_id, current):
        with self.lock:
            if not self.data['barrier'] or self.data['barrier']['id'] != barrier_id:
                return
            value = copy.deepcopy(self.data)
            value.update(barrier=None, context=current, reason=None)
            value['lastOperation'] = {'message': 'Stopped', 'revision': value['revision']}
            self._save(value)

    def immediate(self):
        """Legacy controls remain immediate, with pending dispatch held aside."""
        with self.lock:
            if self.data['active'] or self.data['barrier']:
                raise QueueConflict('A queued task owns Player 2; use Stop or Run now first')
            value = copy.deepcopy(self.data)
            value['paused'], value['reason'] = True, 'Dispatch paused for an immediate manual action'
            value['revision'] += 1
            self._save(value)

    def _invalidate(self, value, current, reason):
        for entry in ([value['active']] if value['active'] else []) + value['pending']:
            self._outcome(value, entry, 'cancelled', reason)
        value.update(active=None, pending=[], barrier=None, context=current, paused=True, reason=reason)
        value['revision'] += 1
        value['commandGeneration'] += 1

    def tick(self):
        # One worker may reconcile/dispatch at a time. UI transactions and Stop
        # can still acquire the short-lived state lock during network I/O.
        if not self.worker_lock.acquire(blocking=False):
            return
        try:
            self._tick()
        finally:
            self.worker_lock.release()

    def _tick(self):
        if self.fault:
            return
        intent = None
        with self.lock:
            world = self.world()
            current = context_of(world)
            value = copy.deepcopy(self.data)
            if current is None:
                if value['active']:
                    value['active'].update(state='reconciling', reason='Waiting for fresh native receipt')
                self._save(value)
                return
            if value['context'] and not same_kitchen(value['context'], current):
                self._invalidate(value, current, 'Kitchen or Player 2 changed; old work invalidated')
                self._save(value)
                return
            if value['barrier']:
                barrier = value['barrier']
                if barrier['context'] and not same_kitchen(barrier['context'], current):
                    self._invalidate(value, current, 'Kitchen changed during cancellation')
                    self._save(value)
                    return
                if barrier['context'] is None:
                    barrier['context'] = current
                intent = ('cancel', copy.deepcopy(barrier))
            elif value['context'] and value['context']['fence'] != current['fence']:
                self._invalidate(value, current, 'Native Stop invalidated the queue')
            elif value['active'] and value['active']['requestId']:
                intent = ('receipt', copy.deepcopy(value['active']))
            elif not value['paused'] and (value['active'] or value['pending']):
                control = world['control']
                first = value['active'] or value['pending'][0]
                if not control.get('available') or control.get('suspended') or control.get('claimed') or control.get('action', {}).get('state') == 'running' or control.get('navigation', {}).get('state') == 'navigating':
                    first.update(state='waiting_to_start', reason=control.get('reason') or 'Player 2 is busy')
                else:
                    try:
                        self._validate_order(value['active'], value['pending'], value['outcomes'])
                        raw = dict(schema_version=registry()['plan_schema_version'], capability_version=REGISTRY_VERSION,
                            command_id=first['commandId'], expected_queue_revision=value['revision'], queue_edit='append',
                            tasks=[first['spec']], after_plan=first['after'], on_failure='pause_pending')
                        validate_plan(raw, capabilities=world['capabilities'])
                    except ValueError as error:
                        if value['active']:
                            value['active'] = None
                        else:
                            value['pending'].pop(0)
                        self._outcome(value, first, 'failed', str(error))
                        value['paused'], value['reason'] = True, str(error)
                    else:
                        if value['active'] is None:
                            first = value['pending'].pop(0)
                        first.update(state='dispatching', reason=None, requestId=unique_id(), context=current)
                        value['active'] = first
                        intent = ('execute', copy.deepcopy(first))
            if value['context'] is None:
                value['context'] = current
            self._save(value)  # Dispatch intent exists on disk before sending.
        if intent is None:
            return
        operation, entry = intent
        try:
            result = self.bridge.queue_io(operation, entry)
        except Exception as error:
            # No retry of execute, even if the send might not have happened.
            with self.lock:
                value = copy.deepcopy(self.data)
                active = value['active']
                if operation != 'cancel' and active and active['requestId'] == entry['requestId']:
                    if isinstance(error, ControlError) and str(error) == 'request_id_conflict':
                        self._outcome(value, active, 'failed', 'Native request ID conflicts with another payload')
                        value['active'] = None
                        value['paused'], value['reason'] = True, 'Conflicting native request identity; dispatch paused'
                    else:
                        active.update(state='reconciling', reason='Native outcome unknown: ' + str(error))
                elif operation == 'cancel' and value['barrier'] and value['barrier']['id'] == entry['id']:
                    value['reason'] = 'Cancellation unconfirmed: ' + str(error)
                self._save(value)
            return
        with self.lock:
            value = copy.deepcopy(self.data)
            if operation == 'cancel':
                if not value['barrier'] or value['barrier']['id'] != entry['id']:
                    return
                latest = context_of(self.world())
                if latest and (not same_kitchen(entry['context'], latest) or latest['fence'] > entry['context']['fence'] + 1):
                    self._invalidate(value, latest, 'Control changed again during cancellation; old work invalidated')
                elif (latest and result and result.get('id') == entry['id'] and result.get('state') == 'succeeded'
                      and latest['fence'] == entry['context']['fence'] + 1):
                    value['barrier'] = None
                    value['context'] = latest
                    if not value['paused']:
                        value['reason'] = None
                    value['lastOperation'] = {'message': 'Native cancellation confirmed', 'revision': value['revision']}
                elif latest and result and result.get('state') == 'rejected' and result.get('reason') == 'stale_dispatch_context':
                    self._invalidate(value, latest, 'Native Stop invalidated pending work during cancellation')
                else:
                    value['reason'] = 'Cancellation outcome unknown; dispatch held'
                self._save(value)
                return
            active = value['active']
            if not active or active['requestId'] != entry['requestId']:
                return  # Stop/replacement won the race; never revive late work.
            if result is None or result.get('id') != active['requestId'] or result.get('epoch') != active['context']['epoch']:
                active.update(state='reconciling', reason='Native receipt missing; use Stop or replace the plan to resolve')
            elif result['state'] in ('running', 'waiting', 'suspended'):
                active.update(state=result['state'], reason=result.get('reason'), receipt=result)
            elif result['state'] == 'rejected' and result.get('reason') in self.RETRYABLE:
                active.update(state='waiting_to_start', reason=result['reason'], receipt=result, requestId=None)
                # Keep the active slot reserved: concurrent appends may already
                # have filled all ten pending slots while this attempt ran.
                value['active'] = active
            elif result['state'] in ('succeeded', 'failed', 'blocked', 'cancelled', 'rejected'):
                success = result['state'] == 'succeeded'
                active['receipt'] = result
                self._outcome(value, active, 'succeeded' if success else 'failed', result.get('reason'))
                value['active'] = None
                if not success or active['after'] == 'pause_pending':
                    value['paused'] = True
                    value['reason'] = 'Plan completed; pending dispatch paused' if success else 'Task failed: ' + str(result.get('reason'))
            else:
                active.update(state='reconciling', reason='Unrecognized native receipt; dispatch held')
            self._save(value)

    def close(self):
        with self.worker_lock, self.lock:
            self.store.close()
