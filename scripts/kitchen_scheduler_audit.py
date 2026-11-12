#!/usr/bin/env python3
"""Exercise native portion overlap and durable admission in an isolated runtime."""
import argparse
import json
from pathlib import Path
import sys
import tempfile
import time
import uuid

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from clients.actions import ActionClient
from clients.capabilities import registry
from harness.server import HarnessBridge
from harness.scheduler import context_of
import native_simulator as sim


def task(action, arguments=None):
    return dict(kind='native_task', action=action, capability_version=registry()['version'],
                arguments=arguments or {}, repeat=dict(mode='finite', count=1))


def audit(runtime, port):
    assert runtime.resolve() != sim.DEFAULT_RUNTIME.resolve()
    assert sim.control_port(runtime) == port
    generation = sim.read_status(runtime)['generation'] + 1
    sim.command(runtime, 'reset'); sim.wait_ready(runtime, generation)
    lost = [False]

    class LostAck(ActionClient):
        def enqueue(self, *args, **kwargs):
            result = super().enqueue(*args, **kwargs)
            if not lost[0]:
                lost[0] = True
                raise OSError('audit: discarded accepted admission acknowledgment')
            return result

    with tempfile.TemporaryDirectory(prefix='kitchen-journal-') as directory:
        journal = Path(directory) / 'queue.db'
        bridge = HarnessBridge(port, factory=lambda: LostAck(port), journal_path=journal)
        def pump():
            bridge.poll(); bridge.scheduler.tick(); return bridge.state()
        try:
            deadline = time.monotonic() + 30
            state = pump()
            while not state.get('fresh') or not state['catalog'].get('complete'):
                assert time.monotonic() < deadline
                time.sleep(.1); state = pump()
            native = state['control']
            assert native.get('kitchenSchedulerVersion') == 1
            preparation = task('prepare_near_player', dict(player_id=1, items=[
                dict(ingredient='SushiRice', preparation='cooked', quantity=1),
                dict(ingredient='SushiFish', preparation='chopped', quantity=2)]))
            raw = dict(schema_version=registry()['plan_schema_version'], capability_version=registry()['version'],
                       command_id=uuid.uuid4().hex, expected_queue_revision=state['queue']['revision'],
                       queue_edit='append', tasks=[preparation], ordering='independent',
                       after_plan='continue_pending', on_failure='pause_pending')
            bridge.accept_plan(raw, context_of(state))
            while not lost[0]: state = pump()
            accepted = bridge.scheduler.snapshot()
            request = next(e['requestId'] for e in ([accepted['active']] if accepted['active'] else []) + accepted['pending'] if e.get('requestId'))
            bridge.close()
            bridge = HarnessBridge(port, journal_path=journal)
            rows = []; overlap = False; paused_overlap = False; identity = set(); deadline = time.monotonic() + 150
            while time.monotonic() < deadline:
                state = pump(); queue = state['queue']
                entries = ([queue['active']] if queue['active'] else []) + queue['pending'] + queue['outcomes']
                for entry in entries:
                    if entry.get('requestId'): identity.add(entry['requestId'])
                    evidence = (entry.get('receipt') or {}).get('evidence') or {}
                    jobs = evidence.get('jobs', [])
                    cooking = any(any(p['processing'] for p in (j.get('progress') or {}).get('details', {}).get('preparation', [])) for j in jobs)
                    working_other = any(j['state'] == 'running' and any(p['ingredient'] == 'SushiFish' for p in (j.get('progress') or {}).get('details', {}).get('preparation', [])) for j in jobs)
                    overlap |= cooking and working_other
                if overlap and not paused_overlap:
                    bridge.scheduler.edit(dict(operation_id='pause_overlap', expected_queue_revision=queue['revision'], operation='pause_pending'))
                    paused_overlap = True
                rows.append({'queue': queue, 'kitchen': state['control'].get('kitchenScheduler')})
                if queue['outcomes']: break
                time.sleep(.2)
            (runtime / 'last-portion-observations.json').write_text(json.dumps(rows))
            outcome = state['queue']['outcomes'][-1]
            assert outcome['state'] == 'succeeded', outcome
            assert identity == {request}, identity
            assert overlap, 'no observed overlap between cooking and fish preparation'
            jobs = outcome['receipt']['evidence']['jobs']
            assert len(jobs) == 3 and all(j['outcome'] == 'succeeded' for j in jobs)
            delivered = [j['progress']['details']['preparation'][0]['lastDeliveredInstanceId'] for j in jobs]
            assert len(set(delivered)) == 3, delivered
            report = dict(passed=True, overlap=overlap, pausedDuringOverlap=paused_overlap, lostAcknowledgment=lost[0], requestId=request,
                          distinctDeliveries=delivered, outcome=outcome, observations=rows)
            destination = runtime / 'kitchen-scheduler-audit.json'
            destination.write_text(json.dumps(report, indent=2))
            print(json.dumps(dict(passed=True, overlap=overlap, portions=3, report=str(destination))))
        finally:
            bridge.stop()
            bridge.close()


def serving_audit(runtime, port):
    generation = sim.read_status(runtime)['generation'] + 1
    sim.command(runtime, 'reset'); sim.wait_ready(runtime, generation)
    with ActionClient(port) as client:
        try:
            state = client.status()
            order = client.available_actions()['semanticCatalog']['assemblyOrders'][0]
            request = uuid.uuid4().hex
            client.enqueue(request, state['kitchenEpoch'], state['dispatchGeneration'],
                           'assemble_dish', {'order_id': order['id']})
            observations = []
            deadline = time.monotonic() + 180
            while time.monotonic() < deadline:
                receipt = client.receipt(request); state = client.status()
                observations.append(dict(receipt=receipt, control=state))
                scheduler = state['kitchenScheduler']
                assert not scheduler.get('error'), scheduler
                assert receipt['state'] not in ('failed', 'cancelled', 'rejected'), receipt
                if scheduler['dutyCompletions'].get('deliver_completed_dishes'):
                    break
                time.sleep(.2)
            assert receipt['state'] == 'succeeded', receipt
            assert scheduler['dutyCompletions'].get('deliver_completed_dishes') == 1, scheduler
            sim.command(runtime, 'application-focus off')
            until = time.monotonic() + 1
            while time.monotonic() < until:
                idle = client.status()['kitchenScheduler']
                assert idle['enabled'] and not idle.get('error'), idle
                time.sleep(.1)
            sim.command(runtime, 'application-focus on')
            destination = runtime / 'serve-audit.json'
            destination.write_text(json.dumps(dict(passed=True, backgroundDuties=True, order=order, observations=observations), indent=2))
            print(json.dumps(dict(passed=True, automaticDeliveries=1, backgroundDuties=True, report=str(destination))))
        finally:
            sim.command(runtime, 'application-focus on')
            client.stop()


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--runtime', type=Path, required=True)
    parser.add_argument('--port', type=int, required=True)
    parser.add_argument('--serve', action='store_true', help='Also assemble and automatically serve an exact ticket')
    args = parser.parse_args()
    audit(args.runtime, args.port)
    if args.serve:
        serving_audit(args.runtime, args.port)
