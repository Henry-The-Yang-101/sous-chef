"""Small browser views derived off-process from kitchen observations."""
from collections import deque
import math
import threading
import time


def finite_vector(value, length):
    return (isinstance(value, list) and len(value) == length and
            all(isinstance(v, (int, float)) and math.isfinite(v) for v in value))


def project(matrix, point):
    # Unity exports column-major localToWorld matrices.
    return [round(sum(matrix[i + 4 * j] * point[j] for j in range(3)) + matrix[i + 12], 3)
            for i in (0, 2)]


def hull(points):
    points = sorted(set(map(tuple, points)))
    def half(values):
        result = []
        for p in values:
            while len(result) >= 2:
                a, b = result[-2:]
                if (b[0]-a[0])*(p[1]-a[1]) - (b[1]-a[1])*(p[0]-a[0]) > 0:
                    break
                result.pop()
            result.append(p)
        return result
    return [list(p) for p in half(points)[:-1] + half(points[::-1])[:-1]]


class KitchenView:
    """Bounded footprint extraction, once per observation, never per HTTP viewer.

    Meshes retain individual projected triangles so holes and separate platforms
    are not filled by a bounding rectangle. Unreadable shapes are identified as
    approximate. This is a view, not a reachability or collision oracle.
    """
    MAX_POLYGONS = 3000
    MAX_MESH_INDICES = 60000

    def __init__(self):
        self.cache = {}

    def build(self, sample, assets):
        layers = {p['index']: p['name'] for p in sample.get('physics', {}).get('layers', [])}
        shapes, players, cache, seen = [], [], {}, set()
        polygons_left, approximate, truncated = self.MAX_POLYGONS, 0, False
        for obj in sample.get('objects', []):
            if not obj.get('activeInHierarchy', True):
                continue
            transform = obj.get('transform', {})
            if any(c.get('type') == 'PlayerControls' for c in obj.get('components', [])):
                if finite_vector(transform.get('position'), 3):
                    players.append({'objectInstanceId': obj.get('unityInstanceId'),
                                    'label': obj['name'], 'position': transform['position']})
                continue
            layer = layers.get(obj.get('layer'))
            kind = {'Ground': 'ground', 'Worktops': 'counter', 'Walls': 'wall',
                    'Default': 'wall', 'PlayerBlocker': 'wall'}.get(layer)
            if not kind:
                continue
            if layer == 'Ground' and 'wall' in obj.get('name', '').lower():
                kind = 'wall'
            for collider in obj.get('colliders', []):
                if not collider.get('enabled') or collider.get('isTrigger'):
                    continue
                bounds = collider.get('worldBounds', {})
                center, size = bounds.get('center'), bounds.get('size')
                if (not finite_vector(center, 3) or not finite_vector(size, 3) or min(size) < 0 or
                        min(size[0], size[2]) <= .001):
                    continue
                # Default-layer mesh planes include grass and decorative paths
                # outside the playable floor. They are not kitchen walls.
                if layer == 'Default' and (collider.get('shape', {}).get('kind') != 'box' or size[1] < .1):
                    continue
                # Do not let scenery, the sky or distant level-wide blockers
                # set the kitchen's extent. Ground is allowed to be large.
                if kind != 'ground' and (max(size[0], size[2]) > 65 or center[1] > 20):
                    continue
                matrix, shape = transform.get('localToWorld'), collider.get('shape', {})
                geometry = dict(shape)
                for asset_kind in ('mesh', 'terrain'):
                    if geometry.get(asset_kind):
                        geometry[asset_kind] = geometry[asset_kind].get('assetId')
                key = (collider.get('componentInstanceId'), repr(matrix), repr(geometry), repr(bounds))
                if key in self.cache:
                    polygons, estimated = self.cache[key]
                else:
                    polygons, estimated = self.footprint(shape, matrix, bounds, assets)
                cache[key] = (polygons, estimated)
                if len(polygons) > polygons_left:
                    polygons = polygons[:polygons_left]
                    truncated = True
                polygons_left -= len(polygons)
                if not polygons:
                    continue
                height = round(center[1] + size[1] / 2, 2)
                signature = (kind, height, repr(polygons))
                if signature in seen:
                    continue
                seen.add(signature)
                shapes.append({'kind': kind, 'height': height, 'polygons': polygons})
                approximate += int(estimated)
        self.cache = cache
        return {'shapes': shapes, 'players': players, 'approximateShapes': approximate,
                'truncated': truncated, 'levelEpoch': sample.get('levelEpoch'),
                'sequence': sample.get('sequence')}

    def footprint(self, shape, matrix, bounds, assets):
        if finite_vector(matrix, 16):
            center, size = shape.get('center'), shape.get('size')
            if shape.get('kind') == 'box' and finite_vector(center, 3) and finite_vector(size, 3):
                points = [project(matrix, [center[i] + sign[i] * size[i] / 2 for i in range(3)])
                          for sign in ((x, y, z) for x in (-1, 1) for y in (-1, 1) for z in (-1, 1))]
                return [hull(points)], False
            if shape.get('kind') == 'mesh':
                data = assets.get((shape.get('mesh') or {}).get('assetId'), {}).get('data', {})
                indices, vertices = data.get('triangles', []), data.get('vertices', [])
                if data.get('status') == 'observed' and len(indices) <= self.MAX_MESH_INDICES:
                    polygons, seen = [], set()
                    for i in range(0, len(indices) - 2, 3):
                        triangle = [project(matrix, vertices[n*3:n*3+3]) for n in indices[i:i+3]
                                    if isinstance(n, int) and 0 <= n*3 < len(vertices)-2]
                        if len(triangle) != 3:
                            continue
                        a, b, c = triangle
                        if abs((b[0]-a[0])*(c[1]-a[1]) - (b[1]-a[1])*(c[0]-a[0])) < .0001:
                            continue
                        if (b[0]-a[0])*(c[1]-a[1]) - (b[1]-a[1])*(c[0]-a[0]) < 0:
                            triangle.reverse()
                        key = tuple(sorted(map(tuple, triangle)))
                        if key not in seen:
                            seen.add(key)
                            polygons.append(triangle)
                    return polygons, False
        center, size = bounds['center'], bounds['size']
        return [[[round(center[0]+x*size[0]/2, 3), round(center[2]+z*size[2]/2, 3)]
                 for x, z in ((-1,-1), (1,-1), (1,1), (-1,1))]], True


class ObservationViews:
    STALE_SECONDS = 1.5

    def __init__(self, clock=time.monotonic):
        self.clock, self.lock = clock, threading.Lock()
        self.kitchen = KitchenView()
        self.samples = deque(maxlen=120)
        self.identity = self.baseline = self.observed = self.map = None

    def publish(self, sample, assets=None):
        now = self.clock()
        valid = sample.get('type') in ('snapshot', 'harness') and sample.get('inRound')
        identity = (sample.get('sessionId'), sample.get('levelEpoch')) if valid else None
        kitchen = self.kitchen.build(sample, assets or {}) if valid else None
        clock = sample.get('clock', {})
        realtime, frame = clock.get('realtime'), clock.get('renderFrame')
        with self.lock:
            if identity != self.identity or not valid:
                self.samples.clear()
                self.baseline = None
            if self.observed is not None and now - self.observed > self.STALE_SECONDS:
                self.samples.clear()
                self.baseline = None
            self.identity, self.map, self.observed = identity, kitchen, now if valid else None
            if not (valid and isinstance(frame, int) and frame >= 0 and
                    isinstance(realtime, (float, int)) and math.isfinite(realtime)):
                self.baseline = None
                self.samples.clear()
                return
            if self.baseline:
                elapsed, frames = realtime - self.baseline[0], frame - self.baseline[1]
                if elapsed < 0 or frames < 0:
                    self.samples.clear()
                    self.baseline = None
                elif elapsed >= .5:
                    self.samples.append({'time': realtime, 'fps': round(frames / elapsed, 1)})
                    self.baseline = None
            if self.baseline is None:
                self.baseline = (realtime, frame)
            while self.samples and realtime - self.samples[0]['time'] > 60:
                self.samples.popleft()

    def decorate(self, state):
        with self.lock:
            age = None if self.observed is None else self.clock() - self.observed
            fresh = (age is not None and age <= self.STALE_SECONDS and self.identity is not None and
                     self.identity[0] == state.get('sessionId') and state.get('fresh', False))
            state['gamePerformance'] = {'fresh': fresh, 'samples': list(self.samples) if fresh else [],
                                        'ageMs': None if age is None else round(age * 1000)}
            state['kitchenMap'] = dict(self.map, ageMs=round(age * 1000)) if fresh else None
