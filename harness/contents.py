"""Names from observed holder edges and ingredient nodes, never from proximity."""
from collections import Counter
import re
import threading
import time

from clients.bridge import BridgeClient


class Unobserved(ValueError):
    pass


def item_name(raw):
    text = re.sub(r'\s*\(Clone\)|\s*\(\d+\)$', '', raw or '', flags=re.I)
    text = re.sub(r'^utensil_|_0?1\b', '', text, flags=re.I)
    return text.replace('_', ' ').strip() or 'Unnamed item'


def grouped(names):
    return ', '.join(name if count == 1 else f'{name} × {count}'
                     for name, count in Counter(names).items())


def ingredient_names(node, depth=0):
    if not isinstance(node, dict) or node.get('status') or depth > 8:
        raise Unobserved('Ingredient details unavailable')
    if node.get('type') == 'NullAssembledNode':
        return []
    fields = node.get('fields', {})
    if 'CompositeAssembledNode.m_composition' in fields:
        names = []
        for key in ['CompositeAssembledNode.m_composition', 'CompositeAssembledNode.m_optional']:
            for child in fields.get(key) or []:
                names.extend(ingredient_names(child, depth + 1))
                if len(names) > 64:
                    raise Unobserved('Too many ingredients to describe')
        return names
    for key in ['IngredientAssembledNode.m_ingriedientOrderNode', 'ItemAssembledNode.m_itemOrderNode']:
        definition = fields.get(key)
        if isinstance(definition, dict) and definition.get('name'):
            return [item_name(definition['name'])]
    prefab = fields.get('AssembledDefinitionNode.m_freeObject')
    if isinstance(prefab, dict) and prefab.get('name'):
        return [item_name(prefab['name'])]
    raise Unobserved('Ingredient name unavailable')


class ContentsIndex:
    def __init__(self, sample):
        self.session = sample['sessionId']
        self.epoch = sample['levelEpoch']
        self.sequence = sample['sequence']
        self.host = sample.get('role') == 'host'
        self.objects = {obj['id']: obj for obj in sample['objects']}
        self.instances = {obj['unityInstanceId']: obj for obj in sample['objects']
                          if 'unityInstanceId' in obj and obj.get('activeInHierarchy', True)}

    @staticmethod
    def adapters(obj):
        return {name: adapter.get('fields', {}) for component in obj.get('components', [])
                for name, adapter in component.get('adapters', {}).items()}

    def contents(self, obj, parents=()):
        if obj['id'] in parents or len(parents) >= 8:
            raise Unobserved('Holder details unavailable')
        parents = (*parents, obj['id'])
        adapters = self.adapters(obj)
        known, labels, references, seen = False, [], [], set()

        def fields_for(suffix):
            prefixes = ['Server', 'Client'] if self.host else ['Client']
            return next((adapters[p + suffix] for p in prefixes if p + suffix in adapters), None)

        def read(fields, key):
            if key not in fields or (isinstance(fields[key], dict) and fields[key].get('status')):
                raise Unobserved('Holder field unavailable')
            return fields[key]

        # These are explicit game relationships; hierarchy children, colliders,
        # nearby utensils and dispenser prefabs are not evidence of occupancy.
        for suffix, keys in [('AttachStation', ['m_item']), ('Workstation', ['m_item']),
                             ('CookingStation', ['m_itemPot']),
                             ('PlateReturnStation', ['m_stack', 'm_attachStation']),
                             ('PlateStackBase', ['m_stack'])]:
            fields = fields_for(suffix)
            if fields is None:
                continue
            for key in keys:
                # Client cooking stations have no supported occupancy adapter;
                # only the explicitly exported server relation is consumed.
                if key not in fields:
                    continue
                reference = read(fields, key)
                if (suffix in {'PlateReturnStation', 'PlateStackBase'} and
                        isinstance(reference, dict) and reference.get('objectId') == obj['id']):
                    continue
                known = True
                if reference is not None:
                    references.append(reference)
        fields = fields_for('Stack')
        if fields is not None:
            known = True
            values = read(fields, 'm_stackItems')
            if not isinstance(values, list):
                raise Unobserved('Stack details unavailable')
            references.extend(values)
        fields = fields_for('IngredientContainer')
        if fields is not None:
            known = True
            values = read(fields, 'm_contents')
            if not isinstance(values, list):
                raise Unobserved('Ingredient contents unavailable')
            for node in values:
                labels.extend(ingredient_names(node))
        if self.host and 'ServerWashingStation' in adapters:
            known = True
            count = read(adapters['ServerWashingStation'], 'm_plateCount')
            if not isinstance(count, int) or count < 0:
                raise Unobserved('Sink contents unavailable')
            if count:
                labels.append('Dirty plate' if count == 1 else f'Dirty plates × {count}')
        for reference in references:
            if not isinstance(reference, dict) or not reference.get('objectId'):
                raise Unobserved('Item reference unavailable')
            identity = reference['objectId']
            # Some station wrappers point to another component on themselves.
            if identity == obj['id'] or identity in seen:
                continue
            seen.add(identity)
            item = self.objects.get(identity)
            if item is None or not item.get('activeInHierarchy', True):
                raise Unobserved('Held item unavailable')
            label = item_name(item['name'])
            try:
                nested_known, nested_labels = self.contents(item, parents)
                if nested_known:
                    label += f' ({grouped(nested_labels) if nested_labels else "empty"})'
            except Unobserved:
                label += ' (contents unavailable)'
            labels.append(label)
        if len(labels) > 64:
            raise Unobserved('Too many contents to describe')
        return known, labels

    def describe(self, destination):
        obj = self.instances.get(destination.get('objectInstanceId'))
        if obj is None or obj['name'] != destination.get('label'):
            return {'status': 'unknown', 'summary': 'Contents unavailable'}
        try:
            known, labels = self.contents(obj)
        except Unobserved:
            return {'status': 'unknown', 'summary': 'Contents unavailable'}
        if not known:
            if destination.get('kind') in {'counter', 'workstation', 'cooking_station', 'washing_station', 'plate_station', 'pickup_or_placement'}:
                return {'status': 'unknown', 'summary': 'Contents unavailable'}
            return {'status': 'not_applicable', 'summary': ''}
        return {'status': 'occupied' if labels else 'empty',
                'summary': grouped(labels) if labels else 'Empty',
                'items': labels, 'objectId': obj['id']}


class ContentsObserver:
    """One read-only full stream, shared by all browser viewers."""
    STALE_SECONDS = 1.5

    def __init__(self, port=24702, factory=None, clock=time.monotonic):
        self.factory = factory or (lambda: BridgeClient(port, timeout=1))
        self.clock = clock
        self.lock = threading.Lock()
        self.halt = threading.Event()
        self.thread = None
        self.index = None
        self.observed = None

    def publish(self, sample):
        index = ContentsIndex(sample) if sample.get('type') == 'snapshot' and sample.get('inRound') else None
        with self.lock:
            self.index = index
            self.observed = self.clock() if index else None

    def decorate(self, state):
        with self.lock:
            index, observed = self.index, self.observed
        fresh = (index is not None and observed is not None and
                 self.clock() - observed <= self.STALE_SECONDS and index.session == state.get('sessionId'))
        state['contentsObservation'] = {'fresh': fresh,
            'ageMs': None if observed is None else round((self.clock() - observed) * 1000),
            'levelEpoch': index.epoch if fresh else None, 'sequence': index.sequence if fresh else None}
        for key in ('destinations', 'targets'):
            for destination in state.get('catalog', {}).get(key, []):
                destination['contents'] = index.describe(destination) if fresh else {
                    'status': 'unknown', 'summary': 'Contents unavailable'}

    def start(self):
        self.thread = threading.Thread(target=self._run, name='contents-observer', daemon=True)
        self.thread.start()

    def _run(self):
        while not self.halt.is_set():
            try:
                with self.factory() as client:
                    for sample in client.messages():
                        if self.halt.is_set():
                            break
                        if sample.get('type') in {'snapshot', 'status', 'hello'}:
                            self.publish(sample)
            except (OSError, EOFError, ValueError, KeyError, TypeError):
                self.publish({})
            if not self.halt.is_set():
                self.halt.wait(.5)

    def close(self):
        self.halt.set()
        if self.thread:
            self.thread.join(timeout=3)
        self.publish({})
