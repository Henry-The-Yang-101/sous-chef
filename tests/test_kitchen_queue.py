import copy
import tempfile
import unittest
from pathlib import Path

from harness.scheduler import Scheduler, context_of
from test_scheduler import Native, plan, spec


class KitchenNative(Native):
    def __init__(self):
        super().__init__()
        self.world['control'].update(kitchenSchedulerVersion=1,
            kitchenScheduler=dict(held=False, activeRequestId=None, receipts=[]))
        self.lost = False
        self.holds = {}

    def publish(self):
        live = self.world['control']['kitchenScheduler']
        live['receipts'] = [copy.deepcopy(r) for r in self.receipts.values()
                            if r['state'] not in ('succeeded', 'failed', 'cancelled')]

    def queue_io(self, operation, entry):
        if operation == 'enqueue':
            self.calls.append((operation, copy.deepcopy(entry)))
            request = entry['requestId']
            self.receipts[request] = dict(id=request, epoch=entry['context']['epoch'], state='waiting',
                evidence=dict(jobs=[dict(state='ready')]))
            self.publish()
            if self.lost:
                self.lost = False
                raise OSError('ack lost')
            return copy.deepcopy(self.receipts[request])
        if operation == 'hold':
            self.calls.append((operation, copy.deepcopy(entry)))
            self.world['control']['kitchenScheduler']['held'] = entry['hold']
            return dict(id=entry['id'], state='succeeded')
        if operation == 'forget':
            self.calls.append((operation, copy.deepcopy(entry)))
            self.receipts[entry['requestId']]['state'] = 'cancelled'
            self.publish()
            return dict(id=entry['id'], state='succeeded')
        return super().queue_io(operation, entry)

    def complete(self, request, state='succeeded'):
        self.receipts[request]['state'] = state
        self.publish()


class KitchenQueueTests(unittest.TestCase):
    def setUp(self):
        self.native = KitchenNative()
        self.queue = Scheduler(self.native)
        self.addCleanup(self.queue.close)

    def accept(self, tasks, ordering='independent', edit='append'):
        revision = self.queue.snapshot()['revision']
        return self.queue.accept(dict(plan(revision, tasks, edit, f'command{revision}'), ordering=ordering),
                                 context_of(self.native.state()))

    def tick(self, n=1):
        for _ in range(n):
            self.queue.tick()

    def requests(self):
        return [entry['requestId'] for op, entry in self.native.calls if op == 'enqueue']

    def test_independent_blocked_jobs_are_admitted_without_waiting_for_completion(self):
        self.accept([spec('monitor_equipment', None), spec('wash_dishes')])
        self.tick(4)
        self.assertEqual(len(self.requests()), 2)
        self.assertTrue(all(r['state'] == 'waiting' for r in self.native.receipts.values()))

    def test_explicit_dependencies_require_success(self):
        self.accept([spec(), spec('take_ready')], ordering='sequence')
        self.tick(3)
        self.assertEqual(len(self.requests()), 1)
        self.native.complete(self.requests()[0])
        self.tick(3)
        self.assertEqual(len(self.requests()), 2)

    def test_failure_does_not_unlock_dependency_and_holds_native_dispatch(self):
        self.accept([spec(), spec('take_ready')], ordering='sequence')
        self.tick(2)
        self.native.complete(self.requests()[0], 'failed')
        self.tick(3)
        self.assertEqual(len(self.requests()), 1)
        self.assertTrue(self.queue.snapshot()['paused'])
        self.assertTrue(self.native.world['control']['kitchenScheduler']['held'])

    def test_lost_ack_and_restart_reconcile_same_request(self):
        with tempfile.TemporaryDirectory() as directory:
            self.queue.close()
            path = Path(directory) / 'queue.db'
            self.queue = Scheduler(self.native, path)
            self.accept([spec()])
            self.native.lost = True
            self.tick()
            self.queue.close()
            self.queue = Scheduler(self.native, path)
            self.addCleanup(self.queue.close)
            self.tick(3)
            self.assertEqual(len(self.requests()), 1)
            self.native.complete(self.requests()[0])
            self.tick(3)
            self.assertEqual(self.queue.snapshot()['outcomes'][-1]['state'], 'succeeded')

    def test_missing_receipt_never_replays_or_admits_more_work(self):
        self.accept([spec(), spec('take_ready')])
        self.tick()
        self.native.receipts.clear(); self.native.publish()
        self.tick(4)
        self.assertEqual(len(self.requests()), 1)
        self.assertEqual(self.queue.snapshot()['active']['state'], 'reconciling')

    def test_removal_cancels_admitted_pending_job(self):
        self.accept([spec(), spec('take_ready')])
        self.tick(4)
        pending = self.queue.snapshot()['pending'][0]
        self.queue.edit(dict(operation_id='remove', expected_queue_revision=1,
                             operation='remove_pending', task_id=pending['id']))
        self.tick()
        self.assertEqual(self.native.receipts[pending['requestId']]['state'], 'cancelled')

    def test_urgent_job_does_not_cancel_original_native_job(self):
        self.accept([spec()]); self.tick(2)
        original = self.requests()[0]
        self.accept([spec('take_ready')], edit='interrupt'); self.tick(3)
        self.assertEqual(len(self.requests()), 2)
        self.assertEqual(self.native.receipts[original]['state'], 'waiting')
        self.assertTrue([entry for op, entry in self.native.calls if op == 'enqueue'][-1]['urgent'])

    def test_pause_and_resume_send_native_hold(self):
        self.accept([spec(), spec('take_ready')]); self.tick(4)
        for revision, operation, held in [(1, 'pause_pending', True), (2, 'resume_pending', False)]:
            self.queue.edit(dict(operation_id=operation, expected_queue_revision=revision, operation=operation))
            self.tick()
            self.assertEqual(self.native.world['control']['kitchenScheduler']['held'], held)
