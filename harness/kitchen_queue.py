"""Durable admission/reconciliation for the native, single-chef dispatcher.

The native runtime chooses work. This coordinator never replays an uncertain
admission and submits dependencies only after their predecessors prove success.
"""
import copy


class KitchenQueue:
    def _tick_kitchen(self):
        from harness.scheduler import context_of, same_kitchen, unique_id
        intent = None
        with self.lock:
            world = self.world()
            current = context_of(world)
            if world.get('control', {}).get('kitchenSchedulerVersion') != 1 or current is None:
                return False
            value = copy.deepcopy(self.data)
            if value['barrier'] or value['discarded'] or value['context'] and value['context'] != current:
                return False
            entries = ([value['active']] if value['active'] else []) + value['pending']
            if value['active'] and not value['active'].get('nativeQueued'):
                return False
            if not any(e.get('nativeQueued') for e in entries):
                return False
            live = world['control'].get('kitchenScheduler', {})
            observed = {r['id']: r for r in live.get('receipts', [])}
            terminal = {'succeeded', 'failed', 'cancelled', 'blocked', 'rejected'}
            retained = []
            missing = []
            for entry in entries:
                receipt = observed.get(entry.get('requestId'))
                if receipt is None and entry.get('receipt', {}).get('state') in terminal:
                    receipt = entry['receipt']
                if entry.get('requestId') and receipt is None:
                    missing.append(entry)
                if receipt is not None:
                    if receipt.get('epoch') != current['epoch']:
                        entry.update(state='reconciling', reason='Native receipt epoch mismatch')
                        missing.append(entry)
                    elif receipt['state'] in terminal:
                        state = 'succeeded' if receipt['state'] == 'succeeded' else 'failed'
                        entry['receipt'] = receipt
                        self._outcome(value, entry, state, receipt.get('reason'))
                        if state != 'succeeded' or entry['after'] == 'pause_pending':
                            value.update(paused=True, reason=receipt.get('reason') or 'Task ended')
                        continue
                    else:
                        entry.update(receipt=receipt, state='running' if live.get('activeRequestId') == entry['requestId'] else 'blocked',
                                     reason=receipt.get('reason'))
                        jobs = (receipt.get('evidence') or {}).get('jobs', [])
                        if entry['state'] != 'running' and any(j.get('state') == 'ready' for j in jobs):
                            entry['state'] = 'ready'
                retained.append(entry)
            active = next((e for e in retained if e.get('requestId') and e.get('requestId') == live.get('activeRequestId')), None)
            if active is None:
                active = next((e for e in retained if e.get('requestId')), None)
            value['active'] = active
            value['pending'] = [e for e in retained if e is not active]
            hold = bool(value['paused'])
            if live.get('held', False) != hold:
                barrier = value.setdefault('kitchenHold', dict(id=unique_id(), hold=hold, context=current))
                if barrier['hold'] != hold:
                    barrier = value['kitchenHold'] = dict(id=unique_id(), hold=hold, context=current)
                intent = ('hold', copy.deepcopy(barrier))
            elif missing:
                entry = missing[0]
                entry.update(state='reconciling', reason='Checking retained native job receipt')
                intent = ('receipt', copy.deepcopy(entry))
            elif not value['paused']:
                succeeded = {e['id'] for e in value['outcomes'] if e['state'] == 'succeeded'}
                manual = next((i for i, e in enumerate(retained) if not e.get('nativeQueued')), len(retained))
                eligible = [e for e in retained[:manual] if not e.get('requestId') and set(e['dependsOn']) <= succeeded]
                entry = next((e for e in eligible if e.get('nativeQueued')), None)
                # A manual primitive is exclusive and uses the established executor.
                if entry is None and not any(e.get('requestId') for e in retained):
                    self._save(value)
                    return False
                if entry:
                    entry.update(state='dispatching', requestId=unique_id(), context=current)
                    intent = ('enqueue', copy.deepcopy(entry))
            if value['context'] is None:
                value['context'] = current
            self._save(value)
        if intent is None:
            return True
        operation, entry = intent
        try:
            result = self.bridge.queue_io(operation, entry)
            error = None
        except Exception as exc:
            result, error = None, str(exc)
        with self.lock:
            value = copy.deepcopy(self.data)
            if not same_kitchen(value['context'], current) or value['barrier']:
                return True
            if operation == 'hold':
                if result and result.get('state') == 'succeeded':
                    value.pop('kitchenHold', None)
                else:
                    value['reason'] = 'Native dispatch hold unconfirmed: ' + str(error or result)
            else:
                entries = ([value['active']] if value['active'] else []) + value['pending']
                target = next((e for e in entries if e.get('requestId') == entry['requestId']), None)
                if target:
                    if result and result.get('id') == target['requestId'] and result.get('epoch') == current['epoch']:
                        target.update(receipt=result, state='ready', reason=result.get('reason'))
                    else:
                        target.update(state='reconciling', reason='Native outcome unknown; admission will not be replayed')
            self._save(value)
        return True
