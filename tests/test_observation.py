import copy
import unittest

from harness.contents import ContentsObserver
from harness.observation import KitchenView, ObservationViews


MATRIX = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]


def snapshot(frame=100, realtime=5, **changes):
    return dict(type='snapshot', sessionId='s', levelEpoch=1, sequence=frame, inRound=True,
                clock=dict(renderFrame=frame, realtime=realtime), objects=[], **changes)


def box(identity=1, layer=9):
    return dict(id=str(identity), name='floor', unityInstanceId=identity, layer=layer, activeInHierarchy=True,
                transform=dict(localToWorld=MATRIX[:], position=[0, 0, 0]), components=[],
                colliders=[dict(componentInstanceId=identity, enabled=True, isTrigger=False,
                                worldBounds=dict(center=[0, 0, 0], size=[2, 1, 2]),
                                shape=dict(kind='box', center=[0, 0, 0], size=[2, 1, 2]))])


def kitchen(*objects):
    result = snapshot()
    result.update(objects=list(objects), physics=dict(layers=[dict(index=9, name='Ground')]))
    return result


class ObservationTests(unittest.TestCase):
    def setUp(self):
        self.now = 0
        self.views = ObservationViews(clock=lambda: self.now)

    def state(self, session='s'):
        state = dict(sessionId=session, fresh=True)
        self.views.decorate(state)
        return state

    def test_fps_uses_game_frames_and_clock_not_network_arrival_or_time_scale(self):
        self.views.publish(snapshot())
        self.now += .1
        self.views.publish(snapshot(130, 5.5))
        self.assertEqual(self.state()['gamePerformance']['samples'], [dict(time=5.5, fps=60)])
        # Re-reading state must neither append a sample nor rebuild the map.
        self.assertEqual(self.state(), self.state())
        self.views.publish(snapshot(160, 6))
        self.assertEqual(self.state()['gamePerformance']['samples'][-1]['fps'], 60)

    def test_no_false_fps_across_stale_gaps_clock_regression_round_or_session(self):
        for reset in ['gap', 'clock', 'frame', 'epoch', 'session', 'status']:
            with self.subTest(reset=reset):
                self.views = ObservationViews(clock=lambda: self.now)
                self.views.publish(snapshot())
                self.views.publish(snapshot(130, 5.5))
                value = snapshot(160, 6)
                if reset == 'gap': self.now += 2
                if reset == 'clock': value['clock']['realtime'] = 1
                if reset == 'frame': value['clock']['renderFrame'] = 1
                if reset == 'epoch': value['levelEpoch'] = 2
                if reset == 'session': value['sessionId'] = 'new'
                if reset == 'status': value = dict(type='status', inRound=False)
                self.views.publish(value)
                self.assertFalse(self.state(value.get('sessionId'))['gamePerformance']['samples'])

    def test_stale_disconnected_or_other_session_cannot_show_map_or_fps(self):
        self.views.publish(kitchen(box()))
        self.assertIsNotNone(self.state()['kitchenMap'])
        self.assertIsNone(self.state('other')['kitchenMap'])
        self.now = 2
        self.assertFalse(self.state()['gamePerformance']['fresh'])
        self.assertIsNone(self.state()['kitchenMap'])
        self.views.publish({})
        self.assertIsNone(self.state()['kitchenMap'])

    def test_history_is_bounded_and_missing_clocks_do_not_invent_fps(self):
        for i in range(400):
            self.views.publish(snapshot(i * 30, i / 2))
        self.assertLessEqual(len(self.state()['gamePerformance']['samples']), 120)
        value = snapshot(); value.pop('clock')
        self.views.publish(value)
        self.assertIsNone(self.views.baseline)
        self.assertFalse(self.state()['gamePerformance']['samples'])

    def test_box_transform_rotation_movement_removal_and_disabled_colliders(self):
        floor = box()
        # Rotate +90 degrees around y and translate x=10,z=3.
        floor['transform']['localToWorld'] = [0,0,-1,0, 0,1,0,0, 1,0,0,0, 10,0,3,1]
        view = KitchenView()
        result = view.build(kitchen(floor), {})
        self.assertEqual(result['shapes'][0]['polygons'], [[[9,2],[11,2],[11,4],[9,4]]])
        floor['transform']['localToWorld'][12] = 20
        self.assertEqual(view.build(kitchen(floor), {})['shapes'][0]['polygons'][0][0], [19,2])
        floor['colliders'][0]['enabled'] = False
        self.assertFalse(view.build(kitchen(floor), {})['shapes'])
        self.assertFalse(view.build(kitchen(), {})['shapes'])
        self.assertFalse(view.cache)

    def test_mesh_keeps_separate_triangles_instead_of_filling_gap(self):
        floor = box()
        floor['colliders'][0]['shape'] = dict(kind='mesh', mesh=dict(assetId='m'))
        assets = dict(m=dict(data=dict(status='observed',
                           vertices=[0,0,0, 1,0,0, 0,0,1, 5,0,0, 6,0,0, 5,0,1],
                           triangles=[0,1,2, 3,4,5])))
        result = KitchenView().build(kitchen(floor), assets)
        self.assertEqual(len(result['shapes'][0]['polygons']), 2)
        self.assertFalse(result['approximateShapes'])
        assets['m']['data'] = dict(status='unreadable')
        self.assertEqual(KitchenView().build(kitchen(floor), assets)['approximateShapes'], 1)

    def test_triggers_scenery_and_players_do_not_become_floor(self):
        trigger, inactive, scenery, player = [box(i) for i in range(4)]
        trigger['colliders'][0]['isTrigger'] = True
        inactive['activeInHierarchy'] = False
        scenery['layer'] = 2
        player['components'] = [dict(type='PlayerControls')]
        result = KitchenView().build(kitchen(trigger, inactive, scenery, player), {})
        self.assertFalse(result['shapes'])
        self.assertEqual(len(result['players']), 1)

    def test_flat_floor_mesh_is_shown_but_default_layer_grass_is_not_a_wall(self):
        floor, grass = box(), box(2, layer=0)
        for obj in (floor, grass):
            obj['colliders'][0]['worldBounds']['size'][1] = 0
            obj['colliders'][0]['shape'] = dict(kind='mesh', mesh=dict(assetId='m'))
        value = kitchen(floor, grass)
        value['physics']['layers'].append(dict(index=0, name='Default'))
        result = KitchenView().build(value, dict(m=dict(data=dict(status='observed',
                    vertices=[0,0,0, 1,0,0, 0,0,1], triangles=[0,1,2]))))
        self.assertEqual([s['kind'] for s in result['shapes']], ['ground'])

    def test_mesh_age_does_not_recompute_cached_geometry_and_limits_are_explicit(self):
        floor = box()
        floor['colliders'][0]['shape'] = dict(kind='mesh', mesh=dict(assetId='m', sampleAgeSeconds=0))
        view = KitchenView()
        view.build(kitchen(floor), {})
        cached = next(iter(view.cache.values()))
        floor['colliders'][0]['shape']['mesh']['sampleAgeSeconds'] = .2
        view.build(kitchen(floor), {})
        self.assertIs(next(iter(view.cache.values()))[0], cached[0])
        view.MAX_POLYGONS = 1
        second = box(2)
        second['transform']['localToWorld'][12] = 10
        result = view.build(kitchen(box(), second), {})
        self.assertTrue(result['truncated'])
        self.assertEqual(sum(len(s['polygons']) for s in result['shapes']), 1)

    def test_same_contents_stream_publishes_both_views_without_touching_targets(self):
        observer = ContentsObserver(clock=lambda: self.now)
        value = kitchen(box()); original = copy.deepcopy(value)
        observer.publish(value)
        state = self.state()
        state['catalog'] = {'targets': [dict(id='d1', objectInstanceId=1, label='floor')]}
        observer.decorate(state)
        self.assertEqual(state['catalog']['targets'][0]['id'], 'd1')
        self.assertTrue(state['kitchenMap']['shapes'])
        self.assertEqual(value, original)


if __name__ == '__main__': unittest.main()
