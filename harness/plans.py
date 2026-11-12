"""Immutable typed plan validation, with no scheduler or execution side effects."""
from dataclasses import dataclass
import json

from clients.capabilities import (REGISTRY_HASH, REGISTRY_VERSION, ValidationError,
                                  contract, registry, validate_arguments, validate_repeat, wire_command)
from harness.capabilities import assess_arguments


def closed(value, allowed, required, path):
    if not isinstance(value, dict) or value.keys() - set(allowed) or set(required) - value.keys():
        raise ValidationError(path + ': missing or unsupported fields')


def identity(value, path):
    if not isinstance(value, str) or not 1 <= len(value) <= 80 or any(not (c.isascii() and (c.isalnum() or c in '-_')) for c in value):
        raise ValidationError(path + ': invalid local identity')
    return value


@dataclass(frozen=True)
class Repeat:
    mode: str
    count: int | None

    @classmethod
    def parse(cls, action, value):
        if isinstance(value, dict) and value.get('mode') == 'finite':
            closed(value, ('mode', 'count'), ('mode', 'count'), 'repeat')
            # None is the wire spelling of unlimited, never a finite count.
            if type(value['count']) is not int:
                raise ValidationError('repeat.count: expected an integer')
            count = value['count']
        elif isinstance(value, dict) and value.get('mode') == 'until_cancelled':
            closed(value, ('mode',), ('mode',), 'repeat')
            count = None
        else:
            raise ValidationError('repeat: expected finite or until_cancelled')
        validate_repeat(action, count)
        return cls(value['mode'], count)


@dataclass(frozen=True)
class NativeTask:
    action: str
    capability_version: int
    repeat: Repeat
    _arguments_json: str

    @property
    def arguments(self):
        return json.loads(self._arguments_json)

    def wire_command(self):
        return wire_command(self.action, self.arguments, self.repeat.count, self.capability_version)


@dataclass(frozen=True)
class NativeAction(NativeTask):
    """An existing navigation/interaction/logistics adapter, always single-shot."""


@dataclass(frozen=True)
class Plan:
    schema_version: str
    capability_version: int
    command_id: str
    expected_queue_revision: int
    queue_edit: str
    tasks: tuple[NativeTask | NativeAction, ...]
    after_plan: str
    on_failure: str


def validate_plan(value, *, capabilities=None, existing_unlimited=False):
    """Validate a whole draft atomically; return data, never accept or dispatch it.

    Omit capabilities for offline shape validation. Supply the fresh native
    snapshot for compatibility/known-contradiction checks. Unknown feasibility
    still requires live native validation; this is not an executable receipt.
    """
    fields = ('schema_version', 'capability_version', 'command_id', 'expected_queue_revision',
              'queue_edit', 'tasks', 'after_plan', 'on_failure')
    closed(value, fields, fields, 'plan')
    data = registry()
    if value['schema_version'] != data['plan_schema_version'] or type(value['capability_version']) is not int or value['capability_version'] != REGISTRY_VERSION:
        raise ValidationError('Plan/capability version mismatch')
    command_id = identity(value['command_id'], 'command_id')
    revision = value['expected_queue_revision']
    if type(revision) is not int or revision < 0:
        raise ValidationError('expected_queue_revision: expected a nonnegative integer')
    if value['queue_edit'] not in ('append', 'insert_next', 'interrupt', 'replace_active', 'replace_pending', 'replace_all'):
        raise ValidationError('queue_edit: expected a task-plan operation')
    if existing_unlimited and value['queue_edit'] == 'append':
        raise ValidationError('Cannot append behind an unlimited task')
    if value['after_plan'] not in ('continue_pending', 'pause_pending') or value['on_failure'] != 'pause_pending':
        raise ValidationError('Unsupported continuation/failure policy')
    raw_tasks = value['tasks']
    if not isinstance(raw_tasks, list) or not 1 <= len(raw_tasks) <= data['max_plan_tasks']:
        raise ValidationError('Plan must contain 1..5 task occurrences')
    supported = None
    if capabilities is not None:
        if (not capabilities.get('compatible') or not capabilities.get('fresh') or
                capabilities.get('version') != REGISTRY_VERSION or capabilities.get('registry_hash') != REGISTRY_HASH):
            raise ValidationError('Fresh compatible native capabilities required')
        supported = {c['name'] for c in capabilities['entries'] if c['support'] == 'implemented'}
    tasks = []
    for i, raw in enumerate(raw_tasks):
        fields = ('kind', 'action', 'capability_version', 'arguments', 'repeat')
        closed(raw, fields, fields, f'tasks[{i}]')
        spec = contract(raw['action'], raw['capability_version'])
        if type(raw['capability_version']) is not int:
            raise ValidationError('capability_version: expected an integer')
        expected = 'native_task' if spec['kind'] == 'task' else 'native_action'
        if raw['kind'] != expected or spec.get('category') == 'control':
            raise ValidationError('Invalid task union branch; Stop uses the independent control path')
        if supported is not None and raw['action'] not in supported:
            raise ValidationError('Native capability not implemented')
        repeat = Repeat.parse(raw['action'], raw['repeat'])
        if repeat.count is None and i != len(raw_tasks) - 1:
            raise ValidationError('Unlimited work must be the final task')
        arguments = validate_arguments(raw['action'], raw['arguments'], raw['capability_version'])
        if capabilities is not None and assess_arguments(raw['action'], arguments, capabilities['semantic_catalog'])['state'] == 'not_applicable':
            raise ValidationError('Arguments contradict observed native capability')
        cls = NativeTask if spec['kind'] == 'task' else NativeAction
        task = cls(raw['action'], raw['capability_version'], repeat, json.dumps(arguments, sort_keys=True))
        task.wire_command()  # Also enforce the native command byte budget.
        tasks.append(task)
    return Plan(value['schema_version'], REGISTRY_VERSION, command_id, revision, value['queue_edit'],
                tuple(tasks), value['after_plan'], value['on_failure'])
