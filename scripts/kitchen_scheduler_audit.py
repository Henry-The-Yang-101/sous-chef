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
            rows = []; overlap = False; identity = set(); deadline = time.monotonic() + 150
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
                rows.append({'queue': queue, 'kitchen': state['control'].get('kitchenScheduler')})
                if queue['outcomes']: break
                time.sleep(.2)
            outcome = state['queue']['outcomes'][-1]
            assert outcome['state'] == 'succeeded', outcome
            assert identity == {request}, identity
            assert overlap, 'no observed overlap between cooking and fish preparation'
            jobs = outcome['receipt']['evidence']['jobs']
            assert len(jobs) == 3 and all(j['outcome'] == 'succeeded' for j in jobs)
            delivered = [j['progress']['details']['preparation'][0]['lastDeliveredInstanceId'] for j in jobs]
            assert len(set(delivered)) == 3, delivered
            report = dict(passed=True, overlap=overlap, lostAcknowledgment=lost[0], requestId=request,
                          distinctDeliveries=delivered, outcome=outcome, observations=rows)
            destination = runtime / 'kitchen-scheduler-audit.json'
            destination.write_text(json.dumps(report, indent=2))
            print(json.dumps(dict(passed=True, overlap=overlap, portions=3, report=str(destination))))
        finally:
            bridge.stop('audit_end')
            bridge.close()


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--runtime', type=Path, required=True)
    parser.add_argument('--port', type=int, required=True)
    args = parser.parse_args()
    audit(args.runtime, args.port)
