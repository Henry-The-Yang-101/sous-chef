import copy
import json
from pathlib import Path
import unittest

from harness.semantic_map import sample_floor, spatial_context, candidate_description
from harness.decision.compiler import Compiler, Clarification, build_context
from harness.decision.service import InterpreterService
from harness.decision.protocol import DecisionError, validate_response
from test_decision import world, response, Script


def scene():
    points = []
    for z in range(5):
        for x in range(5):
            points.append(dict(id=f'd{10+z*5+x}', binding='floor', position=[x,0,z], supportId='ground',
                               standing='verified', routeStatus='unknown', supportLocalPosition=[x,0,z]))
    edges = [{'from': p['id'], 'to': q['id'], 'direction': 'both'} for p in points for q in points
             if sum(abs(a-b) for a,b in zip(p['position'],q['position'])) == 1]
    return dict(epoch='round_1', observedAt=10, orientation=dict(known=True, source='game_camera', right=[1,0,0], up=[0,0,1]),
                actor={'playerId': 2, 'position': [4,0,4]}, points=points, walkingEdges=edges, platforms=[],
                stations=[dict(id='d1', binding='station', kind='workstation', label='Chopping board', position=[0,0,0], objectInstanceId=100),
                          dict(id='d2', binding='station', kind='workstation', label='Chopping board', position=[4,0,4], objectInstanceId=101)],
                coverage={'complete': False, 'reason': 'sampled_geometry'})


def context(native=None):
    value = world()
    value['capabilities']['semantic_catalog'].update(movement=native or scene(), players=[{'playerId':1,'position':[0,0,4]}])
    return build_context(value, {'revision': 0, 'active': None, 'pending': []})


class SamplingTests(unittest.TestCase):
    def test_connected_floor_extremities_and_stable_order(self):
        data=scene(); sampled, coverage=sample_floor(data['points'],data['walkingEdges'])
        self.assertEqual(len(sampled),18)
        self.assertTrue({'d10','d14','d30','d34'} <= {p['id'] for p in sampled})
        again,_=sample_floor(list(reversed(data['points'])),list(reversed(data['walkingEdges'])))
        self.assertEqual([p['id'] for p in sampled],[p['id'] for p in again])
        self.assertTrue(coverage['complete'])

    def test_distinct_supports_and_decks_are_reserved_even_when_connected(self):
        data=scene(); data['points'][0].update(platform=True,supportId='raft')
        data['points'][1]['position'][1]=5
        sampled,_=sample_floor(data['points'],data['walkingEdges'],budget=2)
        self.assertTrue({'d10','d11'} <= {p['id'] for p in sampled})
        self.assertGreaterEqual(len(sampled),3)

    def test_incomplete_edges_never_become_disconnection_or_silent_omission(self):
        data=scene(); sampled,coverage=sample_floor(data['points'],[],budget=12)
        self.assertEqual(len(sampled),25)
        self.assertTrue(coverage['budget_expanded'])
        sampled,coverage=sample_floor(data['points'],[],maximum=20)
        self.assertEqual(sampled,[])
        self.assertFalse(coverage['complete'])

    def test_native_invalid_and_missing_points_never_get_invented(self):
        data=scene();data['points'][0]['standing']='blocked';data['points'][1]['position']=[float('nan'),0,0]
        sampled,_=sample_floor(data['points'],data['walkingEdges'])
        self.assertFalse({'d10','d11'} & {p['id'] for p in sampled})

    def test_camera_rotation_player_reference_and_station_copies(self):
        data=scene();value=spatial_context({'movement':data,'players':[{'playerId':1,'position':[0,0,4]}]})
        a=next(p for p in value['candidates'] if p['id']=='d1')
        self.assertEqual(a['screen'],{'x':0,'y':1})
        self.assertNotIn('location',a)
        self.assertIn('(x=0, y=1)',candidate_description(a))
        self.assertNotIn('bottom left',candidate_description(a))
        self.assertNotIn('position',a)
        self.assertNotIn('right',value['orientation'])
        self.assertEqual({p['playerId'] for p in value['players']},{1,2})
        data['orientation'].update(right=[-1,0,0],up=[0,0,-1])
        rotated=spatial_context({'movement':data})
        self.assertEqual(next(p for p in rotated['candidates'] if p['id']=='d1')['screen'],{'x':1,'y':0})
        data['orientation']['known']=False
        self.assertNotIn('screen',spatial_context({'movement':data})['candidates'][0])

    def test_moving_counter_velocity_uses_same_camera_frame_without_forecasting(self):
        data=scene();data['stations'][0].update(motion='observed_moving',velocity=[2,0,-3])
        data['orientation'].update(right=[-1,0,0],up=[0,0,-1])
        point=next(p for p in spatial_context({'movement':data})['candidates'] if p['id']=='d1')
        self.assertEqual(point['motion'],'observed_moving')
        self.assertEqual(point['screen_velocity'],{'x':-2,'y':-3})
        self.assertNotIn('velocity',point)

    def test_identity_revalidation_allows_motion_but_rejects_rebinding_and_epoch(self):
        before={'movement':scene()};after=copy.deepcopy(before)
        task={'tasks':[{'action':'navigate_to','arguments':{'destination_id':'d10'}}]}
        after['movement']['points'][0]['position']=[100,0,100]
        InterpreterService._exact_bindings(before,after,task)
        after['movement']['points'][0]['supportLocalPosition']=[1,0,0]
        with self.assertRaises(DecisionError):InterpreterService._exact_bindings(before,after,task)
        after=copy.deepcopy(before);after['movement']['epoch']='new_round'
        with self.assertRaises(DecisionError):InterpreterService._exact_bindings(before,after,task)
        after=copy.deepcopy(before);after['movement']['stations'][0]['supportId']='changed_parent'
        task['tasks'][0]['arguments']['destination_id']='d1'
        InterpreterService._exact_bindings(before,after,task)
        after['movement']['stations'][0]['objectInstanceId']=999
        with self.assertRaises(DecisionError):InterpreterService._exact_bindings(before,after,task)


class MovementCompilerTests(unittest.IsolatedAsyncioTestCase):
    def assert_current_request_matches_recording(self, current, recorded):
        recorded = copy.deepcopy(recorded)
        self.assertEqual(recorded['state'].pop('dialogue'), {'recent_turns': []})
        self.assertEqual(current, recorded)

    async def test_historical_labeled_requests_remain_archived(self):
        evidence=json.loads((Path(__file__).resolve().parents[1]/'docs/grounded-movement-language.json').read_text())
        for trial in evidence['trials']:
            validate_response(trial['request'],trial['response'])

    async def test_numerical_playground_requests_match_production(self):
        from scripts.jev_coordinates_playground import fixtures, request
        evidence=json.loads((Path(__file__).resolve().parents[1]/'docs/jev-coordinates-playground.json').read_text())
        for case in fixtures():
            variant='holdout' if case['name'].startswith('holdout_') else 'final'
            trial=next(r for r in evidence['trials'] if r['case']==case['name'] and r['variant']==variant)
            current=await request(case)
            self.assert_current_request_matches_recording(current, trial['request'])
            validate_response(trial['request'], trial['response'])
            self.assertNotIn('"location":',json.dumps(current))
            approved={k for k,a in trial['response']['answers'].items() if a['noul']>=.85}
            self.assertLessEqual(approved,set(case['acceptable']))
            self.assertEqual(bool(approved),bool(case['acceptable']))

    async def test_real_movement_gates_preserve_floor_clarification(self):
        from scripts.jev_coordinates_playground import fixtures
        evidence=json.loads((Path(__file__).resolve().parents[1]/'docs/jev-coordinates-playground.json').read_text())
        for name in ('interior','moving_raft','floor'):
            case=next(c for c in fixtures() if c['name']==name)
            history=[next(r for r in evidence['trials'] if r['case']==name and r['variant']==variant)
                     for variant in ('stages','final')]
            if name=='floor':
                history.append(next(r for r in evidence['trials'] if r['variant']=='equivalence_final2'))
            async def ask(value):
                observed=history.pop(0)
                self.assert_current_request_matches_recording(value, observed['request'])
                return validate_response(observed['request'], observed['response'])
            c=Compiler({'id':'replay','text':case['text']},build_context(case['world'],
                       {'revision':0,'active':None,'pending':[]}),ask)
            if name=='floor':
                with self.assertRaises(Clarification) as raised:await c.movement(1)
                self.assertTrue(raised.exception.stage.endswith('.equivalence'))
            else:
                self.assertIn((await c.movement(1))['destination_id'],case['acceptable'])
            self.assertEqual(history,[])

    async def test_bottom_left_board_has_spatial_context_and_exact_identity(self):
        script=Script([{'simple_coverage':0},{'next_task':'native_action'},{'action':'navigate_to'},
                       {'binding':'station'},{'d1':1,'d2':0},{},{'next_task':'done'},{}])
        c=Compiler({'id':'test','text':'Walk to the chopping board at the bottom left'},context(),script)
        result=await c.compile()
        self.assertEqual(result['plan']['tasks'][0]['arguments'],{'destination_id':'d1'})
        self.assertEqual(result['plan']['after_plan'],'pause_pending')
        self.assertIn('(x=0, y=1)',result['message'])
        self.assertNotIn('bottom left',result['message'])
        candidates=script.requests[4]['state']['compiler']['candidates']
        self.assertEqual(candidates[0]['screen'],{'x':0,'y':1})
        self.assertIn('movement',script.requests[-1]['state']['world'])

    async def test_dense_station_map_splits_checks_without_skipping_stations(self):
        data = scene()
        data['stations'] = [
            dict(id=f'd{100+i}', binding='station', kind='workstation',
                 label=f'Chopping board and preparation counter on the main kitchen floor {i}',
                 position=[i % 8, 0, i // 8], objectInstanceId=i,
                 routeStatus='unknown', standing='not_applicable', motion='observed_stationary')
            for i in range(56)
        ]
        batches = []
        async def ask(request):
            if 'binding' in request['questions']:
                return response(request, {'binding': 'station'})
            batches.append(list(request['questions']))
            return response(request, {key: float(key == 'd155') for key in request['questions']})
        c = Compiler({'id': 'test', 'text': 'Go to the chopping board'}, context(data), ask)
        self.assertEqual(await c.movement(1), {'destination_id': 'd155'})
        self.assertLess(len(batches[0]), 18)
        self.assertEqual([key for batch in batches for key in batch], [s['id'] for s in data['stations']])

    async def test_equivalent_positions_use_approval_not_divided_choice_probability(self):
        script=Script([{'binding':'floor'},dict.fromkeys([p['id'] for p in sample_floor(scene()['points'],scene()['walkingEdges'])[0]],0)])
        script.steps[1].update(d10=.9,d14=.9)
        script.steps.append({'interchangeable':1})
        c=Compiler({'id':'test','text':'Go to the bottom'},context(),script)
        self.assertEqual(await c.movement(1),{'destination_id':'d10'})
        self.assertNotIn('criteria',script.requests[1]['questions']['d10'])

    async def test_distinct_meanings_clarify_and_explicit_answer_replays(self):
        script=Script([{'binding':'station'},{'d1':1,'d2':1},{'interchangeable':.5}])
        c=Compiler({'id':'test','text':'Go to that board'},context(),script)
        with self.assertRaises(Clarification) as raised:await c.movement(1)
        c.resolve(raised.exception,'d2')
        self.assertEqual(await c.movement(1),{'destination_id':'d2'})
        self.assertEqual(len(script.requests),3)

    async def test_no_fit_has_no_fallback_and_explicit_choice_can_resolve(self):
        script=Script([{'binding':'station'},{'d1':0,'d2':0}])
        c=Compiler({'id':'test','text':'Go to the upper deck board'},context(),script)
        with self.assertRaises(Clarification) as raised:await c.movement(1)
        c.resolve(raised.exception,'d1')
        self.assertEqual(await c.movement(1),{'destination_id':'d1'})

    async def test_explicit_follow_on_work_continues_queue(self):
        script=Script([{'simple_coverage':0},{'next_task':'native_action'},{'action':'navigate_to'},
                       {'binding':'station'},{'d1':1,'d2':0},{},{'next_task':'wash_dishes'},{},{'next_task':'done'},{}])
        c=Compiler({'id':'test','text':'Go to the board, then wash dishes'},context(),script)
        self.assertEqual((await c.compile())['plan']['after_plan'],'continue_pending')

    async def test_blocked_platform_is_not_filtered_by_eligibility(self):
        data=scene();data['platforms']=[dict(id='d90',binding='platform',supportId='raft',label='Raft',position=[4,0,0],routeStatus='blocked')]
        script=Script([{'binding':'platform'},{'d90':1}]);c=Compiler({'id':'test','text':'Board the right raft'},context(data),script)
        self.assertEqual(await c.movement(1),{'destination_id':'d90'})
        self.assertEqual(script.requests[1]['state']['compiler']['candidates'][0]['routeStatus'],'blocked')


if __name__=='__main__':unittest.main()
