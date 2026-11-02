#!/usr/bin/env python3
"""Audit every installed two-player kitchen using an isolated native player.
Every measured route uses the production action API; explicit setup fixtures are recorded.
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


def setup_cannon(client, runtime, used=(), aim_down=False):
    """Stage the portal-only route after native teammate-assisted cannon flight."""
    deadline = time.monotonic() + 30
    while True:
        catalog = client.available_actions()
        options = [t for t in catalog.get('targets', []) if 'cannon' in t['label'].lower() and 'activate' in t.get('operations', [])]
        reachable = {d['id'] for d in catalog.get('destinations', [])}
        options = [t for t in options if t['id'] in reachable and t['objectInstanceId'] not in used]
        if catalog.get('complete') and len(options) == 1: break
        if time.monotonic() > deadline: raise RuntimeError('Expected one reachable cannon for test setup')
        time.sleep(.15)
    client.activate(options[0]['id'], 'trigger')
    deadline = time.monotonic() + 30
    while time.monotonic() < deadline:
        status = sim.read_status(runtime)
        chef = next((p for p in status.get('players', []) if p['name'] == 'Player 2'), {})
        if chef.get('enabled') is False and any(c['id'] == options[0]['objectInstanceId'] and c['loadedPlayer'] == 2 and c['ready'] and not c['flying'] for c in status.get('cannons', [])): break
        time.sleep(.1)
    else: raise RuntimeError('Native cannon boarding did not complete')
    before = status
    if aim_down:
        sim.command(runtime, 'aim-cannon-down')
        deadline = time.monotonic() + 5
        while any(c.get('aiming') for c in sim.read_status(runtime).get('cannons', [])):
            if time.monotonic() > deadline: raise RuntimeError('Cannon aiming did not finish')
            time.sleep(.1)
    sim.command(runtime, 'launch-cannon')
    saw_flight = False
    deadline = time.monotonic() + 30
    while time.monotonic() < deadline:
        status = sim.read_status(runtime)
        flying = any(c['flying'] for c in status.get('cannons', []))
        saw_flight |= flying
        chef = next((p for p in status.get('players', []) if p['name'] == 'Player 2'), {})
        if saw_flight and not flying and chef.get('enabled') and chef.get('grounded'):
            return {'kind': 'native_cannon_teammate_button', 'target': options[0], 'before': before, 'after': status}
        time.sleep(.1)
    raise RuntimeError('Native cannon did not finish flight and regain grounded control')


def audit_level(runtime, level, output, cannon_setup=False, cannon_aim_down=False, cannon_transit=False, portal_index=None, boss_portal_setup=False, stage_portal_index=None, portal_points=False):
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
    dormant = [p for p in initial.get('portals', []) if p['playerSender'] and not p['enabled']]
    if dormant and boss_portal_setup:
        result['phaseSetups'] = []
        for _ in range(8):
            if initial.get('phaseCanAdvance') is False: break
            before = initial
            sim.command(runtime, 'advance-phase')
            transition_started = time.monotonic()
            deadline = transition_started + 60
            while time.monotonic() < deadline:
                initial = sim.read_status(runtime)
                players = initial.get('players', [])
                if time.monotonic() - transition_started > 1 and initial.get('phaseTransitionsPending') == 0 and initial.get('dynamicPhase', -1) > before.get('dynamicPhase', -1) and players and all(p['enabled'] and p['inputAllowed'] and p['grounded'] for p in players): break
                time.sleep(.25)
            else: return dict(result, reason='phase_transition_did_not_settle')
            result['phaseSetups'].append({'before': before, 'after': initial})
            dormant = [p for p in initial.get('portals', []) if p['playerSender'] and not p['enabled']]
            if not dormant: break
    if dormant:
        result['activationWait'] = {'before': initial}
        deadline = time.monotonic() + 300
        while time.monotonic() < deadline:
            initial = sim.read_status(runtime)
            dormant = [p for p in initial.get('portals', []) if p['playerSender'] and not p['enabled']]
            if not dormant or not initial.get('inRound'): break
            time.sleep(.25)
        result['activationWait']['after'] = initial
        if not initial.get('inRound'): return dict(result, reason='round_ended_before_portal_activation')
    result['inactivePortals'] = dormant
    portals = [p for p in initial.get('portals', []) if p['enabled'] and p['playerSender'] and p['exitObjectId']]
    result['portals'] = portals
    if not portals:
        result.update(success=not result['inactivePortals'], reason='inactive_portals_uncovered' if result['inactivePortals'] else 'no_active_player_portals')
        return result
    selected = portals if portal_index is None else [sorted(portals, key=lambda p: p['position'])[portal_index]]
    expected = {p['id'] for p in selected}
    result['expected'] = sorted(expected)
    covered, attempted = set(), set()
    with ActionClient(sim.control_port(runtime), timeout=10) as client:
        if client.hello['sessionId'] != initial['sessionId']:
            raise RuntimeError('Wrong native session; refusing control')
        result['setups'] = []
        if stage_portal_index is not None:
            staged = sorted(portals, key=lambda p: p['position'])[stage_portal_index]
            sim.command(runtime, 'stage-portal ' + str(staged['id']))
            time.sleep(1)
            result['setups'].append(json.loads((runtime / 'staging.json').read_text()))
        used_cannons = set()
        if cannon_setup:
            try:
                if cannon_transit:
                    transit = setup_cannon(client, runtime)
                    result['setups'].append(transit); used_cannons.add(transit['target']['objectInstanceId'])
                setup = setup_cannon(client, runtime, used_cannons, cannon_aim_down)
                result['setups'].append(setup); used_cannons.add(setup['target']['objectInstanceId'])
            except Exception as exc: return dict(result, reason='cannon_setup_failed: ' + str(exc))
        for index in range(max(8, len(portals) * 4)):
            deadline = time.monotonic() + 30
            while True:
                catalog = client.available_actions()
                if catalog.get('complete') or time.monotonic() > deadline:
                    break
                time.sleep(.15)
            uncovered = expected - covered
            if not uncovered: break
            if cannon_setup and len(result['setups']) < len(portals) and not any(uncovered.intersection(d.get('routePortalIds', [])) for d in catalog.get('destinations', [])):
                try:
                    setup = setup_cannon(client, runtime, used_cannons, cannon_aim_down)
                    result['setups'].append(setup); used_cannons.add(setup['target']['objectInstanceId'])
                    catalog = client.available_actions()
                except Exception as exc: return dict(result, reason='cannon_setup_failed: ' + str(exc), covered=sorted(covered))
            # Rotating/animated portals can be physically closed during a native
            # timed phase. Observe a full cycle before repositioning; never force
            # their transforms or treat an ordinary walking arrival as coverage.
            links = catalog.get('navigation', {}).get('diagnostics', {}).get('portals', [])
            phase_deadline = time.monotonic() + (75 if any(not p['valid'] for p in links) else 0)
            while uncovered and not any(uncovered.intersection(d.get('routePortalIds', [])) for d in catalog.get('destinations', [])):
                if time.monotonic() >= phase_deadline: break
                time.sleep(.5)
                catalog = client.available_actions()
            options = catalog.get('destinations', [])
            if portal_points:
                options = [dict(id='d000' + str(p['id'] & 0xffffffff), label='native supported exit point',
                    approachPosition=p['exit'], routePortalIds=[p['id']], routeDistance=0)
                    for p in links if p['valid'] and p['id'] in uncovered]
            options = [d for d in options if (tuple(sorted(covered)), d['id']) not in attempted]
            options.sort(key=lambda d: (-len(uncovered.intersection(d.get('routePortalIds', []))), d['routeDistance']))
            if not options or not uncovered:
                break
            chosen = options[0]
            if not uncovered.intersection(chosen.get('routePortalIds', [])):
                # Reposition on supported floor towards an uncovered entrance.
                sources = [p['entry'] for p in links if p['id'] in uncovered and p['valid']]
                if not sources: sources = [p['position'] for p in portals if p['id'] in uncovered]
                options.sort(key=lambda d: min(sum((a-b)**2 for a,b in zip(d['approachPosition'], source)) for source in sources))
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
    parser.add_argument('--portal-phase-setup', '--boss-portal-setup', dest='boss_portal_setup', action='store_true', help='Use native phase transitions to reach dormant portals without waiting or completing recipes')
    parser.add_argument('--portal-points', action='store_true', help='Use production point navigation to native portal exits where there are no reachable stations')
    parser.add_argument('--stage-portal-index', type=int, help='Explicit supported initial pose fixture before measured routes; recorded separately from navigation')
    parser.add_argument('--portal-index', type=int, help='Restrict to one entrance sorted by world position (for separate native setup cases)')
    parser.add_argument('--cannon-transit', action='store_true', help='Take a default cannon flight before boarding and aiming the next cannon')
    parser.add_argument('--cannon-aim-down', action='store_true', help='Use native pilot input to aim at Carnival lower islands during cannon setup')
    parser.add_argument('--cannon-setup', action='store_true', help='Board a native cannon and fire its teammate button before auditing the portal return route')
    parser.add_argument('--level', action='append', help='Audit only these scene names (repeatable)')
    parser.add_argument('--dlc', type=int, help='Restrict the audit to one native session')
    args = parser.parse_args(); args.runtime=args.runtime.resolve(); args.output.mkdir(parents=True,exist_ok=True)
    if args.port_base == 24702: parser.error('Use isolated ports')
    if sim.read_status(args.runtime).get('running') and sim.control_port(args.runtime) != args.port_base + 1:
        parser.error('Running runtime port range does not match --port-base; refusing control')
    if args.stage_portal_index is not None and args.stage_portal_index < 0: parser.error('Staging portal index must be nonnegative')
    if args.portal_index is not None and args.portal_index < 0: parser.error('Portal index must be nonnegative')
    if (args.cannon_transit or args.cannon_aim_down) and not args.cannon_setup: parser.error('Cannon aiming/transit requires --cannon-setup')
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
            result=audit_level(args.runtime,level,args.output,args.cannon_setup,args.cannon_aim_down,args.cannon_transit,args.portal_index,args.boss_portal_setup,args.stage_portal_index,args.portal_points)
        except Exception as exc:
            result=dict(level,success=False,reason=str(exc))
        results.append(result)
        write(report_path,{'totalLevels':len(levels),'results':results})
        print(f"{len(results)}/{len(levels)} DLC {level['dlc']} {level['name']}: {result['reason']} portals={len(result.get('portals',[]))} covered={len(result.get('covered',[]))}",flush=True)
    return 0 if all(r['success'] for r in results) else 2

if __name__=='__main__': sys.exit(main())
