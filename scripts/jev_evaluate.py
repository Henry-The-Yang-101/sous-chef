#!/usr/bin/env python3
"""Offline corpus runner. Provider responses must be copied from the playground UI.

--source-root permits a separate checkout for a paired baseline. No API/SDK is
imported or called. Expected results never enter a provider request.
"""
import argparse
import asyncio
from collections import Counter
import hashlib
import json
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]


def semantic_result(result):
    """Normalize actions, arguments, repetition, queue and after-plan behavior."""
    kind = result['kind']
    if kind == 'plan':
        plan = result['plan']
        return {'kind': kind, 'queue_edit': plan['queue_edit'], 'after_plan': plan['after_plan'],
                'tasks': [{key: task[key] for key in ('action', 'arguments', 'repeat')} for task in plan['tasks']]}
    if kind == 'queue_edit':
        return {'kind': kind, **{key: result['edit'][key] for key in ('operation', 'task_id') if key in result['edit']}}
    return {'kind': kind}


def score_outcome(expected, outcome):
    """Abstention is not a correct completed interpretation."""
    status = outcome['status']
    if status == 'request':
        return 'incomplete'
    if status == 'complete':
        return 'correct_completion' if semantic_result(outcome['result']) == expected else 'incorrect_completion'
    if expected['kind'] == 'blocked' and status in ('rejected', 'clarification'):
        return 'safe_block'
    # New corpora distinguish a clear unsupported request from missing meaning.
    # Preserve the historical 'blocked' score for older published evaluations.
    if expected['kind'] == 'rejected':
        return 'correct_rejection' if status == 'rejected' else 'unnecessary_clarification'
    if expected['kind'] == 'clarification':
        return 'correct_clarification' if status == 'clarification' else 'incorrect_rejection'
    return 'clarification' if status == 'clarification' else 'unexpected_rejection'


def audit_case(case, world, queue):
    """Reject invalid positive gold before sending anything to the playground.

    Contract/catalog consistency is necessary, not proof of physical execution.
    Native prerequisites and source references remain explicit corpus metadata.
    """
    from clients.capabilities import wire_command
    from harness.capabilities import assess_arguments
    from harness.decision.compiler import task_spec
    from harness.plans import validate_plan
    expected = case['expected']
    if expected['kind'] != 'plan':
        return
    tasks = []
    for entry in expected['tasks']:
        count = entry['repeat'].get('count')
        wire_command(entry['action'], entry['arguments'], count)
        semantic = world['capabilities']['semantic_catalog']
        assessment = assess_arguments(entry['action'], entry['arguments'], semantic)
        if assessment['state'] == 'not_applicable':
            raise ValueError(f"{case['id']}: gold contradicts native preparation catalog")
        if entry['action'] == 'prepare_near_player':
            options = {x['id'] for x in semantic['displayOptions']['preparationOptions']}
            if any(x['ingredient'] not in options for x in entry['arguments']['items']):
                raise ValueError(f"{case['id']}: gold ingredient absent from catalog")
            if entry['arguments']['player_id'] not in {p['playerId'] for p in semantic['players']}:
                raise ValueError(f"{case['id']}: gold recipient absent from catalog")
        if entry['action'] == 'navigate_to':
            if entry['arguments']['destination_id'] not in {t['id'] for t in semantic['targets']}:
                raise ValueError(f"{case['id']}: gold destination absent from catalog")
        tasks.append(task_spec(entry['action'], entry['arguments'], count))
    from clients.capabilities import REGISTRY_VERSION, registry
    validate_plan({'schema_version': registry()['plan_schema_version'], 'capability_version': REGISTRY_VERSION,
                   'command_id': 'audit', 'expected_queue_revision': queue['revision'],
                   'queue_edit': expected['queue_edit'], 'tasks': tasks,
                   'after_plan': expected['after_plan'], 'on_failure': 'pause_pending'},
                  capabilities=world['capabilities'])


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
            audit_case(case, corpus['worlds'][case.get('world', 'base')],
                       corpus['queues'][case.get('queue', 'empty')])
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
                       'score': score_outcome(session['expected'], outcome),
                       'stage': outcome.get('stage'), 'field': outcome.get('field'),
                       'requests': len(session['responses'])})
    (args.directory/'batch.json').write_text(json.dumps(batch)+'\n')
    print(json.dumps({'pending_requests': len(batch), 'scores': dict(Counter(case['score'] for case in report)),
                      'cases': report}, indent=2))


if __name__ == '__main__': main()
