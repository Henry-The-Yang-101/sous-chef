import copy
import http.client
import json
import threading
import unittest
from http.server import ThreadingHTTPServer

from clients.control import ControlError
from harness.server import Handler, HarnessBridge, HarnessError
from harness.sharing import PhoneShare


class FakeBridge:
    def __init__(self):
        self.hello = {'sessionId': 'test-session'}
        self.destinations = [dict(id='d7', objectInstanceId=77, label='utensil_pot_01',
                                  kind='item', position=[1, 0, 2], routeDistance=3,
                                  directDistance=2)]
        self.control = dict(ownedByYou=False, available=True, navigation={'state': 'idle'})
        self.calls = []
        self.failure = None
        self.reject = None

    def available_actions(self):
        if self.failure:
            raise self.failure
        self.last_response = {'control': copy.deepcopy(self.control)}
        return {'destinations': copy.deepcopy(self.destinations), 'navigation': self.control['navigation']}

    def navigate_to(self, destination):
        self.calls.append(('navigate_to', destination))
        if self.reject:
            raise ControlError(self.reject)
        self.control.update(ownedByYou=True, navigation={'state': 'navigating', 'destinationId': destination})
        return copy.deepcopy(self.control)

    def status(self):
        return copy.deepcopy(self.control)

    def stop(self):
        self.calls.append(('stop',))
        self.control.update(ownedByYou=False, navigation={'state': 'idle', 'reason': 'stopped'})
        return copy.deepcopy(self.control)

    def release(self):
        self.calls.append(('release',))
        self.control.update(ownedByYou=False, navigation={'state': 'idle', 'reason': 'released'})

    def close(self):
        self.calls.append(('close',))


class HarnessTests(unittest.TestCase):
    def setUp(self):
        self.now = 100.0
        self.native = FakeBridge()
        self.bridge = HarnessBridge(factory=lambda: self.native, clock=lambda: self.now)
        self.bridge.poll()

    def navigate(self):
        return self.bridge.navigate('tab-a', 'test-session', 'd7', 77)

    def test_live_catalog_replaces_removed_destinations(self):
        self.assertEqual(self.bridge.state()['catalog']['destinations'][0]['id'], 'd7')
        self.native.destinations.clear()
        self.bridge.poll()
        self.assertEqual(self.bridge.state()['catalog']['destinations'], [])

    def test_dispenser_metadata_and_distances_follow_live_catalog(self):
        self.native.destinations = [dict(id='d8', objectInstanceId=78, label='DispenserCrate 3 (2)',
                                        kind='ingredient_supply', foodName='Cheese_Raw',
                                        routeDistance=6.5, directDistance=4.2)]
        self.bridge.poll()
        item = self.bridge.state()['catalog']['destinations'][0]
        self.assertEqual((item['foodName'], item['routeDistance'], item['directDistance']),
                         ('Cheese_Raw', 6.5, 4.2))
        self.native.destinations[0].update(foodName='Tomato', routeDistance=3, directDistance=1)
        self.bridge.poll()
        self.assertEqual(self.bridge.state()['catalog']['destinations'][0]['foodName'], 'Tomato')

    def test_stale_state_hides_options_and_refuses_control(self):
        self.now += 2
        self.assertFalse(self.bridge.state()['fresh'])
        self.assertEqual(self.bridge.state()['catalog'], {})
        with self.assertRaisesRegex(HarnessError, 'unavailable'):
            self.navigate()
        self.assertEqual(self.native.calls, [])

    def test_click_revalidates_identity_and_reachability(self):
        with self.assertRaisesRegex(HarnessError, 'restarted'):
            self.bridge.navigate('tab-a', 'old-session', 'd7', 77)
        with self.assertRaisesRegex(HarnessError, 'no longer reachable'):
            self.bridge.navigate('tab-a', 'test-session', 'd7', 88)
        self.native.destinations.clear()
        with self.assertRaisesRegex(HarnessError, 'no longer reachable'):
            self.navigate()
        self.assertEqual(self.native.calls, [])

    def test_navigation_uses_one_persistent_connection_and_exact_id(self):
        value = self.navigate()
        self.bridge.poll()
        self.assertEqual(self.native.calls, [('navigate_to', 'd7')])
        self.assertTrue(value['viewerOwnsAction'])
        self.assertEqual(value['lastCommand']['target']['objectInstanceId'], 77)
        self.assertEqual(value['control']['navigation']['state'], 'navigating')

    def test_rejected_command_is_not_retried(self):
        self.native.reject = 'planning_budget_exceeded'
        with self.assertRaisesRegex(HarnessError, 'planning_budget_exceeded'):
            self.navigate()
        self.bridge.poll()
        self.assertEqual(self.native.calls, [('navigate_to', 'd7')])

    def test_browser_click_focuses_game_then_revalidates_exact_target(self):
        self.native.control.update(available=False, reason='game_unfocused')
        focused = []
        def focus():
            focused.append(True)
            self.native.control.update(available=True, reason=None)
        self.bridge.focus = focus
        self.navigate()
        self.assertEqual(focused, [True])
        self.assertEqual(self.native.calls, [('navigate_to', 'd7')])

    def test_target_removed_during_focus_cannot_start(self):
        self.native.control.update(available=False, reason='game_unfocused')
        def focus():
            self.native.control.update(available=True, reason=None)
            self.native.destinations.clear()
        self.bridge.focus = focus
        with self.assertRaisesRegex(HarnessError, 'no longer reachable'):
            self.navigate()
        self.assertEqual(self.native.calls, [])

    def test_disconnect_clears_catalog_and_reconnect_never_replays(self):
        self.navigate()
        self.native.failure = OSError('connection lost')
        self.bridge.poll()
        self.assertFalse(self.bridge.state()['connected'])
        self.assertEqual(self.bridge.state()['catalog'], {})
        self.native.failure = None
        self.bridge.poll()
        self.assertEqual(self.native.calls.count(('navigate_to', 'd7')), 1)

    def test_abandoned_browser_releases_only_its_lease(self):
        self.navigate()
        self.now += 6
        self.bridge.state('another-tab')
        self.bridge.poll()
        self.assertIn(('release',), self.native.calls)
        self.assertNotIn(('stop',), self.native.calls)

    def test_owning_browser_heartbeat_keeps_action_alive(self):
        self.navigate()
        self.now += 4
        self.bridge.state('tab-a')
        self.now += 4
        self.bridge.poll()
        self.assertNotIn(('release',), self.native.calls)

    def test_closing_another_tab_does_not_cancel_action(self):
        self.navigate()
        self.bridge.leave('tab-b')
        self.assertNotIn(('release',), self.native.calls)
        self.bridge.leave('tab-a')
        self.assertIn(('release',), self.native.calls)

    def test_stop_interrupts_and_clears_old_catalog(self):
        self.navigate()
        value = self.bridge.stop()
        self.assertEqual(value['control']['navigation']['state'], 'idle')
        self.assertEqual(value['catalog'], {})
        self.assertIn(('stop',), self.native.calls)

    def test_shutdown_closes_owning_connection(self):
        self.navigate()
        self.bridge.close()
        self.assertIn(('close',), self.native.calls)


class HttpTests(unittest.TestCase):
    def setUp(self):
        self.native = FakeBridge()
        self.bridge = HarnessBridge(factory=lambda: self.native)
        self.bridge.poll()
        self.server = ThreadingHTTPServer(('127.0.0.1', 0), Handler)
        self.server.bridge = self.bridge
        self.server.token = 'test-token'
        self.server.sharing = PhoneShare(self.server.server_port, self.server.token,
                                        ['192.168.1.23'], 'https://test.trycloudflare.com')
        self.worker = threading.Thread(target=self.server.serve_forever, kwargs={'poll_interval': .01})
        self.worker.start()

    def tearDown(self):
        self.server.shutdown()
        self.server.server_close()
        self.worker.join()
        self.bridge.close()

    def request(self, method, path, data=None, headers=None):
        connection = http.client.HTTPConnection('127.0.0.1', self.server.server_port, timeout=2)
        connection.request(method, path, json.dumps(data) if data is not None else None, headers or {})
        response = connection.getresponse()
        body = response.read()
        code = response.status
        connection.close()
        return code, body

    def test_page_and_live_state_are_served(self):
        code, body = self.request('GET', '/')
        self.assertEqual(code, 200)
        self.assertIn(b'Reachable destinations', body)
        code, body = self.request('GET', '/api/state')
        self.assertEqual(code, 200)
        self.assertEqual(json.loads(body)['catalog']['destinations'][0]['id'], 'd7')

    def test_foreign_origin_host_and_missing_token_are_rejected(self):
        for headers in [{'Origin': 'https://example.com'}, {'Host': 'example.com'}]:
            self.assertEqual(self.request('GET', '/api/bootstrap', headers=headers)[0], 403)
        self.assertEqual(self.request('POST', '/api/stop', {'viewerId': 'x'})[0], 403)
        self.assertEqual(self.native.calls, [])

    def test_qr_sharing_info_contains_access_link_and_offline_svg(self):
        code, body = self.request('GET', '/api/share')
        self.assertEqual(code, 200)
        links = json.loads(body)['links']
        self.assertEqual(links[0]['url'], 'https://test.trycloudflare.com/#access=test-token')
        self.assertEqual(links[1]['url'], f'http://192.168.1.23:{self.server.server_port}/#access=test-token')
        self.assertIn('viewBox=', links[0]['qrSvg'])

    def test_phone_needs_qr_token_even_when_proxy_connects_from_loopback(self):
        for host in [f'192.168.1.23:{self.server.server_port}', 'test.trycloudflare.com']:
            headers = {'Host': host}
            self.assertEqual(self.request('GET', '/', headers=headers)[0], 200)
            for path in ['/api/bootstrap', '/api/state', '/api/share']:
                self.assertEqual(self.request('GET', path, headers=headers)[0], 403)
                headers['X-Page-Token'] = 'test-token'
                code, body = self.request('GET', path, headers=headers)
                self.assertEqual(code, 200)
                headers.pop('X-Page-Token')
        self.assertEqual(self.native.calls, [])

    def test_phone_commands_and_ownership_use_same_live_bridge(self):
        headers = {'Host': 'test.trycloudflare.com', 'Origin': 'https://test.trycloudflare.com'}
        data = dict(token='test-token', viewerId='phone', sessionId='test-session',
                    destinationId='d7', objectInstanceId=77)
        self.assertEqual(self.request('POST', '/api/navigate', data, headers)[0], 200)
        code, body = self.request('GET', '/api/state', headers={**headers, 'X-Page-Token': 'test-token', 'X-Viewer-Id': 'phone'})
        self.assertEqual(code, 200)
        self.assertTrue(json.loads(body)['viewerOwnsAction'])
        self.assertEqual(self.request('POST', '/api/stop', data, headers)[0], 200)
        self.assertEqual(self.native.calls, [('navigate_to', 'd7'), ('stop',)])

    def test_forwarded_host_does_not_bypass_origin_or_token_checks(self):
        headers = {'Host': 'test.trycloudflare.com', 'Origin': 'https://evil.example',
                   'X-Page-Token': 'test-token'}
        self.assertEqual(self.request('GET', '/api/state', headers=headers)[0], 403)
        self.assertEqual(self.request('POST', '/api/stop', dict(token='test-token', viewerId='x'), headers)[0], 403)
        headers = {'Host': 'unknown.trycloudflare.com', 'X-Page-Token': 'test-token'}
        self.assertEqual(self.request('GET', '/api/state', headers=headers)[0], 403)
        self.assertEqual(self.native.calls, [])

    def test_navigation_and_stop_round_trip(self):
        body = dict(token='test-token', viewerId='tab-a', sessionId='test-session', destinationId='d7', objectInstanceId=77)
        code, response = self.request('POST', '/api/navigate', body)
        self.assertEqual(code, 200)
        self.assertEqual(json.loads(response)['control']['navigation']['state'], 'navigating')
        code, response = self.request('POST', '/api/stop', body)
        self.assertEqual(code, 200)
        self.assertEqual(json.loads(response)['control']['navigation']['state'], 'idle')
        self.assertEqual(self.native.calls, [('navigate_to', 'd7'), ('stop',)])

    def test_expired_destination_returns_conflict_without_movement(self):
        self.native.destinations.clear()
        body = dict(token='test-token', viewerId='tab-a', sessionId='test-session', destinationId='d7', objectInstanceId=77)
        self.assertEqual(self.request('POST', '/api/navigate', body)[0], 409)
        self.assertEqual(self.native.calls, [])


class SharingTests(unittest.TestCase):
    def test_invalid_forwarded_urls_are_rejected(self):
        for url in ['file:///etc/passwd', 'https://user:pass@example.com',
                    'https://example.com/path', 'https://example.com/#secret']:
            with self.subTest(url=url), self.assertRaises(ValueError):
                PhoneShare(8765, 'token', public_url=url)

    def test_only_loopback_peer_and_local_host_are_trusted_without_token(self):
        sharing = PhoneShare(8765, 'token', ['192.168.1.23'])
        self.assertTrue(sharing.is_local('localhost:8765', '127.0.0.1'))
        self.assertFalse(sharing.is_local('localhost:8765', '192.168.1.24'))
        self.assertFalse(sharing.is_local('192.168.1.23:8765', '127.0.0.1'))
        self.assertFalse(sharing.authorized(None))
        self.assertFalse(sharing.authorized('wrong'))
        self.assertFalse(sharing.authorized('☃'))
        self.assertTrue(sharing.authorized('token'))
