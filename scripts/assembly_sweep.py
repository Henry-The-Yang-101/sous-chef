#!/usr/bin/env python3
"""Audit allocated dish assembly in varied native kitchens, retaining decisions and food clocks.

Only initial ingredients are supplied by fixtures. Recipes, plates, processing,
navigation and all measured chef actions use the real production executor.
"""
import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path
import threading
import time

from task_audit import Audit
from throw_audit import fields, write
import native_simulator as sim
from clients.bridge import BridgeClient
from harness.contents import ingredient_names


DEFAULT_LEVELS = ['s_balloon_1_5', 's_balloon_2_2', 's_balloon_2_3',
                  's_mine_2_5', 's_wizard_3_1', 's_wizard_3_2', 's_wizard_3_3',
                  's_sushi_4_1', 's_mine_4_3', 's_mine_5_3', 's_balloon_5_2',
                  's_city_h2', 's_city_h3', 's_city_h4']


def nodes(tree):
    yield tree
    for child in tree.get('components', []):
        yield from nodes(child)


def recipe_parts(node, native=False):
    """Compare native snapshot contents independently of the task's success flag."""
    if not native:
        kind = node['kind']
        if kind == 'ingredient':
            return [('ingredient', node['id'])]
        children = tuple(sorted(part for child in node['components'] for part in recipe_parts(child)))
        if kind == 'assembly':
            return list(children)
        if kind not in ('cooked', 'mixed'):
            raise ValueError('Unknown recipe node: ' + kind)
        return [(kind, node.get('cookingStepId') if kind == 'cooked' else None, children)]
    kind = node['type']; data = node.get('fields', {})
    if kind == 'NullAssembledNode':
        return []
    if kind == 'IngredientAssembledNode':
        definition = data['IngredientAssembledNode.m_ingriedientOrderNode']
        return [('ingredient', 'i' + str(definition['fields']['OrderDefinitionNode.m_uID']))]
    if data.get('CompositeAssembledNode.m_optional'):
        raise ValueError('Unresolved optional native recipe contents')
    children = tuple(sorted(part for child in data['CompositeAssembledNode.m_composition'] for part in recipe_parts(child, True)))
    if kind == 'CompositeAssembledNode':
        return list(children)
    if kind == 'CookedCompositeAssembledNode' and data[kind + '.m_progress'] == 'Cooked':
        step = data[kind + '.m_cookingStep']['fields']['CookingStepData.m_uID']
        return [('cooked', step, children)]
    if kind == 'MixedCompositeAssembledNode' and data[kind + '.m_progress'] == 'Mixed':
        return [('mixed', None, children)]
    raise ValueError('Incomplete or unknown native processing node: ' + kind)


def verify_plate(report, snapshot):
    state = report['result']['details']['assembly']
    plate = next(o for o in snapshot['objects'] if o['unityInstanceId'] == state['baseInstanceId'] and o['activeInHierarchy'])
    contents = fields(plate, 'ServerIngredientContainer')['m_contents']
    actual = sorted(part for node in contents for part in recipe_parts(node, True))
    expected = sorted(recipe_parts(report['order']['composition']))
    if actual != expected:
        raise AssertionError('Final native plate does not match the exact recipe tree')
    return {'matched': True, 'plateInstanceId': plate['unityInstanceId'], 'parts': actual}


def review(directory):
    """Index the retained trace for human review; these metrics are not a pass verdict."""
    decisions = [json.loads(line) for line in (directory / 'decisions.ndjson').open()]
    native = [json.loads(line) for line in (directory / 'kitchen.ndjson').open()]
    events = [event for row in decisions for event in row.get('schedulerDecisions', [])]
    initial = json.loads((directory / 'before.json').read_text())
    processors = {o['unityInstanceId'] for o in initial['objects'] if any(c['type'] in
                  ('ServerCookingStation', 'ServerCookingRegion', 'ServerMixingStation') for c in o['components'])}
    waits = []; start = None
    for row in decisions:
        waiting = row['phase'].startswith('waiting_')
        if waiting and start is None:
            start = row
        if not waiting and start is not None:
            waits.append({'start': start['elapsed'], 'seconds': round(row['elapsed'] - start['elapsed'], 2), 'reason': start['phase']})
            start = None
    if start is not None:
        waits.append({'start': start['elapsed'], 'seconds': round(decisions[-1]['elapsed'] - start['elapsed'], 2), 'reason': start['phase']})
    heat = []; active = {}
    for row in native:
        for vessel in row['vessels']:
            state = vessel['cooking']
            ident = vessel['id']
            if state and state['state'] in ('Completed', 'OverDoing', 'Ruined') and vessel['holder'] in processors and ident not in active:
                active[ident] = {'vessel': ident, 'firstObservedAt': row['time'], 'holder': vessel['holder'], 'ruined': False}
            exposure = active.get(ident)
            if exposure is None:
                continue
            exposure['ruined'] |= state is not None and state['state'] == 'Ruined'
            if vessel['holder'] != exposure['holder']:
                exposure['removedAfterSeconds'] = round(row['time'] - exposure['firstObservedAt'], 2)
                heat.append(exposure); del active[ident]
    heat.extend(active.values())
    first_time = native[0]['time'] if native else 0
    for exposure in heat:
        exposure['firstObservedAt'] = round(exposure['firstObservedAt'] - first_time, 2)
    return {'decisionReasons': dict(Counter(e['reason'] for e in events)),
            'waitingIntervals': sorted(waits, key=lambda row: -row['seconds']),
            'completedVesselHolderChanges': heat,
            'navigationReasons': dict(Counter(row['navigation'].get('reason') for row in decisions)),
            'note': 'Intervals and holder changes need review against native processing and reachability; they do not independently prove unreasonable waiting.'}


def processing(obj, mixing=False):
    component, data, prefix = ('ServerMixingHandler', 'm_serverData', 'MixingStateMessage.m_mixing') if mixing else (
        'ServerCookingHandler', 'm_ServerData', 'CookingStateMessage.m_cooking')
    state = fields(obj, component).get(data, {}).get('fields', {})
    if not state:
        return None
    handler = fields(obj, 'MixingHandler' if mixing else 'CookingHandler')
    return {'state': state.get(prefix + 'State'), 'progress': state.get(prefix + 'Progress'),
            'duration': handler.get('m_mixingtime' if mixing else 'm_cookingtime')}


def kitchen_sample(message):
    objects = message['objects']; by_ref = {o['id']: o for o in objects}
    holders = {}
    for obj in objects:
        ref = fields(obj, 'ServerAttachStation').get('m_item')
        if ref:
            holders[ref.get('objectId')] = obj['unityInstanceId']
    vessels, players, fires = [], [], []
    for obj in objects:
        if not obj.get('activeInHierarchy', True):
            continue
        cooking, mixing = processing(obj), processing(obj, True)
        contents = fields(obj, 'ServerIngredientContainer').get('m_contents')
        if cooking or mixing:
            names = []
            for part in contents or []:
                try:
                    names.extend(ingredient_names(part))
                except ValueError:
                    names.append('?')
            vessels.append({'id': obj['unityInstanceId'], 'name': obj['name'], 'holder': holders.get(obj['id']),
                            'cooking': cooking, 'mixing': mixing, 'contents': names})
        carrier = fields(obj, 'ServerPlayerAttachmentCarrier')
        if carrier:
            players.append({'name': obj['name'], 'position': obj.get('transform', {}).get('position'),
                            'held': [by_ref[r['objectId']]['unityInstanceId'] for r in carrier.get('m_carriedObjects', []) if r and r.get('objectId') in by_ref]})
        for component in obj['components']:
            if 'Flammable' in component['type']:
                fire = fields(obj, component['type'])
                if fire:
                    fires.append({'id': obj['unityInstanceId'], 'fields': fire})
    return {'sequence': message['sequence'], 'epoch': message['levelEpoch'], 'time': time.monotonic(),
            'vessels': vessels, 'players': players, 'fires': fires}


class NativeMonitor:
    def __init__(self, port, session, output):
        self.port, self.session, self.output = port, session, output
        self.latest = None; self.errors = []; self.stop = threading.Event()
        self.thread = threading.Thread(target=self.run, daemon=True)

    def run(self):
        try:
            with BridgeClient(self.port, timeout=10) as client, self.output.open('w') as output:
                last = 0
                for message in client.messages():
                    if self.stop.is_set():
                        break
                    if client.session_id != self.session:
                        raise RuntimeError('native observation session changed')
                    if message['type'] != 'snapshot' or not message.get('inRound') or time.monotonic() - last < .4:
                        continue
                    last = time.monotonic(); self.latest = kitchen_sample(message)
                    output.write(json.dumps(self.latest) + '\n'); output.flush()
        except Exception as error:
            self.errors.append(str(error))

    def __enter__(self):
        self.thread.start(); return self

    def __exit__(self, *args):
        self.stop.set(); self.thread.join(timeout=2)


class Sweep(Audit):
    def catalog(self, complete=True):
        started = time.monotonic(); deadline = started + (90 if self.args.recipe else 35); best = None
        while time.monotonic() < deadline:
            catalog = self.client.available_actions()
            # Moving kitchens can invalidate the full route tree continuously.
            # Require concrete observed resources, not a globally static map.
            orders = catalog.get('assemblyOrders', [])
            wanted = not self.args.recipe or any(o['recipe'] == self.args.recipe for o in orders)
            if catalog.get('displayOptions', {}).get('items') and orders and wanted:
                best = catalog
                if catalog.get('complete'):
                    return catalog
                if time.monotonic() - started >= 12:
                    return best
            time.sleep(.15)
        raise TimeoutError('No live assembly inventory: ' + str(catalog.get('reason')))

    def allocate(self, level):
        catalog = self.load(level)
        write(self.directory / 'catalog-before.json', catalog)
        snapshot = self.snap('before'); objects = {o['unityInstanceId']: o for o in snapshot['objects']}
        supplies = [i for i in catalog['items'] if i['supply']]
        observed = catalog['displayOptions']['items']
        plates = [i for i in observed if i.get('cleanPlate') and i.get('routeDistance') is not None]
        equipment = [i for i in observed if i.get('empty') and
                     any(c['type'] in ('ServerCookingHandler', 'ServerMixingHandler') for c in objects.get(i['objectInstanceId'], {}).get('components', []))]
        counters = [d for d in catalog['deliveryDestinations'] if d['kind'] == 'counter' and
                    d.get('routeDistance') is not None and
                    fields(objects[d['objectInstanceId']], 'ServerAttachStation').get('m_item') is None and
                    not any(c['type'] in ('ServerPlateStation', 'ServerConveyorStation', 'ServerMixingStation') for c in objects[d['objectInstanceId']]['components'])]
        # Delivery descriptors do not always carry distance; use live targets.
        routes = {d['id']: d.get('routeDistance') for d in catalog['destinations']}
        if not counters:
            counters = [d for d in catalog['deliveryDestinations'] if d['kind'] == 'counter' and routes.get(d['id']) is not None and
                        fields(objects[d['objectInstanceId']], 'ServerAttachStation').get('m_item') is None and
                        not any(c['type'] in ('ServerPlateStation', 'ServerConveyorStation', 'ServerMixingStation') for c in objects[d['objectInstanceId']]['components'])]
        candidates = []
        for order in catalog['assemblyOrders']:
            recipe = list(nodes(order['composition'])); leaves = [n for n in recipe if n['kind'] == 'ingredient']
            if not leaves or len(counters) < len(leaves) or not plates:
                continue
            if any(n['kind'] == 'mixed' for n in recipe) and not any(fields(objects[e['objectInstanceId']], 'ServerMixingHandler') for e in equipment):
                continue
            if self.args.recipe and order['recipe'] != self.args.recipe:
                continue
            candidates.append((len(leaves) + 2 * sum(n['kind'] != 'ingredient' and n['kind'] != 'assembly' for n in recipe), order, leaves))
        if not candidates:
            raise ValueError('No fixture-feasible recipe: ' + json.dumps({'orders': [o['recipe'] for o in catalog['assemblyOrders']], 'plates': len(plates), 'counters': len(counters), 'supplies': [s['ingredientName'] for s in supplies]}))
        _, order, leaves = max(candidates, key=lambda c: (c[0], c[1]['remainingSeconds']))
        assigned = []; unused = []
        for leaf, counter in zip(leaves, counters):
            result = self.fixture(f"ingredient {leaf['id']} {counter['objectInstanceId']} chopped")
            time.sleep(.25); assigned.append((leaf,result['itemInstanceId']))
        catalog = self.catalog()
        items = {i['objectInstanceId']: i for i in catalog['displayOptions']['items']}
        for leaf, ident in assigned:
            if items[ident]['ingredientId'] != leaf['id']:
                raise ValueError('Supplied ingredient definition does not match recipe: ' + str((leaf, items[ident])))
        # All assigned equipment is explicit; stations are native capability
        # descriptors, not prefab names. The executor chooses appropriate ones.
        stations = [t['id'] for t in catalog['targets'] if t.get('processingEquipment')]
        allocation = dict(order_id=order['id'], bases=[p['id'] for p in sorted(plates, key=lambda p: p['routeDistance'])[:4]],
                          components=[items[i]['id'] for _, i in assigned], equipment=list(dict.fromkeys([e['id'] for e in equipment] + stations)))
        write(self.directory / 'allocation.json', {'order': order, 'request': allocation, 'components': [items[i] for _, i in assigned], 'unassigned': unused})
        write(self.directory / 'catalog-allocated.json', catalog)
        return order, allocation

    def observe_decisions(self, order, allocation):
        self.client.assemble_dish(**allocation)
        request_id = self.client.last_response['control']['action']['requestId']
        start = time.monotonic(); transitions = []; previous = None; warnings = []; decision_sequence = 0
        result = None
        with NativeMonitor(self.args.port_base, self.client.hello['sessionId'], self.directory / 'kitchen.ndjson') as native, (self.directory / 'decisions.ndjson').open('w') as output:
            while time.monotonic() - start < self.args.seconds:
                catalog = self.client.available_actions(); control = self.client.last_response['control']; action = control['action']
                details = action.get('details') or {}; child = details.get('child') or {}; nav = control.get('navigation') or {}
                assembly = dict(details.get('assembly') or {})
                decisions = [d for d in assembly.pop('decisions', []) if d['sequence'] > decision_sequence]
                if decisions:
                    if decisions[0]['sequence'] != decision_sequence + 1:
                        warnings.append('scheduler_decision_gap')
                    decision_sequence = decisions[-1]['sequence']
                event = {'elapsed': round(time.monotonic() - start, 3), 'requestId': action['requestId'],
                         'state': action['state'], 'reason': action.get('reason'), 'phase': action['phase'],
                         'waiting': details.get('waitingReason'), 'assembly': assembly, 'schedulerDecisions': decisions,
                         'child': child, 'navigation': {k:v for k,v in nav.items() if k != 'executionTrace'},
                         'motion': (nav.get('executionTrace') or [{}])[-1], 'owned': control.get('ownedByYou'),
                         'resources': [{k:i.get(k) for k in ['id','objectInstanceId','sourceInstanceId','empty','inUse','routeDistance']} for i in catalog.get('displayOptions', {}).get('items', [])]}
                output.write(json.dumps(event)+'\n'); output.flush()
                signature = (action['state'],action['phase'],details.get('waitingReason'),child.get('operation'),child.get('targetId'),child.get('phase'),child.get('reason'),json.dumps(assembly))
                if signature != previous:
                    transitions.append({k:event[k] for k in ['elapsed','state','reason','phase','waiting','assembly','child','navigation']}); previous = signature
                if action['requestId'] != request_id:
                    warnings.append('request_identity_changed'); break
                if native.latest:
                    for fire in native.latest['fires']:
                        warning = f"fire:{fire['id']}"
                        if fire['fields'].get('m_onFire') and warning not in warnings:
                            warnings.append(warning)
                    for vessel in native.latest['vessels']:
                        for kind in ('cooking','mixing'):
                            if vessel[kind] and vessel[kind]['state']=='Ruined':
                                warning=f"ruined_{kind}:{vessel['id']}"
                                if warning not in warnings: warnings.append(warning)
                if action['state'] != 'running':
                    result = action; break
                if not control.get('ownedByYou'):
                    warnings.append('lease_lost'); break
                time.sleep(.2)
            errors = list(native.errors)
        if result is None:
            result = self.client.status()['action']; self.client.stop(); warnings.append('observation_timeout')
        write(self.directory / 'transitions.json', transitions)
        after = self.snap('after')
        report = {'order':order, 'allocation':allocation, 'elapsed':round(time.monotonic()-start,2), 'result':result,
                'warnings':warnings,'observationErrors':errors,'transitions':len(transitions),
                'nativeSamples':sum(1 for _ in (self.directory/'kitchen.ndjson').open()), 'schedulerDecisions':decision_sequence,
                'review':review(self.directory), 'afterSequence':after['sequence']}
        if result['state'] == 'succeeded':
            report['independentComposition'] = verify_plate(report, after)
        return report


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--runtime',type=Path,required=True); parser.add_argument('--port-base',type=int,required=True)
    parser.add_argument('--output',type=Path,required=True); parser.add_argument('--levels',nargs='+',default=DEFAULT_LEVELS)
    parser.add_argument('--recipe'); parser.add_argument('--seconds',type=float,default=140)
    args=parser.parse_args(); args.runtime=args.runtime.resolve(); args.output.mkdir(parents=True,exist_ok=False)
    process=json.loads((args.runtime/'process.json').read_text())
    if process['portBase'] != args.port_base:
        raise ValueError('Port range does not belong to the selected native runtime')
    sim.wait_ready(args.runtime)
    results=[]; root=args.output
    for level in args.levels:
        args.output=root/level; args.output.mkdir(); audit=Sweep(args)
        try:
            order,allocation=audit.allocate(level)
            print(level,order['recipe'],'START',flush=True)
            report=audit.observe_decisions(order,allocation)
        except Exception as error:
            report={'error':str(error)}
        finally:
            if audit.client:
                try:
                    if audit.client.status()['action']['state']=='running':audit.client.stop()
                except Exception:pass
                audit.client.close()
        report['level']=level
        report['dllSha256']=hashlib.sha256((args.runtime/'BepInEx/plugins/SousChef.Bridge.dll').read_bytes()).hexdigest()
        write(args.output/'report.json',report);results.append(report);write(root/'report.json',results)
        print(level,report.get('result',{}).get('state',report.get('error')),report.get('warnings',[]),flush=True)


if __name__=='__main__':main()
