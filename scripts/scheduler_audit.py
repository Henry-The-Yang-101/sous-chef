#!/usr/bin/env python3
"""Verify scheduler receipts/recovery against an explicitly isolated native game."""
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
from clients.control import ControlError
from harness.scheduler import context_of
from harness.server import HarnessBridge
import native_simulator as sim


def task(action, arguments=None, count=1):
    return dict(kind='native_task', action=action, capability_version=1, arguments=arguments or {},
                repeat={'mode': 'until_cancelled'} if count is None else dict(mode='finite', count=count))


def accept(bridge, tasks, operation='append'):
    state = bridge.state()
    plan = dict(schema_version=registry()['plan_schema_version'], capability_version=registry()['version'],
                command_id=uuid.uuid4().hex, expected_queue_revision=state['queue']['revision'], queue_edit=operation,
                tasks=tasks, after_plan='continue_pending', on_failure='pause_pending')
    bridge.accept_plan(plan, context_of(state))


def pump(bridge, predicate, timeout=90):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        bridge.poll(); bridge.scheduler.tick()
        state = bridge.state()
        if predicate(state): return state
        if state['queue']['paused'] and state['queue']['outcomes'] and state['queue']['outcomes'][-1]['state'] == 'failed':
            raise AssertionError(state['queue'])
        time.sleep(.1)
    raise AssertionError('Timed out: ' + json.dumps(bridge.state()['queue']))


class LostAckClient(ActionClient):
    def execute(self, *args, **kwargs):
        result = super().execute(*args, **kwargs)
        if result['state'] != 'rejected' and not lost_ack[0]:
            lost_ack[0] = True
            raise OSError('Audit fixture: acknowledgment discarded after native acceptance')
        return result


lost_ack = [False]


def audit(args):
    assert args.runtime.resolve() != sim.DEFAULT_RUNTIME.resolve(), 'Use an isolated runtime'
    assert sim.control_port(args.runtime) == args.port, 'Port/runtime mismatch'
    generation = sim.read_status(args.runtime)['generation'] + 1
    sim.command(args.runtime, 'reset'); sim.wait_ready(args.runtime, generation)
    report = {'runtime': str(args.runtime), 'port': args.port, 'build': json.loads((args.runtime / 'process.json').read_text()), 'scenarios': {}}
    with tempfile.TemporaryDirectory(prefix='sous-chef-queue-') as directory:
        path = Path(directory) / 'queue.sqlite3'
        factory = lambda: LostAckClient(args.port, timeout=2)
        bridge = HarnessBridge(args.port, factory=factory, journal_path=path, focus=lambda: (_ for _ in ()).throw(AssertionError('Unexpected autofocus')))
        try:
            state = pump(bridge, lambda s: s['fresh'] and s['catalog'].get('complete'))
            choices = state['catalog']['semanticCatalog']['displayOptions']['preparationOptions']
            ingredient = next(i['id'] for i in choices if not i['utensil'] and i.get('preparations', {}).get('raw') == 'applicable')
            prep = task('prepare_near_player', {'player_id': 1, 'items': [{'ingredient': ingredient, 'preparation': 'raw', 'quantity': 1}]})
            accept(bridge, [prep, prep])
            state = pump(bridge, lambda s: s['queue']['active'] and s['queue']['active']['state'] == 'reconciling')
            first = state['queue']['active']
            report['scenarios']['lost_ack'] = {'taskId': first['id'], 'requestId': first['requestId'], 'state': first['state']}
            bridge.close()
            bridge = HarnessBridge(args.port, journal_path=path, focus=lambda: (_ for _ in ()).throw(AssertionError('Unexpected autofocus')))
            state = pump(bridge, lambda s: s['queue']['active'] and s['queue']['active']['state'] != 'reconciling')
            assert state['queue']['active']['requestId'] == first['requestId']
            sim.command(args.runtime, 'pause on')
            state = pump(bridge, lambda s: s['queue']['active']['state'] == 'suspended', 10)
            assert len(state['queue']['pending']) == 1
            sim.command(args.runtime, 'input-focus off')
            sim.command(args.runtime, 'pause off')
            state = pump(bridge, lambda s: s['control']['reason'] == 'game_unfocused', 10)
            assert state['queue']['active']['requestId'] == first['requestId']
            sim.command(args.runtime, 'input-focus on')
            state = pump(bridge, lambda s: len(s['queue']['outcomes']) == 2 and not s['queue']['active'])
            assert all(t['state'] == 'succeeded' for t in state['queue']['outcomes'])
            report['scenarios']['restart_pause_sequence'] = state['queue']['outcomes']
            ctx = first['context']
            with ActionClient(args.port) as client:
                duplicate = client.execute(first['requestId'], ctx['epoch'], ctx['fence'], prep['action'], prep['arguments'])
                assert duplicate['state'] == 'succeeded'
                try:
                    client.execute(first['requestId'], ctx['epoch'], ctx['fence'], 'wash_dishes', {})
                except ControlError as error:
                    assert 'request_id_conflict' in str(error)
                else: raise AssertionError('Conflicting payload accepted')
                report['scenarios']['deduplication'] = duplicate
            # Run now is a fenced cancellation barrier, then a new dispatch.
            bridge.poll()
            accept(bridge, [task('monitor_equipment', count=None)])
            state = pump(bridge, lambda s: s['queue']['active'] is not None)
            replaced = state['queue']['active']['requestId']
            accept(bridge, [prep], 'replace_active')
            state = pump(bridge, lambda s: not s['queue']['active'] and not s['queue']['pending'] and not s['queue']['barrier'])
            assert state['queue']['outcomes'][-1]['state'] == 'succeeded'
            with ActionClient(args.port) as client:
                cancelled = client.receipt(replaced)
                assert cancelled['state'] == 'cancelled'
            report['scenarios']['replacement_cleanup'] = {'cancelled': cancelled, 'replacement': state['queue']['outcomes'][-1]}
            # Unlimited tail survives socket lifetime and is cancelled explicitly.
            bridge.poll()
            accept(bridge, [task('prepare_near_player', prep['arguments'], None)])
            state = pump(bridge, lambda s: s['queue']['active'] is not None)
            before = context_of(state)
            stopped = bridge.stop()
            assert not stopped['queue']['active'] and not stopped['queue']['pending'] and not stopped['queue']['barrier']
            with ActionClient(args.port) as client:
                late = client.execute(uuid.uuid4().hex, before['epoch'], before['fence'], 'prepare_near_player', prep['arguments'])
                assert late['state'] == 'rejected' and late['reason'] == 'stale_dispatch_context'
            report['scenarios']['stop_fence'] = late
            bridge.poll()
            accept(bridge, [prep])
            generation = sim.read_status(args.runtime)['generation'] + 1
            sim.command(args.runtime, 'reset')
            sim.wait_ready(args.runtime, generation)
            state = pump(bridge, lambda s: not s['queue']['pending'])
            assert state['queue']['paused'] and state['queue']['outcomes'][-1]['state'] == 'cancelled'
            report['scenarios']['round_reset'] = state['queue']['reason']
        finally:
            bridge.stop()
            bridge.close()
    report['passed'] = True
    return report


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--runtime', type=Path, required=True)
    parser.add_argument('--port', type=int, required=True)
    parser.add_argument('--output', type=Path, default=ROOT / 'artifacts/scheduler-audit.json')
    args = parser.parse_args()
    report = audit(args)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2) + '\n')
    print(json.dumps({'passed': report['passed'], 'scenarios': list(report['scenarios']), 'report': str(args.output)}, indent=2))
