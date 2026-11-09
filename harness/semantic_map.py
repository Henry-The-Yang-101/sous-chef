"""Grounded spatial menus. Geometry and route permission remain native-owned.

Sampling never reads the utterance. Jev judges meaning; distance only breaks ties
inside a set it explicitly judged interchangeable. Unseen paths remain unknown.
"""
import copy
import math


def _position(value):
    p = value.get('position')
    return p if isinstance(p, list) and len(p) == 3 and all(type(x) in (int, float) and math.isfinite(x) for x in p) else None


def _distance(a, b):
    return sum((x-y)**2 for x, y in zip(a['position'], b['position']))


def sample_floor(points, edges=(), budget=18, *, maximum=180):
    """Reserve each observed walking component/support/deck, then farthest-first.

    Components are a sampling partition, never a claim that missing edges cannot
    be crossed. Budget expands to retain distinct observed areas; a hard bound
    returns an explicit coverage gap instead of silently dropping an area.
    """
    candidates = {p['id']: p for p in points if p.get('standing') == 'verified' and _position(p)}
    parents = {key: key for key in candidates}
    def root(key):
        while parents[key] != key:
            parents[key] = parents[parents[key]]
            key = parents[key]
        return key
    for edge in edges:
        a, b = edge.get('from'), edge.get('to')
        if edge.get('direction') == 'both' and a in parents and b in parents:
            parents[root(b)] = root(a)
    groups = {}
    for key, point in sorted(candidates.items()):
        # A temporary crossing does not merge physical moving supports. Distinct
        # decks remain represented even if a staircase connects their graphs.
        group = (root(key), point.get('supportId') if point.get('platform') else 'static', round(point['position'][1]*2))
        groups.setdefault(group, []).append(point)
    if len(groups) > maximum:
        return [], {'complete': False, 'reason': 'too_many_observed_components', 'required': len(groups)}
    selected = []
    for group in groups.values():
        # Begin with the point nearest the group's centroid (stable ID ties),
        # then cover its extremities below. This avoids a systematic corner bias.
        centre = {'position': [sum(p['position'][i] for p in group)/len(group) for i in range(3)]}
        selected.append(min(group, key=lambda p: (_distance(p, centre), p['id'])))
    wanted = min(max(budget, len(groups)), maximum, len(candidates))
    chosen = {p['id'] for p in selected}
    while len(selected) < wanted:
        remaining = [p for p in candidates.values() if p['id'] not in chosen]
        point = min(remaining, key=lambda p: (-min(_distance(p, q) for q in selected), p['id']))
        selected.append(point); chosen.add(point['id'])
    return selected, {'complete': True, 'sampled': len(selected), 'observed_points': len(candidates),
                      'observed_components': len(groups), 'budget_expanded': len(selected) > budget}


def spatial_context(semantic, *, budget=18):
    native = semantic.get('movement') or {}
    points = [p for p in native.get('points', []) if _position(p)]
    floor, coverage = sample_floor(points, native.get('walkingEdges', []), budget)
    orientation = copy.deepcopy(native.get('orientation') or {'known': False, 'source': 'unavailable'})
    right, up = orientation.get('right'), orientation.get('up')
    oriented = orientation.get('known') and _position({'position': right}) and _position({'position': up})
    def project(p):
        return [sum(p[i]*axis[i] for i in range(3)) for axis in (right, up)]
    playable = [project(p['position']) for p in points if p.get('standing') == 'verified'] if oriented else []
    bounds = ([min(p[i] for p in playable) for i in range(2)], [max(p[i] for p in playable) for i in range(2)]) if playable else None
    def describe(item):
        result = copy.deepcopy(item)
        p = _position(item)
        if p and bounds:
            xy = project(p)
            result['screen'] = {key: round((xy[i]-bounds[0][i])/max(.01, bounds[1][i]-bounds[0][i]), 3) for i, key in enumerate(('right', 'up'))}
            result['location'] = location_description(result['screen'])
        if p:
            result['height'] = round(p[1], 3)
            stations = sorted((s for s in native.get('stations', []) if _position(s)), key=lambda s: (_distance(item, s), s['id']))
            result['nearby_stations'] = [{'id': s['id'], 'label': s['label'], 'kind': s.get('kind'),
                'distance': round(math.sqrt(_distance(item, s)), 2)} for s in stations[:3] if _distance(item,s) <= 16]
        # Raw world/local axes compete with the player-facing frame, especially
        # in rotated kitchens. Exact transforms stay in the native registry.
        for key in ('position', 'supportLocalPosition', 'objectInstanceId'):
            result.pop(key, None)
        return result
    stations = native.get('stations', [])
    candidates = [describe(p) for p in stations + native.get('platforms', []) + floor]
    players = [describe(p) for p in semantic.get('players', []) + [native.get('actor', {})] if _position(p)]
    supports = {}
    for p in points:
        if not p.get('platform'): continue
        supports.setdefault(p['supportId'], {'id': p['supportId'], 'point_count': 0})['point_count'] += 1
    for support in native.get('supports', []):
        value = describe(support)
        velocity = value.pop('velocity', None)
        if velocity and oriented:
            value['screen_velocity'] = dict(zip(('right', 'up'), project(velocity)))
        supports.setdefault(support['id'], {}).update(value)
    return {'epoch': native.get('epoch'), 'observed_at': native.get('observedAt'),
            'orientation': {k:v for k,v in orientation.items() if k not in ('right','up')},
            'coordinate_meaning': 'screen.right increases left to right; screen.up increases bottom to top; normalized to observed playable standing geometry. Height is separate. Unknown orientation cannot resolve directional language.',
            'binding_meaning': 'station follows that exact object; platform and floor references retain their support-local point. Right/left platform labels refer to THIS snapshot, even after platforms exchange sides. Arrival performs no interaction.',
            'coverage': {'native': copy.deepcopy(native.get('coverage', {})), 'menu': coverage},
            'players': players, 'supports': list(supports.values()), 'candidates': candidates,
            'route_policy': 'Only native checked routes authorize movement. Blocked and unknown destinations remain selectable; execution waits neutrally and rechecks. Motion does not predict a future crossing or opening time.'}


def candidate_description(item):
    label = item.get('label') or ('Floor position' if item.get('binding') == 'floor' else 'Platform')
    screen = item.get('screen')
    if screen:
        label += ' · ' + location_description(screen)
    return label + f' ({item["id"]})'


def location_description(screen):
    vertical = 'bottom' if screen['up'] < 1/3 else 'top' if screen['up'] > 2/3 else 'middle'
    horizontal = 'left' if screen['right'] < 1/3 else 'right' if screen['right'] > 2/3 else 'centre'
    return f'{vertical} {horizontal}'
