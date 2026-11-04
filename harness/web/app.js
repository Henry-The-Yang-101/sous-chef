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
let state = null;
let receivedAt = 0;
let pending = false;
let lastError = '';
let sessionId = null;
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
  paused: 'Unpause the round to see available destinations.',
  input_suppressed: 'The game is blocking input. Return focus to the unpaused kitchen.',
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
};
const explain = reason => reasons[reason] || (reason ? reason.replaceAll('_', ' ') : '');
const kindName = kind => (kind || 'destination').replaceAll('_', ' ');
const number = value => Number.isFinite(value) ? value.toFixed(1) : '—';
const contentsOf = d => d.contents?.summary || '';
const actionNames = {navigate_to: 'Navigate', pick_up: 'Pick up', place_at: 'Place held item', add_ingredient: 'Add held ingredient',
  collect_contents: 'Collect ready contents', work_at: 'Work', activate: 'Activate', discard: 'Discard held item', wait_until: 'Wait', throw_to: 'Throw held item', throw_to_player: 'Pass held item',
  move_item: 'Move item', fetch_ingredient: 'Fetch ingredient', get_plate: 'Get clean plate', get_utensil: 'Get empty utensil', stage_item: 'Stage item', return_utensil: 'Return utensil', clear_station: 'Clear station'};
const phaseNames = {picking_up: 'collecting', drop_press: 'placing nearby', dropping: 'checking nearby handoff', throw_hold: 'aiming', flight: 'checking arrival'};
const throwIdentity = target => JSON.stringify([state?.sessionId, target.id, target.objectInstanceId]);
function throwTargets(catalog, operation) {
  return (operation === 'throw_to_player' ? catalog.players || [] : catalog.targets || [])
    .filter(target => target.operations?.includes(operation));
}
function renderThrows(catalog, busy, live) {
  const control = state?.control || {};
  const operation = el('throw-kind').value;
  const toPlayer = operation === 'throw_to_player';
  const targets = throwTargets(catalog, operation);
  const inputBlocked = !control.available && control.reason !== 'game_unfocused';
  const blocked = !live || pending || busy || inputBlocked;
  el('throw-target-name').textContent = toPlayer ? 'Player' : 'Place';
  el('throw-run').textContent = toPlayer ? 'Throw to player' : 'Throw to place';
  setJobOptions(el('throw-target'), targets.map(target => ({value: throwIdentity(target),
    label: toPlayer ? nameOf(target) : `${nameOf(target)} · x ${number(target.position?.[0])}, z ${number(target.position?.[2])}, height ${number(target.position?.[1])}`})));
  el('throw-kind').disabled = blocked;
  el('throw-target').disabled = blocked || !targets.length;
  el('throw-run').disabled = blocked || !targets.some(target => throwIdentity(target) === el('throw-target').value);
  const previews = [...(catalog.targets || []), ...(catalog.players || [])].map(target => target.throw);
  let message = '';
  if (!live) message = 'Waiting for fresh game state before throwing.';
  else if (pending) message = 'Sending the selected action…';
  else if (busy) message = 'Player 2 is running an action. Wait for it to finish or press Stop.';
  else if (inputBlocked) message = explain(control.reason) || 'The game is not accepting Player 2 actions.';
  else if (previews.some(preview => preview?.reason === 'hands_empty')) message = 'Pick up an ingredient with Player 2 to see throw targets.';
  else if (catalog.throwOptionsComplete === false) message = 'Checking throw paths. Safe targets appear as they are verified.';
  else if (!targets.length) message = toPlayer ? 'No player can receive this item from a reachable release position. Try a place or change position.'
    : 'No place can receive this item from a reachable release position. Pick up a throwable ingredient or change position.';
  else if (!el('throw-target').value) message = toPlayer ? 'Choose a player to receive the held item.' : 'Choose a place to receive the held item.';
  el('throw-status').textContent = message;
  el('throw-status').hidden = !message;
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
function renderJobs(catalog, busy, live) {
  const control = state?.control || {};
  const inputBlocked = !control.available && control.reason !== 'game_unfocused';
  const jobOptions = catalog.jobOptions || {};
  const jobs = Object.keys(jobOptions).filter(name => jobOptions[name].length);
  setJobOptions(el('job-kind'), jobs.map(name => ({value: name, label: actionNames[name]})));
  const kind = el('job-kind').value;
  const options = jobOptions[kind] || [];
  const sourceIds = new Set(options.map(option => option.sourceId));
  const unsupported = live && !busy && !inputBlocked && !catalog.jobOptions;
  const blocked = !live || pending || busy || unsupported || inputBlocked || !jobs.includes(kind);
  el('job-kind').disabled = !live || pending || busy || !jobs.length;
  const items = catalog.items || [];
  let sources = items.map(item => ({value: item.id, label: nameOf(item)}));
  el('job-source-name').textContent = kind === 'fetch_ingredient' ? 'Ingredient' : kind === 'get_utensil' ? 'Utensil type' : kind === 'clear_station' ? 'Station' : 'Item';
  if (kind === 'fetch_ingredient') sources = (catalog.ingredients || []).map(item => ({value: item.id, label: item.label.replaceAll('_', ' ')}));
  if (kind === 'get_utensil') sources = (catalog.utensilTypes || []).map(type => ({value: type, label: type.replaceAll('_', ' ')}));
  if (kind === 'clear_station') sources = (catalog.targets || []).map(item => ({value: item.id, label: nameOf(item)}));
  sources = sources.filter(source => sourceIds.has(source.value));
  setJobOptions(el('job-source'), sources);
  const source = kind === 'get_plate' ? '' : el('job-source').value;
  const destinationIds = new Set(options.find(option => option.sourceId === source)?.destinationIds || []);
  const destinations = (catalog.deliveryDestinations || []).filter(item => destinationIds.has(item.id)).map(item => ({value: item.id, label: nameOf(item)}));
  setJobOptions(el('job-destination'), destinations);
  el('job-source-label').hidden = kind === 'get_plate';
  el('job-destination-label').hidden = kind === 'return_utensil';
  el('job-source').disabled = blocked || !sources.length; el('job-destination').disabled = blocked || !destinations.length;
  el('job-run').disabled = blocked || (kind !== 'get_plate' && !el('job-source').value) || (kind !== 'return_utensil' && !el('job-destination').value);
  let message = '';
  if (!live) message = 'Waiting for fresh bridge state before jobs can run.';
  else if (unsupported) message = `The running bridge${state.bridgeVersion ? ` (${state.bridgeVersion})` : ''} needs current job options. Quit Overcooked! 2, run python3 scripts/install.py, then relaunch with python3 scripts/launch.py and refresh this page.`;
  else if (pending) message = 'Sending the selected job…';
  else if (busy) message = 'Player 2 is running an action. Wait for it to finish or press Stop to choose another job.';
  else if (inputBlocked) message = explain(control.reason) || 'The game is not accepting Player 2 actions.';
  else if (!jobs.length) message = explain(catalog.reason || catalog.navigation?.catalogReason) || 'No supply jobs are currently possible. Options update as the kitchen changes.';
  else if (!kind) message = 'Choose an available job.';
  else if (el('job-run').disabled) message = 'Choose the required fields to run this job.';
  el('job-status').textContent = message;
  el('job-status').hidden = !message;
}
function actionChoices(d) {
  const choices = d.walkReachable ? [{value: 'navigate_to', label: 'Navigate'}] : [];
  for (const operation of d.operations || []) {
    if (operation === 'work_at' || operation === 'wait_until') {
      for (const condition of d[operation === 'work_at' ? 'workConditions' : 'waitConditions'] || []) {
        const label = condition.replaceAll('_', ' ');
        choices.push({value: `${operation}|${condition}`, label: `${actionNames[operation]}: ${label}`});
      }
    } else if (operation === 'activate') {
      for (const desired of d.activationStates || ['trigger']) choices.push({value: `activate|${desired}`, label: `Activate: ${desired}`});
    } else choices.push({value: operation, label: actionNames[operation] || operation.replaceAll('_', ' ')});
  }
  return choices;
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
function renderMap(catalog, destinations, busy, live) {
  if (document.hidden) return;
  renderFps(state, live && performance.now() - receivedAt + (state?.gamePerformance?.ageMs || 0) <= 1500);
  const mode = el('map-mode').value;
  const control = state?.control || {};
  const blocked = !live || pending || busy || (!control.available && control.reason !== 'game_unfocused');
  let choices = destinations.filter(d => actionChoices(d).length), selected = null;
  let description = 'Choose an available target on the map.';
  if (mapSelection && (!live || mapSelection.session !== state.sessionId || mapSelection.epoch !== state.kitchenMap?.levelEpoch ||
      !destinations.some(d => d.id === mapSelection.id && d.objectInstanceId === mapSelection.objectInstanceId))) mapSelection = null;
  if (mode === 'throw') {
    choices = throwTargets(catalog, el('throw-kind').value);
    selected = choices.find(d => throwIdentity(d) === el('throw-target').value);
    description = selected ? `Throw to ${nameOf(selected)}. Use the throw button below to send it.` : el('throw-status').textContent || 'Choose a throw recipient on the map.';
  } else if (mode === 'job') {
    const ids = new Set(Array.from(el('job-destination').options).map(option => option.value).filter(Boolean));
    choices = (catalog.deliveryDestinations || []).filter(d => ids.has(d.id));
    selected = choices.find(d => d.id === el('job-destination').value);
    description = el('job-kind').value === 'return_utensil' ? 'Return utensil uses its observed home. Choose the utensil below.'
      : selected ? `Deliver to ${nameOf(selected)}. Use Run job below to start.` : 'Choose a job and source below, then select an available delivery target here.';
    if (el('job-kind').value === 'return_utensil') choices = [];
  } else {
    selected = choices.find(d => d.id === mapSelection?.id && d.objectInstanceId === mapSelection?.objectInstanceId);
    description = selected ? `${nameOf(selected)} · height ${number(selected.position?.[1])}${contentsOf(selected) ? ` · ${contentsOf(selected)}` : ''}` : description;
  }
  // Include every observed target as context, but enable only the exact current
  // operation/source-compatible catalogue. Map clicks never issue commands.
  const all = new Map([...destinations, ...(catalog.deliveryDestinations || []), ...choices].map(d => [d.id, d]));
  const map = state?.kitchenMap;
  const mapLive = live && map && performance.now() - receivedAt + (map.ageMs || 0) <= 1500;
  mapPicker.render(mapLive ? map : null, Array.from(all.values()), blocked ? [] : choices, selected?.id, live);
  el('map-action-controls').hidden = mode !== 'action';
  setJobOptions(el('map-action'), selected ? actionChoices(selected) : []);
  if (selected && !el('map-action').value) el('map-action').selectedIndex = 1;
  el('map-action').disabled = blocked || !selected || !mapLive;
  el('map-run').disabled = blocked || !selected || !el('map-action').value || !mapLive;
  el('map-selection').textContent = !mapLive ? 'Waiting for a fresh kitchen map.' : busy ? 'Player 2 is running an action. Press Stop to choose another.' : description;
}
function render() {
  const live = fresh();
  const control = state?.control || {};
  const nav = control.navigation || {};
  const receipt = control.action || {};
  const moving = live && nav.state === 'navigating';
  const acting = live && receipt.state === 'running';
  const busy = moving || acting;
  const catalog = live ? state.catalog || {} : {};
  renderThrows(catalog, busy, live);
  renderJobs(catalog, busy, live);
  renderTasks(catalog, busy, live);
  const byId = new Map((catalog.destinations || []).map(d => [d.id, {...d, walkReachable: true}]));
  for (const d of [...(catalog.targets || []), ...(catalog.players || [])]) byId.set(d.id, {...byId.get(d.id), ...d});
  const destinations = Array.from(byId.values());
  const catalogReason = catalog.reason || catalog.navigation?.catalogReason || control.reason;
  const useReceipt = receipt.operation && (acting || (!moving && state?.lastCommand?.operation !== 'navigate_to'));
  const recorded = useReceipt ? (destinations.find(d => d.id === receipt.targetId || (receipt.operation === 'throw_to_player' && String(d.playerId) === receipt.targetId)) || state?.lastCommand?.target)
    : nav.destination || (nav.destinationId && state?.lastCommand?.target);
  const active = destinations.find(d => d.id === recorded?.id) || recorded;
  el('connection').textContent = live ? 'Bridge connected' : state?.connected ? 'State stale' : 'Bridge disconnected';
  el('light').className = 'light ' + (live ? 'live' : 'offline');
  el('freshness').textContent = live ? `Updated ${Math.max(0, Math.round((state.ageMs || 0) + performance.now() - receivedAt))} ms ago` : 'Waiting for a fresh sample';
  el('action').textContent = acting ? `${actionNames[receipt.operation] || receipt.operation} · ${phaseNames[receipt.phase] || receipt.phase.replaceAll('_', ' ')}` : moving ? `${nav.phase === 'aligning' ? 'Aligning with' : 'Walking to'} ${active ? nameOf(active) : nav.destinationId}` : live ? 'Player 2 is idle' : 'Waiting for the bridge';
  el('target').textContent = active
    ? `${active.label}${contentsOf(active) ? ` · Contents: ${contentsOf(active)}` : ''} · ${active.id} · object ${active.objectInstanceId} · x ${number(active.position?.[0])}, z ${number(active.position?.[2])}${Number.isFinite(active.routeDistance) ? ` · ${number(active.routeDistance)} units to walk` : ''}`
    : 'Join Player 2 with Split Controller, then enter an unpaused Arcade → Couch kitchen.';
  const ready = nav.arrival;
  const possible = ready ? [['canPickup', 'pickup'], ['canPlace', 'place'], ['canUse', 'use']].filter(([key]) => ready[key]).map(([,name]) => name).join(', ') : '';
  el('result').textContent = useReceipt ? `${receipt.state}: ${explain(receipt.reason) || 'Checking the requested outcome.'}` : moving ? 'The plugin is navigating and checking final alignment. Stop interrupts it.'
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
    row.select.disabled = pending || busy || !live || !choices.length;
    row.button.textContent = busy && active?.id === destination.id ? 'Running…' : 'Run';
    row.button.setAttribute('aria-label', `${choices.find(c => c.value === row.select.value)?.label || 'Run'}: ${nameOf(destination)}`);
    row.button.disabled = pending || busy || !choices.length || (!control.available && control.reason !== 'game_unfocused') || !live;
    row.tr.classList.toggle('active', busy && active?.id === destination.id);
    const atIndex = el('rows').children[index];
    if (atIndex !== row.tr) el('rows').insertBefore(row.tr, atIndex || null);
  }
  renderMap(catalog, destinations, busy, live);
  el('details').textContent = JSON.stringify({sessionId: state?.sessionId, bridgeVersion: state?.bridgeVersion, supportedActions: state?.supportedActions, pad: control.pad, side: control.side, navigation: nav, action: receipt, error: state?.error}, null, 2);
}

async function post(path, data = {}) {
  const response = await fetch(path, {method: 'POST', headers: {'Content-Type': 'application/json'},
    body: JSON.stringify({...data, token, viewerId}), signal: AbortSignal.timeout(6000)});
  const result = await response.json();
  if (!response.ok) throw new Error(result.error || 'Command failed');
  return result;
}
async function perform(d, choice) {
  if (pending || !fresh()) return;
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
  const target = throwTargets(state.catalog || {}, operation).find(target => throwIdentity(target) === el('throw-target').value);
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
      token = (await response.json()).token;
    }
    const response = await fetch('/api/state', {headers: {...authHeaders(), 'X-Viewer-Id': viewerId}, signal: AbortSignal.timeout(2000)});
    if (!response.ok) throw new Error('Could not read live state');
    state = await response.json(); receivedAt = performance.now();
  } catch (error) {
    state = {connected: false, fresh: false, error: error.message.startsWith('Scan ') ? error.message : 'Harness connection lost. Reconnecting…'};
    token = null;
  }
  render();
  setTimeout(poll, 250);
}
let shareSignature = '';
let shareLinks = [];
let qrObjectUrl = null;
function showPhoneLink() {
  const link = shareLinks[Number(el('phone-network').value) || 0];
  el('phone-qr').hidden = !link;
  el('phone-link').hidden = !link;
  if (qrObjectUrl) { URL.revokeObjectURL(qrObjectUrl); qrObjectUrl = null; }
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
      const previous = shareLinks[Number(el('phone-network').value) || 0]?.url;
      shareSignature = signature; shareLinks = sharing.links;
      el('phone-network').replaceChildren(...shareLinks.map((link, index) => {
        const option = document.createElement('option');
        option.value = index; option.textContent = `${link.label} · ${new URL(link.url).host}`;
        return option;
      }));
      const selected = shareLinks.findIndex(link => link.url === previous);
      el('phone-network').value = Math.max(0, selected);
      el('phone-network-label').hidden = shareLinks.length < 2;
      el('phone-status').textContent = sharing.status || (shareLinks.length
        ? 'Connect your phone to the same Wi-Fi, then scan. Keep the game focused on your Mac.'
        : 'No Wi-Fi address found. Connect your Mac to a network, or restart with --tunnel for an internet link.');
      showPhoneLink();
    }
  } catch {
    shareSignature = ''; shareLinks = []; showPhoneLink();
    el('phone-network-label').hidden = true;
    el('phone-status').textContent = 'Open this page on the Mac to get the current phone QR code.';
  }
  setTimeout(pollSharing, 3000);
}
el('phone-network').addEventListener('change', showPhoneLink);
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

const taskLabels = {prepare_near_player: 'Prepare and give to player', take_ready: 'Take ready food off heat',
  wash_dishes: 'Wash dirty dishes', deliver_dirty_dishes: 'Deliver dirty dishes', put_out_fire: 'Put out fires',
  toss_burnt_food: 'Toss burnt food', monitor_equipment: 'Monitor cooking and mixing'};
function addTaskItem() {
  if (el('task-items').children.length >= 10) return;
  const row = document.createElement('div'); row.className = 'task-item';
  const ingredient = document.createElement('input'); ingredient.placeholder = 'Ingredient or utensil'; ingredient.required = true; ingredient.maxLength = 64;
  ingredient.setAttribute('aria-label', 'Ingredient or utensil');
  const preparation = document.createElement('select'); preparation.setAttribute('aria-label', 'Preparation');
  for (const kind of ['raw', 'chopped', 'cooked', 'mixed', 'empty']) preparation.add(new Option(kind === 'empty' ? 'Empty utensil / plate' : kind, kind));
  const quantity = document.createElement('input'); quantity.type = 'number'; quantity.min = '1'; quantity.max = '10'; quantity.value = '1'; quantity.required = true; quantity.setAttribute('aria-label', 'Quantity');
  const remove = document.createElement('button'); remove.type = 'button'; remove.textContent = 'Remove'; remove.onclick = () => row.remove();
  row.append(ingredient, preparation, quantity, remove); el('task-items').append(row);
}
function renderTasks(catalog, busy, live) {
  const choices = catalog.taskOptions || [];
  setJobOptions(el('task-kind'), choices.map(t => ({value: t.name, label: taskLabels[t.name] || t.name})));
  el('task-preparation').hidden = el('task-kind').value !== 'prepare_near_player';
  const blocked = !live || pending || busy || (!state.control?.available && state.control?.reason !== 'game_unfocused');
  el('task-run').disabled = blocked || !choices.length;
  for (const field of el('task-form').querySelectorAll('input, select, button')) if (field.id !== 'task-run') field.disabled = blocked;
  const task = state.control?.action;
  el('task-status').textContent = task?.operation === 'task' ? `${taskLabels[task.details?.taskAction] || 'Task'}: ${task.state} · ${task.phase.replaceAll('_', ' ')} · ${task.details?.completedRepetitions || 0} repetitions completed${task.details?.waitingReason ? ' · waiting: ' + explain(task.details.waitingReason) : ''}`
    : !choices.length ? 'No tasks currently available. Options update with the kitchen.' : '';
}
el('task-kind').addEventListener('change', render);
el('task-add-item').addEventListener('click', addTaskItem);
el('task-form').addEventListener('submit', async event => {
  event.preventDefault(); if (el('task-run').disabled) return;
  const action = el('task-kind').value;
  const args = {action, repetitions: el('task-repeat').value === 'forever' ? null : Number(el('task-repeat').value)};
  if (action === 'prepare_near_player') {
    args.player_id = Number(el('task-player').value);
    args.items = Array.from(el('task-items').children).map(row => ({ingredient: row.children[0].value, preparation: row.children[1].value, quantity: Number(row.children[2].value)}));
  }
  pending = true; lastError = ''; render();
  try { state = await post('/api/task', {sessionId: state.sessionId, arguments: args}); receivedAt = performance.now(); }
  catch (error) { lastError = error.message; }
  finally { pending = false; render(); }
});
addTaskItem();
