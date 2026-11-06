#!/usr/bin/env python3
"""Typed autonomous Player 2 actions; the caller never supplies steering input."""
import argparse
import json
import time
try:
    from .control import ControlClient, ControlError
    from .capabilities import TASK_NAMES, WORK_CONDITIONS, WAIT_CONDITIONS, contract, validate, selector, wire_command
except ImportError:
    from control import ControlClient, ControlError
    from capabilities import TASK_NAMES, WORK_CONDITIONS, WAIT_CONDITIONS, contract, validate, selector, wire_command


class ActionClient(ControlClient):
    TASK_ACTIONS = TASK_NAMES
    WORK_CONDITIONS = WORK_CONDITIONS
    WAIT_CONDITIONS = WAIT_CONDITIONS

    def run_capability(self, name, arguments, repetitions=1, *, version=None):
        """Shared validated adapter; names are registry keys, never method names."""
        operation, tokens = wire_command(name, arguments, repetitions, version)
        context = getattr(self, 'dispatch_context', None)
        if context and name != 'stop':
            import uuid
            receipt = self.execute(uuid.uuid4().hex, context['epoch'], context['fence'], name, arguments, repetitions, version=version)
            if receipt is None or receipt['state'] == 'rejected':
                raise ControlError((receipt or {}).get('reason', 'Missing native receipt'))
            return self.last_response['control']
        return self._request(operation, *tokens)

    def execute(self, request_id, epoch, generation, name, arguments, repetitions=1, *, version=None):
        """Fenced, deduplicated dispatch. An absent receipt is never a retry signal."""
        self._context(epoch, generation)
        operation, tokens = wire_command(name, arguments, repetitions, version)
        if name == 'stop':
            raise ValueError('Stop uses the independent cancellation path')
        self._request('execute', epoch, generation, operation, *tokens, request_id=request_id)
        return self.last_response['receipt']

    @staticmethod
    def _context(epoch, generation):
        import re
        if not isinstance(epoch, str) or not re.fullmatch(r'[A-Za-z0-9_-]{1,32}', epoch) or type(generation) is not int or generation < 0:
            raise ValueError('Invalid native dispatch context')

    def receipt(self, request_id):
        self._context(request_id, 0)
        self._request('receipt', request_id)
        return self.last_response['receipt']

    def cancel(self, request_id, epoch, generation):
        self._context(epoch, generation)
        self._request('cancel', epoch, generation, request_id=request_id)
        return self.last_response['receipt']

    def run_task(self, action, repetitions=1, *, items=None, player_id=1, order_id=None, bases=None, components=None, equipment=None):
        """Run one task. None means until Stop; assembly accepts exactly one ticket."""
        if contract(action)['kind'] != 'task':
            raise ValueError('Unknown task action')
        arguments = {key: value for key, value in dict(items=items, order_id=order_id, bases=bases,
                     components=components, equipment=equipment).items() if value is not None}
        if action == 'prepare_near_player' or player_id != 1 or type(player_id) is not int:
            arguments['player_id'] = player_id
        return self.run_capability(action, arguments, repetitions)

    def assemble_dish(self, order_id, *, bases, components, equipment=None):
        """Assemble one exact live ticket using only these allocated native IDs."""
        return self.run_task('assemble_dish', order_id=order_id, bases=bases,
                             components=components, equipment=equipment)

    def available_actions(self):
        self._request('actions')
        return self.last_response['availableActions']

    @staticmethod
    def _destination(value):
        return validate(contract('navigate_to')['parameters']['properties']['destination_id'], value)

    _selector = staticmethod(selector)

    @staticmethod
    def _timeout(value):
        return round(validate(contract('work_at')['parameters']['properties']['timeout'], value) * 1000)

    def navigate_to(self, destination_id):
        return self.run_capability('navigate_to', dict(destination_id=destination_id))

    def move_item(self, item, destination):
        return self.run_capability('move_item', dict(item=item, destination=destination))

    def fetch_ingredient(self, ingredient, destination):
        return self.run_capability('fetch_ingredient', dict(ingredient=ingredient, destination=destination))

    def get_plate(self, destination):
        return self.run_capability('get_plate', dict(destination=destination))

    def get_utensil(self, utensil_type, destination):
        return self.run_capability('get_utensil', dict(utensil_type=utensil_type, destination=destination))

    def stage_item(self, item, handoff_location):
        return self.run_capability('stage_item', dict(item=item, handoff_location=handoff_location))

    def return_utensil(self, utensil):
        return self.run_capability('return_utensil', dict(utensil=utensil))

    def clear_station(self, station, destination):
        return self.run_capability('clear_station', dict(station=station, destination=destination))

    def pick_up(self, target_id):
        return self.run_capability('pick_up', dict(target_id=target_id))

    def place_at(self, target_id):
        return self.run_capability('place_at', dict(target_id=target_id))

    def add_ingredient(self, target_id):
        return self.run_capability('add_ingredient', dict(target_id=target_id))

    def collect_contents(self, target_id):
        return self.run_capability('collect_contents', dict(target_id=target_id))

    def discard(self, bin_id):
        return self.run_capability('discard', dict(target_id=bin_id))

    def throw_to(self, target_id):
        return self.run_capability('throw_to', dict(target_id=target_id))

    def throw_to_player(self, player_id):
        return self.run_capability('throw_to_player', dict(player_id=player_id))

    def work_at(self, target_id, until='work_complete', timeout=60):
        return self.run_capability('work_at', dict(target_id=target_id, until=until, timeout=timeout))

    def activate(self, target_id, desired_state='trigger'):
        return self.run_capability('activate', dict(target_id=target_id, desired_state=desired_state))

    def wait_until(self, target_id, condition, timeout=60):
        return self.run_capability('wait_until', dict(target_id=target_id, condition=condition, timeout=timeout))

    def stop(self):
        """Interrupt current native work, including from another client."""
        return self.run_capability('stop', {})


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--port', type=int, default=24703)
    commands = parser.add_subparsers(dest='operation', required=True)
    commands.add_parser('list')
    commands.add_parser('status')
    commands.add_parser('stop')
    task = commands.add_parser('task')
    task.add_argument('action', choices=ActionClient.TASK_ACTIONS)
    task.add_argument('--repeat', default='1', help='1..10 or forever')
    task.add_argument('--items', type=json.loads, help='JSON preparation list')
    task.add_argument('--player', type=int, default=1)
    task.add_argument('--allocation', type=json.loads, default={}, help='Assembly JSON: order_id, bases, components, equipment')
    for operation in ('navigate_to', 'pick_up', 'place_at', 'add_ingredient', 'collect_contents', 'discard', 'throw_to', 'get_plate', 'return_utensil'):
        commands.add_parser(operation).add_argument('target_id')
    for operation in ('move_item', 'stage_item', 'clear_station', 'fetch_ingredient', 'get_utensil'):
        job = commands.add_parser(operation)
        job.add_argument('source')
        job.add_argument('destination')
    work = commands.add_parser('work_at')
    work.add_argument('target_id')
    work.add_argument('--until', choices=ActionClient.WORK_CONDITIONS, default='work_complete')
    work.add_argument('--timeout', type=float, default=60)
    activate = commands.add_parser('activate')
    activate.add_argument('target_id')
    activate.add_argument('desired_state', choices=('on', 'off', 'trigger'), default='trigger', nargs='?')
    wait = commands.add_parser('wait_until')
    wait.add_argument('target_id')
    wait.add_argument('condition', choices=ActionClient.WAIT_CONDITIONS)
    wait.add_argument('--timeout', type=float, default=60)
    commands.add_parser('throw_to_player').add_argument('player_id', type=int, choices=(1, 3, 4))
    args = parser.parse_args()
    try:
        with ActionClient(args.port) as client:
            if args.operation == 'list': print(json.dumps(client.available_actions(), indent=2))
            elif args.operation == 'stop': print(json.dumps(client.stop(), indent=2))
            elif args.operation == 'status': print(json.dumps(client.status(), indent=2))
            else:
                if args.operation == 'task': result = client.run_task(args.action, None if args.repeat == 'forever' else int(args.repeat), items=args.items, player_id=args.player, **args.allocation)
                elif args.operation == 'work_at': result = client.work_at(args.target_id, args.until, args.timeout)
                elif args.operation == 'wait_until': result = client.wait_until(args.target_id, args.condition, args.timeout)
                elif args.operation == 'activate': result = client.activate(args.target_id, args.desired_state)
                elif args.operation == 'throw_to_player': result = client.throw_to_player(args.player_id)
                elif args.operation in ('move_item', 'stage_item', 'clear_station', 'fetch_ingredient', 'get_utensil'):
                    result = getattr(client, args.operation)(args.source, args.destination)
                else: result = getattr(client, args.operation)(args.target_id)
                print(json.dumps(result), flush=True)
                try:
                    while True:
                        control = client.status()
                        state = control['navigation'] if args.operation == 'navigate_to' else control['action']
                        if state['state'] != ('navigating' if args.operation == 'navigate_to' else 'running'):
                            print(json.dumps(state), flush=True)
                            break
                        time.sleep(.1)  # Observe completion; does not drive navigation.
                except KeyboardInterrupt:
                    client.stop()
                    raise
    except (OSError, ControlError, ValueError) as error:
        parser.exit(1, str(error) + '\n')
