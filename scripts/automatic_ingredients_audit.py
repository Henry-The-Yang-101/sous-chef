#!/usr/bin/env python3
"""Verify automatic-food preparation in the native, windowless Unity player.

Every case resets story 4-1 and uses its ordinary timed spawns, chef positions,
traffic, cooking and input. Optional --seafood-side stages the chefs on the
seafood side to isolate pickups from road navigation. No food fixtures or
accepted-request retries.
"""
import argparse
import hashlib
import json
from pathlib import Path
import time

import native_simulator as sim
from task_audit import Audit
from throw_audit import fields, write
from harness.contents import ingredient_names


FOOD = {'Cucumber', 'SushiFish', 'SushiPrawn', 'SushiRice', 'Seaweed'}
CASES = {
    'fish_raw': ('SushiFish', 'raw'),
    'prawn_raw': ('SushiPrawn', 'raw'),
    'fish_chopped': ('SushiFish', 'chopped'),
    'prawn_chopped': ('SushiPrawn', 'chopped'),
    'rice_raw': ('SushiRice', 'raw'),
    'seaweed_raw': ('Seaweed', 'raw'),
    'cucumber_chopped': ('Cucumber', 'chopped'),
    'rice_cooked': ('SushiRice', 'cooked'),
}


def roots(catalog):
    return {r['label']: r['id'] for r in catalog['displayOptions']['preparationOptions'] if not r['utensil']}


def run(audit, case):
    audit.load('s_sushi_4_1')
    initial = sim.read_status(audit.args.runtime)
    assert initial['graphicsDevice'] == 'Null' and initial['physicsAutoSimulation']
    ingredient, preparation = CASES[case]
    staged = []
    if audit.args.seafood_side:
        assert ingredient in ('SushiFish', 'SushiPrawn') and preparation == 'raw', 'Staged seafood side has no chopping boards'
        for command in ('stage-player 1 35.5 0 5.5 0', 'stage-player 2 34.8 0 3.6 0'):
            sim.command(audit.args.runtime, command)
            staged.append(json.loads((audit.args.runtime / 'staging.json').read_text()))
        audit.catalog()
    catalog = audit.client.available_actions()
    options = roots(catalog)
    assert set(options) == FOOD, options
    assert {r['label'] for r in catalog['preparationOptions'] if not r['utensil']} == FOOD
    write(audit.directory / 'catalog.json', catalog)
    before = audit.snap('before')
    configured = []
    for obj in before['objects']:
        source = fields(obj, 'TriggerAttachedSpawn')
        if source:
            configured.extend(entry['fields']['TriggerAttachedSpawn+WeightedPrefab.AttachmentPrefab']['name']
                              for entry in source['m_attachmentPrefabs'])
    assert set(configured) == FOOD - {'Cucumber'}, configured
    dispensers = [fields(o, 'PickupItemSpawner')['m_itemPrefab']['name'] for o in before['objects']
                  if fields(o, 'PickupItemSpawner')]
    assert set(dispensers) == {'Cucumber'}, dispensers
    started = time.monotonic()
    audit.request_when_available('prepare_near_player', items=[{
        'ingredient': options[ingredient], 'preparation': preparation,
    }])
    receipts = audit.observe('task', timeout=audit.args.timeout)
    after = audit.snap('after')
    final = receipts[-1]
    assert final['details']['completedRepetitions'] == 1
    handoff = final['details']['handoff']
    item = next(o for o in after['objects'] if o['unityInstanceId'] == handoff['itemInstanceId'])
    if handoff['targetInstanceId'] is not None:
        holder = next(o for o in after['objects'] if o['unityInstanceId'] == handoff['targetInstanceId'])
        assert fields(holder, 'ServerAttachStation').get('m_item', {}).get('objectId') == item['id'], 'Exact cargo is absent from the receiving counter'
    else:
        human = next(o for o in after['objects'] if o['name'] == 'Player 1' and fields(o, 'ServerPlayerAttachmentCarrier'))
        held = any(ref and ref['objectId'] == item['id'] for ref in fields(human, 'ServerPlayerAttachmentCarrier')['m_carriedObjects'])
        if not held:
            interaction = fields(human, 'PlayerControls')['m_interactionObjects']['fields']
            selected = interaction.get('PlayerControls+InteractionObjects.m_TheOriginalHandlePickup') or {}
            assert handoff['mode'] == 'drop_at_player' and selected.get('objectId') == item['id'], 'P1 must hold the exact item or natively select it for pickup'
            assert fields(item, 'ServerPhysicalAttachment')['m_isHeld'] is False
    phases = {r['phase'] for r in receipts}
    if preparation == 'chopped':
        assert 'work_at' in phases, 'Native chopping was not observed'
        assert not any(c['type'] == 'WorkableItem' for c in item['components']), 'Delivered raw food'
        node = fields(item, 'IngredientPropertiesComponent')['m_ingredientOrderNode']
        assert node['name'] == ingredient, node
    elif preparation == 'cooked':
        contents = fields(item, 'ServerIngredientContainer').get('m_contents')
        assert contents and any(n.get('fields', {}).get('CookedCompositeAssembledNode.m_progress') == 'Cooked' for n in contents), contents
        assert ingredient in [name for node in contents for name in ingredient_names(node)], contents
    else:
        assert item['name'] == ingredient, item['name']
    write(audit.directory / 'initial-status.json', initial)
    return {'ingredient': ingredient, 'preparation': preparation, 'options': sorted(options),
            'automaticOutputs': sorted(configured), 'dispensers': dispensers,
            'requestId': final['requestId'], 'seconds': round(time.monotonic() - started, 2),
            'waitingReasons': sorted({r['details']['waitingReason'] for r in receipts if r['details'].get('waitingReason')}),
            'handoff': handoff, 'nativeItem': item['name'], 'phases': sorted(phases), 'fixtures': staged}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--runtime', type=Path, required=True)
    parser.add_argument('--port-base', type=int, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--case', action='append', choices=CASES)
    parser.add_argument('--fps', type=int, default=60)
    parser.add_argument('--timeout', type=float, default=150)
    parser.add_argument('--seafood-side', action='store_true', help='Explicitly stage both chefs beside the seafood belt; raw seafood cases only')
    args = parser.parse_args()
    args.runtime = args.runtime.resolve()
    process = json.loads((args.runtime / 'process.json').read_text())
    if args.port_base == 24702 or args.port_base != process['portBase'] or not sim.is_running(args.runtime):
        parser.error('Use a running isolated native runtime and its matching non-default ports')
    digest = hashlib.sha256((args.runtime / 'BepInEx/plugins/SousChef.Bridge.dll').read_bytes()).hexdigest()
    if digest != process['bridgeSha256']:
        parser.error('Runtime DLL differs from its launch manifest')
    sim.command(args.runtime, 'fps ' + str(args.fps))
    args.output.mkdir(parents=True, exist_ok=True)
    results = []
    for case in args.case or ('rice_raw', 'seaweed_raw', 'cucumber_chopped', 'rice_cooked'):
        audit = Audit(args)
        audit.directory = args.output / case
        audit.directory.mkdir(parents=True, exist_ok=True)
        try:
            result = dict(run(audit, case), case=case, success=True)
        except Exception as error:
            result = {'case': case, 'success': False, 'error': str(error), 'errorType': type(error).__name__}
            if audit.client:
                try:
                    audit.snap('failure-snapshot')
                    audit.client.stop()
                except Exception as cleanup:
                    result['cleanupError'] = str(cleanup)
        finally:
            if audit.client:
                audit.client.close()
        results.append(result)
        print(json.dumps(result), flush=True)
        write(args.output / 'report.json', {'bridgeSha256': digest, 'fps': args.fps,
                                          'passed': sum(r['success'] for r in results),
                                          'attempts': len(results), 'results': results})
    return 0 if results and all(r['success'] for r in results) else 2


if __name__ == '__main__':
    raise SystemExit(main())
