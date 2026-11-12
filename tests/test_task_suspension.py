import copy
import tempfile
import unittest
from pathlib import Path

from harness.scheduler import Scheduler, context_of
from harness.decision.compiler import build_context
from test_scheduler import Native, plan, spec
from test_decision import compiler, world


class SuspendableNative(Native):
    def __init__(self):
        super().__init__()
        self.world['control']['taskSuspensionVersion'] = 1
        self.operations = {}

    def queue_io(self, operation, entry):
        if operation not in ('suspend', 'resume', 'forget'):
            return super().queue_io(operation, entry)
        self.calls.append((operation, copy.deepcopy(entry)))
        ident = entry.get('resumeId', entry.get('id'))
        if ident not in self.operations:
            receipt = self.receipts[entry['requestId']]
            if operation == 'suspend':
                receipt['state'] = 'suspended'
                self.world['control'].update(claimed=False, dispatchGeneration=self.world['control']['dispatchGeneration'] + 1)
            elif operation == 'resume':
                receipt['state'] = 'running'
                self.world['control']['claimed'] = True
            else:
                receipt['state'] = 'cancelled'
            self.operations[ident] = dict(id=ident, state='succeeded')
        if operation == 'resume':
            return copy.deepcopy(self.receipts[entry['requestId']])
        return self.operations[ident]


class SuspensionTests(unittest.TestCase):
    def test_interrupt_resume_restart_and_progress_identity(self):
        native = SuspendableNative()
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / 'queue.sqlite3'
            queue = Scheduler(native, path)
            queue.accept(plan(tasks=[spec(count=None)]), context_of(native.state()))
            queue.tick()
            original = queue.snapshot()['active']
            native.receipts[original['requestId']]['evidence'] = {'completedRepetitions': 2, 'completedSteps': 7}
            queue.accept(plan(1, [spec('deliver_completed_dishes')], 'interrupt', 'urgent'), context_of(native.state()))
            queue.tick()
            queue.close()
            queue = Scheduler(native, path)
            try:
                queue.tick()
                self.assertEqual(queue.snapshot()['active']['spec']['action'], 'deliver_completed_dishes')
                native.finish()
                queue.tick()
                queue.tick()
                resumed = queue.snapshot()['active']
                self.assertEqual(resumed['requestId'], original['requestId'])
                self.assertEqual(resumed['id'], original['id'])
                self.assertEqual(resumed['receipt']['evidence']['completedRepetitions'], 2)
                self.assertEqual(len([c for c in native.calls if c[0] == 'execute']), 2)
            finally:
                queue.close()

    def test_nested_urgent_tasks_resume_in_order_and_stop_clears_all(self):
        native = SuspendableNative()
        queue = Scheduler(native)
        try:
            queue.accept(plan(tasks=[spec(count=None)]), context_of(native.state())); queue.tick()
            original = queue.snapshot()['active']['requestId']
            queue.accept(plan(1, [spec('take_ready')], 'interrupt', 'first'), context_of(native.state()))
            queue.tick(); queue.tick()
            first = queue.snapshot()['active']['requestId']
            queue.accept(plan(2, [spec('deliver_completed_dishes')], 'interrupt', 'second'), context_of(native.state()))
            queue.tick(); queue.tick()
            native.finish(); queue.tick(); queue.tick()
            self.assertEqual(queue.snapshot()['active']['requestId'], first)
            native.receipts[first]['state'] = 'succeeded'
            native.world['control']['claimed'] = False
            queue.tick(); queue.tick()
            self.assertEqual(queue.snapshot()['active']['requestId'], original)
            queue.stop(); queue.tick()
            self.assertIsNone(queue.snapshot()['active'])
            self.assertFalse(queue.snapshot()['pending'])
            self.assertEqual(native.receipts[original]['state'], 'cancelled')
        finally:
            queue.close()

    def test_lost_suspend_and_resume_acknowledgments_do_not_reexecute(self):
        native = SuspendableNative()
        queue = Scheduler(native)
        try:
            queue.accept(plan(), context_of(native.state())); queue.tick()
            original = queue.snapshot()['active']['requestId']
            queue.accept(plan(1, [spec('take_ready')], 'interrupt', 'first'), context_of(native.state()))
            original_io = native.queue_io
            lost = set()
            def lose_ack(operation, entry):
                result = original_io(operation, entry)
                if operation in ('suspend', 'resume') and operation not in lost:
                    lost.add(operation)
                    raise OSError('lost acknowledgment')
                return result
            native.queue_io = lose_ack
            queue.tick(); queue.tick(); queue.tick()
            native.finish(); queue.tick(); queue.tick(); queue.tick()
            self.assertEqual(queue.snapshot()['active']['requestId'], original)
            self.assertEqual(queue.snapshot()['active']['state'], 'running')
            self.assertEqual(len([c for c in native.calls if c[0] == 'execute']), 2)
            self.assertEqual(native.world['control']['dispatchGeneration'], 1)
        finally:
            queue.close()

    def test_removing_suspended_work_discards_native_checkpoint(self):
        native = SuspendableNative()
        queue = Scheduler(native)
        try:
            queue.accept(plan(), context_of(native.state())); queue.tick()
            original = queue.snapshot()['active']
            queue.accept(plan(1, [spec('deliver_completed_dishes')], 'interrupt', 'urgent'), context_of(native.state()))
            queue.tick()
            queue.edit(dict(operation_id='remove', expected_queue_revision=2, operation='remove_pending', task_id=original['id']))
            queue.tick()
            self.assertEqual(native.receipts[original['requestId']]['state'], 'cancelled')
            self.assertFalse(queue.snapshot()['discarded'])
        finally:
            queue.close()

    def test_urgent_plan_keeps_original_sequence_dependencies(self):
        native = SuspendableNative()
        queue = Scheduler(native)
        try:
            queue.accept(plan(tasks=[spec(), spec('take_ready')]), context_of(native.state()))
            queue.tick()
            original = queue.snapshot()['active']
            queue.accept(plan(1, [spec('deliver_completed_dishes')], 'interrupt', 'urgent'), context_of(native.state()))
            pending = queue.snapshot()['pending']
            self.assertEqual([t['spec']['action'] for t in pending], ['deliver_completed_dishes', 'wash_dishes', 'take_ready'])
            self.assertEqual(pending[-1]['dependsOn'], [original['id']])
        finally:
            queue.close()


class UrgentCompilerTests(unittest.IsolatedAsyncioTestCase):
    async def test_urgent_delivery_can_interrupt_unlimited_assignment(self):
        kitchen = world()
        kitchen['control']['taskSuspensionVersion'] = 1
        context = build_context(kitchen, dict(revision=0, active=dict(id='active', spec=spec(count=None)), pending=[]))
        c, script = compiler('Deliver this order right now', [dict(queue_edit='interrupt', simple='deliver_completed_dishes_once'), {}], context)
        result = await c.compile()
        self.assertEqual(result['plan']['queue_edit'], 'interrupt')
        self.assertEqual(result['plan']['tasks'][0]['action'], 'deliver_completed_dishes')
        self.assertIn('right now', script.requests[0]['questions']['queue_edit']['instructions'])
