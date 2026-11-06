// Exercise the production render/eligibility functions with small form controls.
// No browser, third-party DOM package, game process, or network is needed.
const {test} = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const source = fs.readFileSync(path.join(__dirname, '../harness/web/app.js'), 'utf8');
const helpers = source.slice(source.indexOf('const capabilityContracts ='), source.indexOf('function nameOf('));
const labels = source.slice(source.indexOf('const taskLabels ='), source.indexOf('function addTaskItem('));
const tasks = source.slice(source.indexOf('function selectedResources('), source.indexOf("el('task-kind').addEventListener"));

class Select {
  constructor(values = []) { this.dataset = {}; this.disabled = false; this.replaceChildren(...values.map(value => ({value, textContent: value}))); }
  get selectedOptions() { return this.multiple ? this.options.filter(o => o.selected) : this.selected ? [this.selected] : []; }
  get value() { return this.selected?.value || ''; }
  set value(value) { this.selected = this.options.find(option => option.value === value); }
  replaceChildren(...options) { this.options = options; this.selected = options[0]; }
}
function fixture() {
  const elements = new Map();
  const el = id => {
    if (!elements.has(id)) elements.set(id, new Select());
    return elements.get(id);
  };
  const row = {children: [new Select(), new Select(['raw', 'empty']), {checkValidity: () => true}]};
  el('task-items').children = [row];
  for (const id of ['assembly-bases', 'assembly-components', 'assembly-equipment']) el(id).multiple = true;
  el('task-form').querySelectorAll = () => ['task-kind', 'task-player', 'task-run', 'task-add-item'].map(el);
  el('task-preparation').querySelectorAll = () => row.children;
  el('task-assembly').querySelectorAll = () => [];
  el('task-player').replaceChildren({value: '1'}, {value: '3'});
  el('throw-kind').replaceChildren({value: 'throw_to_player'}, {value: 'throw_to'});
  const context = vm.createContext({
    capabilityRegistry: JSON.parse(fs.readFileSync(path.join(__dirname, '../clients/capabilities.json'), 'utf8')),
    el, document: {createElement: () => ({})}, draftTasks: [], pending: false, displayedCatalog: {}, displayScope: null,
    state: {connected: true, fresh: true, sessionId: 'session', control: {available: true}, capabilities: {compatible: true}, queue: {revision: 1, nativeContext: {}, pending: [], capacity: 10}, kitchenMap: {levelEpoch: 1}},
    nameOf: target => target.label, number: value => String(value), explain: reason => reason,
    fresh: () => context.state.fresh,
  });
  vm.runInContext(helpers + labels + tasks + source.slice(source.indexOf('function selectedTaskSpec('), source.indexOf('async function submitPlan(')), context);
  return {context, el, row};
}
const preparation = [{id: 'tomato', label: 'Tomato', utensil: false}, {id: 'pot', label: 'pot', utensil: true}];
const visible = {displayOptions: {
  preparationOptions: preparation, ingredients: [{id: 'tomato', label: 'Tomato'}],
  items: [{id: 'pot1', label: 'Pot', utensilType: 'pot'}], utensilTypes: ['pot'],
  deliveryDestinations: [{id: 'counter', label: 'Counter'}, {id: 'board', label: 'Board'}],
}};

test('assembly keeps allocation explicit, preserves choices and rejects overlaps', () => {
  const {context: c, el} = fixture();
  const catalog = {taskOptions: [{name: 'assemble_dish'}], assemblyOrders: [{id: 'flow:One:7', recipe: 'Sushi_Fish'}]};
  const kitchen = {targets: [{id: 'burner', label: 'Burner', processingEquipment: true}], displayOptions: {items: [
    {id: 'plate', label: 'Plate', plate: true}, {id: 'fish', label: 'Fish'},
    {id: 'pot', label: 'Pot', utensilType: 'pot'}, {id: 'supply', label: 'Fish dispenser', supply: true},
  ]}};
  c.renderTasks(catalog, kitchen, false, true); el('task-kind').value = 'assemble_dish';
  c.renderTasks(catalog, kitchen, false, true);
  assert.equal(el('task-repeat').disabled, true);
  assert.equal(el('task-run').disabled, true);
  assert.equal(el('assembly-bases').selectedOptions.length, 0);
  assert.deepEqual(el('assembly-equipment').options.map(o => o.value), ['pot', 'burner']);
  assert.ok(!el('assembly-components').options.some(o => o.value === 'supply'));
  el('assembly-order').value = 'flow:One:7';
  el('assembly-bases').options[0].selected = true;
  el('assembly-components').options.find(o => o.value === 'fish').selected = true;
  c.renderTasks(catalog, kitchen, false, true);
  assert.equal(el('task-run').disabled, false);
  assert.deepEqual(Array.from(c.selectedResources('assembly-components')), ['fish']);
  el('assembly-components').options.find(o => o.value === 'plate').selected = true;
  c.renderTasks(catalog, kitchen, false, true);
  assert.equal(el('task-run').disabled, true);
  el('assembly-components').options.find(o => o.value === 'plate').selected = false;
  c.renderTasks({...catalog, assemblyOrders: []}, kitchen, false, true);
  assert.equal(el('task-run').disabled, true);
});

test('all tasks and jobs remain selectable while unavailable, pending or busy', () => {
  const {context: c, el} = fixture();
  c.renderTasks({}, visible, true, true); c.renderJobs({}, visible, true, true);
  assert.equal(el('task-kind').options.length, 10); assert.equal(el('job-kind').options.length, 8);
  assert.equal(el('task-kind').disabled, false); assert.equal(el('job-kind').disabled, false);
  assert.equal(el('task-run').disabled, true); assert.equal(el('job-run').disabled, true);
  el('task-kind').value = 'wash_dishes';
  c.renderTasks({taskOptions: [{name: 'wash_dishes'}]}, visible, false, true);
  assert.equal(el('task-run').disabled, false);
  c.renderTasks({}, visible, false, true);
  assert.equal(el('task-run').disabled, false); assert.equal(el('task-kind').value, 'wash_dishes');
  c.pending = true; c.renderTasks({taskOptions: [{name: 'wash_dishes'}]}, visible, false, true);
  assert.equal(el('task-run').disabled, true); assert.equal(el('task-kind').disabled, false);
  c.pending = false; c.state.control = {available: false, reason: 'paused'};
  c.renderTasks({taskOptions: [{name: 'wash_dishes'}]}, visible, false, true);
  assert.equal(el('task-run').disabled, false); assert.equal(el('task-kind').value, 'wash_dishes');
});

test('task labels, repetition limits and future operations come from shared registry', () => {
  const {context: c, el} = fixture();
  c.renderTasks({}, visible, true, true);
  const expected = c.capabilityRegistry.contracts.filter(spec => spec.kind === 'task').map(spec => spec.name);
  assert.deepEqual(el('task-kind').options.map(option => option.value).filter(Boolean), expected);
  el('task-kind').value = 'assemble_dish'; c.renderTasks({}, visible, true, true);
  assert.deepEqual(el('task-repeat').options.map(option => option.value), ['', '1']);
  el('task-kind').value = 'wash_dishes'; c.renderTasks({}, visible, true, true);
  assert.equal(el('task-repeat').options.at(-1).value, 'forever');
  assert.equal(el('task-repeat').title, 'Each repetition: washing batch');
  assert.equal(c.actionChoices({possibleOperations: ['transfer_contents', 'move_to_region']}).length, 0);
});

test('semantic choices survive suspension while required allocations remain explicit', () => {
  const {context: c, el} = fixture();
  c.state.control = {available: false, reason: 'paused'};
  const catalog = {semanticCatalog: {targets: [], assemblyOrders: [{id: 'current:7', recipe: 'Sushi'}],
    displayOptions: {items: [], preparationOptions: []}}};
  const display = c.displayChoices(catalog);
  c.renderTasks(catalog, display, false, true); el('task-kind').value = 'assemble_dish';
  c.renderTasks(catalog, display, false, true);
  assert.ok(el('assembly-order').options.some(option => option.value === 'current:7'));
  assert.equal(el('task-run').disabled, true);
  c.state.connected = false; c.state.fresh = false;
  assert.deepEqual(Object.keys(c.displayChoices({})), []);
});

test('preparation requires level choices, valid quantities and an observed recipient', () => {
  const {context: c, el, row} = fixture();
  const catalog = {taskOptions: [{name: 'prepare_near_player'}], players: [{playerId: 1}]};
  c.renderTasks(catalog, visible, false, true); el('task-kind').value = 'prepare_near_player';
  c.renderTasks(catalog, visible, false, true);
  assert.deepEqual(row.children[0].options.map(o => o.value), ['', 'tomato']);
  assert.equal(el('task-run').disabled, true);
  row.children[0].value = 'tomato'; c.renderTasks(catalog, visible, false, true);
  assert.equal(el('task-run').disabled, false);
  el('task-player').value = '3'; c.renderTasks(catalog, visible, false, true);
  assert.equal(el('task-run').disabled, true);
  el('task-player').value = '1'; row.children[2].checkValidity = () => false;
  c.renderTasks(catalog, visible, false, true); assert.equal(el('task-run').disabled, true);
  row.children[1].value = 'empty'; c.renderTasks(catalog, visible, false, true);
  assert.deepEqual(row.children[0].options.map(o => o.value), ['', 'pot']);
});

test('source and destination menus stay complete while exact job eligibility changes', () => {
  const {context: c, el} = fixture();
  c.renderJobs({}, visible, false, true); el('job-kind').value = 'fetch_ingredient';
  c.renderJobs({}, visible, false, true); el('job-source').value = 'tomato'; el('job-destination').value = 'counter';
  c.renderJobs({}, visible, false, true); assert.equal(el('job-run').disabled, true);
  const catalog = {jobOptions: {fetch_ingredient: [{sourceId: 'tomato', destinationIds: ['counter']}]}};
  c.renderJobs(catalog, visible, false, true); assert.equal(el('job-run').disabled, false);
  el('job-destination').value = 'board'; c.renderJobs(catalog, visible, false, true);
  assert.equal(el('job-run').disabled, true);
  assert.deepEqual(el('job-destination').options.map(o => o.value), ['', 'counter', 'board']);
  el('job-destination').value = 'counter'; c.renderJobs({}, visible, false, true);
  assert.equal(el('job-source').value, 'tomato'); assert.equal(el('job-destination').value, 'counter');
  c.renderJobs(catalog, visible, false, true); assert.equal(el('job-run').disabled, false);
  c.renderJobs(catalog, visible, false, false); assert.equal(el('job-run').disabled, true);
});

test('throws retain the selected recipient and use only fresh checked paths', () => {
  const {context: c, el} = fixture();
  const player = {id: 'p1', objectInstanceId: 55, label: 'Player 1', possibleOperations: ['throw_to_player'], operations: []};
  const display = {players: [player]};
  c.renderThrows({}, display, false, true); el('throw-target').value = el('throw-target').options[1].value;
  c.renderThrows({}, display, false, true); assert.equal(el('throw-run').disabled, true);
  const catalog = {players: [{...player, operations: ['throw_to_player']}]};
  c.renderThrows(catalog, display, false, true); assert.equal(el('throw-run').disabled, false);
  c.renderThrows({}, display, false, true); assert.equal(el('throw-run').disabled, true);
  assert.equal(el('throw-target').options.length, 2); assert.notEqual(el('throw-target').value, '');
  c.renderThrows(catalog, display, true, true); assert.equal(el('throw-run').disabled, true);
});

test('visible target actions never authorize missing operations, conditions or routes', () => {
  const {context: c} = fixture();
  const board = {possibleOperations: ['navigate_to', 'work_at'], possibleWorkConditions: ['work_complete'], operations: []};
  assert.equal(c.actionChoices(board).some(choice => choice.value === 'work_at|work_complete'), true);
  assert.equal(c.canPerform(board, 'work_at|work_complete'), false);
  board.operations = ['work_at']; board.workConditions = ['work_complete'];
  assert.equal(c.canPerform(board, 'work_at|work_complete'), false);
  board.walkReachable = true;
  assert.equal(c.canPerform(board, 'work_at|work_complete'), true);
  assert.equal(c.canPerform(board, 'work_at|one_plate_clean'), false);
  board.operations = []; assert.equal(c.canPerform(board, 'work_at|work_complete'), false);
});

test('temporary missing map/stale data retains choices; level changes and removals clear them', () => {
  const {context: c} = fixture();
  const catalog = {targets: [{id: 'd1'}], displayOptions: visible.displayOptions};
  assert.equal(c.displayChoices(catalog), catalog);
  c.state.kitchenMap = null; c.state.fresh = false;
  assert.equal(c.displayChoices({}), catalog);
  c.state.fresh = true; c.state.kitchenMap = {levelEpoch: 2};
  assert.equal(Object.keys(c.displayChoices({})).length, 0);
  const empty = {targets: [], displayOptions: {ingredients: []}};
  assert.equal(c.displayChoices(empty), empty);
  c.state.connected = false; assert.equal(Object.keys(c.displayChoices({})).length, 0);
});


test('queue Add works while busy; unlimited and dependent Run now are blocked', () => {
  const {context: c, el} = fixture();
  c.renderTasks({}, visible, true, true); el('task-kind').value = 'wash_dishes';
  c.renderTasks({}, visible, true, true);
  assert.equal(el('task-run').disabled, false);
  assert.equal(el('task-now').disabled, false);
  c.state.queue.active = {id: 'active', spec: {repeat: {mode: 'until_cancelled'}}};
  c.renderTasks({}, visible, true, true);
  assert.equal(el('task-run').disabled, true);
  assert.equal(el('task-now').disabled, false);
  c.state.queue.active.spec.repeat = {mode: 'finite', count: 1};
  c.state.queue.pending = [{id: 'next', dependsOn: ['active'], spec: {repeat: {mode: 'finite', count: 1}}}];
  c.renderTasks({}, visible, true, true);
  assert.equal(el('task-run').disabled, false);
  assert.equal(el('task-now').disabled, true);
  c.state.queue.barrier = {id: 'stopping'};
  c.renderTasks({}, visible, true, true);
  assert.equal(el('task-run').disabled, true);
});
