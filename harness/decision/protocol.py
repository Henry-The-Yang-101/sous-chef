"""Validate the exact documented Choice/Noul wire format, including evidence."""
import json
import math
from .config import MODEL


class DecisionError(ValueError):
    pass


def probability(value):
    if type(value) not in (int, float) or not math.isfinite(value) or not 0 <= value <= 1:
        raise DecisionError('Jev returned an invalid probability')
    return value


def validate_request(request):
    if set(request) != {'model', 'state', 'questions'} or request['model'] != MODEL:
        raise DecisionError('Unsupported Jev request envelope')
    questions = request['questions']
    if not isinstance(questions, dict) or not 1 <= len(questions) <= 24:
        raise DecisionError('Invalid Jev question batch')
    for question in questions.values():
        if (not isinstance(question, dict) or question.keys() - {'type', 'instructions', 'criteria'}
                or not isinstance(question.get('instructions'), str) or not question['instructions']):
            raise DecisionError('Invalid Jev question')
        if question.get('type') == 'choice':
            criteria = question.get('criteria')
            if (not isinstance(criteria, dict) or not 2 <= len(criteria) <= 255
                    or any(not isinstance(k, str) or not isinstance(v, str) for k, v in criteria.items())):
                raise DecisionError('Invalid Choice criteria')
        elif question.get('type') != 'noul':
            raise DecisionError('Only Choice and Noul are enabled')
    # UTF-8 bytes are a conservative upper bound, not a claimed Jev tokenizer.
    # Leave large margins below the 32k per-question and 64k total token limits.
    size = lambda value: len(json.dumps(value, ensure_ascii=False, allow_nan=False).encode())
    state_size = size(request['state'])
    if size(request) > 48000 or any(state_size + size(q) > 24000 for q in questions.values()):
        raise DecisionError('Decision context is too large; narrow or split this command')
    return request


def validate_response(request, response):
    if not isinstance(response, dict) or response.get('model') != MODEL:
        raise DecisionError('Jev model version mismatch')
    answers, usage = response.get('answers'), response.get('usage')
    if not isinstance(answers, dict) or answers.keys() != request['questions'].keys():
        raise DecisionError('Jev omitted or added an answer')
    if (not isinstance(usage, dict) or any(type(usage.get(k)) is not int or usage[k] < 0
                                         for k in ('input_tokens', 'output_tokens'))):
        raise DecisionError('Jev omitted token usage')
    for key, question in request['questions'].items():
        answer = answers[key]
        if not isinstance(answer, dict) or answer.get('type') != question['type']:
            raise DecisionError('Jev answer type mismatch')
        if question['type'] == 'noul':
            probability(answer.get('noul'))
            continue
        choice, distribution = answer.get('choice'), answer.get('probabilities')
        if not isinstance(choice, str) or choice not in question['criteria']:
            raise DecisionError('Jev selected a value outside the sent candidates')
        if not isinstance(distribution, dict) or distribution.keys() != question['criteria'].keys():
            raise DecisionError('Jev returned incomplete Choice evidence')
        for value in distribution.values():
            probability(value)
        probability(answer.get('confidence'))
        if abs(sum(distribution.values()) - 1) > .025 or distribution[choice] < max(distribution.values()):
            raise DecisionError('Jev returned an inconsistent distribution')
    return response
