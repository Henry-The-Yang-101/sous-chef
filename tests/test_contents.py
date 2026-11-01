import copy
import unittest

from harness.contents import ContentsIndex, ContentsObserver
from harness.server import HarnessBridge
from test_harness import FakeBridge


def obj(identity, instance, name, **adapters):
    return dict(id=identity, unityInstanceId=instance, name=name, activeInHierarchy=True,
                components=[dict(adapters={name: {'fields': fields} for name, fields in adapters.items()})])


def ref(identity):
    return {'objectId': identity, 'component': 'ServerPhysicalAttachment'}


def ingredient(name):
    return {'type': 'IngredientAssembledNode', 'fields': {
        'IngredientAssembledNode.m_ingriedientOrderNode': {'type': 'IngredientOrderNode', 'name': name}}}


def sample(*objects, session='test-session', epoch=1):
    return dict(type='snapshot', sessionId=session, levelEpoch=epoch, sequence=3,
                role='host', inRound=True, objects=list(objects))


def destination(instance=77, label='countertop_01_standard', kind='counter'):
    return dict(id='d7', objectInstanceId=instance, label=label, kind=kind)


class ContentsTests(unittest.TestCase):
    def setUp(self):
        self.counter = obj('counter', 77, 'countertop_01_standard',
                           ServerAttachStation={'m_item': ref('pot')},
                           ClientAttachStation={'m_item': ref('pot')})
        self.pot = obj('pot', 78, 'utensil_pot_01 (1)',
                       ServerIngredientContainer={'m_contents': [ingredient('Rice'), ingredient('Tomato')]})

    def describe(self, *objects, dest=None):
        return ContentsIndex(sample(*objects)).describe(dest or destination())

    def test_counter_shows_exact_item_and_its_ingredients(self):
        result = self.describe(self.counter, self.pot)
        self.assertEqual(result['status'], 'occupied')
        self.assertEqual(result['summary'], 'pot (Rice, Tomato)')
        self.assertEqual(result['objectId'], 'counter')

    def test_cooker_and_chopping_station_follow_their_item_edges(self):
        for name, adapter, key in [('cooker', 'ServerCookingStation', 'm_itemPot'),
                                   ('chopping', 'ServerWorkstation', 'm_item')]:
            with self.subTest(station=name):
                station = obj(name, 77, name, **{adapter: {key: ref('pot')},
                              'ServerAttachStation': {'m_item': ref('pot')}})
                result = self.describe(station, self.pot, dest=destination(label=name))
                self.assertEqual(result['summary'], 'pot (Rice, Tomato)')

    def test_item_row_shows_its_own_contents_and_empty_container(self):
        dest = destination(78, self.pot['name'], 'item')
        self.assertEqual(self.describe(self.pot, dest=dest)['summary'], 'Rice, Tomato')
        self.pot['components'][0]['adapters']['ServerIngredientContainer']['fields']['m_contents'] = []
        self.assertEqual(self.describe(self.counter, self.pot)['summary'], 'pot (empty)')
        self.assertEqual(self.describe(self.pot, dest=dest)['status'], 'empty')

    def test_plate_stacks_and_sink_dirty_plate_counts(self):
        stack = obj('stack', 77, 'plate_stack', ServerStack={'m_stackItems': [ref('a'), ref('b')]})
        plates = [obj(i, n, 'Plate', ServerIngredientContainer={'m_contents': []})
                  for i, n in [('a', 78), ('b', 79)]]
        self.assertEqual(self.describe(stack, *plates, dest=destination(label='plate_stack'))['summary'],
                         'Plate (empty) × 2')
        sink = obj('sink', 77, 'WashingPart', ServerWashingStation={'m_plateCount': 3})
        self.assertEqual(self.describe(sink, dest=destination(label='WashingPart'))['summary'], 'Dirty plates × 3')

    def test_host_and_client_values_are_not_combined(self):
        self.counter['components'][0]['adapters']['ServerAttachStation']['fields']['m_item'] = None
        self.assertEqual(self.describe(self.counter, self.pot)['status'], 'empty')
        value = sample(self.counter, self.pot)
        value['role'] = 'client'
        # A client has no supported client ingredient fields in this fixture;
        # the server's ingredient list must not be used to invent its contents.
        self.assertEqual(ContentsIndex(value).describe(destination())['summary'], 'pot')

    def test_unknown_does_not_mean_empty_and_nearby_objects_are_not_contents(self):
        cases = [sample(self.counter), sample(obj('counter', 77, self.counter['name'],
                      ServerAttachStation={'m_item': {'status': 'read_error'}})),
                 sample(obj('counter', 77, self.counter['name']))]
        for value in cases:
            with self.subTest(sample=value):
                self.assertEqual(ContentsIndex(value).describe(destination())['status'], 'unknown')
        self.counter['components'][0]['adapters']['ServerAttachStation']['fields']['m_item'] = None
        self.pot['parentId'] = 'counter'
        self.assertEqual(self.describe(self.counter, self.pot)['status'], 'empty')

    def test_cycles_and_unobserved_ingredients_are_reported_unavailable(self):
        self.pot['components'][0]['adapters']['ServerAttachStation'] = {'fields': {'m_item': ref('counter')}}
        self.assertIn('contents unavailable', self.describe(self.counter, self.pot)['summary'])
        self.pot['components'][0]['adapters'].pop('ServerAttachStation')
        self.pot['components'][0]['adapters']['ServerIngredientContainer']['fields']['m_contents'] = [{'status': 'truncated'}]
        self.assertEqual(self.describe(self.counter, self.pot)['summary'], 'pot (contents unavailable)')

    def test_composite_recipe_nodes_show_actual_ingredients(self):
        node = {'type': 'CookedCompositeAssembledNode', 'fields': {
            'CompositeAssembledNode.m_composition': [ingredient('Rice'), ingredient('Rice')],
            'CompositeAssembledNode.m_optional': [ingredient('Fish_Raw')]}}
        self.pot['components'][0]['adapters']['ServerIngredientContainer']['fields']['m_contents'] = [node]
        self.assertEqual(self.describe(self.counter, self.pot)['summary'], 'pot (Rice × 2, Fish Raw)')

    def test_live_move_stale_session_and_round_replacement(self):
        now = [100]
        observer = ContentsObserver(clock=lambda: now[0])
        state = dict(sessionId='test-session', catalog={'destinations': [destination()]})
        def current():
            value = copy.deepcopy(state)
            observer.decorate(value)
            return value['catalog']['destinations'][0]['contents']
        observer.publish(sample(self.counter, self.pot))
        self.assertEqual(current()['status'], 'occupied')
        self.counter['components'][0]['adapters']['ServerAttachStation']['fields']['m_item'] = None
        observer.publish(sample(self.counter, self.pot))
        self.assertEqual(current()['status'], 'empty')
        now[0] += 2
        self.assertEqual(current()['status'], 'unknown')
        observer.publish(sample(self.counter, self.pot, session='other-session'))
        self.assertEqual(current()['status'], 'unknown')
        observer.publish(sample(epoch=2))
        self.assertEqual(current()['status'], 'unknown')
        observer.publish({'type': 'status', 'inRound': False})
        self.assertEqual(current()['status'], 'unknown')

    def test_bridge_enrichment_preserves_exact_navigation_target(self):
        native = FakeBridge()
        native.destinations = [destination()]
        observer = ContentsObserver()
        observer.publish(sample(self.counter, self.pot))
        bridge = HarnessBridge(factory=lambda: native, contents=observer)
        bridge.poll()
        result = bridge.state()['catalog']['destinations'][0]
        self.assertEqual(result['contents']['summary'], 'pot (Rice, Tomato)')
        self.assertEqual(result['id'], 'd7')
        bridge.navigate('browser', 'test-session', 'd7', 77)
        self.assertEqual(native.calls, [('navigate_to', 'd7')])
        bridge.close()
