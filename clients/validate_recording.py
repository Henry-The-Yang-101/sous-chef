#!/usr/bin/env python3
"""Validate graph integrity and summarize an NDJSON recording without Unity."""
import argparse
from collections import Counter
import json
from pathlib import Path
import statistics


def walk(value):
    yield value
    if isinstance(value, dict):
        for child in value.values(): yield from walk(child)
    elif isinstance(value, list):
        for child in value: yield from walk(child)


def validate_snapshot(snapshot, assets):
    assert snapshot['inRound'] is True, 'Inactive kitchen snapshot'
    objects = {obj['id']: obj for obj in snapshot['objects']}
    assert len(objects) == len(snapshot['objects']), 'Duplicate object identity'
    assert snapshot['flowId'] in objects, 'Unresolved flow'
    component_ids = {obj['id']: {c['instanceId'] for c in obj['components']} for obj in objects.values()}
    for obj in objects.values():
        assert obj['parentId'] is None or obj['parentId'] in objects, 'Unresolved parent'
        chain = set()
        current = obj
        while current:
            assert current['id'] not in chain, 'Parent cycle'
            chain.add(current['id'])
            current = objects.get(current['parentId'])
    for value in walk(snapshot['objects']):
        if not isinstance(value, dict): continue
        if 'objectId' in value:
            assert value['objectId'] in objects, 'Dangling object reference: ' + value['objectId']
            if value.get('componentInstanceId') is not None:
                assert value['componentInstanceId'] in component_ids[value['objectId']], 'Dangling component reference'
        if 'assetId' in value:
            assert value['assetId'] in assets, 'Unresolved geometry asset'


def validate(path):
    assets = {}; session = None; sequence = -1; snapshots = 0
    kinds = Counter(); scenes = set(); times = []; unsupported = Counter(); errors = 0
    object_counts = []; collider_counts = []; paused = 0; asset_states = Counter()
    with Path(path).open() as file:
        for line in file:
            message = json.loads(line); kind = message['type']; kinds[kind] += 1
            if kind == 'hello':
                assert message['protocolVersion'] == 1
                session = message['sessionId']; sequence = -1; assets.clear()
            elif kind == 'asset':
                assets[message['id']] = message
                asset_states[message['data']['status']] += 1
            elif kind in ('status', 'snapshot'):
                assert message['sessionId'] == session, 'Wrong session'
                assert message['sequence'] > sequence, 'Sequence regressed'
                sequence = message['sequence']
                if kind == 'status':
                    assert message['inRound'] is False and 'objects' not in message
                    assets.clear(); continue
                validate_snapshot(message, assets)
                snapshots += 1
                scenes.update(s['name'] for s in message['scenes'])
                times.append(message['diagnostics']['captureMilliseconds'])
                object_counts.append(len(message['objects']))
                collider_counts.append(sum(len(o['colliders']) for o in message['objects']))
                paused += bool(message.get('paused'))
                coverage = message['coverage']
                assert not coverage['missingFields'], 'Adapter drift'
                errors += len(coverage['errors']) + coverage['fieldErrors']
                unsupported.update(coverage['unsupportedValues'])
    return {'messages': dict(kinds), 'snapshots': snapshots, 'pausedSnapshots': paused, 'scenes': sorted(scenes),
            'objectsRange': [min(object_counts), max(object_counts)] if snapshots else None,
            'collidersRange': [min(collider_counts), max(collider_counts)] if snapshots else None,
            'captureMsMedian': statistics.median(times) if times else None,
            'captureMsMax': max(times) if times else None, 'readErrors': errors,
            'unsupportedValues': dict(unsupported), 'geometryAssetStates': dict(asset_states)}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('recording', type=Path)
    result = validate(parser.parse_args().recording)
    print(json.dumps(result, indent=2))
    raise SystemExit(1 if result['readErrors'] else 0)
