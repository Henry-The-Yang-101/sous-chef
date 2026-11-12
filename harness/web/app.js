const el = id => document.getElementById(id);
// randomUUID is unavailable on an ordinary LAN HTTP origin (for example iPhone
// Safari); getRandomValues also works outside a secure context.
const viewerId = Array.from(crypto.getRandomValues(new Uint8Array(16)), byte => byte.toString(16).padStart(2, '0')).join('');
const accessFromLink = new URLSearchParams(location.hash.slice(1)).get('access');
let accessToken = accessFromLink || sessionStorage.getItem('phoneAccess');
if (accessFromLink) {
  sessionStorage.setItem('phoneAccess', accessFromLink);
  history.replaceState(null, '', location.pathname + location.search);
}
const authHeaders = () => accessToken ? {'X-Page-Token': accessToken} : {};
let token = null;
let capabilityRegistry = null;
let state = null;
let latestVoice = null;
function withCurrentVoice(next) {
  if (!next.voice) return next;
  if (latestVoice?.instanceId === next.voice.instanceId && latestVoice.revision > next.voice.revision) next.voice = latestVoice;
  else latestVoice = next.voice;
  if (state?.interpreter && next.interpreter && state.voice?.instanceId === next.voice?.instanceId &&
      (state.interpreter.generation > next.interpreter.generation ||
       (state.interpreter.generation === next.interpreter.generation && state.interpreter.revision > next.interpreter.revision))) {
    next.interpreter = state.interpreter;
  }
  return next;
}
let receivedAt = 0;
let pending = false;
let lastError = '';
let draftTasks = [];
let draftOrigin = null;
const newId = () => Array.from(crypto.getRandomValues(new Uint8Array(16)), b => b.toString(16).padStart(2, '0')).join('');
let sessionId = null;
let displayedCatalog = {};
let displayScope = null;
const rows = new Map();
let mapSelection = null;
const mapPicker = new KitchenPicker(target => {
  const mode = el('map-mode').value;
  if (!fresh()) return;
  if (mode === 'throw') el('throw-target').value = throwIdentity(target);
  else if (mode === 'job') el('job-destination').value = target.id;
  else mapSelection = {id: target.id, objectInstanceId: target.objectInstanceId,
    session: state.sessionId, epoch: state.kitchenMap?.levelEpoch};
  render();
}, nameOf);

const reasons = {
  outside_local_round: 'Enter an Arcade → Couch kitchen with two local players.',
  player2_absent_or_ambiguous: 'Add Player 2 with Split Controller.',
  player2_not_ready: 'Player 2 is not ready yet.',
  paused: 'Unpause the round to run actions.',
  input_suppressed: 'The game is temporarily blocking Player 2 input.',
  game_unfocused: 'Choose a destination to bring the game forward and navigate.',
  planning_budget_exceeded: 'The planner reached its time limit. Waiting for a complete live route search.',
  planning_in_progress: 'Checking the rest of the kitchen. Confirmed reachable destinations appear as they are verified.',
  no_safe_start: 'The planner cannot find safe walking space around Player 2.',
  arrived: 'Arrived at the selected destination.',
  positioned_no_action: 'Positioned and facing the target. No action is currently available (for example, an empty sink).',
  alignment_failed: 'Stopped: could not establish a stable, usable stance and facing.',
  stopped: 'Stopped.',
  destination_unreachable: 'Stopped: the destination became unreachable.',
  destination_unavailable: 'Stopped: the destination is no longer available.',
  control_lost: 'Stopped: the control connection or input lease was lost.',
  released: 'Control released.',
  navigation_timeout: 'Stopped: navigation timed out.',
  no_progress: 'Stopped: Player 2 was not making progress.',
  waiting_for_wash_access: 'Needs reachable dirty dishes and a usable sink or water gun.',
};
const explain = reason => reasons[reason] || (reason ? reason.replaceAll('_', ' ') : '');
const kindName = kind => (kind || 'destination').replaceAll('_', ' ');
const number = value => Number.isFinite(value) ? value.toFixed(1) : '—';
const contentsOf = d => d.contents?.summary || '';
const capabilityContracts = () => capabilityRegistry?.contracts || [];
const capability = name => capabilityContracts().find(c => c.name === name);
const parameter = (name, field) => capability(name)?.parameters.properties[field];
const definition = field => field?.$ref ? {...capabilityRegistry.$defs[field.$ref.split('/').at(-1)], ...field} : field;
const capabilityLabels = kind => Object.fromEntries(capabilityContracts().filter(c => c.kind === kind).map(c => [c.name, c.label]));
const actionNames = new Proxy({}, {get: (_, name) => capability(name)?.label || String(name).replaceAll('_', ' ')});
const phaseNames = {picking_up: 'collecting', drop_press: 'placing nearby', dropping: 'checking nearby handoff', throw_hold: 'aiming', flight: 'checking arrival'};
const throwIdentity = target => JSON.stringify([state?.sessionId, target.id, target.objectInstanceId]);
const jobNames = () => capabilityContracts().filter(c => c.category === 'logistics').map(c => c.name);
function displayChoices(catalog) {
  const session = state?.sessionId;
  const epoch = state?.kitchenMap?.levelEpoch ?? (displayScope?.session === session ? displayScope.epoch : null);
  if (session !== displayScope?.session || epoch !== displayScope?.epoch || !state?.connected || state?.control?.reason === 'outside_local_round') {
    displayedCatalog = {}; displayScope = {session, epoch};
  }
  // Pause/staleness may remove eligibility without removing kitchen choices.
  // A new session/round discards them; observed object removals replace the list.
  if (fresh() && catalog.semanticCatalog && !catalog.semanticCatalog.error) {
    const semantic = catalog.semanticCatalog;
    const merge = key => (semantic[key] || []).map(item => ({...item, ...(catalog[key] || []).find(live => live.id === item.id && live.objectInstanceId === item.objectInstanceId)}));
    displayedCatalog = {...catalog, ...semantic, targets: merge('targets'), players: merge('players')};
  }
  else if (fresh() && catalog.targets) displayedCatalog = catalog;
  return displayedCatalog;
}
function displayOptions(catalog) { return catalog.displayOptions || catalog; }
function actionBlocked(busy, live) {
  return !live || pending || busy || (!state?.control?.available && state?.control?.reason !== 'game_unfocused');
}
function throwTargets(catalog, operation, availableOnly = false) {
  const targets = operation === 'throw_to_player' ? catalog.players || [] : catalog.targets || [];
  return availableOnly ? targets.filter(target => target.operations?.includes(operation))
    : targets.filter(target => target.possibleOperations?.includes(operation) || target.operations?.includes(operation) ||
      (operation === 'throw_to_player' || ['counter', 'workstation', 'cooking_station', 'plate_station'].includes(target.kind)));
}
function renderThrows(catalog, visible, busy, live) {
  const operation = el('throw-kind').value;
  const toPlayer = operation === 'throw_to_player';
  const targets = throwTargets(visible, operation);
  const available = throwTargets(catalog, operation, true);
  el('throw-target-name').textContent = toPlayer ? 'Player' : 'Place';
  el('throw-run').textContent = toPlayer ? 'Throw to player' : 'Throw to place';
  setJobOptions(el('throw-target'), targets.map(target => ({value: throwIdentity(target),
    label: toPlayer ? nameOf(target) : `${nameOf(target)} · x ${number(target.position?.[0])}, z ${number(target.position?.[2])}, height ${number(target.position?.[1])}`})));
  el('throw-kind').disabled = false;
  el('throw-target').disabled = !targets.length;
  el('throw-run').disabled = actionBlocked(busy, live) || !available.some(target => throwIdentity(target) === el('throw-target').value);
  let message = blockedReason(busy, live);
  if (!message && !el('throw-target').value) message = toPlayer ? 'Choose a player to receive the held item.' : 'Choose a place to receive the held item.';
  else if (!message && el('throw-run').disabled) message = catalog.throwOptionsComplete === false
    ? 'Checking the selected throw path…' : 'This throw is currently unavailable. Hold a throwable item and wait for a safe release path.';
  el('throw-status').textContent = message;
  el('throw-status').hidden = !message;
  el('throw-run').title = message;
}
function setJobOptions(select, choices) {
  const signature = JSON.stringify(choices);
  if (select.dataset.choices === signature) return;
  const selected = select.value;
  const placeholder = document.createElement('option'); placeholder.value = ''; placeholder.textContent = 'Choose…';
  select.replaceChildren(placeholder, ...choices.map(choice => {
    const option = document.createElement('option'); option.value = choice.value; option.textContent = choice.label; return option;
  }));
  select.value = choices.some(choice => choice.value === selected) ? selected : '';
  select.dataset.choices = signature;
}
function blockedReason(busy, live) {
  if (!live) return 'Waiting for fresh game state.';
  if (pending) return 'Sending the selected action…';
  if (busy) return 'Player 2 is running an action. Wait for it to finish or press Stop.';
  if (!state?.control?.available && state?.control?.reason !== 'game_unfocused') return explain(state?.control?.reason) || 'The game is not accepting Player 2 actions.';
  return '';
}
function renderJobs(catalog, visible, busy, live) {
  const display = displayOptions(visible);
  setJobOptions(el('job-kind'), jobNames().map(name => ({value: name, label: actionNames[name]})));
  const kind = el('job-kind').value;
  const options = catalog.jobOptions?.[kind] || [];
  const items = display.items || [];
  let sources = items.map(item => ({value: item.id, label: nameOf(item)}));
  el('job-source-name').textContent = kind === 'fetch_ingredient' ? 'Ingredient' : kind === 'get_utensil' ? 'Utensil type' : kind === 'clear_station' ? 'Station' : 'Item';
  if (kind === 'fetch_ingredient') sources = (display.ingredients || []).map(item => ({value: item.id, label: item.label.replaceAll('_', ' ')}));
  if (kind === 'get_utensil') sources = (display.utensilTypes || []).filter(type => type !== 'plate').map(type => ({value: type, label: type.replaceAll('_', ' ')}));
  if (kind === 'clear_station') sources = (visible.targets || []).filter(item => ['counter', 'workstation', 'cooking_station', 'plate_station', 'washing_station'].includes(item.kind) || item.possibleOperations?.includes('place_at'))
    .map(item => ({value: item.id, label: nameOf(item)}));
  if (kind === 'return_utensil') sources = items.filter(item => item.utensilType).map(item => ({value: item.id, label: nameOf(item)}));
  setJobOptions(el('job-source'), sources);
  const source = kind === 'get_plate' ? '' : el('job-source').value;
  const destinations = (display.deliveryDestinations || []).map(item => ({value: item.id, label: nameOf(item)}));
  setJobOptions(el('job-destination'), destinations);
  const pair = options.find(option => option.sourceId === source);
  const runnable = pair?.destinationIds?.length && (kind === 'return_utensil' || pair.destinationIds.includes(el('job-destination').value));
  el('job-source-label').hidden = kind === 'get_plate';
  el('job-destination-label').hidden = kind === 'return_utensil';
  el('job-kind').disabled = false;
  el('job-source').disabled = !sources.length; el('job-destination').disabled = !destinations.length;
  el('job-run').disabled = actionBlocked(busy, live) || !runnable;
  let message = blockedReason(busy, live);
  if (!message && !kind) message = 'Choose a job.';
  else if (!message && ((kind !== 'get_plate' && !source) || (kind !== 'return_utensil' && !el('job-destination').value))) message = 'Choose the required fields to run this job.';
  else if (!message && !runnable) message = 'This source and destination are currently unavailable or incompatible. Run job enables when they form a valid job.';
  el('job-status').textContent = message;
  el('job-status').hidden = !message;
  el('job-run').title = message;
}
function actionChoices(d) {
  const operations = new Set(d.possibleOperations || ['navigate_to', ...(d.operations || [])]);
  for (const operation of d.operations || []) operations.add(operation);
  const choices = [];
  for (const operation of operations) {
    if (!capability(operation) || capability(operation).kind !== 'action') continue;
    if (operation === 'work_at' || operation === 'wait_until') {
      const key = operation === 'work_at' ? 'WorkConditions' : 'WaitConditions';
      const conditions = new Set([...(d[`possible${key}`] || []), ...(d[key[0].toLowerCase() + key.slice(1)] || [])]);
      for (const condition of conditions) choices.push({value: `${operation}|${condition}`, label: `${actionNames[operation]}: ${condition.replaceAll('_', ' ')}`});
    } else if (operation === 'activate') {
      for (const desired of d.activationStates || ['trigger']) choices.push({value: `activate|${desired}`, label: `Activate: ${desired}`});
    } else choices.push({value: operation, label: actionNames[operation] || operation.replaceAll('_', ' ')});
  }
  return choices;
}
function canPerform(d, choice) {
  if (!d || !choice) return false;
  const [operation, condition] = choice.split('|');
  if (operation === 'navigate_to') return !!d.walkReachable;
  if (!d.operations?.includes(operation)) return false;
  if (!['wait_until', 'throw_to', 'throw_to_player'].includes(operation) && !d.walkReachable) return false;
  if (operation === 'work_at') return d.workConditions?.includes(condition);
  if (operation === 'wait_until') return d.waitConditions?.includes(condition);
  if (operation === 'activate') return (d.activationStates || ['trigger']).includes(condition);
  return true;
}
function targetsWithRoutes(catalog) {
  const byId = new Map((catalog.destinations || []).map(d => [d.id, {...d, walkReachable: true}]));
  for (const d of [...(catalog.targets || []), ...(catalog.players || [])]) byId.set(d.id, {...byId.get(d.id), ...d});
  return Array.from(byId.values());
}
function nameOf(d) {
  if (d.foodName) return `${d.foodName.replaceAll('_', ' ')} dispenser`;
  if (d.kind === 'ingredient_supply') return 'Food dispenser (name unavailable)';
  if (d.kind === 'washing_station') return 'Sink';
  if (d.kind === 'workstation') return 'Chopping station';
  return d.label.replace(/^utensil_/i, '').replace(/_0?1\b/g, '').replaceAll('_', ' ').replace(/\(Clone\)/g, '').trim();
}
function fresh() {
  return state?.fresh && performance.now() - receivedAt + (state.ageMs || 0) <= 1500;
}
function chooseOnMap(mode) {
  el('map-mode').value = mode;
  mapPicker.overlapIds = [];
  render();
  el('map-title').closest('.kitchen').scrollIntoView({block: 'start'});
}
function renderMap(catalog, visible, destinations, busy, live) {
  if (document.hidden) return;
  renderFps(state, live && performance.now() - receivedAt + (state?.gamePerformance?.ageMs || 0) <= 1500);
  const mode = el('map-mode').value;
  const control = state?.control || {};
  const blocked = !live || pending || busy || (!control.available && control.reason !== 'game_unfocused');
  let choices = destinations.filter(d => actionChoices(d).length), selected = null;
  let description = 'Choose a target on the map to inspect its actions.';
  if (mapSelection && (mapSelection.session !== state.sessionId || mapSelection.epoch !== displayScope?.epoch ||
      !destinations.some(d => d.id === mapSelection.id && d.objectInstanceId === mapSelection.objectInstanceId))) mapSelection = null;
  if (mode === 'throw') {
    choices = throwTargets(visible, el('throw-kind').value);
    selected = choices.find(d => throwIdentity(d) === el('throw-target').value);
    description = selected ? `Throw to ${nameOf(selected)}. Use the throw button below to send it.` : el('throw-status').textContent || 'Choose a throw recipient on the map.';
  } else if (mode === 'job') {
    const ids = new Set(Array.from(el('job-destination').options).map(option => option.value).filter(Boolean));
    choices = (displayOptions(visible).deliveryDestinations || []).filter(d => ids.has(d.id));
    selected = choices.find(d => d.id === el('job-destination').value);
    description = el('job-kind').value === 'return_utensil' ? 'Return utensil uses its observed home. Choose the utensil below.'
      : selected ? `Deliver to ${nameOf(selected)}. Use Run job below to start.` : 'Choose a job and source below, then select a delivery target here.';
    if (el('job-kind').value === 'return_utensil') choices = [];
  } else {
    selected = choices.find(d => d.id === mapSelection?.id && d.objectInstanceId === mapSelection?.objectInstanceId);
    description = selected ? `${nameOf(selected)} · height ${number(selected.position?.[1])}${contentsOf(selected) ? ` · ${contentsOf(selected)}` : ''}` : description;
  }
  // Choosing an unavailable target is useful for inspecting its options.
  // Only Run dispatches, using the current live eligibility catalogue.
  const all = new Map([...destinations, ...(displayOptions(visible).deliveryDestinations || []), ...choices].map(d => [d.id, d]));
  const map = state?.kitchenMap;
  const mapLive = live && map && performance.now() - receivedAt + (map.ageMs || 0) <= 1500;
  mapPicker.render(mapLive ? {...map, orientation: state?.catalog?.semanticCatalog?.movement?.orientation} : null, Array.from(all.values()), choices, selected?.id, live);
  el('map-action-controls').hidden = mode !== 'action';
  setJobOptions(el('map-action'), selected ? actionChoices(selected) : []);
  if (selected && !el('map-action').value) el('map-action').selectedIndex = 1;
  el('map-action').disabled = !selected;
  const current = targetsWithRoutes(catalog).find(d => d.id === selected?.id && d.objectInstanceId === selected?.objectInstanceId);
  el('map-run').disabled = blocked || !canPerform(current, el('map-action').value) || !mapLive;
  el('map-selection').textContent = !mapLive ? 'Waiting for a fresh kitchen map.' : `${description}${mode === 'action' && selected && el('map-run').disabled ? ' This action is currently unavailable.' : ''}`;
}
function render() {
  renderVoice();
  const live = fresh();
  if (draftOrigin && state?.queue && (state.queue.commandGeneration !== draftOrigin.generation ||
      (state.queue.nativeContext && JSON.stringify(state.queue.nativeContext) !== JSON.stringify(draftOrigin.context)))) {
    draftTasks = []; draftOrigin = null;
  }
  const control = state?.control || {};
  const nav = control.navigation || {};
  const receipt = control.action || {};
  const moving = live && nav.state === 'navigating';
  const acting = live && receipt.state === 'running';
  const busy = moving || acting;
  const catalog = live ? state.catalog || {} : {};
  const visible = displayChoices(catalog);
  renderThrows(catalog, visible, busy, live);
  renderJobs(catalog, visible, busy, live);
  renderTasks(catalog, visible, busy, live);
  renderQueue();
  const destinations = targetsWithRoutes(visible);
  const currentTargets = targetsWithRoutes(catalog);
  const catalogReason = catalog.reason || catalog.navigation?.catalogReason || control.reason;
  const useReceipt = receipt.operation && (acting || (!moving && state?.lastCommand?.operation !== 'navigate_to'));
  const recorded = useReceipt ? (destinations.find(d => d.id === receipt.targetId || (receipt.operation === 'throw_to_player' && String(d.playerId) === receipt.targetId)) || state?.lastCommand?.target)
    : nav.destination || (nav.destinationId && state?.lastCommand?.target);
  const active = destinations.find(d => d.id === recorded?.id) || recorded;
  el('connection').textContent = live ? 'Bridge connected' : state?.connected ? 'State stale' : 'Bridge disconnected';
  el('light').className = 'light ' + (live ? 'live' : 'offline');
  el('freshness').textContent = live ? `Updated ${Math.max(0, Math.round((state.ageMs || 0) + performance.now() - receivedAt))} ms ago` : 'Waiting for a fresh sample';
  el('action').textContent = acting ? `${receipt.operation === 'task' ? taskLabels()[receipt.details?.taskAction] || 'Task' : actionNames[receipt.operation] || receipt.operation} · ${phaseNames[receipt.phase] || receipt.phase.replaceAll('_', ' ')}` : moving ? `${nav.phase === 'waiting_route' ? 'Waiting to reach' : nav.phase === 'aligning' ? 'Aligning with' : 'Walking to'} ${active ? nameOf(active) : nav.destinationId}` : live ? 'Player 2 is idle' : 'Waiting for the bridge';
  el('target').textContent = receipt.operation === 'task'
    ? `${receipt.details?.completedRepetitions || 0} repetitions completed${receipt.details?.infinite ? ' · until stopped' : ` of ${receipt.details?.repetitions || 1}`}`
    : active
    ? `${active.label}${contentsOf(active) ? ` · Contents: ${contentsOf(active)}` : ''} · ${active.id} · object ${active.objectInstanceId} · x ${number(active.position?.[0])}, z ${number(active.position?.[2])}${Number.isFinite(active.routeDistance) ? ` · ${number(active.routeDistance)} units to walk` : ''}`
    : 'Join Player 2 with Split Controller, then enter an unpaused Arcade → Couch kitchen.';
  const ready = nav.arrival;
  const possible = ready ? [['canPickup', 'pickup'], ['canPlace', 'place'], ['canUse', 'use']].filter(([key]) => ready[key]).map(([,name]) => name).join(', ') : '';
  el('result').textContent = busy && control.suspended
    ? control.reason === 'game_unfocused' ? 'Suspended while the game is unfocused. Return to the game to resume, or press Stop to cancel.'
      : 'Suspended while the game blocks input. The same job resumes when input is available. Stop cancels it.'
    : useReceipt ? `${receipt.state}: ${explain(receipt.reason) || 'Checking the requested outcome.'}` : moving ? (nav.phase === 'waiting_route' ? 'Waiting for a checked route to the selected destination. Stop cancels the wait.' : 'Walking to the selected destination and verifying arrival. Stop interrupts it.')
    : live ? explain(nav.reason) + (nav.reason === 'arrived' && possible ? ` Available at arrival: ${possible}.` : '') : '';
  const error = lastError || (!live && state?.error) || '';
  el('error').hidden = !error;
  el('error').textContent = error;
  el('stop').disabled = !token;
  const query = el('filter').value.toLowerCase().trim();
  const shown = destinations.filter(d => `${nameOf(d)} ${contentsOf(d)} ${d.label} ${kindName(d.kind)} ${d.id}`.toLowerCase().includes(query));
  el('count').textContent = (query ? `${shown.length} of ${destinations.length}` : String(destinations.length)) + (catalog.complete === false && destinations.length ? ' · still checking' : '');
  el('empty').hidden = shown.length > 0;
  el('table-wrap').hidden = !shown.length;
  el('empty').textContent = !live ? 'No live destinations. Start the game with the bridge; this page reconnects automatically.'
    : query && destinations.length ? 'No destinations match this filter.'
    : explain(catalogReason) || 'No observed action targets at the moment. This list updates automatically.';

  if (sessionId !== state?.sessionId) { rows.forEach(row => row.tr.remove()); rows.clear(); sessionId = state?.sessionId; }
  const ids = new Set(shown.map(d => d.id));
  for (const [id, row] of rows) if (!ids.has(id)) { row.tr.remove(); rows.delete(id); }
  shown.sort((a, b) => kindName(a.kind).localeCompare(kindName(b.kind)) || nameOf(a).localeCompare(nameOf(b), undefined, {numeric: true}) || a.id.localeCompare(b.id, undefined, {numeric: true}));
  for (const [index, destination] of shown.entries()) {
    let row = rows.get(destination.id);
    if (!row) {
      const tr = document.createElement('tr');
      const titleCell = document.createElement('td');
      const title = document.createElement('strong');
      const contents = document.createElement('small'); contents.className = 'contents';
      const meta = document.createElement('small');
      const raw = document.createElement('small'); raw.className = 'raw';
      titleCell.append(title, contents, meta, raw);
      const position = document.createElement('td');
      const distance = document.createElement('td'); distance.className = 'distance';
      const walkDistance = document.createElement('strong');
      const directDistance = document.createElement('small');
      distance.append(walkDistance, directDistance);
      const action = document.createElement('td'); action.className = 'action-controls';
      const select = document.createElement('select');
      const button = document.createElement('button'); button.type = 'button';
      action.append(select, button); tr.append(titleCell, position, distance, action);
      row = {tr, title, contents, meta, raw, position, walkDistance, directDistance, select, button, destination};
      select.addEventListener('change', render);
      button.addEventListener('click', () => perform(row.destination, row.select.value));
      rows.set(destination.id, row);
    }
    row.destination = destination;
    row.title.textContent = nameOf(destination);
    const contents = destination.contents;
    row.contents.hidden = !contentsOf(destination);
    row.contents.textContent = contents?.status === 'occupied' ? `Contents: ${contents.summary}` : contentsOf(destination);
    row.contents.classList.toggle('unknown', contents?.status === 'unknown');
    row.meta.textContent = `${kindName(destination.kind)} · ${destination.id} · object ${destination.objectInstanceId}`;
    row.raw.textContent = `${destination.foodName ? `Dispenses: ${destination.foodName} · ` : ''}${destination.label}`;
    row.position.textContent = `${number(destination.position?.[0])} / ${number(destination.position?.[2])}`;
    row.walkDistance.textContent = Number.isFinite(destination.routeDistance) ? `${number(destination.routeDistance)} units walk` : 'Walk distance unavailable';
    row.directDistance.textContent = Number.isFinite(destination.directDistance) ? `${number(destination.directDistance)} units direct` : 'Direct distance unavailable';
    const choices = actionChoices(destination);
    const signature = JSON.stringify(choices);
    if (row.choiceSignature !== signature) {
      const selected = row.select.value;
      row.select.replaceChildren(...choices.map(choice => {
        const option = document.createElement('option'); option.value = choice.value; option.textContent = choice.label; return option;
      }));
      if (choices.some(choice => choice.value === selected)) row.select.value = selected;
      row.choiceSignature = signature;
    }
    row.select.setAttribute('aria-label', `Action for ${nameOf(destination)}`);
    row.select.disabled = !choices.length;
    row.button.textContent = busy && active?.id === destination.id ? 'Running…' : 'Run';
    row.button.setAttribute('aria-label', `${choices.find(c => c.value === row.select.value)?.label || 'Run'}: ${nameOf(destination)}`);
    const current = currentTargets.find(d => d.id === destination.id && d.objectInstanceId === destination.objectInstanceId);
    row.button.disabled = actionBlocked(busy, live) || !canPerform(current, row.select.value);
    row.button.title = row.button.disabled ? blockedReason(busy, live) || 'This action is currently unavailable for this target.' : '';
    row.tr.classList.toggle('active', busy && active?.id === destination.id);
    const atIndex = el('rows').children[index];
    if (atIndex !== row.tr) el('rows').insertBefore(row.tr, atIndex || null);
  }
  renderMap(catalog, visible, destinations, busy, live);
  el('details').textContent = JSON.stringify({sessionId: state?.sessionId, bridgeVersion: state?.bridgeVersion, supportedActions: state?.supportedActions, pad: control.pad, side: control.side, navigation: nav, action: receipt, error: state?.error}, null, 2);
}

async function post(path, data = {}) {
  const response = await fetch(path, {method: 'POST', headers: {'Content-Type': 'application/json'},
    body: JSON.stringify({...data, token, viewerId}), signal: AbortSignal.timeout(6000)});
  const result = await response.json();
  if (!response.ok) throw new Error(result.error || 'Command failed');
  return withCurrentVoice(result);
}
async function perform(d, choice) {
  if (actionBlocked(state?.control?.navigation?.state === 'navigating' || state?.control?.action?.state === 'running', fresh()) ||
      !canPerform(targetsWithRoutes(state.catalog || {}).find(target => target.id === d.id && target.objectInstanceId === d.objectInstanceId), choice)) return;
  pending = true; lastError = ''; render();
  try {
    const [operation, option] = choice.split('|');
    const args = operation === 'work_at' ? {until: option} : operation === 'wait_until' ? {condition: option} : operation === 'activate' ? {desired_state: option} : {};
    state = await post('/api/action', {destinationId: d.id, objectInstanceId: d.objectInstanceId, sessionId: state.sessionId, operation, arguments: args});
    receivedAt = performance.now();
  } catch (error) { lastError = error.message; }
  finally { pending = false; render(); }
}
async function runThrow(event) {
  event.preventDefault();
  if (pending || !fresh() || el('throw-run').disabled) return;
  const operation = el('throw-kind').value;
  const target = throwTargets(state.catalog || {}, operation, true).find(target => throwIdentity(target) === el('throw-target').value);
  if (target) await perform(target, operation);
}
async function runJob(event) {
  event.preventDefault();
  if (pending || !fresh() || el('job-run').disabled) return;
  const operation = el('job-kind').value, sourceId = el('job-source').value, destinationId = el('job-destination').value;
  const catalog = state.catalog || {}; let args = {};
  const target = operation === 'return_utensil' ? (catalog.items || []).find(item => item.id === sourceId)
    : (catalog.deliveryDestinations || []).find(item => item.id === destinationId);
  if (!target) return;
  if (operation === 'fetch_ingredient') args = {ingredient: sourceId};
  else if (operation === 'get_utensil') args = {utensil_type: sourceId};
  else if (['move_item', 'stage_item', 'clear_station'].includes(operation)) {
    const source = (operation === 'clear_station' ? catalog.targets || [] : catalog.items || []).find(item => item.id === sourceId);
    if (!source) return;
    args = {[operation === 'clear_station' ? 'station_id' : 'item_id']: sourceId, source_instance_id: source.objectInstanceId};
  }
  pending = true; lastError = ''; render();
  try {
    state = await post('/api/action', {destinationId: target.id, objectInstanceId: target.objectInstanceId, sessionId: state.sessionId, operation, arguments: args});
    receivedAt = performance.now();
  } catch (error) { lastError = error.message; }
  finally { pending = false; render(); }
}
async function stop() {
  releaseVoice(true);
  draftTasks = []; draftOrigin = null;
  if (!token) return;
  lastError = '';
  try { state = await post('/api/stop'); receivedAt = performance.now(); }
  catch (error) { lastError = error.message; }
  render();
}
async function poll() {
  try {
    if (!token) {
      const response = await fetch('/api/bootstrap', {headers: authHeaders(), signal: AbortSignal.timeout(2000)});
      if (response.status === 403) throw new Error('Scan the current QR code on the Mac to connect this phone.');
      if (!response.ok) throw new Error('Could not connect to the harness');
      const bootstrap = await response.json();
      capabilityRegistry = bootstrap.capabilityRegistry; token = bootstrap.token;
      if (!el('task-items').children.length) addTaskItem();
    }
    const response = await fetch('/api/state', {headers: {...authHeaders(), 'X-Viewer-Id': viewerId}, signal: AbortSignal.timeout(2000)});
    if (!response.ok) throw new Error('Could not read live state');
    state = withCurrentVoice(await response.json()); receivedAt = performance.now();
  } catch (error) {
    state = {connected: false, fresh: false, error: error.message.startsWith('Scan ') ? error.message : 'Harness connection lost. Reconnecting…'};
    token = null;
  }
  render();
  setTimeout(poll, 250);
}
let shareSignature = '';
let shareLinks = [];
let selectedPhoneUrl = null;
let shareStatus = '';
let internetPending = false;
let qrObjectUrl = null;
function showPhoneLink() {
  const link = internetPending && !selectedPhoneUrl ? null : shareLinks[Number(el('phone-network').value) || 0];
  el('phone-qr').hidden = !link;
  el('phone-link').hidden = !link;
  if (qrObjectUrl) { URL.revokeObjectURL(qrObjectUrl); qrObjectUrl = null; }
  el('phone-status').textContent = !link
    ? shareStatus || 'No phone link available. Connect your Mac to a network.'
    : link.label === 'Internet'
      ? 'Scan to connect through the internet. Keep this harness and the game running on your Mac.'
      : `${shareStatus ? shareStatus + ' ' : ''}For this Wi-Fi link, keep your phone on the same Wi-Fi as your Mac.`;
  if (!link) return;
  qrObjectUrl = URL.createObjectURL(new Blob([link.qrSvg], {type: 'image/svg+xml'}));
  el('phone-qr').src = qrObjectUrl;
  el('phone-link').href = link.url;
  el('phone-link').textContent = link.url;
}
async function pollSharing() {
  try {
    const response = await fetch('/api/share', {headers: authHeaders(), signal: AbortSignal.timeout(2000)});
    if (!response.ok) throw new Error();
    const sharing = await response.json();
    const signature = JSON.stringify(sharing);
    if (signature !== shareSignature) {
      shareSignature = signature; shareLinks = sharing.links;
      shareStatus = sharing.status; internetPending = sharing.internetPending;
      el('phone-network').replaceChildren(...shareLinks.map((link, index) => {
        const option = document.createElement('option');
        option.value = index; option.textContent = `${link.label} · ${new URL(link.url).host}`;
        return option;
      }));
      if (internetPending) {
        const waiting = document.createElement('option');
        waiting.value = ''; waiting.textContent = 'Waiting for Cloudflare…';
        el('phone-network').prepend(waiting);
      }
      // Follow the preferred Internet link as it arrives unless the user chose a link.
      const selected = shareLinks.findIndex(link => link.url === selectedPhoneUrl);
      if (selected < 0) selectedPhoneUrl = null;
      el('phone-network').value = internetPending && selected < 0 ? '' : Math.max(0, selected);
      el('phone-network-label').hidden = shareLinks.length < (internetPending ? 1 : 2);
      showPhoneLink();
    }
  } catch {
    shareSignature = ''; shareLinks = []; showPhoneLink();
    el('phone-network-label').hidden = true;
    el('phone-status').textContent = 'Open this page on the Mac to get the current phone QR code.';
  }
  setTimeout(pollSharing, 3000);
}
el('phone-network').addEventListener('change', () => {
  const value = el('phone-network').value;
  selectedPhoneUrl = value === '' ? null : shareLinks[Number(value)]?.url || null;
  showPhoneLink();
});
if (matchMedia('(max-width: 650px)').matches) el('phone-share').open = false;
el('filter').addEventListener('input', render);
el('job-kind').addEventListener('change', render);
el('job-source').addEventListener('change', render);
el('job-destination').addEventListener('change', render);
el('job-form').addEventListener('submit', runJob);
el('throw-kind').addEventListener('change', render);
el('throw-target').addEventListener('change', render);
el('throw-form').addEventListener('submit', runThrow);
el('throw-map').addEventListener('click', () => chooseOnMap('throw'));
el('job-map').addEventListener('click', () => chooseOnMap('job'));
el('map-mode').addEventListener('change', () => { mapPicker.overlapIds = []; render(); });
el('map-action').addEventListener('change', render);
el('map-run').addEventListener('click', () => {
  const d = mapPicker.targets.get(mapSelection?.id);
  if (d && d.objectInstanceId === mapSelection?.objectInstanceId && !el('map-run').disabled) perform(d, el('map-action').value);
});
document.addEventListener('visibilitychange', render);
el('stop').addEventListener('click', stop);
document.addEventListener('keydown', event => { if (event.key === 'Escape') { event.preventDefault(); stop(); } });
window.addEventListener('pagehide', () => {
  if (token) navigator.sendBeacon('/api/leave', new Blob([JSON.stringify({token, viewerId})], {type: 'application/json'}));
});
setInterval(() => { if (!fresh()) render(); }, 250);
poll();
pollSharing();

const taskLabels = () => capabilityLabels('task');
function addTaskItem() {
  const items = parameter('prepare_near_player', 'items');
  if (!items || el('task-items').children.length >= items.maxItems) return;
  const row = document.createElement('div'); row.className = 'task-item';
  const ingredient = document.createElement('select'); ingredient.required = true;
  ingredient.setAttribute('aria-label', 'Ingredient or utensil');
  ingredient.addEventListener('change', render);
  const preparation = document.createElement('select'); preparation.setAttribute('aria-label', 'Preparation');
  for (const kind of capabilityRegistry.$defs.preparation.enum) preparation.add(new Option(kind === 'empty' ? 'Empty utensil / plate' : kind, kind));
  preparation.addEventListener('change', render);
  const quantity = document.createElement('input'); quantity.type = 'number'; quantity.min = capabilityRegistry.$defs.quantity.minimum; quantity.max = capabilityRegistry.$defs.quantity.maximum; quantity.value = definition(capabilityRegistry.$defs.preparation_item.properties.quantity).default; quantity.required = true; quantity.setAttribute('aria-label', 'Quantity');
  quantity.addEventListener('input', render);
  const remove = document.createElement('button'); remove.type = 'button'; remove.textContent = 'Remove'; remove.onclick = () => { row.remove(); render(); };
  row.append(ingredient, preparation, quantity, remove); el('task-items').append(row);
}
function selectedResources(id) {
  return Array.from(el(id).selectedOptions || []).map(option => option.value).filter(Boolean);
}
function setResourceOptions(id, options) {
  const select = el(id), selected = new Set(selectedResources(id));
  const signature = JSON.stringify(options.map(option => [option.value, option.label]));
  if (select.dataset.options === signature) return;
  select.dataset.options = signature;
  select.replaceChildren(...options.map(option => {
    const node = document.createElement('option'); node.value = option.value; node.textContent = option.label;
    node.selected = selected.has(option.value); return node;
  }));
}
function renderTasks(catalog, visible, busy, live) {
  const task = state?.control?.action;
  setJobOptions(el('task-kind'), Object.entries(taskLabels()).map(([value, label]) => ({value, label})));
  const kind = el('task-kind').value;
  const preparing = kind === 'prepare_near_player', assembling = kind === 'assemble_dish';
  const pressing = kind === 'press_button';
  el('task-button').hidden = !pressing;
  const controls = (catalog.semanticCatalog?.targets || visible.targets || []).filter(target => target.possibleOperations?.includes('press_button'));
  setJobOptions(el('button-target'), controls.map(target => ({value: target.id, label: `${nameOf(target)} · ${target.id}`})));
  const selectedControl = controls.find(target => target.id === el('button-target').value);
  setJobOptions(el('button-state'), (selectedControl?.activationStates || ['trigger']).map(value => ({value, label: value === 'trigger' ? 'Press once' : `Set ${value}`})));
  const validButton = !pressing || Boolean(selectedControl && el('button-state').value);
  el('task-assembly').hidden = !assembling;
  el('task-preparation').hidden = !preparing;
  const recipient = definition(parameter('prepare_near_player', 'player_id'));
  if (recipient) {
    setJobOptions(el('task-player'), recipient.enum.map(id => ({value: String(id), label: `Player ${id}`})));
    if (!el('task-player').value) el('task-player').value = String(recipient.default);
  }
  const preparationOptions = displayOptions(visible).preparationOptions || [];
  for (const row of el('task-items').children) {
    const utensil = row.children[1].value === 'empty';
    setJobOptions(row.children[0], preparationOptions.filter(option => option.utensil === utensil && option.preparations?.[row.children[1].value] !== 'not_applicable')
      .map(option => ({value: option.id, label: option.label.replaceAll('_', ' ')})));
  }
  const orders = catalog.semanticCatalog?.assemblyOrders || catalog.assemblyOrders || visible.assemblyOrders || [];
  setJobOptions(el('assembly-order'), orders.map(order => ({value: order.id, label: `${order.recipe.replaceAll('_', ' ')} · ticket ${order.id.split(':').at(-1)}`})));
  const resources = displayOptions(visible).items || [];
  const resourceLabel = item => `${nameOf(item)} · ${item.id}`;
  const bases = resources.filter(item => !item.supply && (item.plate || item.cleanPlate || item.assemblyBase));
  setResourceOptions('assembly-bases', bases.map(item => ({value: item.id, label: resourceLabel(item)})));
  setResourceOptions('assembly-components', resources.map(item => ({value: item.id, label: resourceLabel(item)})));
  const equipment = new Map(resources.filter(item => item.utensilType).map(item => [item.id, item]));
  for (const target of visible.targets || []) if (target.processingEquipment) equipment.set(target.id, target);
  setResourceOptions('assembly-equipment', Array.from(equipment.values()).map(item => ({value: item.id, label: resourceLabel(item)})));
  const assigned = ['assembly-bases', 'assembly-components', 'assembly-equipment'].map(selectedResources);
  const validAssembly = !assembling || (el('assembly-order').value && (assigned.flat().length === 0 || assigned[0].length > 0) && assigned.every((group, i) => group.length <= parameter('assemble_dish', ['bases', 'components', 'equipment'][i]).maxItems) && new Set(assigned.flat()).size === assigned.flat().length);
  // Browsing and editing remain possible while execution is blocked.
  for (const field of el('task-form').querySelectorAll('input, select, button')) if (field.id !== 'task-run') field.disabled = false;
  for (const field of el('task-preparation').querySelectorAll('input, select, button')) field.disabled = !preparing;
  for (const field of el('task-assembly').querySelectorAll('select')) field.disabled = !assembling;
  for (const field of el('task-button').querySelectorAll('select')) field.disabled = !pressing;
  const repetition = capability(kind)?.repeat;
  const repeatSelect = el('task-repeat');
  if (repetition) {
    const choices = Array.from({length: repetition.maximum}, (_, i) => ({value: String(i + 1), label: String(i + 1)}));
    if (repetition.until_cancelled) choices.push({value: 'forever', label: 'Until Stop'});
    setJobOptions(repeatSelect, choices);
    if (!repeatSelect.value) repeatSelect.value = String(repetition.default);
  }
  repeatSelect.disabled = !repetition || repetition.maximum === 1;
  repeatSelect.title = repetition ? `Each repetition: ${repetition.unit.replaceAll('_', ' ')}` : '';
  el('task-add-item').disabled = !preparing || el('task-items').children.length >= (parameter('prepare_near_player', 'items')?.maxItems || 0);
  const eligible = (catalog.taskOptions || []).some(t => t.name === kind);
  const validItems = !preparing || (el('task-items').children.length > 0 && Array.from(el('task-items').children).every(row =>
    row.children[0].value && row.children[2].checkValidity()));
  const playerPresent = !preparing || (catalog.semanticCatalog?.players || catalog.players || []).some(player => String(player.playerId) === el('task-player').value);
  const queueReady = live && !pending && state?.capabilities?.compatible && state?.queue?.nativeContext && !state.queue.barrier && !state.queue.error;
  const validSelection = Boolean(kind && validItems && playerPresent && validAssembly && validButton);
  const unlimitedDraft = draftTasks.some(t => t.repeat.mode === 'until_cancelled');
  el('task-stage').disabled = !queueReady || !validSelection || draftTasks.length >= (capabilityRegistry?.max_plan_tasks || 5) || unlimitedDraft;
  const proposed = draftTasks.length ? draftTasks : validSelection ? [selectedTaskSpec()] : [];
  const pendingTasks = state?.queue?.pending || [];
  const activeTask = state?.queue?.active;
  const appendForever = [activeTask, ...pendingTasks].some(t => t?.spec.repeat.mode === 'until_cancelled');
  el('task-run').disabled = !queueReady || !proposed.length || appendForever || pendingTasks.length + proposed.length > (state?.queue?.capacity || 10);
  const dependentActive = activeTask && pendingTasks.some(t => t.dependsOn.includes(activeTask.id));
  const nowForever = proposed.some(t => t.repeat.mode === 'until_cancelled') && pendingTasks.length > 0;
  el('task-now').disabled = !queueReady || !proposed.length || dependentActive || nowForever || pendingTasks.length + proposed.length > (state?.queue?.capacity || 10);
  let message = !live ? 'Waiting for fresh game state.' : !queueReady ? 'Queue unavailable or reconciling. Stop remains independent.' : '';
  if (!message && !validSelection && !draftTasks.length) message = 'Choose a task and its required ingredients, recipient or exact allocation.';
  else if (!message && appendForever) message = 'Cannot add behind unlimited work. Stop or explicitly replace that assignment.';
  else if (!message && !eligible) message = 'This task may wait until native start conditions are met.';
  if (draftTasks.length) message += ` ${draftTasks.length}-step plan ready; all steps are accepted together.`;
  el('task-run').title = message;
  el('task-now').title = dependentActive ? 'Replace the dependent plan before interrupting its active step.' : 'Cancel active work, retain independent pending tasks, and run this plan.';
  el('plan-draft').hidden = !draftTasks.length;
  const signature = JSON.stringify(draftTasks);
  if (el('plan-steps').dataset.signature !== signature) {
    el('plan-steps').dataset.signature = signature;
    el('plan-steps').replaceChildren(...draftTasks.map((spec, index) => {
      const li = document.createElement('li'); li.textContent = taskDescription(spec);
      const remove = document.createElement('button'); remove.type = 'button'; remove.textContent = 'Remove step';
      remove.onclick = () => { draftTasks.splice(index, 1); render(); }; li.append(remove); return li;
    }));
  }
  const progress = task?.operation === 'task' ? `${taskLabels()[task.details?.taskAction] || 'Task'}: ${task.state} · ${(task.phase || '').replaceAll('_', ' ')} · ${task.details?.completedRepetitions || 0} repetitions completed${task.state === 'running' && task.details?.waitingReason ? ' · waiting: ' + explain(task.details.waitingReason) : ''}` : '';
  el('task-status').textContent = [progress, message].filter(Boolean).join(' · ');
}
el('task-kind').addEventListener('change', render);
el('task-add-item').addEventListener('click', () => { addTaskItem(); render(); });
el('task-player').addEventListener('change', render);
for (const id of ['assembly-order', 'assembly-bases', 'assembly-components', 'assembly-equipment', 'button-target', 'button-state']) el(id).addEventListener('change', render);
function selectedTaskSpec() {
  const action = el('task-kind').value;
  const args = {};
  if (action === 'press_button') {
    args.target_id = el('button-target').value; args.desired_state = el('button-state').value;
  }
  if (action === 'assemble_dish') {
    args.order_id = el('assembly-order').value;
    args.bases = selectedResources('assembly-bases'); args.components = selectedResources('assembly-components'); args.equipment = selectedResources('assembly-equipment');
  }
  if (action === 'prepare_near_player') {
    args.player_id = Number(el('task-player').value);
    args.items = Array.from(el('task-items').children).map(row => ({ingredient: row.children[0].value, preparation: row.children[1].value, quantity: Number(row.children[2].value)}));
  }
  return {kind: 'native_task', action, capability_version: capability(action).version, arguments: args,
    repeat: el('task-repeat').value === 'forever' ? {mode: 'until_cancelled'} : {mode: 'finite', count: Number(el('task-repeat').value)}};
}
function taskDescription(spec) {
  const repeat = spec.repeat.mode === 'until_cancelled' ? 'until Stop' : `${spec.repeat.count} × ${capability(spec.action)?.repeat.unit.replaceAll('_', ' ') || 'once'}`;
  let details = '';
  if (spec.arguments.items) details = ' · ' + spec.arguments.items.map(i => `${i.quantity} ${i.preparation} ${i.ingredient}`).join(', ') + ` → Player ${spec.arguments.player_id}`;
  if (spec.arguments.order_id) details = ' · ticket ' + spec.arguments.order_id;
  if (spec.arguments.target_id) details = ` · ${spec.arguments.target_id} · ${spec.arguments.desired_state || 'trigger'}`;
  return `${taskLabels()[spec.action] || actionNames[spec.action]} · ${repeat}${details}`;
}
async function submitPlan(edit) {
  if (el(edit === 'append' ? 'task-run' : 'task-now').disabled) return;
  const plan = {schema_version: capabilityRegistry.plan_schema_version, capability_version: capabilityRegistry.version,
    command_id: newId(), expected_queue_revision: draftOrigin?.revision ?? state.queue.revision, queue_edit: edit,
    tasks: draftTasks.length ? [...draftTasks] : [selectedTaskSpec()], after_plan: 'continue_pending', on_failure: 'pause_pending'};
  const context = draftOrigin?.context || state.queue.nativeContext;
  pending = true; lastError = ''; render();
  try {
    state = await post('/api/plan', {plan, context, focus: edit === 'replace_active'});
    receivedAt = performance.now(); draftTasks = []; draftOrigin = null;
  } catch (error) { lastError = error.message; }
  finally { pending = false; render(); }
}
function renderQueue() {
  const queue = state?.queue;
  const enabled = Boolean(token && queue && !pending && !queue.error);
  el('queue-status').textContent = !queue ? 'Waiting for the queue…' : queue.error || (queue.barrier ? 'Stopping native work; dispatch held.' : queue.active?.state === 'reconciling' ? 'Dispatch held: native outcome unknown.' : queue.paused ? 'Dispatch paused.' : 'Dispatch enabled.') +
    ` ${queue.pending.length} / ${queue.capacity} pending.` + (queue.reason ? ' ' + explain(queue.reason) : '');
  el('queue-active').textContent = queue?.active ? `${taskDescription(queue.active.spec)} · ${queue.active.state.replaceAll('_', ' ')}${queue.active.reason ? ' · ' + explain(queue.active.reason) : ''}` : 'No active queued task.';
  el('queue-pause').disabled = !enabled || queue.paused;
  el('queue-resume').disabled = !enabled || !queue.paused || !fresh() || Boolean(queue.barrier) || queue.active?.state === 'reconciling';
  el('queue-clear').disabled = !enabled || !queue.pending.length;
  el('queue-skip').disabled = !enabled || !queue.active || Boolean(queue.barrier) || queue.pending.some(t => t.dependsOn.includes(queue.active?.id));
  const signature = JSON.stringify([queue?.pending, enabled]);
  if (el('queue-pending').dataset.signature !== signature) {
    el('queue-pending').dataset.signature = signature;
    el('queue-pending').replaceChildren(...(queue?.pending || []).map((entry, index, all) => {
      const li = document.createElement('li'); const text = document.createElement('span');
      text.textContent = `${taskDescription(entry.spec)} · ${entry.state.replaceAll('_', ' ')}${entry.reason ? ' · ' + explain(entry.reason) : ''}`;
      li.append(text);
      const button = (label, disabled, action) => { const b = document.createElement('button'); b.type = 'button'; b.textContent = label; b.disabled = !enabled || disabled; b.onclick = action; li.append(b); };
      button('Remove', all.some(t => t.dependsOn.includes(entry.id)), () => queueEdit('remove_pending', {task_id: entry.id}));
      for (const [label, delta] of [['↑', -1], ['↓', 1]]) {
        const reordered = [...all]; const other = index + delta;
        if (other >= 0 && other < all.length) [reordered[index], reordered[other]] = [reordered[other], reordered[index]];
        const positions = new Map(reordered.map((t, i) => [t.id, i]));
        const breaksDependency = reordered.some(t => t.dependsOn.some(dep => positions.has(dep) && positions.get(dep) > positions.get(t.id)));
        const breaksUnlimited = reordered.slice(0, -1).some(t => t.spec.repeat.mode === 'until_cancelled');
        button(label, other < 0 || other >= all.length || breaksDependency || breaksUnlimited, () => queueEdit('reorder_pending', {task_ids: reordered.map(t => t.id)}));
      }
      return li;
    }));
  }
  const last = queue?.outcomes.at(-1);
  el('queue-outcome').textContent = last ? `Last outcome: ${taskDescription(last.spec)} · ${last.state} · ${explain(last.reason)}` : queue?.lastOperation?.message || '';
}
async function queueEdit(operation, fields = {}) {
  if (!state?.queue || pending) return;
  const edit = {operation_id: newId(), expected_queue_revision: state.queue.revision, operation, ...fields};
  pending = true; lastError = ''; render();
  try { state = await post('/api/queue', {edit}); receivedAt = performance.now(); }
  catch (error) { lastError = error.message; }
  finally { pending = false; render(); }
}
el('task-form').addEventListener('submit', event => { event.preventDefault(); submitPlan('append'); });
el('task-now').addEventListener('click', () => submitPlan('replace_active'));
el('task-stage').addEventListener('click', () => { if (!el('task-stage').disabled) { if (!draftTasks.length) draftOrigin = {revision: state.queue.revision, generation: state.queue.commandGeneration, context: state.queue.nativeContext}; draftTasks.push(selectedTaskSpec()); render(); } });
el('plan-clear').addEventListener('click', () => { draftTasks = []; draftOrigin = null; render(); });
el('task-repeat').addEventListener('change', render);
for (const [id, operation] of [['queue-pause', 'pause_pending'], ['queue-resume', 'resume_pending'], ['queue-clear', 'clear_pending'], ['queue-skip', 'skip_active']]) el(id).addEventListener('click', () => queueEdit(operation));
addTaskItem();

let voicePending = '';
let voiceRequest = null;
let voiceFailure = '';
let voiceSettingsSignature = '';
let voiceInventorySignature = '';
let voiceTurn = null;
let voiceHeartbeat = null;
const voiceStatusLabels = {muted: 'Muted · voice / Jev off', muting: 'Muting… waiting for microphone acknowledgment',
  preparing: 'Preparing on-device recognition…', downloading: 'Downloading Apple recognition assets…',
  listening: 'Microphone enabled', typed_ready: 'Typed Jev commands enabled · microphone off', finalizing: 'Finalizing command…', error: 'Voice unavailable'};

let interpreterBusy = false;
let interpreterFailure = '';
let interpreterAnswers = '';
function renderJevUsage(usage) {
  const format = (tokens, unit = '') => {
    if (tokens == null) return '—';
    const cost = (tokens * 0.042 / 1_000_000).toLocaleString('en-US', {
      style: 'currency', currency: 'USD', maximumFractionDigits: 9,
    });
    return `${tokens.toLocaleString('en-US')}${unit} · ${cost}`;
  };
  el('jev-current-usage').textContent = format(usage?.currentInputTokens, ' tokens');
  el('jev-saved-usage').textContent = format(usage?.savedInputTokens, ' tokens');
  const history = usage?.savedRounds || [];
  const list = el('jev-usage-rounds');
  const signature = history.join(',');
  if (list.dataset.counts !== signature) {
    list.dataset.counts = signature;
    list.replaceChildren(...history.slice().reverse().map(tokens => {
      const item = document.createElement('li');
      item.textContent = format(tokens);
      return item;
    }));
  }
  list.hidden = !history.length;
  el('jev-usage-empty').hidden = !!history.length;
  el('jev-usage-empty').textContent = usage?.savedInputTokens == null ? '—' : 'None yet';
  el('jev-usage-error').textContent = usage?.error || '';
  el('jev-usage-error').hidden = !usage?.error;
}

function renderInterpreter() {
  const current = state?.interpreter;
  renderJevUsage(current?.usage);
  const armed = !!current?.configured && state?.voice && !state.voice.muted && !!token;
  const resolvingReply = ['interpreting_reply', 'interpreting_followup'].includes(current?.status);
  const waiting = current?.pending;
  const question = waiting?.kind === 'clarification' ? waiting : null;
  el('interpreter-status').textContent = !current?.configured
    ? current?.configurationError || 'Jev is not configured. Add JEV_API_KEY to .env and restart the harness.'
    : question ? '' : `${current.mode === 'preview' ? 'Preview · acceptance requires your click' : 'Automatic · validated plans enter the queue'} · ${current.status.replaceAll('_', ' ')}${current.inbox ? ` · ${current.inbox} waiting` : ''}`;
  el('interpreter-enable').disabled = !current?.configured || !state?.voice?.muteAcknowledged || interpreterBusy;
  el('interpreter-mode').disabled = !current || interpreterBusy || !!current.pending || ['interpreting', 'interpreting_reply'].includes(current.status);
  if (current) el('interpreter-mode').value = current.mode;
  el('interpreter-submit').disabled = !armed || interpreterBusy || current?.status === 'interpreting_reply' || current?.inbox >= 3;
  el('interpreter-text').disabled = !armed;
  el('interpreter-label').textContent = question ? 'Type a reply' : 'Type a command';
  el('interpreter-submit').textContent = question ? 'Send reply' : 'Send command';
  el('interpreter-text').placeholder = question ? '' : 'Bring me cooked rice';
  const outcome = current?.result;
  const edit = outcome?.plan?.queue_edit || outcome?.edit?.operation;
  let message = outcome?.message || (edit ? `Queue change: ${edit.replaceAll('_', ' ')}` : '');
  if (question) message = '';
  else if (current?.status === 'expired' && message === 'Interpretation expired') {
    message = 'This command expired before it was queued. Say it again or type it below.';
  }
  el('interpreter-message').textContent = message;
  const tasks = outcome?.plan?.tasks || [];
  el('interpreter-plan').replaceChildren(...tasks.map(task => {
    const li = document.createElement('li');
    const repeat = task.repeat.mode === 'until_cancelled' ? 'until cancelled' : `${task.repeat.count} ×`;
    const items = task.arguments.items?.map(item => `${item.quantity} ${item.preparation} ${item.ingredient}`).join(', ');
    const semantic = state.capabilities?.semantic_catalog || state.catalog?.semanticCatalog || {};
    const targets = [...(outcome.movement || []), ...(semantic.targets || []), ...(semantic.players || []), ...(semantic.displayOptions?.items || []), ...(semantic.movement?.stations || []), ...(semantic.movement?.platforms || []), ...(semantic.movement?.points || [])];
    const argumentsText = Object.entries(task.arguments).map(([field, value]) => {
      const target = targets.find(target => target.id === value);
      const label = target ? `${target.label || (target.binding === 'floor' ? 'Floor position' : value)}${target.screen ? ` · (x=${target.screen.x}, y=${target.screen.y})` : ''}${target.height != null ? ` · height=${target.height}` : ''} (${value})` : typeof value === 'object' ? JSON.stringify(value) : String(value).replaceAll('_', ' ');
      return `${field.replaceAll('_', ' ')}: ${label}`;
    }).join('; ');
    const detail = items ? `${items} for Player ${task.arguments.player_id}` : argumentsText;
    li.textContent = `${repeat} ${capability(task.action)?.label || task.action.replaceAll('_', ' ')}${detail ? `: ${detail}` : ''}`;
    return li;
  }));
  if (tasks.length) el('interpreter-message').textContent = `Queue change: ${edit.replaceAll('_', ' ')}${outcome.plan.after_plan === 'pause_pending' ? ' · pause retained work afterwards' : ''}`;
  el('interpreter-clarification').hidden = !question;
  const questionHeading = el('interpreter-question');
  const questionText = question?.question || '';
  if (questionHeading.textContent !== questionText) questionHeading.textContent = questionText;
  el('interpreter-clarification-note').textContent = question
    ? `Command on hold · ${question.remainingSeconds}s to reply` : '';
  const shortLabels = new Map([
    ['prepare_near_player', 'Bring ingredients or utensils'],
    ['native_action', 'Move or interact with something'],
    ['done', 'Nothing else'],
  ]);
  const answers = Object.entries(question?.options || {}).map(([key, description]) => {
    return [key, shortLabels.get(key) || capability(key)?.label || description];
  });
  const signature = JSON.stringify([question?.id, answers, armed, interpreterBusy, resolvingReply]);
  if (signature !== interpreterAnswers) {
    interpreterAnswers = signature;
    el('interpreter-answers').replaceChildren(...answers.map(([key, label]) => {
      const button = document.createElement('button'); button.type = 'button'; button.textContent = label;
      button.disabled = !armed || interpreterBusy || resolvingReply;
      button.addEventListener('click', () => interpreterControl('answer', {id: question.id, key}));
      return button;
    }));
  }
  el('interpreter-accept').hidden = waiting?.kind !== 'preview';
  el('interpreter-accept').disabled = !armed || interpreterBusy || resolvingReply;
  el('interpreter-discard').hidden = !waiting;
  el('interpreter-discard').disabled = !armed || interpreterBusy;
  el('interpreter-error').textContent = interpreterFailure;
  el('interpreter-error').hidden = !interpreterFailure;
}

async function interpreterControl(operation, fields = {}) {
  interpreterBusy = true; interpreterFailure = ''; renderInterpreter();
  try {
    state = withCurrentVoice(await post('/api/interpreter', {interpreter: {
      operation, generation: state?.voice?.generation, ...fields}}));
    receivedAt = performance.now();
    if (operation === 'submit') el('interpreter-text').value = '';
  } catch (error) { interpreterFailure = error.message; }
  finally { interpreterBusy = false; renderInterpreter(); }
}

function renderVoice() {
  const voice = state?.voice;
  const known = !!voice && !!token;
  renderInterpreter();
  el('voice-status').textContent = voicePending === 'mute' ? 'Muting… waiting for server acknowledgment'
    : !known ? 'Voice status unknown · waiting for the harness'
    : (voiceStatusLabels[voice.status] || voice.status) + (voice.drafts?.some(d => d.finalizing) ? ' · finalizing a turn' : '');
  const error = voiceFailure || voice?.error || '';
  el('voice-error').textContent = error; el('voice-error').hidden = !error;
  el('voice-arm').disabled = !known || !voice.muted || !voice.muteAcknowledged || !!voicePending;
  // Mute remains independent of any pending model, capture or game request.
  el('voice-mute').disabled = !token || (voice?.muted && voice?.muteAcknowledged && !voicePending);
  el('voice-talk').disabled = !known || voice.muted || voice.status !== 'listening' || voice.settings.mode !== 'push_to_talk' || (!!voice.pttActive && !voiceTurn);
  el('voice-talk').textContent = voiceTurn ? 'Listening… release to submit' : voice?.pttActive ? 'Listening…' : 'Hold to talk';
  el('voice-talk').setAttribute('aria-pressed', String(!!voiceTurn));
  const gameKey = state?.gamePushToTalkKey;
  el('voice-game-key').hidden = typeof gameKey !== 'string';
  el('voice-game-key').textContent = gameKey ? `In-game push to talk: ${gameKey}`
    : 'Set a push-to-talk key in the game’s Controls menu.';
  for (const input of el('voice-settings').elements) input.disabled = !known || !voice.muted || !voice.muteAcknowledged || !!voicePending;
  el('voice-clear').disabled = !known;
  el('voice-transcript').textContent = voice?.drafts?.map(d => d.text).filter(Boolean).join(' / ') || voice?.commands?.at(-1)?.text || '';
  el('voice-fake').textContent = voice?.fakeResult?.message || '';
  el('voice-input').textContent = voice?.inputDevice ? `${voice.inputDevice}${Number.isFinite(voice.audioLevel?.rms) ? ` · input energy ${voice.audioLevel.rms.toFixed(3)}` : ''}` : '';
  el('voice-inspection-data').textContent = voice ? JSON.stringify({transport: voice.transport,
    metrics: state?.interpreter?.metrics, drafts: voice.drafts, commands: voice.commands}, null, 2) : 'Waiting for the harness.';
  if (!voice) return;
  const inventory = JSON.stringify([voice.devices, voice.locales]);
  if (inventory !== voiceInventorySignature) {
    voiceInventorySignature = inventory;
    const selected = el('voice-device').value || voice.settings.deviceId;
    const choices = [{id: '', name: 'System default microphone'}, ...voice.devices];
    if (selected && !choices.some(d => d.id === selected)) choices.push({id: selected, name: 'Selected device (disconnected)'});
    el('voice-device').replaceChildren(...choices.map(d => { const o = document.createElement('option'); o.value = d.id; o.textContent = d.name; return o; }));
    el('voice-device').value = selected;
    el('voice-locales').replaceChildren(...voice.locales.map(locale => { const o = document.createElement('option'); o.value = locale; return o; }));
  }
  const signature = JSON.stringify(voice.settings);
  if (signature !== voiceSettingsSignature) {
    voiceSettingsSignature = signature;
    for (const [id, key] of [['device', 'deviceId'], ['locale', 'locale'], ['mode', 'mode'], ['pause', 'pauseMs'],
      ['energy', 'energyThreshold'], ['fast', 'fastResults'], ['inspection', 'inspection']]) el(`voice-${id}`).value = String(voice.settings[key]);
    el('voice-duration').value = voice.settings.maxCommandMs / 1000;
  }
}

async function voiceControl(operation, fields = {}) {
  const requestId = newId();
  voiceRequest = requestId;
  voicePending = operation; voiceFailure = ''; renderVoice();
  try {
    const result = await post('/api/voice', {voice: {operation, generation: state?.voice?.generation, ...fields}});
    // Older HTTP responses cannot roll back a more recent mute generation.
    state = withCurrentVoice(result);
    receivedAt = performance.now();
    return result;
  } catch (error) { voiceFailure = error.message; throw error; }
  finally { if (voiceRequest === requestId) voicePending = ''; renderVoice(); }
}

function beginVoice() {
  if (voiceTurn || el('voice-talk').disabled) return;
  const turn = {id: newId(), generation: state.voice.generation};
  voiceTurn = turn;
  turn.started = voiceControl('begin', {turnId: turn.id, generation: turn.generation});
  turn.started.then(() => {
    if (voiceTurn !== turn) return;
    voiceHeartbeat = setInterval(() => {
      post('/api/voice', {voice: {operation: 'heartbeat', generation: turn.generation, turnId: turn.id}})
        .catch(() => releaseVoice(true));
    }, 750);
  }).catch(() => { if (voiceTurn === turn) { voiceTurn = null; renderVoice(); } });
  renderVoice();
}
function releaseVoice(cancel = false) {
  const turn = voiceTurn;
  if (!turn) return;
  voiceTurn = null;
  clearInterval(voiceHeartbeat); voiceHeartbeat = null;
  // Release can beat the begin response on a phone. Preserve that ordering.
  turn.started.then(() => voiceControl(cancel ? 'cancel' : 'finalize',
    {turnId: turn.id, generation: turn.generation})).catch(() => {});
  renderVoice();
}
for (const [id, operation] of [['voice-arm', 'arm'], ['voice-clear', 'clear'], ['voice-devices', 'devices']]) {
  el(id).addEventListener('click', () => { voiceControl(operation).catch(() => {}); });
}
el('voice-mute').addEventListener('click', () => {
  releaseVoice(true);
  voiceControl('mute').catch(() => {});
});
el('voice-talk').addEventListener('pointerdown', event => {
  if (event.button !== 0) return;
  event.preventDefault();
  el('voice-talk').setPointerCapture(event.pointerId);
  beginVoice();
});
el('voice-talk').addEventListener('pointerup', () => releaseVoice());
el('voice-talk').addEventListener('pointercancel', () => releaseVoice(true));
el('voice-talk').addEventListener('lostpointercapture', () => releaseVoice(true));
el('voice-talk').addEventListener('keydown', event => {
  if ([' ', 'Enter'].includes(event.key)) { event.preventDefault(); if (!event.repeat) beginVoice(); }
});
el('voice-talk').addEventListener('keyup', event => {
  if ([' ', 'Enter'].includes(event.key)) { event.preventDefault(); releaseVoice(); }
});
window.addEventListener('blur', () => releaseVoice(true));
document.addEventListener('visibilitychange', () => { if (document.hidden) releaseVoice(true); });
window.addEventListener('pagehide', () => {
  if (!voiceTurn || !token) return;
  navigator.sendBeacon('/api/voice', new Blob([JSON.stringify({token, viewerId,
    voice: {operation: 'cancel', turnId: voiceTurn.id, generation: voiceTurn.generation}})], {type: 'application/json'}));
});
el('voice-settings').addEventListener('submit', event => {
  event.preventDefault();
  voiceControl('configure', {settings: {deviceId: el('voice-device').value, locale: el('voice-locale').value,
    mode: el('voice-mode').value, pauseMs: Number(el('voice-pause').value), maxCommandMs: Number(el('voice-duration').value) * 1000,
    fastResults: el('voice-fast').value === 'true', energyThreshold: Number(el('voice-energy').value),
    inspection: el('voice-inspection').value}}).catch(() => {});
});
el('interpreter-enable').addEventListener('click', () => voiceControl('arm_text').catch(() => {}));
el('interpreter-mode').addEventListener('change', () => interpreterControl('mode', {mode: el('interpreter-mode').value}));
el('interpreter-form').addEventListener('submit', event => {
  event.preventDefault();
  interpreterControl('submit', {command_id: newId(), text: el('interpreter-text').value});
});
el('interpreter-accept').addEventListener('click', () => interpreterControl('accept', {id: state?.interpreter?.pending?.id}));
el('interpreter-discard').addEventListener('click', () => interpreterControl('discard', {id: state?.interpreter?.pending?.id}));
