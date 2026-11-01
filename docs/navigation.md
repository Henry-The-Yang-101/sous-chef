# Player 2 navigation actions

`navigate_to(destination_id)` chooses its approach, route, steering and termination
inside the game. The caller supplies only a destination from `available_actions()`.
`stop()` interrupts the current action or raw bridge input and returns the chef's
internal action state to `idle`. No model or speech service is required.

```python
from clients.actions import ActionClient

with ActionClient() as chef:
    catalog = chef.available_actions()
    # A future decision model selects one of these IDs; it never sends waypoints.
    destinations = catalog['destinations']
    if destinations:
        chef.navigate_to(destinations[0]['id'])
        # The plugin runs autonomously while this connection remains open.
        # status() is optional observation, not a required control heartbeat.
        print(chef.status()['navigation'])
        chef.stop()
```

The CLI keeps the connection open until completion or interruption:

```sh
python3 clients/actions.py list
python3 clients/actions.py navigate_to d7   # use a current returned ID
python3 clients/actions.py status
python3 clients/actions.py stop            # can interrupt another bridge client
```

The action list contains one `navigate_to` schema with enumerated destination
options and one `stop`. Each object has a lifetime-specific ID within the bridge
session, label, semantic kind, Unity instance ID, current position, chosen approach
position and route distance. Multiple colliders for the same object are deduplicated.
Targets include pickup items/supplies, workstations, washing/cooking stations,
placement surfaces and native interactables such as buttons. Their current native
pickup/interaction predicates and the chef's held item are checked. Geometry alone
does not establish that chopping or washing will be possible after arrival.
Approaches also respect the native interaction layer mask and counter grid:
standing near a counter's corner does not make it selectable from a neighbouring
counter's cell. The chosen stance must be in a valid cardinally adjacent cell.

Listing or starting refreshes the catalogue. Targets appear only if a supported
walking route currently exists. Query again to see a previously disconnected
location after a barrier opens. Starting revalidates the ID; a stale catalogue is
never permission to follow an obsolete route. `stop` remains available outside
rounds and without Player 2. A raw input owner must release before navigation can
claim the chef. Concurrent navigation/raw-input commands cannot override an action.

## Geometry and routes

The navigator runs against Unity's live physics scene, independently of NDJSON
sampling. It uses the chef's scaled capsule dimensions and native ground/layer
masks. Ground rays check floor support across the footprint; capsule overlaps and
sweeps check clearance along edges. This accounts for rotating walls, moving
parents, enabled/disabled collision, narrow passages, other chefs and objects.
Triggers are not automatically solid walls; known fire, respawn and teleport
regions are excluded. Other chefs have a short velocity-based occupancy margin.

A single Dijkstra search builds shortest-distance routes from Player 2 to **all**
reachable destinations. A binary heap, cached geometry probes and a spatial index
of reachable approach cells avoid repeating a search per target. Changed collider
bounds/poses invalidate nearby probes. Periodic reconciliation catches changes
that preserve bounds. Query buffers, node counts and planning time are bounded;
incomplete searches are explicitly unavailable, never interpreted as open space.
Diagnostics report planning time, queries and node counts.
The first catalogue query can return `planning_budget_exceeded` while a cold
graph warms. Up to eight bounded background updates continue that work; query
again for the complete catalogue. A persistently oversized graph stays
unavailable and does not cause unlimited background retries. Unchanged primitive
colliders retain probes. Readable collision meshes compare their vertices and
triangle indices every 0.2 seconds, sharing that comparison across colliders
using the same mesh. Unchanged meshes retain probes; in-place geometry edits
invalidate them even when the bounds stay the same. Terrain, unreadable meshes,
and meshes beyond the comparison limits (65,536 vertices / 524,288 indices)
remain conservatively invalidated on reconciliation.
The 12 ms search budget is not a hard bound on the entire first request: Unity
queries, discovery, runtime initialization and destination matching also take
time. `planMilliseconds` reports the complete measured refresh duration.

The search lattice is 0.4 world units horizontally with height-aware keys. Routes
are shortest on that sampled graph, not guaranteed continuous-world geodesics.
Freshly checked shortcuts reduce stair-step paths without cutting corners or
crossing unsupported floor. Search is bounded to the local kitchen vicinity
(60 world units, 16,000 discovered nodes). Very narrow or unusual geometry may
be conservatively omitted. Height checks exclude drops/jumps and steep or abrupt
unsupported transitions; teleporters and cannons are not walking shortcuts.

Floor conveyors and supported moving surfaces contribute native surface velocity;
wind is evaluated using current volumes, filters and direction. A directed edge is
excluded when walking cannot counter its cross-flow or make forward progress.
Execution compensates using the chef's actual wind/surface velocities. Countertop
conveyors move the destination rather than the chef. Slipperiness affects momentum
checks and steering; unsupported sliding/override-gravity surfaces are excluded.
The final approach uses the same flow compensation and next-step checks.
No prediction of an entire moving kitchen or another player's future decisions is
claimed. Geometry and target eligibility are rechecked during execution.

## Lifecycle and stopping

States are `idle` and `navigating`; `reason` records completion or interruption.
The plugin checks the selected object and live geometry each update while moving,
replans on observed changes, and polls reachability at least every 0.1 seconds.
If the destination loses its route or eligibility, movement input is released
and state becomes `idle` in that detecting update. It does not wait for a bridge
or door to reopen, silently resume, or choose a different semantic task.

The next walking step is checked against live support and collision. Completion
requires the game's own interaction/pickup selection to match the destination;
merely reaching an approximate coordinate is insufficient. Failure to align,
loss of progress, excess duration, query overflow, planning failure, focus loss,
pause, respawn, ownership loss, disconnect or round change ends the action.
`stop` clears action input before reporting idle and restores the original P2
bindings. Native momentum, wind, conveyors and human input after restoration can
still move the chef; stopping does not teleport or zero the game's physics.
Detection and command handling are bounded by Unity updates, not zero wall-clock
latency. The navigator never presses dash, pickup or use.

## Protocol

The existing control endpoint (default 24703) accepts:

```text
actions request_id
navigate_to request_id destination_id
stop request_id
```

The greeting advertises these actions. Replies retain the existing `control`
record and include `control.navigation`. `actions` additionally returns
`availableActions` with `actions`, `destinations` and navigation diagnostics.
Sequences remain internal to the direct-input lease. An autonomous action renews
its own short input lease; the decision maker does not issue keepalive inputs.
Closing its owning connection cancels it. Idle connections may stay open (four
maximum); input TTLs and bounded dispatch/write timeouts still apply.
