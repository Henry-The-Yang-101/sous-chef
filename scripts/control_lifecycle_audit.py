#!/usr/bin/env python3
"""Verify focus suspension and Stop-only client cancellation in isolated Unity.

Uses the production action endpoint and native input-focus fixture. No chef
poses, work progress, inventory, deadlines or bindings are forced.
"""
import argparse
import hashlib
import json
from pathlib import Path
import socket
import time

import native_simulator as sim
from task_audit import Audit
from throw_audit import carrier_items, fields, write
from clients.actions import ActionClient
from clients.control import ControlError


def observe(audit, predicate, timeout=30):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        audit.client.available_actions()
        control = audit.client.last_response['control']
        if predicate(control):
            return control
        if control.get('action', {}).get('state') in ('cancelled', 'failed', 'blocked'):
            raise AssertionError(control)
        time.sleep(.03)
    raise TimeoutError(json.dumps(control))


def suspend(audit, label, request, seconds):
    sim.command(audit.args.runtime, 'input-focus off')
    records = []
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        audit.client.available_actions()
        control = audit.client.last_response['control']
        action = control['action']
        assert action['requestId'] == request and action['state'] == 'running', control
        assert control['claimed'] and control['suspended'] and control['reason'] == 'game_unfocused', control
        records.append(control)
        time.sleep(.1)
    write(audit.directory / (label + '.json'), records)
    return control


def preparation(audit):
    catalog = audit.load('s_sushi_1_2')
    ingredient = next(i['id'] for i in catalog['ingredients'] if i['label'] == 'SushiFish')
    audit.request_when_available('prepare_near_player', items=[{'ingredient': ingredient, 'preparation': 'chopped'}])
    request = audit.client.last_response['control']['action']['requestId']
    observe(audit, lambda c: c['navigation']['state'] == 'navigating')
    suspend(audit, 'walking-unfocused', request, 6)
    sim.command(audit.args.runtime, 'input-focus on')
    observe(audit, lambda c: c['action']['phase'] == 'work_at' and
            (c['action']['details'].get('child') or {}).get('phase') == 'working')
    suspend(audit, 'working-unfocused', request, 2)
    # Pause/unpause while already unfocused must remain one suspension.
    sim.command(audit.args.runtime, 'pause on')
    control = audit.client.status()
    assert control['suspended'] and control['reason'] == 'paused', control
    sim.command(audit.args.runtime, 'pause off')
    suspend(audit, 'pause-to-unfocused', request, 1)
    sim.command(audit.args.runtime, 'input-focus on')
    observe(audit, lambda c: c['action']['state'] == 'succeeded', timeout=120)
    control = audit.client.last_response['control']
    assert control['action']['requestId'] == request, control
    assert control['action']['details']['completedRepetitions'] == 1, control
    assert not control['claimed'] and not control['suspended'], control
    write(audit.directory / 'preparation-finished.json', control)
    return {'requestId': request, 'completedRepetitions': 1, 'unfocusedSeconds': 9}


def disconnects(audit):
    results = []
    for abrupt in (False, True):
        audit.request_when_available('monitor_equipment', None)
        request = audit.client.last_response['control']['action']['requestId']
        suspend(audit, 'disconnect-' + str(abrupt), request, .5)
        if abrupt:
            audit.client.socket.shutdown(socket.SHUT_RDWR)
            audit.client.claimed = False  # Close without a release command.
        audit.client.close()
        audit.client = ActionClient(audit.args.port_base + 1, timeout=15)
        control = audit.client.status()
        assert control['claimed'] and not control['ownedByYou'] and control['suspended'], control
        assert control['action']['state'] == 'running' and control['action']['requestId'] == request, control
        write(audit.directory / ('reconnected-' + str(abrupt) + '.json'), control)
        sim.command(audit.args.runtime, 'input-focus on')
        observe(audit, lambda c: not c['suspended'])
        try:
            audit.client.claim()
        except ControlError as error:
            assert str(error) == 'already_claimed', error
        else:
            raise AssertionError('A reconnect replaced the original request')
        if not abrupt:
            sim.command(audit.args.runtime, 'input-focus off')
        started = time.monotonic()
        control = audit.client.stop()
        elapsed = time.monotonic() - started
        assert control['action']['requestId'] == request, control
        assert control['action']['state'] == 'cancelled' and control['action']['reason'] == 'stopped', control
        assert not control['claimed'] and not control['suspended'], control
        sim.command(audit.args.runtime, 'input-focus on')
        write(audit.directory / ('stopped-' + str(abrupt) + '.json'), control)
        results.append({'abrupt': abrupt, 'requestId': request, 'stopSeconds': elapsed})
    return results


def held_cargo(audit):
    audit.client.close()
    catalog = audit.load('s_sushi_1_2')
    ingredient = next(i['id'] for i in catalog['ingredients'] if i['label'] == 'SushiFish')
    audit.request_when_available('prepare_near_player', items=[{'ingredient': ingredient, 'preparation': 'chopped'}])
    request = audit.client.last_response['control']['action']['requestId']
    observe(audit, lambda c: c['action']['phase'] == 'place_at' and c['navigation']['state'] == 'navigating')
    suspend(audit, 'carrying-unfocused', request, 1)
    before = audit.snap('cargo-before')
    cargo = {o['unityInstanceId'] for o in carrier_items(before)}
    assert cargo, 'Expected the native picked-up fish still held on the way to its board'
    # A real client close sends release first. Neither event may cancel the job.
    audit.client.close()
    audit.client = ActionClient(audit.args.port_base + 1, timeout=15)
    suspend(audit, 'carrying-reconnected', request, 1)
    after = audit.snap('cargo-after')
    assert {o['unityInstanceId'] for o in carrier_items(after)} == cargo, 'Focus loss/disconnect changed carried food'
    control = audit.client.stop()
    assert control['action']['state'] == 'cancelled' and control['action']['reason'] == 'stopped', control
    assert {o['unityInstanceId'] for o in carrier_items(audit.snap('cargo-stopped'))} == cargo, 'Stop disposed of held food'
    sim.command(audit.args.runtime, 'input-focus on')
    return {'requestId': request, 'cargo': sorted(cargo)}


def standalone_actions(audit):
    audit.client.close()
    catalog = audit.load('s_sushi_1_2')
    target = max(catalog['destinations'], key=lambda t: t['routeDistance'])
    navigation_destination = target['id']
    audit.client.navigate_to(target['id'])
    sim.command(audit.args.runtime, 'input-focus off')
    time.sleep(1)
    audit.client.close()
    audit.client = ActionClient(audit.args.port_base + 1, timeout=15)
    control = audit.client.status()
    assert control['suspended'] and control['claimed'] and not control['ownedByYou'], control
    assert control['navigation']['state'] == 'navigating' and control['navigation']['destinationId'] == target['id'], control
    write(audit.directory / 'navigation-reconnected.json', control)
    control = audit.client.stop()
    assert control['navigation']['reason'] == 'stopped' and not control['claimed'], control
    sim.command(audit.args.runtime, 'input-focus on')

    state = audit.snap('wait-targets')
    empty = {o['unityInstanceId'] for o in state['objects']
             if fields(o, 'ServerAttachStation') and not fields(o, 'ServerAttachStation').get('m_item')}
    catalog = audit.client.available_actions()
    target = next(t for t in catalog['targets'] if t['objectInstanceId'] in empty and 'occupied' in t['waitConditions'])
    control = audit.client.wait_until(target['id'], 'occupied', timeout=1.5)
    request = control['action']['requestId']
    deadline = control['action']['deadline']
    suspend(audit, 'finite-wait-unfocused', request, 3)
    audit.client.close()
    audit.client = ActionClient(audit.args.port_base + 1, timeout=15)
    sim.command(audit.args.runtime, 'input-focus on')
    control = observe(audit, lambda c: not c['suspended'])
    assert control['action']['state'] == 'running' and control['action']['requestId'] == request, control
    extension = control['action']['deadline'] - deadline
    assert extension >= 3, control
    write(audit.directory / 'finite-wait-resumed.json', control)
    audit.client.stop()
    return {'navigationDestination': navigation_destination, 'finiteWaitRequest': request, 'deadlineExtension': extension}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--runtime', type=Path, required=True)
    parser.add_argument('--port-base', type=int, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    args.runtime = args.runtime.resolve()
    process = json.loads((args.runtime / 'process.json').read_text())
    if args.port_base == 24702 or args.port_base != process['portBase'] or not sim.is_running(args.runtime):
        parser.error('Use the matching isolated native runtime and non-default ports')
    binary = args.runtime / 'BepInEx/plugins/SousChef.Bridge.dll'
    if hashlib.sha256(binary.read_bytes()).hexdigest() != process['bridgeSha256']:
        parser.error('Runtime DLL differs from its launch manifest')
    args.output.mkdir(parents=True, exist_ok=True)
    audit = Audit(args)
    try:
        result = {'preparation': preparation(audit), 'disconnects': disconnects(audit),
                  'heldCargo': held_cargo(audit), 'standaloneActions': standalone_actions(audit),
                  'binary': process['bridgeSha256'], 'success': True}
        write(args.output / 'result.json', result)
        print(json.dumps(result))
    finally:
        if sim.is_running(args.runtime):
            sim.command(args.runtime, 'pause off')
            sim.command(args.runtime, 'input-focus on')
        if audit.client is not None:
            try:
                audit.client.stop()
            finally:
                audit.client.close()


if __name__ == '__main__':
    main()
