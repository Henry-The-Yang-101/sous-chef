#!/usr/bin/env python3
"""Export/replay the production compiler through manual playground UI. NO network.

python3 scripts/jev_playground.py start NAME 'Wash dishes'
python3 scripts/jev_playground.py next NAME
python3 scripts/jev_playground.py record NAME path/to/copied-response.json
python3 scripts/jev_playground.py answer NAME chopped

Each next call writes exact state/questions/request JSON into .local/jev-playground.
Only recorded visible playground responses advance compilation. The kitchen is
deliberately synthetic; this evaluates interpretation, not native execution.
"""
import argparse
import asyncio
import copy
import json
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from clients.capabilities import REGISTRY_HASH, registry
from harness.decision.compiler import Compiler, Clarification, build_context
from harness.decision.protocol import validate_response

DIRECTORY = ROOT / '.local' / 'jev-playground'


def synthetic_world():
    return {'capabilities': {'fresh': True, 'compatible': True, 'version': 1, 'registry_hash': REGISTRY_HASH,
        'entries': [{'name': c['name'], 'support': 'implemented'} for c in registry()['contracts']],
        'semantic_catalog': {'players': [{'playerId': 1}, {'playerId': 3}],
            'targets': [{'id': 'd1', 'objectInstanceId': 101, 'label': 'Sink', 'possibleOperations': ['navigate_to', 'work_at']}],
            'displayOptions': {'preparationOptions': [
                {'id': 'carrot', 'label': 'Carrot', 'preparations': {'raw':'applicable','chopped':'applicable','cooked':'unknown','empty':'not_applicable'}},
                {'id': 'onion', 'label': 'Onion', 'preparations': {'raw':'applicable','chopped':'applicable','cooked':'unknown','empty':'not_applicable'}},
                {'id': 'rice', 'label': 'Rice', 'preparations': {'raw':'applicable','chopped':'not_applicable','cooked':'applicable','empty':'not_applicable'}}]}}}}


class NextRequest(Exception):
    def __init__(self, request): self.request = request


async def next_request(session, *, settings=None):
    queue = session.get('queue', {'revision': 0, 'active': None, 'pending': []})
    context = build_context(session.get('world', synthetic_world()), queue)
    history = iter(session['responses'])
    async def ask(request):
        item = next(history, None)
        if item is None: raise NextRequest(request)
        # Replays must use the exact current request, including tuned instructions.
        if item['request'] != request: raise ValueError('Prompt changed; start a new evaluation session')
        return validate_response(request, item['response'])
    c = Compiler({'id':session['name'], 'text':session['text'], **session.get('utterance_fields', {})}, context, ask, settings)
    for item in session.get('clarifications', []):
        c.overrides[(item['stage'], item['field'])] = item['key']
        c.clarified.append({'question':item['question'], 'answer':item['answer']})
    try:
        result = await c.compile()
        return {'status':'complete', 'result':result}
    except NextRequest as next_:
        return {'status':'request', 'request':next_.request}
    except Clarification as clarification:
        return {'status':'clarification', 'question':clarification.message, 'stage':clarification.stage,
                'field':clarification.field, 'options':clarification.options}
    except ValueError as error:
        return {'status':'rejected', 'message':str(error)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('operation', choices=('start','next','record','answer'))
    parser.add_argument('name')
    parser.add_argument('value', nargs='?')
    args = parser.parse_args()
    if not args.name.replace('_','').replace('-','').isalnum(): parser.error('Use a simple session name')
    DIRECTORY.mkdir(parents=True, exist_ok=True)
    path = DIRECTORY / (args.name + '.json')
    if args.operation == 'start':
        if path.exists(): parser.error('Session already exists; choose a new name')
        session = {'name':args.name, 'text':args.value, 'responses':[]}
    else:
        session = json.loads(path.read_text())
        previous = asyncio.run(next_request(session))
        if args.operation == 'record':
            if previous['status'] != 'request': parser.error('No pending request')
            response = json.loads(Path(args.value).read_text())
            validate_response(previous['request'], response)
            session['responses'].append({'request':previous['request'], 'response':response})
        elif args.operation == 'answer':
            if previous['status'] != 'clarification' or args.value not in previous['options']: parser.error('Not a clarification option')
            session.setdefault('clarifications', []).append(dict(stage=previous['stage'],field=previous['field'],key=args.value,
                question=previous['question'],answer=previous['options'][args.value]))
    result = asyncio.run(next_request(session))
    path.write_text(json.dumps(session, indent=2)+'\n')
    (DIRECTORY/'current.json').write_text(json.dumps(result, indent=2)+'\n')
    if result['status'] == 'request':
        for key in ('state','questions'):
            (DIRECTORY/(key+'.json')).write_text(json.dumps(result['request'][key], indent=2)+'\n')
        (DIRECTORY/'request.json').write_text(json.dumps(result['request'], indent=2)+'\n')
        print(json.dumps({'status':'request','questions':list(result['request']['questions']), 'session':str(path)}))
    else: print(json.dumps(result, indent=2))


if __name__ == '__main__': main()
