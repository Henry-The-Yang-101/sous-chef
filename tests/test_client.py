import io
import json
from pathlib import Path
import sys
import time
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'clients'))
from bridge import BridgeClient
from validate_recording import validate_snapshot
from world import World


def snapshot(sequence=1):
    return {'type': 'snapshot', 'protocolVersion': 1, 'sessionId': 'test', 'sequence': sequence,
            'inRound': True, 'levelEpoch': 1, 'flowId': 'chef', 'objects': [
                {'id': 'chef', 'parentId': None, 'name': 'Chef', 'colliders': [], 'components': [
                    {'type': 'ServerPlayerAttachmentCarrier', 'instanceId': 10, 'adapters': {
                        'ServerPlayerAttachmentCarrier': {'fields': {'m_carriedObjects': [
                            {'objectId': 'plate', 'componentInstanceId': 20}]}}}}]},
                {'id': 'plate', 'name': 'Plate', 'parentId': 'chef', 'colliders': [],
                 'components': [{'type': 'ServerPhysicalAttachment', 'instanceId': 20}]}]}


def client_for(*messages):
    client = BridgeClient.__new__(BridgeClient)
    client.reader = io.BytesIO(b''.join((json.dumps(m) + '\n').encode() for m in messages))
    client.assets = {}; client.snapshot = None; client.session_id = None
    client.last_received = None; client.last_sequence = None
    return client


class ClientTests(unittest.TestCase):
    def test_graph_and_carried_relationship(self):
        value = snapshot(); validate_snapshot(value, {})
        chef, plate = next(World(value).held_items())
        self.assertEqual((chef['id'], plate['id']), ('chef', 'plate'))

    def test_dangling_reference_rejected(self):
        value = snapshot(); value['objects'].pop()
        with self.assertRaisesRegex(AssertionError, 'Dangling'): validate_snapshot(value, {})

    def test_parent_cycle_rejected(self):
        value = snapshot(); value['objects'][0]['parentId'] = 'plate'
        with self.assertRaisesRegex(AssertionError, 'cycle'): validate_snapshot(value, {})

    def test_status_and_disconnect_clear_world(self):
        client = client_for({'type': 'hello', 'protocolVersion': 1, 'sessionId': 'test'}, snapshot(),
                            {'type': 'status', 'sessionId': 'test', 'sequence': 2, 'inRound': False})
        stream = client.messages(); next(stream); next(stream)
        self.assertIsNotNone(client.current())
        next(stream); self.assertIsNone(client.current())
        with self.assertRaises(EOFError): next(stream)
        self.assertIsNone(client.snapshot)

    def test_stale_and_regressed_state_rejected(self):
        client = client_for({'type': 'hello', 'protocolVersion': 1, 'sessionId': 'test'}, snapshot(), snapshot())
        stream = client.messages(); next(stream); next(stream)
        client.last_received = time.monotonic() - 10
        self.assertIsNone(client.current())
        with self.assertRaisesRegex(ValueError, 'Non-monotonic'): next(stream)
        self.assertIsNone(client.snapshot)

    def test_asset_required_before_snapshot(self):
        value = snapshot()
        value['objects'][0]['colliders'] = [{'shape': {'kind': 'mesh', 'mesh': {'assetId': 'missing'}}}]
        client = client_for({'type': 'hello', 'protocolVersion': 1, 'sessionId': 'test'}, value)
        stream = client.messages(); next(stream)
        with self.assertRaisesRegex(ValueError, 'Missing geometry'): next(stream)
        self.assertIsNone(client.snapshot)


if __name__ == '__main__': unittest.main()
