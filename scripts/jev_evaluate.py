#!/usr/bin/env python3
"""Offline corpus runner. Provider responses must be copied from the playground UI.

--source-root permits a separate checkout for a paired baseline. No API/SDK is
imported or called. Expected results never enter a provider request.
"""
import argparse
import asyncio
import hashlib
import json
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('operation', choices=('prepare', 'advance', 'report'))
    parser.add_argument('variant')
    parser.add_argument('cohort', choices=('development', 'holdout'))
    parser.add_argument('--source-root', type=Path, default=ROOT)
    parser.add_argument('--directory', type=Path, default=ROOT/'.local/jev-round2')
    parser.add_argument('--corpus', type=Path, default=ROOT/'tests/fixtures/jev-generalization-corpus.json')
    args = parser.parse_args()
    if not args.variant.replace('_', '').isalnum(): parser.error('Use a simple variant name')
    sys.path.insert(0, str(args.source_root.resolve()))
    from scripts.jev_playground import next_request
    corpus = json.loads(args.corpus.read_text())
    args.directory.mkdir(parents=True, exist_ok=True)
    prefix = f'{args.variant}_{args.cohort}_'
    if args.operation == 'prepare':
        fingerprint = hashlib.sha256((args.source_root/'harness/decision/compiler.py').read_bytes()).hexdigest()
        for case in corpus[args.cohort]:
            name = prefix + case['id']
            path = args.directory/(name+'.json')
            if path.exists(): parser.error(f'{name} already exists; use a new variant')
            session = {'name': name, 'type': 'command', 'variant': args.variant, 'cohort': args.cohort,
                       'text': case['text'], 'expected': case['expected'], 'responses': [],
                       'world': corpus['worlds'][case.get('world', 'base')],
                       'queue': corpus['queues'][case.get('queue', 'empty')],
                       'source_sha256': fingerprint,
                       **({'utterance_fields': case['utterance_fields']} if 'utterance_fields' in case else {})}
            path.write_text(json.dumps(session, indent=2)+'\n')
    batch, report = [], []
    for path in sorted(args.directory.glob(prefix+'*.json')):
        session = json.loads(path.read_text())
        if args.operation != 'report':
            session['outcome'] = asyncio.run(next_request(session))
            path.write_text(json.dumps(session, indent=2)+'\n')
        outcome = session['outcome']
        if outcome['status'] == 'request':
            batch.append({'name': session['name'], 'request': outcome['request']})
        report.append({'name': session['name'], 'status': outcome['status'],
                       'stage': outcome.get('stage'), 'field': outcome.get('field'),
                       'requests': len(session['responses'])})
    (args.directory/'batch.json').write_text(json.dumps(batch)+'\n')
    print(json.dumps({'pending_requests': len(batch), 'cases': report}, indent=2))


if __name__ == '__main__': main()
