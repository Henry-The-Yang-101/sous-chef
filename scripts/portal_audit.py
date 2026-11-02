#!/usr/bin/env python3
"""Audit every installed two-player kitchen using an isolated native player.
No coordinate writes or raw movement: every route uses the production action API.
"""
import argparse
import json
from pathlib import Path
import sys
import time
from types import SimpleNamespace
import native_simulator as sim
sys.path.insert(0, str(sim.ROOT))
from clients.actions import ActionClient


def write(path, value):
    path.write_text(json.dumps(value, indent=2) + '\n')


def audit_level(runtime, level, output):
    result = dict(level, routes=[], covered=[], success=False)
    if level['name'].startswith(('Throne_Room_', 'ThroneRoom_')):
        return dict(result, success=True, reason='non_kitchen_story_scene')
    generation = sim.read_status(runtime).get('generation', 0) + 1
    sim.command(runtime, 'load ' + level['name'])
    try:
        initial = sim.wait_ready(runtime, generation, 35)
    except TimeoutError:
        initial = sim.read_status(runtime)
        result.update(reason='round_not_ready', initial=initial)
        return result
    time.sleep(1)
    initial = sim.read_status(runtime)
    result['initial'] = initial
    result['runtimeProcess'] = json.loads((runtime / 'process.json').read_text())
    result['inactivePortals'] = [p for p in initial.get('portals', []) if p['playerSender'] and not p['enabled']]
    portals = [p for p in initial.get('portals', []) if p['enabled'] and p['playerSender'] and p['exitObjectId']]
    result['portals'] = portals
    if not portals:
        result.update(success=not result['inactivePortals'], reason='inactive_portals_uncovered' if result['inactivePortals'] else 'no_active_player_portals')
        return result
    expected = {p['id'] for p in portals}
    covered, attempted = set(), set()
    with ActionClient(sim.control_port(runtime), timeout=10) as client:
        if client.hello['sessionId'] != initial['sessionId']:
            raise RuntimeError('Wrong native session; refusing control')
        for index in range(max(8, len(portals) * 4)):
            deadline = time.monotonic() + 30
            while True:
                catalog = client.available_actions()
                if catalog.get('complete') or time.monotonic() > deadline:
                    break
                time.sleep(.15)
            uncovered = expected - covered
            # Rotating/animated portals can be physically closed during a native
            # timed phase. Observe a full cycle before repositioning; never force
            # their transforms or treat an ordinary walking arrival as coverage.
            phase_deadline = time.monotonic() + 75
            while uncovered and not any(uncovered.intersection(d.get('routePortalIds', [])) for d in catalog.get('destinations', [])):
                if time.monotonic() >= phase_deadline: break
                time.sleep(.5)
                catalog = client.available_actions()
            options = catalog.get('destinations', [])
            options = [d for d in options if (tuple(sorted(covered)), d['id']) not in attempted]
            options.sort(key=lambda d: (-len(uncovered.intersection(d.get('routePortalIds', []))), d['routeDistance']))
            if not options or not uncovered:
                break
            chosen = options[0]
            if not uncovered.intersection(chosen.get('routePortalIds', [])):
                # Reposition on supported floor towards an uncovered entrance.
                source = next(p for p in portals if p['id'] in uncovered)['position']
                options.sort(key=lambda d: sum((a-b)**2 for a,b in zip(d['approachPosition'], source)))
                chosen = options[0]
            attempted.add((tuple(sorted(covered)), chosen['id']))
            route = {'target': chosen, 'observedPortals': [], 'success': False}
            result['routes'].append(route)
            pending, count = None, 0
            try:
                client.navigate_to(chosen['id'])
                deadline = time.monotonic() + 50
                with (output / f"{level['dlc']}-{level['name']}-{index}.ndjson").open('w') as trace:
                    while time.monotonic() < deadline:
                        state = client.status(); nav = state['navigation']
                        trace.write(json.dumps(state) + '\n')
                        if nav.get('portal'): pending = nav['portal']['entranceId']
                        completed = nav.get('teleportsCompleted', 0)
                        if completed > count:
                            if pending is not None: route['observedPortals'].append(pending)
                            count = completed
                        if nav['state'] != 'navigating':
                            route.update(reason=nav['reason'], arrival=nav.get('arrival'), teleportsCompleted=count)
                            route['success'] = nav['reason'] in ('arrived', 'positioned_no_action') and bool((nav.get('arrival') or {}).get('positioned'))
                            break
                        time.sleep(.05)
                    else:
                        client.stop(); route['reason'] = 'test_timeout'
            except Exception as exc:
                client.stop(); route['reason'] = str(exc)
            if route['success']:
                covered.update(route['observedPortals'])
            else:
                break # Preserve failed attempts; never hide them with an in-place retry.
    result.update(covered=sorted(covered), success=expected.issubset(covered) and not result['inactivePortals'] and all(r['success'] for r in result['routes']))
    result['reason'] = 'covered' if result['success'] else 'failed_or_uncovered'
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--runtime', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--port-base', type=int, required=True)
    parser.add_argument('--resume', action='store_true')
    parser.add_argument('--level', action='append', help='Audit only these scene names (repeatable)')
    parser.add_argument('--dlc', type=int, help='Restrict the audit to one native session')
    args = parser.parse_args(); args.runtime=args.runtime.resolve(); args.output.mkdir(parents=True,exist_ok=True)
    if args.port_base == 24702: parser.error('Use isolated ports')
    levels = json.loads((args.runtime/'all-levels.json').read_text())
    levels = list({(x['dlc'],x['name']):x for x in levels}.values())
    if args.level: levels = [x for x in levels if x['name'] in args.level]
    if args.dlc is not None: levels = [x for x in levels if x['dlc'] == args.dlc]
    if not levels: parser.error('No matching installed scenes')
    report_path=args.output/'report.json'
    results=json.loads(report_path.read_text())['results'] if args.resume and report_path.exists() else []
    done={(x['dlc'],x['name']) for x in results}
    for level in levels:
        if (level['dlc'],level['name']) in done: continue
        try:
            status=sim.read_status(args.runtime)
            if not status.get('running') or status.get('dlc') != level['dlc']:
                if status.get('running'): sim.command(args.runtime,'quit',timeout=15)
                sim.start(SimpleNamespace(runtime=args.runtime,game=sim.DEFAULT_GAME,level=level['name'],dlc=level['dlc'],fps=60,port_base=args.port_base,timeout=90))
            result=audit_level(args.runtime,level,args.output)
        except Exception as exc:
            result=dict(level,success=False,reason=str(exc))
        results.append(result)
        write(report_path,{'totalLevels':len(levels),'results':results})
        print(f"{len(results)}/{len(levels)} DLC {level['dlc']} {level['name']}: {result['reason']} portals={len(result.get('portals',[]))} covered={len(result.get('covered',[]))}",flush=True)
    return 0 if all(r['success'] for r in results) else 2

if __name__=='__main__': sys.exit(main())
