#!/usr/bin/env python3
"""Export frozen spatial fixtures through the current production compiler.

No network calls. Binding gold is supplied only to isolate candidate approval.
Recorded candidate answers can expose the real equivalence-stage request.
"""
import argparse
import asyncio
import json
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from scripts.jev_playground import NextRequest
from harness.decision.compiler import Compiler, build_context
from harness.decision.protocol import validate_response

FIXTURES = ROOT / 'tests/fixtures/jev-movement-cases.json'


def fixtures():
    return json.loads(FIXTURES.read_text())


async def request(case, stage='candidates', recorded=()):
    history = iter(recorded)

    async def ask(value):
        if 'binding' not in value['questions']:
            saved = next(history, None)
            if saved is None:
                raise NextRequest(value)
            if saved['request'] != value:
                raise ValueError('Recorded prompt differs from production')
            return validate_response(value, saved['response'])
        if stage == 'binding':
            raise NextRequest(value)
        criteria = value['questions']['binding']['criteria']
        key = case['binding']
        # Fixture setup, not claimed model evidence. The candidate request is
        # otherwise exactly what Compiler.movement sends in production.
        return validate_response(value, {
            'model': value['model'],
            'answers': {'binding': {
                'type': 'choice', 'choice': key, 'confidence': 1,
                'probabilities': {k: float(k == key) for k in criteria}}},
            'usage': {'input_tokens': 0, 'output_tokens': 0}})

    compiler = Compiler(
        {'id': 'coordinates', 'text': case['text']},
        build_context(case['world'], {'revision': 0, 'active': None, 'pending': []}), ask)
    try:
        await compiler.movement(1)
    except NextRequest as pending:
        return pending.request
    raise ValueError('No further model request: the movement choice completed')


async def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--stage', choices=('binding', 'candidates'), default='candidates')
    parser.add_argument('--output', type=Path, default=ROOT / '.local/jev-coordinates/current.json')
    args = parser.parse_args()
    rows = [{'case': case, 'request': await request(case, args.stage)} for case in fixtures()]
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(rows, indent=2) + '\n')
    print(f'Exported {len(rows)} {args.stage} requests to {args.output}; no model calls.')


if __name__ == '__main__':
    asyncio.run(main())
