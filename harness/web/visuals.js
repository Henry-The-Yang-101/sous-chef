// No animation loop or extra requests: draw only when an existing state poll
// changes the view. All projection/chart work runs in this browser.
const visualEl = id => document.getElementById(id);
const svgNode = (tag, attributes = {}) => {
  const node = document.createElementNS('http://www.w3.org/2000/svg', tag);
  for (const [key, value] of Object.entries(attributes)) node.setAttribute(key, value);
  return node;
};
function renderFps(state, live) {
  const data = state?.gamePerformance;
  const samples = live && data?.fresh ? data.samples || [] : [];
  const latest = samples.at(-1);
  const graph = visualEl('fps-graph');
  const width = Math.max(240, graph.clientWidth);
  const signature = JSON.stringify([samples, Boolean(live && data?.fresh), width]);
  if (graph.dataset.signature === signature) return;
  graph.dataset.signature = signature;
  graph.setAttribute('viewBox', `0 0 ${width} 100`);
  visualEl('fps-current').textContent = latest ? `${latest.fps.toFixed(1)} FPS` : 'Waiting';
  visualEl('fps-summary').textContent = samples.length ? `Average ${(samples.reduce((s, p) => s + p.fps, 0) / samples.length).toFixed(1)} · Low ${Math.min(...samples.map(p => p.fps)).toFixed(1)}` : '';
  visualEl('fps-note').textContent = latest ? 'Last 60 seconds · interval averages from game frames / real time · paused frames included'
    : live && data?.fresh ? 'Collecting game frames for the first FPS interval…'
    : 'FPS is available in a kitchen. Waiting for fresh game observations.';
  const ceiling = Math.max(60, Math.ceil(Math.max(...samples.map(p => p.fps), 0) / 30) * 30);
  const plotY = fps => 76 - Math.min(ceiling, fps) / ceiling * 66;
  const grid = [0, ceiling / 2, ceiling].flatMap(value => {
    const line = svgNode('line', {x1: 40, x2: width - 10, y1: plotY(value), y2: plotY(value), stroke: '#e5e9e0', 'stroke-width': 1});
    const label = svgNode('text', {x: 32, y: plotY(value) + 4, 'text-anchor': 'end', fill: '#65715d', 'font-size': 11});
    label.textContent = value;
    return [line, label];
  });
  if (latest) {
    const d = samples.map((point, i) => `${i ? 'L' : 'M'}${40 + (point.time - latest.time + 60) / 60 * (width - 50)},${plotY(point.fps)}`).join(' ');
    grid.push(svgNode('path', {d, fill: 'none', stroke: '#4d7a3c', 'stroke-width': 2, 'vector-effect': 'non-scaling-stroke'}));
    grid.push(svgNode('circle', {cx: width - 10, cy: plotY(latest.fps), r: 2.5, fill: '#4d7a3c'}));
  }
  for (const [x, text, anchor] of [[40, '60s ago', 'start'], [width - 10, 'now', 'end']]) {
    const label = svgNode('text', {x, y: 94, 'text-anchor': anchor, fill: '#65715d', 'font-size': 11});
    label.textContent = text; grid.push(label);
  }
  graph.replaceChildren(...grid);
  graph.setAttribute('aria-label', latest ? `Game FPS: ${latest.fps.toFixed(1)}. Last 60 seconds, interval averages.` : 'Waiting for game FPS');
}

class KitchenPicker {
  constructor(onPick, nameOf) {
    this.onPick = onPick;
    this.nameOf = nameOf;
    this.nodes = new Map();
    this.overlapIds = [];
    this.geometrySignature = '';
  }
  shortName(d) {
    if (d.playerId != null) return `P${d.playerId}`;
    if (d.foodName) return d.foodName.replace(/^DLC\d+_/i, '').replaceAll('_', ' ').slice(0, 3);
    return ({workstation: 'CH', washing_station: 'SI', cooking_station: 'CO',
      plate_station: 'PL', bin: 'BIN', item: 'IT'})[d.kind] || '·';
  }
  label(d) {
    return `${this.nameOf(d)} · ${d.kind.replaceAll('_', ' ')} · height ${Number(d.position[1]).toFixed(1)} · ${d.id}${d.contents?.summary ? ` · ${d.contents.summary}` : ''}`;
  }
  showOverlaps() {
    const panel = visualEl('map-overlaps');
    const signature = JSON.stringify(this.overlapIds.map(id => [id, this.targets.get(id)?.objectInstanceId, this.enabled.has(id), this.targets.get(id)?.position]));
    if (panel.dataset.signature === signature) return;
    panel.dataset.signature = signature;
    panel.replaceChildren();
    for (const id of this.overlapIds) {
      const target = this.targets.get(id);
      if (!target || !this.enabled.has(id)) continue;
      const button = document.createElement('button'); button.type = 'button';
      button.textContent = this.label(target);
      button.addEventListener('click', () => { this.overlapIds = []; this.onPick(target); });
      panel.append(button);
    }
    panel.hidden = !panel.children.length;
  }
  render(map, targets, enabled, selected, live) {
    this.targets = new Map(targets.filter(d => Array.isArray(d.position) && d.position.length === 3 && d.position.every(Number.isFinite)).map(d => [d.id, d]));
    this.enabled = new Set(enabled.map(d => d.id));
    const stage = visualEl('map-stage');
    stage.hidden = !live || !map;
    visualEl('map-empty').hidden = live && Boolean(map);
    visualEl('map-empty').textContent = !live ? 'Waiting for fresh game state.' : 'Enter a kitchen to see its live map.';
    if (!live || !map) {
      this.nodes.forEach(node => node.remove()); this.nodes.clear();
      visualEl('map-geometry').replaceChildren(); this.geometrySignature = '';
      this.overlapIds = []; this.showOverlaps();
      visualEl('map-note').textContent = '';
      return;
    }
    const shapes = map.shapes || [];
    const points = shapes.filter(s => s.kind === 'ground').flatMap(s => s.polygons.flat());
    // Prefer the actual ground extent; surrounding scenery must not shrink the
    // kitchen to a dot. Include live targets and chefs even beyond that extent.
    points.push(...Array.from(this.targets.values()).map(d => [d.position[0], d.position[2]]),
      ...(map.players || []).map(p => [p.position[0], p.position[2]]));
    if (!points.length) points.push([-1, -1], [1, 1]);
    const xs = points.map(p => p[0]), zs = points.map(p => p[1]);
    const minX = Math.min(...xs) - .9, maxX = Math.max(...xs) + .9;
    const minZ = Math.min(...zs) - .9, maxZ = Math.max(...zs) + .9;
    const scale = Math.min(1000 / (maxX - minX), 600 / (maxZ - minZ));
    const centerX = (maxX + minX) / 2, centerZ = (maxZ + minZ) / 2;
    const project = p => [500 + (p[0] - centerX) * scale, 300 - (p[1] - centerZ) * scale];
    const geometrySignature = JSON.stringify([shapes, minX, maxX, minZ, maxZ]);
    if (geometrySignature !== this.geometrySignature) {
      this.geometrySignature = geometrySignature;
      const paths = [...shapes].sort((a, b) => ({ground: 0, counter: 1, wall: 2}[a.kind] - ({ground: 0, counter: 1, wall: 2}[b.kind])) || a.height - b.height).map(shape => {
        const path = shape.polygons.map(polygon => polygon.map((p, i) => `${i ? 'L' : 'M'}${project(p).join(',')}`).join(' ') + 'Z').join(' ');
        // Separate triangles use nonzero fill; adjacent triangles remain solid,
        // while absent triangles (holes/water) remain absent.
        return svgNode('path', {d: path, fill: {ground: '#fafbf8', counter: '#c2cbb9', wall: '#acb6a3'}[shape.kind],
          stroke: shape.kind === 'ground' ? 'none' : '#8f9d84', 'stroke-width': 1, 'vector-effect': 'non-scaling-stroke'});
      });
      visualEl('map-geometry').replaceChildren(...paths);
    }
    const groups = [];
    const clusterRadius = Math.max(16, 32 * 1000 / Math.max(1, stage.clientWidth));
    for (const target of this.targets.values()) {
      const point = project([target.position[0], target.position[2]]);
      let group = groups.find(g => Math.hypot(g.point[0] - point[0], g.point[1] - point[1]) < clusterRadius);
      if (!group) { group = {point, targets: []}; groups.push(group); }
      group.targets.push(target);
    }
    const wanted = new Set();
    const nodeFor = (key, create) => {
      wanted.add(key);
      if (!this.nodes.has(key)) {
        const node = create(); this.nodes.set(key, node); visualEl('map-markers').append(node);
      }
      return this.nodes.get(key);
    };
    for (const group of groups) {
      const ids = group.targets.map(d => d.id).sort();
      const button = nodeFor(`target:${ids.join(',')}`, () => {
        const node = document.createElement('button'); node.type = 'button'; node.className = 'map-marker';
        node.addEventListener('click', () => {
          const choices = ids.map(id => this.targets.get(id)).filter(d => d && this.enabled.has(d.id));
          if (choices.length === 1) { this.overlapIds = []; this.onPick(choices[0]); }
          else { this.overlapIds = ids; this.showOverlaps(); }
        });
        return node;
      });
      const selectable = group.targets.filter(d => this.enabled.has(d.id));
      button.disabled = !selectable.length;
      button.textContent = group.targets.length > 1 ? String(group.targets.length) : this.shortName(group.targets[0]);
      button.title = group.targets.map(d => this.label(d)).join('\n');
      button.setAttribute('aria-label', group.targets.length > 1 ? `Choose among ${group.targets.length} targets here: ${button.title}` : button.title);
      button.classList.toggle('selected', ids.includes(selected));
      button.setAttribute('aria-pressed', String(ids.includes(selected)));
      button.style.left = `${group.point[0] / 10}%`; button.style.top = `${group.point[1] / 6}%`;
    }
    for (const player of map.players || []) {
      const node = nodeFor(`chef:${player.objectInstanceId}`, () => { const node = document.createElement('span'); node.className = 'map-player'; return node; });
      const point = project([player.position[0], player.position[2]]);
      node.textContent = player.label.replace('Player ', 'P');
      node.title = player.label;
      node.classList.toggle('selectable', targets.some(d => d.objectInstanceId === player.objectInstanceId));
      node.style.left = `${point[0] / 10}%`; node.style.top = `${point[1] / 6}%`;
    }
    for (const [key, node] of this.nodes) if (!wanted.has(key)) { node.remove(); this.nodes.delete(key); }
    this.showOverlaps();
    visualEl('map-note').textContent = `${this.enabled.size} selectable targets · Choose a target to inspect its options; Run enables when available. Numbers group nearby or stacked targets.${map.approximateShapes ? ` ${map.approximateShapes} shapes use approximate bounds.` : ''}${map.truncated ? ' Geometry display limit reached.' : ''}`;
  }
}
