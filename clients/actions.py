#!/usr/bin/env python3
"""Typed autonomous Player 2 actions; the caller never supplies steering input."""
import argparse
import base64
import json
import math
import time
import unicodedata
try:
    from .control import ControlClient, ControlError
except ImportError:
    from control import ControlClient, ControlError


class ActionClient(ControlClient):
    WORK_CONDITIONS = ('work_complete', 'one_plate_clean', 'sink_empty')
    WAIT_CONDITIONS = (*WORK_CONDITIONS, 'occupied', 'empty', 'cooked', 'mixed', 'reachable', 'ready')

    TASK_ACTIONS = ('prepare_near_player', 'take_ready', 'wash_dishes', 'deliver_dirty_dishes',
                    'put_out_fire', 'toss_burnt_food', 'monitor_equipment')

    def run_task(self, action, repetitions=1, *, items=None, player_id=1):
        """Run one autonomous task; repetitions=None repeats until Stop/disconnect.

        Preparation items: [{'ingredient': 'chicken', 'preparation': 'chopped', 'quantity': 2}].
        Use preparation='empty' and ingredient='plate'/'pot' for utensils.
        """
        if action not in self.TASK_ACTIONS:
            raise ValueError('Unknown task action')
        if repetitions is not None and (type(repetitions) is not int or not 1 <= repetitions <= 10):
            raise ValueError('repetitions must be 1..10 or None (infinite)')
        args = [action, 'forever' if repetitions is None else repetitions]
        if action == 'prepare_near_player':
            if type(player_id) is not int or player_id not in (1, 3, 4):
                raise ValueError('Choose another local player')
            if not isinstance(items, list) or not 1 <= len(items) <= 10:
                raise ValueError('Provide 1..10 preparation items')
            args.append(player_id)
            for item in items:
                if not isinstance(item, dict) or item.keys() - {'ingredient', 'preparation', 'quantity'}:
                    raise ValueError('Invalid preparation item')
                preparation, quantity = item.get('preparation', 'raw'), item.get('quantity', 1)
                if preparation not in ('raw', 'chopped', 'cooked', 'mixed', 'empty') or type(quantity) is not int or not 1 <= quantity <= 10:
                    raise ValueError('Choose a preparation and quantity 1..10')
                args.extend((self._selector(item.get('ingredient')), preparation, quantity))
        elif items is not None or player_id != 1:
            raise ValueError('Only preparation takes items/player_id')
        return self._request('task', *args)

    def available_actions(self):
        self._request('actions')
        return self.last_response['availableActions']

    def navigate_to(self, destination_id):
        return self._target('navigate_to', destination_id)

    def _target(self, operation, destination_id, *arguments):
        return self._request(operation, self._destination(destination_id), *arguments)

    @staticmethod
    def _destination(destination_id):
        if (not isinstance(destination_id, str) or not 2 <= len(destination_id) <= 32
                or destination_id[0] != 'd' or not destination_id[1:].isascii()
                or not destination_id[1:].isdigit()):
            raise ValueError('Use a destination ID from available_actions()')
        return destination_id

    @staticmethod
    def _selector(value):
        if not isinstance(value, str) or not value.strip() or len(value) > 64 or len(value.encode('utf-8')) > 96 or any(unicodedata.category(c) == 'Cc' for c in value):
            raise ValueError('Selector must contain 1..64 printable characters, at most 96 UTF-8 bytes')
        return 's' + base64.urlsafe_b64encode(value.encode('utf-8')).decode('ascii').rstrip('=')

    def move_item(self, item, destination):
        return self._request('move_item', self._destination(item), self._destination(destination))

    def fetch_ingredient(self, ingredient, destination):
        return self._request('fetch_ingredient', self._selector(ingredient), self._destination(destination))

    def get_plate(self, destination):
        return self._target('get_plate', destination)

    def get_utensil(self, utensil_type, destination):
        return self._request('get_utensil', self._selector(utensil_type), self._destination(destination))

    def stage_item(self, item, handoff_location):
        return self._request('stage_item', self._destination(item), self._destination(handoff_location))

    def return_utensil(self, utensil):
        return self._target('return_utensil', utensil)

    def clear_station(self, station, destination):
        return self._request('clear_station', self._destination(station), self._destination(destination))

    def pick_up(self, target_id):
        return self._target('pick_up', target_id)

    def place_at(self, target_id):
        return self._target('place_at', target_id)

    def add_ingredient(self, target_id):
        """Add the held single ingredient; container transfers are a higher layer."""
        return self._target('add_ingredient', target_id)

    def collect_contents(self, target_id):
        """Collect ready contents into the held compatible plate/container."""
        return self._target('collect_contents', target_id)

    def discard(self, bin_id):
        """Dispose of the held item/contents at the specified native rubbish bin."""
        return self._target('discard', bin_id)

    def throw_to(self, target_id):
        """Plan, navigate, aim and throw the held object to a native receiver."""
        return self._target('throw_to', target_id)

    def throw_to_player(self, player_id):
        if type(player_id) is not int or player_id not in (1, 3, 4):
            raise ValueError('player_id must be another local player: 1, 3, or 4')
        return self._request('throw_to_player', player_id)

    @staticmethod
    def _timeout(seconds):
        if isinstance(seconds, bool) or not isinstance(seconds, (int, float)) or not math.isfinite(seconds) or not .1 <= seconds <= 120:
            raise ValueError('timeout must be finite seconds in 0.1..120')
        return round(seconds * 1000)

    def work_at(self, target_id, until='work_complete', timeout=60):
        if until not in self.WORK_CONDITIONS:
            raise ValueError('Unknown work completion condition')
        return self._target('work_at', target_id, until, self._timeout(timeout))

    def activate(self, target_id, desired_state='trigger'):
        if desired_state not in ('on', 'off', 'trigger'):
            raise ValueError('desired_state must be on, off, or trigger')
        return self._target('activate', target_id, desired_state)

    def wait_until(self, target_id, condition, timeout=60):
        if condition not in self.WAIT_CONDITIONS:
            raise ValueError('Unknown wait condition')
        return self._target('wait_until', target_id, condition, self._timeout(timeout))

    def stop(self):
        """Interrupt the current bridge action/input, including from another client."""
        return self._request('stop')


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
                if args.operation == 'task': result = client.run_task(args.action, None if args.repeat == 'forever' else int(args.repeat), items=args.items, player_id=args.player)
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
