import copy
import tempfile
import threading
import unittest
from pathlib import Path

from clients.capabilities import REGISTRY_HASH, REGISTRY_VERSION, registry
from harness.scheduler import Scheduler, QueueConflict, context_of


def spec(action='wash_dishes', count=1, arguments=None):
    return dict(kind='native_task', action=action, capability_version=1, arguments=arguments or {},
                repeat={'mode': 'until_cancelled'} if count is None else {'mode': 'finite', 'count': count})


def plan(revision=0, tasks=None, edit='append', command='command', after='continue_pending'):
    return dict(schema_version=registry()['plan_schema_version'], capability_version=REGISTRY_VERSION,
        command_id=command, expected_queue_revision=revision, queue_edit=edit,
        tasks=tasks or [spec()], after_plan=after, on_failure='pause_pending')


class Native:
    def __init__(self):
        self.world = dict(fresh=True, sessionId='session', control=dict(receiptProtocolVersion=1,
            kitchenEpoch='epoch', chefInstanceId=8, dispatchGeneration=0, available=True, claimed=False),
            capabilities=dict(compatible=True, fresh=True, version=REGISTRY_VERSION, registry_hash=REGISTRY_HASH,
                semantic_catalog={}, entries=[dict(name=c['name'], support='implemented') for c in registry()['contracts']]))
        self.receipts = {}
        self.calls = []
        self.lose_ack = False
        self.block = None
        self.arrived = threading.Event()
        self.next_rejection = None

    def state(self): return copy.deepcopy(self.world)

    def queue_io(self, operation, entry):
        self.calls.append((operation, copy.deepcopy(entry)))
        if operation == 'cancel':
            self.world['control']['dispatchGeneration'] += 1
            self.world['control']['claimed'] = False
            for receipt in self.receipts.values():
                if receipt['state'] in ('running', 'waiting', 'suspended'):
                    receipt['state'] = 'cancelled'
            return dict(id=entry['id'], state='succeeded', reason='stopped')
        request = entry['requestId']
        if operation == 'execute':
            if self.block:
                self.arrived.set()
                self.block.wait(3)
            receipt = dict(id=request, epoch=entry['context']['epoch'], state='running', reason=None)
            if self.next_rejection:
                receipt.update(state='rejected', reason=self.next_rejection)
                self.next_rejection = None
            self.receipts[request] = receipt
            self.world['control']['claimed'] = receipt['state'] == 'running'
            if self.lose_ack:
                self.lose_ack = False
                raise OSError('ack lost')
        return copy.deepcopy(self.receipts.get(request))

    def finish(self, state='succeeded', reason='task_complete'):
        request = next(reversed(self.receipts))
        self.receipts[request].update(state=state, reason=reason)
        self.world['control']['claimed'] = state in ('running', 'waiting', 'suspended')


class SchedulerTests(unittest.TestCase):
    def setUp(self):
        self.native = Native()
        self.s = Scheduler(self.native)
        self.addCleanup(self.s.close)

    def accept(self, tasks=None, edit='append', command=None, after='continue_pending'):
        return self.s.accept(plan(self.s.snapshot()['revision'], tasks, edit, command or f'c{self.s.snapshot()["revision"]}', after), context_of(self.native.state()))

    def edit(self, operation, **kwargs):
        return self.s.edit(dict(operation_id=f'e{self.s.snapshot()["revision"]}', expected_queue_revision=self.s.snapshot()['revision'], operation=operation, **kwargs))

    def test_atomic_validation_and_duplicate_operation(self):
        raw = plan(tasks=[spec(), spec(count=11)])
        with self.assertRaises(ValueError): self.s.accept(raw, context_of(self.native.state()))
        self.assertEqual(self.s.snapshot()['pending'], [])
        self.assertEqual(self.native.calls, [])
        raw = plan(tasks=[spec(), spec()])
        result = self.s.accept(raw, context_of(self.native.state()))
        self.assertEqual(self.s.accept(raw, context_of(self.native.state())), result)
        self.assertEqual(len(self.s.snapshot()['pending']), 2)
        with self.assertRaisesRegex(ValueError, 'different arguments'):
            self.s.accept(dict(raw, tasks=[spec()]), context_of(self.native.state()))
        with self.assertRaises(QueueConflict):
            self.s.accept(dict(raw, command_id='late'), context_of(self.native.state()))

    def test_sequential_only_after_success_waiting_suspended_not_complete(self):
        self.accept([spec(), spec('take_ready')])
        self.s.tick()
        self.assertEqual(len(self.s.snapshot()['pending']), 1)
        for state in ('waiting', 'suspended', 'running'):
            self.native.finish(state, 'waiting_for_dishes')
            self.s.tick()
            self.assertEqual(self.s.snapshot()['active']['state'], state)
            self.assertEqual(len([c for c in self.native.calls if c[0] == 'execute']), 1)
        self.native.finish()
        self.s.tick(); self.s.tick()
        self.assertEqual(self.s.snapshot()['active']['spec']['action'], 'take_ready')
        self.assertEqual(len([c for c in self.native.calls if c[0] == 'execute']), 2)
        self.assertEqual(self.s.snapshot()['revision'], 1)  # Progress is not an edit.

    def test_failure_pauses_and_does_not_satisfy_dependency(self):
        self.accept([spec(), spec()]); self.s.tick()
        self.native.finish('failed', 'lost_cargo'); self.s.tick(); self.s.tick()
        self.assertTrue(self.s.snapshot()['paused'])
        self.assertEqual(len(self.s.snapshot()['pending']), 1)
        with self.assertRaisesRegex(QueueConflict, 'dependency'): self.edit('resume_pending')
        self.edit('clear_pending'); self.edit('resume_pending')
        self.assertFalse(self.s.snapshot()['paused'])

    def test_lost_ack_is_adopted_without_replay_and_missing_receipt_holds(self):
        self.accept(); self.native.lose_ack = True; self.s.tick()
        self.assertEqual(self.s.snapshot()['active']['state'], 'reconciling')
        self.s.tick()
        self.assertEqual(self.s.snapshot()['active']['state'], 'running')
        self.native.receipts.clear(); self.s.tick()
        self.assertEqual(self.s.snapshot()['active']['state'], 'reconciling')
        with self.assertRaises(QueueConflict): self.edit('resume_pending')
        self.assertEqual(len([c for c in self.native.calls if c[0] == 'execute']), 1)

    def test_unlimited_entire_queue_and_capacity(self):
        self.accept([spec(count=None)])
        for edit in ('append', 'insert_next'):
            tasks = [spec()] if edit == 'append' else [spec(count=None)]
            with self.assertRaises(ValueError): self.accept(tasks, edit)
        self.s.tick()
        with self.assertRaises(ValueError): self.accept([spec()], 'insert_next')
        self.accept([spec()], 'replace_all')
        self.s.tick(); self.s.tick()
        self.assertIsNotNone(self.s.snapshot()['active'])
        self.accept([spec()] * 5)
        self.accept([spec()] * 5)
        with self.assertRaisesRegex(ValueError, 'capacity'): self.accept()
        self.assertEqual(len(self.s.snapshot()['pending']), 10)

    def test_remove_reorder_and_replace_preserve_dependencies(self):
        ids = self.accept([spec(), spec()])['taskIds']
        independent = self.accept()['taskIds'][0]
        self.edit('reorder_pending', task_ids=[independent, *ids])
        with self.assertRaisesRegex(QueueConflict, 'dependency'):
            self.edit('reorder_pending', task_ids=[ids[1], independent, ids[0]])
        with self.assertRaisesRegex(QueueConflict, 'dependency'):
            self.edit('remove_pending', task_id=ids[0])
        self.edit('remove_pending', task_id=ids[1])
        self.edit('remove_pending', task_id=independent)
        self.s.tick()
        self.accept([spec(), spec()], 'replace_active')
        self.s.tick(); self.s.tick()
        with self.assertRaisesRegex(QueueConflict, 'dependency'): self.accept(edit='replace_active')
        self.accept(edit='replace_all')

    def test_pause_clear_insert_and_after_plan_pause(self):
        self.accept(after='pause_pending'); self.accept([spec('take_ready')])
        self.edit('pause_pending'); self.s.tick(); self.assertEqual(self.native.calls, [])
        self.edit('resume_pending'); self.s.tick()
        self.accept([spec('put_out_fire')], 'insert_next')
        self.native.finish(); self.s.tick(); self.s.tick()
        self.assertTrue(self.s.snapshot()['paused'])
        self.assertEqual(self.s.snapshot()['pending'][0]['spec']['action'], 'put_out_fire')
        self.edit('clear_pending'); self.assertEqual(self.s.snapshot()['pending'], [])

    def test_restart_reconciles_journal_dispatch_before_send(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'queue.sqlite3'
            first = Scheduler(self.native, path)
            raw = plan(tasks=[spec(), spec()])
            first.accept(raw, context_of(self.native.state()))
            with self.assertRaisesRegex(RuntimeError, 'Another harness'): Scheduler(self.native, path)
            self.native.lose_ack = True
            first.tick()
            task_id = first.snapshot()['active']['id']
            first.close()
            resumed = Scheduler(self.native, path)
            try:
                resumed.tick()
                self.assertEqual(resumed.snapshot()['active']['id'], task_id)
                self.assertEqual(resumed.snapshot()['active']['state'], 'running')
                resumed.accept(raw, context_of(self.native.state()))
                self.assertEqual(len(resumed.snapshot()['pending']), 1)
                self.assertEqual(len([c for c in self.native.calls if c[0] == 'execute']), 1)
            finally: resumed.close()

    def test_stop_during_dispatch_ignores_late_result_and_revision(self):
        self.accept([spec(), spec()])
        self.native.block = threading.Event()
        thread = threading.Thread(target=self.s.tick)
        thread.start(); self.assertTrue(self.native.arrived.wait(1))
        barrier = self.s.stop()
        self.assertEqual(self.s.snapshot()['pending'], [])
        self.native.block.set(); thread.join(2)
        self.assertIsNone(self.s.snapshot()['active'])
        self.s.tick()
        self.assertIsNone(self.s.snapshot()['barrier'])
        with self.assertRaises(QueueConflict): self.s.accept(plan(1, command='late'), context_of(self.native.state()))
        self.assertEqual(self.native.calls[-1][0], 'cancel')

    def test_epoch_assignment_and_external_stop_invalidate(self):
        for field, changed in [('kitchenEpoch', 'new'), ('chefInstanceId', 9), ('dispatchGeneration', 9)]:
            native = Native(); scheduler = Scheduler(native)
            try:
                scheduler.accept(plan(), context_of(native.state()))
                native.world['control'][field] = changed
                scheduler.tick()
                self.assertEqual(scheduler.snapshot()['pending'], [])
                self.assertTrue(scheduler.snapshot()['paused'])
                self.assertEqual(native.calls, [])
            finally: scheduler.close()

    def test_suspension_and_native_busy_wait_without_focus(self):
        self.accept()
        for changes in ({'available': False, 'reason': 'paused'}, {'available': False, 'reason': 'game_unfocused'}, {'available': True, 'claimed': True}):
            self.native.world['control'].update(changes); self.s.tick()
            self.assertEqual(self.native.calls, [])
            self.assertEqual(self.s.snapshot()['pending'][0]['state'], 'waiting_to_start')
        self.native.world['control'].update(available=True, claimed=False, reason=None)
        self.native.next_rejection = 'task_not_available'; self.s.tick()
        self.assertIsNone(self.s.snapshot()['active'])
        self.s.tick()
        self.assertEqual(self.s.snapshot()['active']['state'], 'running')
        ids = [c[1]['requestId'] for c in self.native.calls if c[0] == 'execute']
        self.assertEqual(len(set(ids)), 2)  # Proven rejection allows a new attempt.

    def test_immediate_controls_hold_pending_and_never_take_queue_ownership(self):
        self.accept(); self.s.immediate(); self.s.tick()
        self.assertTrue(self.s.snapshot()['paused']); self.assertEqual(self.native.calls, [])
        self.edit('resume_pending'); self.s.tick()
        with self.assertRaises(QueueConflict): self.s.immediate()
