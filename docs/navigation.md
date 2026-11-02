# Player 2 navigation actions

Bridge 0.6 keeps discovered empty counters in the `targets` registry even when
the chef has empty hands, so logistics jobs can name a delivery surface before
pickup. The `destinations` list still applies walking eligibility and verified
reachability. Internally generated throw stances use supported heights from the
same walking graph; callers continue to supply semantic IDs rather than coordinates.

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

The navigation entries contain one `navigate_to` schema with enumerated
destination options and one `stop`. Bridge 0.5 also includes
[first-layer interaction schemas](actions.md) and discovered nonwalking targets. Each object has a lifetime-specific ID within the bridge
session, label, semantic kind, Unity instance ID, current position, chosen approach
position and route distance. Multiple colliders for the same object are deduplicated.
Targets include pickup items/supplies, workstations, washing/cooking stations,
plate stations and native interactables such as buttons. Fixed task stations
remain destinations even when no work is available: an empty sink can be
approached. Ordinary counters are included when their native pickup predicate is
true or the chef carries something to place. This avoids listing every empty
counter. Rubbish bins are fixed disposal stations and can be approached with empty
hands; only the discard action requires a held item. Navigation availability does not establish that chopping, pickup or
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
masks. A native-radius sphere cast resolves the supported centre pose. Capsule overlaps and
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
Supported heights resolve to canonical height keys, so the same stair tread
reached from above and below shares a node while stacked decks remain distinct.
Stairs use the chef's native `StepHeightMax` (capped at 0.65 units) per transition,
not a limit on total elevation change. The centre probe uses spherical contact,
matching native GroundCast at ramp edges and stair wedge lips, and converts the
contact into the capsule’s supported foot height. Narrow tread seams and capsule overhang are allowed when this native sphere
remains supported; a ray under every footprint edge is not required.
Ground normals use the native GroundCast limit of 58 degrees.
Height alone does not make a ledge climbable: discontinuities between support
samples require `Steppable` on the upper collider's exact GameObject, matching
the native collision handler. Continuous slopes, including ramp-to-floor joins,
are distinguished using the support normals. Step-component changes participate
in periodic geometry reconciliation, and a changed native step height clears
cached probes and edges.

Segments sample support at most 0.15 units apart. Height-changing segments sweep
up, across, then down to check risers and overhead clearance instead of casting
straight through the staircase. No ground collider is ignored wholesale. Every
sample must have native spherical support and remain within the native step limit of
the preceding support. Intermediate waypoints allow native stepping lag in height
while still requiring horizontal progress; final station arrival remains subject
to the position, facing and selection checks below. Both staircases participate
in the same shortest-path search, with fresh collision checks for shortcuts.
Flat graph edges use the same sampled spherical support and capsule checks as
execution; a clear midpoint and capsule sweep alone cannot certify continuous
support beside a hole. Cached edges use padded clearance regardless of the chef's current pose.
Environmental flow is checked live separately from cached geometry. Each cached
edge retains every sampled support point and ground collider, so a narrow belt
between calm endpoints still participates in each live flow check. Wind is also
evaluated at those points rather than at one segment midpoint. The search radius
is applied when expanding cached edges, relative to the current chef position;
moving closer can reveal connections omitted by an earlier search boundary. A nearby
waypoint is skipped only when the direct connection to the next point is clear,
including its floor support. If optional lookahead beyond a turn is unsupported,
the actual next movement step must still pass before input is sent. This does not
permit unsupported hole crossing, ignore actual obstacles, or retry a stopped action.
Freshly checked shortcuts reduce stair-step paths without cutting corners or
crossing unsupported floor. Discovery is bounded to a 60-world-unit radius around
Player 2; walking cells stay within 58 units, with at most 16,000 discovered nodes.
This covers the tested kitchens but is a real distance limit for unusually large
levels. Very narrow or unusual geometry may
be conservatively omitted. Height checks exclude drops/jumps and steep or abrupt
unsupported transitions; teleporters and cannons are not walking shortcuts.

Floor conveyors and supported moving surfaces contribute native surface velocity;
wind is evaluated using current volumes, filters and direction. Travelator corner
velocity retains the entering surface, including across graph edges and remaining
route checks. The graph distinguishes arrivals with different prior surfaces while
sharing their geometry probes. Connectors from the chef use native surface history.
A directed edge is
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

The next walking step is checked against live support and collision. Each walking
command uses the full preceding unscaled frame interval, rounded up to milliseconds
with a minimum native-step window of 17 ms. The safety projection also covers the
scaled game delta when it is larger. There is no 60 ms cap that truncates a slow
frame. Native input reads neutralize the command when its checked motion window
expires; a separate, longer ownership lease permits renewal after frame jitter.
An update interval above 0.9 seconds or an invalid interval stops with
`unsafe_update_interval`. Ownership expiry after a stall still stops the action.

The chef must
reach within 0.20 units of the checked working stance, face the target within five
degrees, settle to at most 0.2 units/second relative to the destination, and retain exact native selection for at
least 0.1 seconds. An adjacent grid selection by itself cannot finish navigation.
Settling checks combine native locomotion, wind/platform drift and observed pose
changes. The previous chef pose is transported with the target's transform to
account for shared platform translation and rotation; a first observation cannot
establish settled speed.
Once settling begins, up to 0.28 units of stance error is allowed so small braking
drift does not repeatedly restart full-speed walking. A larger displacement resets
this latch. Near the destination, the chosen approach is retained in the target's
local frame while it remains valid and has a certified walking route. If that
side loses its route, another certified side can be selected. An incomplete search
can still preserve the previous route after checking all its remaining segments.
Switching sides resets the alignment latch. The final route is shortest to the
selected stance. At the stance, walking input is neutral and the game's own turn helper rotates
Player 2 at the native turn rate. Position is never teleported; a nonzero movement
axis cannot be used as a slow turning pulse because the game normalizes it to full
walking speed.

Item arrival requires its native pickup/interaction selection. Fixed stations may
use exact grid selection. Completion checks the selected native pickup, placement
and use predicates: `arrived` means at least one is currently available;
`positioned_no_action` means alignment is complete but there is no current work
(for example, an empty sink). Neither result presses an action button. The
`arrival` record reports pose error, measured `relativeSpeed` (null before a
valid motion sample), selection, `positioned`, `canPickup`,
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

For execution diagnosis, launch the plugin with `SOUS_CHEF_NAV_TRACE=1`.
`navigation.executionTrace` retains up to 96 recent controller decisions, including
native ground contact, route points, commanded input, and the rejected physics
check. A `no_safe_start` stop also includes the attempted start connectors. Tracing
is off by default and does not change movement decisions. Capture this trace and
the gameplay stream together; a reachable destination or a manually assisted
arrival does not establish that the controller can execute a stair route.

Stair execution distinguishes continuous ramps from native `Steppable` objects.
Only the latter permit an up/across/down body sweep. When the support collider or
contact normal changes, segment checking subdivides that transition down to half
the collision skin. This detects the blocked strip at a stair wedge's outside
corner, where the capsule overlaps the wedge before its contact becomes shallow
enough to meet the game's 58-degree ground limit. A coarse chord between valid
endpoints can otherwise incorrectly certify that approach.

A running action can enter `settling_ground` when native stair momentum lifts the
chef beyond GroundCast's contact distance. It issues neutral input while checking
a clear, collision-free landing below the body, within the native step-height
range. Ground contact resumes the same action and forces replanning; absent or
blocked support stops immediately, and contact must return within the existing
two-second stall interval. This does not allow airborne steering across holes.
