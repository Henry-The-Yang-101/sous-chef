#!/usr/bin/env python3
"""Typed autonomous Player 2 actions; the caller never supplies steering input."""
import argparse
import json
import time
try:
    from .control import ControlClient, ControlError
except ImportError:
    from control import ControlClient, ControlError


class ActionClient(ControlClient):
    def available_actions(self):
        self._request('actions')
        return self.last_response['availableActions']

    def navigate_to(self, destination_id):
        if (not isinstance(destination_id, str) or not 2 <= len(destination_id) <= 32
                or destination_id[0] != 'd' or not destination_id[1:].isascii()
                or not destination_id[1:].isdigit()):
            raise ValueError('Use a destination ID from available_actions()')
        return self._request('navigate_to', destination_id)

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
    navigate = commands.add_parser('navigate_to')
    navigate.add_argument('destination_id')
    args = parser.parse_args()
    try:
        with ActionClient(args.port) as client:
            if args.operation == 'list': print(json.dumps(client.available_actions(), indent=2))
            elif args.operation == 'stop': print(json.dumps(client.stop(), indent=2))
            elif args.operation == 'status': print(json.dumps(client.status(), indent=2))
            else:
                print(json.dumps(client.navigate_to(args.destination_id)), flush=True)
                try:
                    while True:
                        state = client.status()['navigation']
                        if state['state'] == 'idle':
                            print(json.dumps(state), flush=True)
                            break
                        time.sleep(.1)  # Observe completion; does not drive navigation.
                except KeyboardInterrupt:
                    client.stop()
                    raise
    except (OSError, ControlError, ValueError) as error:
        parser.exit(1, str(error) + '\n')
