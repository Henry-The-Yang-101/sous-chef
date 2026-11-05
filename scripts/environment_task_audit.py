#!/usr/bin/env python3
"""Audit P2 handoffs, dish returns and equipment tasks in an isolated DLC session."""
import argparse
import gzip
import json
import math
from pathlib import Path
import time

import native_simulator as sim
from throw_audit import ActionClient, ControlError, fields, item_holders, snapshot, write


def save_gz(path, value):
    with gzip.open(path, 'wt', compresslevel=1) as output:
        json.dump(value, output, separators=(',', ':'))


def select(catalog, selector):
    options = catalog.get('preparationOptions', [])
    # Select only a currently obtainable source. On a split kitchen, the
    # kitchen-wide dropdown can legitimately include a dispenser on P1's side.
    for item in sorted(catalog.get('items', []), key=lambda i: (
            i.get('routeDistance') is None, i.get('routeDistance') or 0, i['label'])):
        if item.get('routeDistance') is None or item.get('inUse'):
            continue
        if item.get('supply') and item.get('ingredientId'):
            choice, state = item['ingredientId'], 'raw'
        elif item.get('empty') and (item.get('cleanPlate') or item.get('utensilType')):
            choice, state = ('plate' if item.get('cleanPlate') else item['utensilType']), 'empty'
        else:
            continue
        option = next((o for o in options if o['id'] == choice), None)
        if option and (not selector or selector.casefold() == option['label'].casefold()):
            yield dict(ingredient=choice, preparation=state), option['label'], item


def progress(control):
    a, nav = control['action'], control['navigation']
    d = a.get('details') or {}
    child = d.get('child') or {}
    child_details = child.get('details') or {}
    return dict(state=a['state'], reason=a.get('reason'), phase=a['phase'], steps=d.get('completedSteps'),
                repetitions=d.get('completedRepetitions'), waiting=d.get('waitingReason'), handoff=d.get('handoff'),
                observationError=d.get('observationError'), child=child.get('phase'), childReason=child.get('reason'),
                childDetails=child_details, childTarget=child.get('targetId'), recovery=child.get('recovery'),
                destination=nav.get('destination'), navReason=nav.get('reason'),
                decision=(nav.get('executionTrace') or [None])[-1])


def world_evidence(state, handoff):
    objects = state['objects']
    human = next(o for o in objects if o['name'] == 'Player 1' and fields(o, 'ServerPlayerAttachmentCarrier'))
    item = next((o for o in objects if handoff and o['unityInstanceId'] == handoff['itemInstanceId']), None)
    if not item:
        return dict(exactItemObserved=False)
    holders = item_holders(state, item)
    dist = math.dist(human['transform']['position'], item['transform']['position'])
    mode = handoff['mode']
    if mode in ('counter_near_player', 'throw_near_player'):
        evidence = 'exact_target' if any(h['instanceId'] == handoff['targetInstanceId'] for h in holders) else 'needs_review'
    elif any(h['name'] == 'Player 1' for h in holders):
        evidence = 'exact_human'
    elif item.get('activeInHierarchy') and not holders and dist <= 5:
        evidence = 'near_human'  # physical proximity only; does not prove a pickup route
    else:
        evidence = 'needs_review'
    return dict(exactItemObserved=True, objectName=item['name'], active=item.get('activeInHierarchy'),
                holders=holders, distanceToP1=round(dist, 2), evidence=evidence)


def capture(args, initial, directory, name):
    path = directory / (name + '.json')
    state = snapshot(args.port_base, initial['sessionId'], path)
    if not any(scene['name'] == initial['scene'] for scene in state['scenes']):
        raise RuntimeError('Snapshot belongs to a different scene')
    save_gz(directory / (name + '.json.gz'), state)
    path.unlink()
    return state


def cooker_evidence(snapshot, target, original=None):
    station = next(o for o in snapshot['objects'] if o['unityInstanceId'] == target)
    occupant = (fields(station, 'ServerAttachStation').get('m_item') or {}).get('objectId')
    item = next((o for o in snapshot['objects'] if o['id'] == (original or occupant)), {})
    data = fields(item, 'ServerCookingHandler').get('m_ServerData', {}).get('fields', {})
    return dict(stationItem=occupant, originalPresent=bool(item), containerName=item.get('name'),
                contents=fields(item, 'ServerIngredientContainer').get('m_contents'),
                cookState=data.get('CookingStateMessage.m_cookingState'),
                fires=sum(bool(fields(o, 'ServerFlammable').get('m_onFire')) for o in snapshot['objects']))


def supply_cooker(args, initial, directory, result, client):
    deadline = time.monotonic() + args.discovery_seconds
    while True:
        catalog = client.available_actions()
        ids = {i['id'] for i in catalog.get('ingredients', []) if i['label'] == args.ingredient}
        eligible = {d for row in catalog.get('jobOptions', {}).get('fetch_ingredient', [])
                    if row['sourceId'] in ids for d in row['destinationIds']}
        choices = [t for t in catalog.get('destinations', []) if t['kind'] == 'cooking_station' and t['id'] in eligible]
        if choices and catalog.get('complete'):
            break
        if time.monotonic() >= deadline:
            if not choices:
                raise RuntimeError('No compatible reachable cooker for ' + args.ingredient)
            break
        time.sleep(.2)
    target = min(choices, key=lambda t: t['routeDistance'])
    result.update(item=args.ingredient, cooker=target)
    control = client.fetch_ingredient(args.ingredient, target['id'])
    request = control['action']['requestId']
    try:
        deadline = time.monotonic() + 55
        while control['action']['state'] == 'running' and time.monotonic() < deadline:
            if not control['ownedByYou'] or control['action']['requestId'] != request:
                raise RuntimeError('Setup job lost ownership or changed identity')
            time.sleep(.25)
            control = client.status()
        write(directory / 'supply.json', control['action'])
        if control['action']['state'] != 'succeeded' or control['action']['requestId'] != request:
            raise RuntimeError('Setup job did not deliver ' + args.ingredient)
    finally:
        if client.claimed:
            client.stop()
    result['before'] = cooker_evidence(capture(args, initial, directory, 'before'), target['objectInstanceId'])
    if not result['before']['contents'] or result['before']['cookState'] is None:
        raise RuntimeError('No native evidence of food in the selected cooker')


def probe(args, level, directory, result):
    generation = sim.read_status(args.runtime)['generation'] + 1
    sim.command(args.runtime, 'load ' + level['name'])
    initial = sim.wait_ready(args.runtime, generation, 65)
    if (initial.get('scene'), initial.get('dlc')) != (level['name'], level['dlc']):
        raise RuntimeError('Unexpected native scene or DLC')
    write(directory / 'initial.json', initial)
    with ActionClient(args.port_base + 1, timeout=10) as client:
        if client.hello['sessionId'] != initial['sessionId']:
            raise RuntimeError('Unexpected native control session')
        wash = args.probe == 'wash'
        heat = args.probe in ('monitor_equipment', 'take_ready')
        if heat:
            supply_cooker(args, initial, directory, result, client)
        if wash:
            sim.command(args.runtime, 'task-fixture dirty 1')
            fixture = json.loads((args.runtime / 'task-fixture.json').read_text())
            if fixture.get('generation') != generation or fixture.get('stackInstanceId') is None:
                raise RuntimeError('Fixture did not return a dirty stack in the selected round')
            before = capture(args, initial, directory, 'before')
            stack = next(o for o in before['objects'] if o['unityInstanceId'] == fixture['stackInstanceId'])
            types = {c['type'] for o in before['objects'] for c in o['components']}
            result.update(fixture=fixture, stack=stack['name'], washable=any(c['type'] == 'ServerWashable' for c in stack['components']),
                          hasSink='ServerWashingStation' in types, hasWaterGun='ServerWaterGunSpray' in types)
        until = time.monotonic() + args.discovery_seconds
        while True:
            catalog = client.available_actions()
            tasks = [t['name'] for t in catalog.get('taskOptions', [])]
            choices = list(select(catalog, args.ingredient))
            foods = [c for c in choices if c[0]['preparation'] == 'raw']
            eligible = (args.probe in tasks if heat else 'wash_dishes' in tasks if wash
                        else choices and 'prepare_near_player' in tasks)
            if (catalog.get('complete') and eligible) or time.monotonic() >= until:
                break
            time.sleep(.2)
        save_gz(directory / 'catalog.json.gz', catalog)
        result.update(catalogComplete=catalog.get('complete'), catalogReason=catalog.get('reason'),
                      destinations=len(catalog.get('destinations', [])), tasks=tasks,
                      observedOptions=[o['label'] for o in catalog.get('preparationOptions', [])],
                      observationErrors=catalog.get('logisticsObservationErrors', []))
        if not wash and not heat:
            if not choices and args.menu_source:
                # Replays only: ask for a kitchen dropdown entry without making
                # a claim that its dispenser is currently reachable from spawn.
                options = [o for o in catalog.get('preparationOptions', []) if not o['utensil']
                           and (not args.ingredient or o['label'].casefold() == args.ingredient.casefold())]
                if options:
                    option = min(options, key=lambda o: o['label'])
                    choices = [(dict(ingredient=option['id'], preparation='raw'), option['label'],
                                dict(label=None, sourceInstanceId=None))]
            if not choices or 'prepare_near_player' not in tasks:
                result['outcome'] = 'no_eligible_probe'
                return
            item, label, source = (foods or choices)[0]
            if args.preparation:
                item['preparation'] = args.preparation
            result.update(item=label, parameters=item, source=source['label'], sourceInstanceId=source['sourceInstanceId'])
        try:
            if heat or wash:
                control = client.run_task('wash_dishes' if wash else args.probe)
            else:
                control = client.run_task('prepare_near_player', items=[item])
        except ControlError as error:
            result.update(outcome='start_rejected', error=str(error))
            return
        request = control['action']['requestId']
        result['requestId'] = request
        begin = time.monotonic()
        frame0 = sim.read_status(args.runtime)
        try:
            with gzip.open(directory / 'trace.ndjson.gz', 'wt', compresslevel=1) as trace:
                while True:
                    elapsed = time.monotonic() - begin
                    row = progress(control)
                    trace.write(json.dumps(dict(seconds=round(elapsed, 2), owned=control.get('ownedByYou'),
                                                request=control['action']['requestId'], **row)) + '\n')
                    if control['action']['requestId'] != request:
                        result['outcome'] = 'request_changed'
                        break
                    if row['state'] != 'running':
                        result['outcome'] = row['state']
                        break
                    if not control.get('ownedByYou'):
                        result['outcome'] = 'ownership_lost'
                        break
                    if elapsed >= args.seconds:
                        result['outcome'] = 'observation_limit'
                        break
                    time.sleep(.2)
                    control = client.status()
            result.update(seconds=round(elapsed, 2), final={k: v for k, v in row.items() if k not in ('decision','childDetails')})
            write(directory / 'navigation.json', control['navigation'])
            native = sim.read_status(args.runtime)
            write(directory / 'end.json', native)
            if native['realtime'] > frame0['realtime']:
                result['measuredFps'] = round((native['frame'] - frame0['frame']) / (native['realtime'] - frame0['realtime']), 1)
            if row['state'] == 'running':
                client.stop()
            state = capture(args, initial, directory, 'after')
            if heat:
                result['world'] = cooker_evidence(state, result['cooker']['objectInstanceId'], result['before']['stationItem'])
            elif wash:
                result['world'] = dict(stackStillPresent=any(o['unityInstanceId'] == fixture['stackInstanceId'] for o in state['objects']),
                                       platesInSinks=[fields(o, 'ServerWashingStation')['m_plateCount'] for o in state['objects']
                                                      if fields(o, 'ServerWashingStation')])
            else:
                result['world'] = world_evidence(state, row['handoff'])
        finally:
            if client.claimed:
                client.stop()


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--runtime', required=True, type=Path)
    p.add_argument('--port-base', required=True, type=int)
    p.add_argument('--output', required=True, type=Path, help='New evidence root (one directory per DLC/scene)')
    p.add_argument('--level', action='append')
    p.add_argument('--probe', choices=['handoff', 'wash', 'monitor_equipment', 'take_ready'], default='handoff')
    p.add_argument('--menu-source', action='store_true', help='Also allow advertised ingredient roots without a proven source')
    p.add_argument('--ingredient', help='Select a specific offered label on replay')
    p.add_argument('--preparation', choices=['raw', 'chopped', 'cooked', 'mixed', 'empty'])
    p.add_argument('--seconds', type=float, default=20)
    p.add_argument('--discovery-seconds', type=float, default=6)
    p.add_argument('--resume', action='store_true', help='Skip previously recorded probes in this output directory')
    args = p.parse_args()
    if args.probe in ('monitor_equipment', 'take_ready') and not args.ingredient:
        p.error('Equipment tests require --ingredient for the initial cooker delivery')
    args.runtime = args.runtime.resolve()
    if any(not math.isfinite(n) or n <= 0 for n in (args.seconds, args.discovery_seconds)):
        p.error('Observation and discovery limits must be positive finite seconds')
    if (args.port_base == 24702 or args.runtime == sim.DEFAULT_RUNTIME.resolve() or not sim.is_running(args.runtime)
            or sim.control_port(args.runtime) != args.port_base + 1):
        p.error('Start an isolated native simulator with matching ports first')
    status = sim.read_status(args.runtime)
    process = json.loads((args.runtime / 'process.json').read_text())
    levels = [x for x in json.loads((args.runtime / 'all-levels.json').read_text())
              if x['dlc'] == status['dlc'] and not x['name'].startswith(('ThroneRoom_', 'Throne_Room_'))
              and (not args.level or x['name'] in args.level)]
    if not levels or (args.level and set(args.level) - {x['name'] for x in levels}):
        p.error('Unknown kitchen for current DLC')
    for level in levels:
        directory = args.output / (str(level['dlc']) + '-' + level['name'])
        output = directory / 'result.json'
        if args.resume and output.exists():
            continue
        if directory.exists():
            p.error(f'Evidence already exists: {directory}')
        directory.mkdir(parents=True)
        result = dict(**level, bridgeSha256=process['bridgeSha256'], secondsLimit=args.seconds,
                      discoveryLimit=args.discovery_seconds, probe=args.probe, requestedIngredient=args.ingredient,
                      requestedPreparation=args.preparation, allowMenuSource=args.menu_source)
        try:
            probe(args, level, directory, result)
        except (OSError, RuntimeError, ValueError, KeyError, StopIteration) as error:
            result.update(previousOutcome=result.get('outcome'), outcome='probe_error',
                          error=f'{type(error).__name__}: {error}')
        write(output, result)
        print(level['name'], result['outcome'], result.get('item'), result.get('error', ''), flush=True)


if __name__ == '__main__':
    main()
