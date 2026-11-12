"""Read-only capability view, independent of whether P2 owns an active job."""
import copy

from clients.capabilities import REGISTRY_HASH, REGISTRY_VERSION, contract, registry, validate_arguments


def snapshot(state):
    catalog, control = state.get('catalog', {}), state.get('control', {})
    native = catalog.get('capabilities') or state.get('nativeCapabilities') or {}
    compatible = (type(native.get('version')) is int and native.get('version') == REGISTRY_VERSION
                  and native.get('registryHash') == REGISTRY_HASH)
    fresh = state.get('fresh', False)
    semantic = copy.deepcopy(catalog.get('semanticCatalog', {})) if fresh and compatible else {}
    busy = control.get('action', {}).get('state') == 'running' or control.get('navigation', {}).get('state') == 'navigating'
    blocked = ('stale_observation' if not fresh else
               'capability_version_mismatch' if not compatible else
               (control.get('reason') or 'input_suspended') if control.get('suspended') else
               (control.get('reason') or 'native_input_unavailable') if not control.get('available') else
               'action_in_progress' if busy else 'already_claimed' if control.get('claimed') else None)
    tasks = {t['name'] for t in catalog.get('taskOptions', [])}
    actions = {a['name'] for a in catalog.get('actions', [])}
    entries = []
    for spec in registry()['contracts']:
        name = spec['name']
        present = name in native.get('tasks' if spec['kind'] == 'task' else 'actions', [])
        support = 'implemented' if compatible and present else 'not_implemented' if compatible else 'unknown'
        applicability = {'state': 'unknown', 'reason': 'native_applicability_not_observed'}
        offered = name in (tasks if spec['kind'] == 'task' else actions)
        if compatible and fresh:
            if spec['kind'] == 'task':
                applicability = catalog.get('taskApplicability', {}).get(name, applicability)
            elif offered:
                applicability = {'state': 'applicable', 'reason': 'native_candidates_observed'}
        reason = blocked or (None if offered else 'native_start_preconditions_not_met')
        if support != 'implemented':
            reason = 'capability_version_mismatch' if not compatible else 'not_implemented'
        if name == 'stop' and fresh and support == 'implemented':
            reason = None
        entries.append({'name': name, 'version': spec['version'], 'support': support,
                        'applicability': copy.deepcopy(applicability),
                        'start_eligibility': {'state': 'eligible' if reason is None else 'blocked',
                                              'reason': reason, 'arguments_require_validation': name != 'stop'}})
    return {'version': REGISTRY_VERSION, 'registry_hash': REGISTRY_HASH,
            'compatible': compatible, 'fresh': fresh, 'entries': entries, 'semantic_catalog': semantic}


def assess_arguments(name, arguments, semantic):
    """Check observed contradictions, preserving unknown physical prerequisites.

    This is not a route oracle or an assembly allocation generator. Incomplete
    observations cannot certify a complete recipe, future route, or availability.
    """
    args = validate_arguments(name, arguments)
    checks = []
    def add(state, reason, path):
        checks.append({'state': state, 'reason': reason, 'path': path})
    if not semantic or semantic.get('error'):
        add('unknown', 'semantic_observation_unavailable', 'arguments')
    elif name == 'navigate_to' and semantic.get('movement'):
        movement = semantic['movement']
        candidates = movement.get('points', []) + movement.get('platforms', []) + movement.get('stations', [])
        found = next((c for c in candidates if c['id'] == args['destination_id']), None)
        add('applicable' if found else 'not_applicable', 'registered_movement_binding' if found else 'movement_binding_stale', 'destination_id')
    elif name == 'press_button':
        target = next((t for t in semantic.get('targets', []) if t['id'] == args['target_id']), None)
        valid = target is not None and 'press_button' in target.get('possibleOperations', [])
        add('applicable' if valid else 'not_applicable', 'exact_activation_target', 'target_id')
        if valid:
            add('applicable' if args['desired_state'] in target.get('activationStates', ['trigger']) else 'not_applicable',
                'observed_activation_state', 'desired_state')
        add('unknown', 'route_and_interaction_require_native_revalidation', 'target_id')
    elif name == 'prepare_near_player':
        players = {p.get('playerId') for p in semantic.get('players', [])}
        add('applicable' if args['player_id'] in players else 'unknown', 'recipient_observation', 'player_id')
        options = semantic.get('displayOptions', {}).get('preparationOptions', [])
        for i, item in enumerate(args['items']):
            # Ground compiler requests with exact semantic IDs. Friendly client
            # aliases still resolve natively; never guess a match from a label.
            matches = [o for o in options if o.get('id') == item['ingredient']]
            status = matches[0].get('preparations', {}).get(item['preparation'], 'unknown') if len(matches) == 1 else 'unknown'
            add(status, 'native_preparation_chain' if status != 'unknown' else 'preparation_compatibility_requires_native_validation', f'items[{i}]')
    elif name == 'assemble_dish':
        tickets = {o.get('id') for o in semantic.get('assemblyOrders', [])}
        add('applicable' if args['order_id'] in tickets else 'unknown', 'exact_ticket_observation', 'order_id')
        items = {i['id']: i for i in semantic.get('displayOptions', {}).get('items', [])}
        targets = {t['id']: t for t in semantic.get('targets', [])}
        for group in ('bases', 'components', 'equipment'):
            for ident in args[group]:
                item = items.get(ident)
                target = targets.get(ident)
                status = 'unknown'
                if item:
                    status = 'applicable'
                    if (item.get('supply') and group != 'components') or (group == 'bases' and not any(item.get(k) for k in ('plate', 'cleanPlate', 'assemblyBase'))):
                        status = 'not_applicable'
                    if group == 'equipment' and not item.get('utensilType') and not (target or {}).get('processingEquipment'):
                        status = 'not_applicable'
                elif target:
                    status = 'applicable' if group == 'equipment' and target.get('processingEquipment') else 'not_applicable'
                add(status, 'exact_allocation_resource', group + '.' + ident)
        add('unknown', 'recipe_composition_and_physical_compatibility_require_native_validation', 'allocation')
    else:
        add('unknown', 'native_start_validation_required', 'arguments')
    state = 'not_applicable' if any(c['state'] == 'not_applicable' for c in checks) else 'unknown' if any(c['state'] == 'unknown' for c in checks) else 'applicable'
    return {'state': state, 'checks': checks, 'native_validation_required': True}
