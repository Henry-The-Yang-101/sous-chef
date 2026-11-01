"""Convenience indexes over one full snapshot; never infer missing observations."""
from collections import defaultdict


class World:
    def __init__(self, snapshot):
        if snapshot.get('type') != 'snapshot' or not snapshot.get('inRound'):
            raise ValueError('World requires an active-round snapshot')
        self.snapshot = snapshot
        self.objects = {obj['id']: obj for obj in snapshot['objects']}
        self.components = defaultdict(list)
        self.adapters = defaultdict(list)
        for obj in self.objects.values():
            for component in obj['components']:
                self.components[component['type']].append((obj, component))
                for name, adapter in component.get('adapters', {}).items():
                    self.adapters[name].append((obj, component, adapter['fields']))

    def resolve(self, reference):
        if reference is None: return None
        return self.objects[reference['objectId']]

    def with_component(self, type_name):
        return [obj for obj, _ in self.components[type_name]]

    def observations(self, adapter_name):
        """Yield (object, component, original-field-map); inherited adapters work too."""
        return iter(self.adapters[adapter_name])

    def held_items(self):
        """Authoritative host carrier edges. On a client, use client observations."""
        for chef, component, fields in self.observations('ServerPlayerAttachmentCarrier'):
            for reference in fields['m_carriedObjects']:
                if reference: yield chef, self.resolve(reference)


class GameplayWorld(World):
    """Indexes a complete gameplay sample, not map geometry or a merged full world.

    parentId may refer outside this subset. Use a matching session/epoch full
    snapshot for map structure; never treat old map transforms as current motion.
    """
    def __init__(self, sample):
        if sample.get('type') != 'gameplay' or not sample.get('inRound'):
            raise ValueError('GameplayWorld requires an active gameplay sample')
        super().__init__(dict(sample, type='snapshot'))
        self.snapshot = sample
