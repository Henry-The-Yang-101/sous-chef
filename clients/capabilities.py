"""Shared, offline contracts. Validation proves shape, never physical feasibility.

The native executor remains authoritative. No I/O, model calls or dispatch occurs
in this module. Public accessors return copies so a caller cannot edit contracts.
"""
import base64
import copy
import hashlib
import json
import math
import re
import unicodedata
from pathlib import Path

_SOURCE = Path(__file__).with_suffix('.json').read_bytes()
_REGISTRY = json.loads(_SOURCE)
REGISTRY_VERSION = _REGISTRY['version']
REGISTRY_HASH = hashlib.sha256(_SOURCE).hexdigest()
_CONTRACTS = {entry['name']: entry for entry in _REGISTRY['contracts']}
TASK_NAMES = tuple(c['name'] for c in _CONTRACTS.values() if c['kind'] == 'task')
ACTION_NAMES = tuple(c['name'] for c in _CONTRACTS.values() if c['kind'] == 'action')
WORK_CONDITIONS = tuple(_REGISTRY['$defs']['work_condition']['enum'])
WAIT_CONDITIONS = tuple(_REGISTRY['$defs']['wait_condition']['enum'])


class ValidationError(ValueError):
    """A contract/version/argument error, with a path suitable for local UI."""


def registry():
    return dict(copy.deepcopy(_REGISTRY), registry_hash=REGISTRY_HASH)


def contract(name, version=None):
    if not isinstance(name, str) or name not in _CONTRACTS:
        raise ValidationError('Unsupported capability')
    result = _CONTRACTS[name]
    if version is not None and (type(version) is not int or version != result['version']):
        raise ValidationError('Capability version mismatch: ' + name)
    return copy.deepcopy(result)


def schema(node):
    if '$ref' in node:
        return {**_REGISTRY['$defs'][node['$ref'].split('/')[-1]], **{k: v for k, v in node.items() if k != '$ref'}}
    return node


def validate(node, value, path='arguments'):
    """Validate the registry's closed JSON Schema subset and apply defaults."""
    node = schema(node)
    kind = node['type']
    if kind == 'object':
        if not isinstance(value, dict) or any(not isinstance(k, str) for k in value):
            raise ValidationError(path + ': expected an object')
        extra = value.keys() - node['properties'].keys()
        if extra:
            raise ValidationError(path + ': unsupported fields: ' + ', '.join(sorted(extra)))
        missing = set(node['required']) - value.keys()
        if missing:
            raise ValidationError(path + ': missing fields: ' + ', '.join(sorted(missing)))
        result = {}
        for key, child in node['properties'].items():
            if key in value:
                result[key] = validate(child, value[key], path + '.' + key)
            elif 'default' in schema(child):
                result[key] = validate(child, copy.deepcopy(schema(child)['default']), path + '.' + key)
        return result
    if kind == 'array':
        if not isinstance(value, list) or not node['minItems'] <= len(value) <= node['maxItems']:
            raise ValidationError(path + ': list length outside contract limits')
        result = [validate(node['items'], item, f'{path}[{i}]') for i, item in enumerate(value)]
        if node.get('uniqueItems') and len({json.dumps(v, sort_keys=True) for v in result}) != len(result):
            raise ValidationError(path + ': duplicate resource')
        return result
    if kind == 'integer' and type(value) is not int:
        raise ValidationError(path + ': expected an integer')
    if kind == 'number' and (type(value) not in (int, float) or (type(value) is float and not math.isfinite(value))):
        raise ValidationError(path + ': expected a finite number')
    if kind in ('integer', 'number'):
        if ('minimum' in node and value < node['minimum']) or ('maximum' in node and value > node['maximum']):
            raise ValidationError(path + ': outside contract limits')
    if kind == 'string':
        if not isinstance(value, str):
            raise ValidationError(path + ': expected a string')
        # C# counts UTF-16 code units, including two for supplementary characters.
        try:
            length = len(value.encode('utf-16-le')) // 2
            byte_length = len(value.encode('utf-8'))
        except UnicodeError as error:
            raise ValidationError(path + ': invalid Unicode') from error
        if not node.get('minLength', 0) <= length <= node.get('maxLength', math.inf):
            raise ValidationError(path + ': string length outside contract limits')
        if node.get('format') == 'destination' and not re.fullmatch(r'd[0-9]{1,31}', value):
            raise ValidationError(path + ': use an exact native destination ID')
        if node.get('format') == 'selector' and (not value.strip() or byte_length > 96 or any(unicodedata.category(c) == 'Cc' for c in value)):
            raise ValidationError(path + ': use a printable selector of at most 96 UTF-8 bytes')
    if 'enum' in node and value not in node['enum']:
        raise ValidationError(path + ': unsupported value')
    return value


def validate_repeat(name, repetitions):
    policy = contract(name)['repeat']
    if repetitions is None and policy['until_cancelled']:
        return None
    if type(repetitions) is not int or not policy['minimum'] <= repetitions <= policy['maximum']:
        raise ValidationError(name + ': unsupported repetition policy')
    return repetitions


def validate_arguments(name, arguments, version=None):
    result = validate(contract(name, version)['parameters'], arguments)
    if name == 'assemble_dish':
        assigned = result['bases'] + result['components'] + result['equipment']
        if len(set(assigned)) != len(assigned):
            raise ValidationError('assembly: assign each resource to exactly one group')
    if name in ('move_item', 'stage_item', 'clear_station'):
        values = list(result.values())
        if values[0] == values[1]:
            raise ValidationError(name + ': source and destination must differ')
    return result


def selector(value):
    value = validate(_REGISTRY['$defs']['selector'], value, 'selector')
    return 's' + base64.urlsafe_b64encode(value.encode('utf-8')).decode('ascii').rstrip('=')


def wire_command(name, arguments, repetitions=1, version=None):
    """Return only a whitelisted native operation and its validated wire tokens."""
    spec = contract(name, version)
    args = validate_arguments(name, arguments, version)
    validate_repeat(name, repetitions)
    operation = 'task' if spec['kind'] == 'task' else name
    tokens = [name, 'forever' if repetitions is None else repetitions] if operation == 'task' else []
    if name == 'prepare_near_player':
        tokens.append(args['player_id'])
        for item in args['items']:
            tokens.extend((selector(item['ingredient']), item['preparation'], item['quantity']))
    elif name == 'assemble_dish':
        tokens.append(selector(args['order_id']))
        for group in ('bases', 'components', 'equipment'):
            tokens.extend((len(args[group]), *args[group]))
    else:
        for key in spec.get('wire_arguments', []):
            field = spec['parameters']['properties'][key]
            value = args[key]
            if schema(field).get('format') == 'selector':
                value = selector(value)
            elif key == 'timeout':
                value = round(value * 1000)
            tokens.append(value)
    # Reserve the entire native request-ID allowance, including for future IDs.
    size = len(' '.join(map(str, [operation, '0' * 32, *tokens])))
    if size > (2048 if operation == 'task' else 256):
        raise ValidationError('Encoded command exceeds the native wire limit')
    return operation, tuple(tokens)
