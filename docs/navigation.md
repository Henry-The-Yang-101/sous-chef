# Player 2 navigation actions

`navigate_to(destination_id)` chooses its approach, route, steering and termination
inside the game. The caller supplies only a destination from `available_actions()`.
`stop()` interrupts the current action or raw bridge input and returns the chef's
internal action state to `idle`. No model or speech service is required.

For a live clickable list, run `python3 -m harness.server` and open
[the localhost harness](http://127.0.0.1:8765). See [harness usage](harness.md).

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
plate stations and native interactables such as buttons. Fixed task stations
remain destinations even when no work is available: an empty sink can be
approached. Ordinary counters are included when their native pickup predicate is
true or the chef carries something to place. This avoids listing every empty
counter. Navigation availability does not establish that chopping, pickup or
washing will be possible after arrival; those actions need separate preconditions.
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
parents, enabled/disabled collision, narrow passages and other physical objects.
Triggers are not automatically solid walls; known fire, respawn and teleport
regions are excluded. **Local player chefs are deliberately ignored as obstacles**,
including their carried descendants; Player 2 can push past Player 1 using normal
game physics. Pedestrians and other non-player objects retain their native solid
colliders and are rechecked as they move. A fully jammed chef can still hit the
no-progress watchdog; ignoring a chef does not teleport through a physical jam.

The lattice uses an extra 0.025-unit body clearance margin. A chef already resting
against a wall may legally be inside that margin. Connectors from the current pose use consistent native body clearance with a
0.02-unit contact skin, rather than expanding the capsule while it slides away
from a wall. Start discovery checks a 5×5 lattice neighbourhood; lattice nodes
and edges retain the padded clearance. Every connector still checks support,
collisions and hazards.

A single Dijkstra search builds shortest-distance routes from Player 2 to **all**
reachable destinations, including a checked final connector to a centred working
stance. A binary heap, cached geometry probes and a spatial index
of reachable approach cells avoid repeating a search per target. Changed collider
bounds/poses invalidate nearby probes. Periodic reconciliation catches changes
that preserve bounds. Query buffers, node counts and planning time are bounded.
When a search exhausts its budget, already settled Dijkstra nodes retain proven
shortest paths. A destination is published only if its best checked approach costs
no more than the minimum pending frontier distance, so no unexplored approach
could provide a shorter route. Unsettled nodes never become executable guesses.
The catalogue reports `complete: false` and `planning_in_progress` while additional
destinations remain unresolved, instead of clearing every confirmed destination.
Diagnostics report preparation, search, destination matching and total refresh
time, together with queries and node counts. Ground speeds are read once per
physics refresh, identical ground checks are shared across an edge, and calm
stationary floors bypass the flow solver. Wind and moving-surface velocities
remain live. Dynamic non-player collider poses invalidate their affected regions.
The first catalogue query can return a partial or empty `planning_in_progress`
catalogue while a cold graph warms. Up to eight bounded background updates continue
that work; query again for additional confirmed destinations. A persistently
oversized graph reports incomplete coverage and does not cause unlimited background
retries. Unchanged primitive
colliders retain probes. Readable collision meshes compare their vertices and
triangle indices every 0.2 seconds, sharing that comparison across colliders
using the same mesh. Unchanged meshes retain probes; in-place geometry edits
invalidate them even when the bounds stay the same. Terrain, unreadable meshes,
and meshes beyond the comparison limits (65,536 vertices / 524,288 indices)
remain conservatively invalidated on reconciliation.
The 12 ms budget applies to graph search, after geometry preparation and discovery.
It is not a hard bound on the entire request: Unity queries, discovery, runtime
initialization and destination matching also take time. `planMilliseconds`
reports the complete measured refresh duration. Heavy geometry changes can still
exhaust the budget. If a search is incomplete, an active action can retain its previous route only
when the target stance and every remaining segment pass fresh physics checks.
An observed blockage, moved/invalid target or observation failure still stops the
action in that update. This does not republish an unresolved destination or claim
that the retained route is newly optimal; other certified destinations remain available.

The search lattice is 0.4 world units horizontally with height-aware keys. Routes
are shortest on that sampled graph, not guaranteed continuous-world geodesics.
Freshly checked shortcuts reduce stair-step paths without cutting corners or
crossing unsupported floor. Discovery is bounded to a 60-world-unit radius around
Player 2; walking cells stay within 58 units, with at most 16,000 discovered nodes.
This covers the tested kitchens but is a real distance limit for unusually large
levels. Very narrow or unusual geometry may
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

The next walking step is checked against live support and collision. The chef must
reach within 0.20 units of the checked working stance, face the target within five
degrees, settle below 0.2 units/second, and retain exact native selection for at
least 0.1 seconds. An adjacent grid selection by itself cannot finish navigation.
Once settling begins, up to 0.28 units of stance error is allowed so small braking
drift does not repeatedly restart full-speed walking. A larger displacement resets
this latch. Near the destination, the chosen approach is retained while it remains
valid, instead of changing sides during alignment. The final route is shortest
to that retained stance. At the stance, walking input is neutral and the game's own turn helper rotates
Player 2 at the native turn rate. Position is never teleported; a nonzero movement
axis cannot be used as a slow turning pulse because the game normalizes it to full
walking speed.

Item arrival requires its native pickup/interaction selection. Fixed stations may
use exact grid selection. Completion checks the selected native pickup, placement
and use predicates: `arrived` means at least one is currently available;
`positioned_no_action` means alignment is complete but there is no current work
(for example, an empty sink). Neither result presses an action button. The
`arrival` record reports pose error, selection, `positioned`, `canPickup`,
`canPlace`, `canUse` and `readyForAction` at completion; these are historical once
idle, not a promise that a moving target or its contents stay available. Failure to align,
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
`navigation.destination` identifies the current or last attempted target by ID,
raw label, kind, Unity instance ID and position. It survives completion/cancellation
until another action or round reset, so an interruption cannot look like arrival
at a different object. `destinationId` remains null when idle. While navigating,
`phase` distinguishes `walking` from `aligning`; both are interrupted by Stop.

Read-only catalogues can be queried while the game is unfocused, provided the
other native round, pause, menu and player gates pass. Movement still requires
game focus and reports `game_unfocused` otherwise. The browser harness focuses
the game only after the user clicks Navigate, then revalidates the exact target.
Sequences remain internal to the direct-input lease. An autonomous action renews
its own short input lease; the decision maker does not issue keepalive inputs.
Closing its owning connection cancels it. Idle connections may stay open (four
maximum); input TTLs and bounded dispatch/write timeouts still apply.
