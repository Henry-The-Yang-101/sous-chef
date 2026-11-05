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
Triggers are not automatically solid walls; known fire and respawn regions are
excluded. Portal triggers are excluded from ordinary walking and crossed only by
explicit native transport edges. **Local player chefs are deliberately ignored as obstacles**,
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
Geometry caching is capped at 65,536 FIFO records, including invalidated entries
awaiting eviction. Regional invalidation uses spatial buckets rather than scanning
every retained probe for each moving collider. Eviction also removes the source's
cached edges and invalidates saved search continuity. Throw-stance route costs
reuse the reachable-cell index, inspecting only nearby nodes and portal exits
before applying the existing native connector checks. Diagnostics expose
`cachedProbes`, `cachedEdges` and `geometryCacheEvictions`.
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
catalogue while a cold graph warms. Stable graphs retain their frontier and
settled nodes, continuing in approximately 3 ms search slices on later render
frames. Up to 32 short background slices (or eight full-budget fallback passes)
continue that work; query again for additional confirmed destinations. Exact
starting-state, geometry, support-speed and wind checks guard reuse. Changed
inputs restart the tree. Native travelators also retain search progress after
exact checks of their speed, direction, border, enabled state, world pose, grid
and connected neighbours, plus the chef's entering-surface history. Other moving
surfaces and portal readiness retain fresh searches with paced idle warming.
Starting an action can reuse a certified idle tree after those same live checks;
a fresh command does not discard a distant route merely to rebuild it in one slice.
Completed stable trees can serve fresh
target/approach observations without another graph expansion. A persistently
oversized graph reports incomplete coverage and does not cause unlimited background
retries. Unchanged primitive
colliders retain probes. Readable collision meshes compare their vertices and
triangle indices every 0.2 seconds, sharing that comparison across colliders
using the same mesh. Unchanged meshes retain probes; in-place geometry edits
invalidate them even when the bounds stay the same. Terrain, unreadable meshes,
and meshes beyond the comparison limits (65,536 vertices / 524,288 indices)
remain conservatively invalidated on reconciliation.
The 3 ms initial/continued idle and 12 ms fallback/active budgets apply to graph search,
after geometry preparation and discovery. `searchGeneration`, `searchSlices`,
`searchReused`, `searchReuseFailure` and `searchComplete` distinguish continued
work from restarts and explain why a continuity check failed.
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
unsupported transitions. Cannons remain unsupported; teleporters use separate
directed transitions as described below.

### Teleporters

The graph reads each live `Teleportal.m_exitPortal` link, the native player sender
and receiver, and the receiver's attach point and animation endpoint. It supports
one-way links, chains, cycles, receiver-only endpoints and exits on different decks. Routes minimize
walking distance: entry and exit connectors count toward `routeDistance`, while
transport displacement does not. `routeTeleports` reports the number of portal
transitions in a destination's selected route; `routePortalIds` identifies their
native source components. This is not a shortest-time estimate.

Entry must lie inside the native activation cone, on supported floor, near the
front of the trigger. Its centre can be behind the portal's solid frame. Only that
trigger is exempted from the entry connector's collision check; walls, other
portals, fire and respawn hazards still block it. The native landing must be
supported and clear, with a checked connection back to walking cells. An elevated receiver may finish above that floor: the navigator mirrors the
native grounded-ray/fallback offset and checks a vertical landing of at most
four units. Only downward separation from the horizontal underside of a level
box is allowed for native ceiling contact; hazards, side overlaps and blocked
landing sweeps are rejected. Missing endpoints and disabled senders/receivers are
omitted. Normal discovery radius and node limits still apply.

Portal connector geometry is cached and invalidated by geometry and endpoint
changes; environmental flow remains live. Execution checks the actual entry step
again and uses ordinary walking input to enter. Native cooldowns, occupancy and
per-player teleport predicates govern entry. A busy portal gets neutral input
while waiting. Waiting and transport share a timeout derived from native delays
and animations, bounded to 5–30 seconds. Disabled or broken links stop the action.
Graph connectors line up on open floor before the portal frame. Execution can
start that approach slightly early and brakes for two updates before entering,
allowing the game's preceding lateral movement command to settle at low FPS.
A shallow platform uses a shorter checked approach and three braking updates.
Exit connectors may turn sideways only when a normal sweep clears the trigger;
the trigger exemption remains restricted to outward departure.

During native transport, the action retains its lease with neutral commands.
The temporary disabled chef/collider state is accepted only for an active portal
transition with native teleport state. Pause, focus, ownership, respawn, menu and
round checks remain in force. The bridge never calls the teleport method or
writes the chef's transform. Completion requires native teleport state to finish
and the chef to reach the linked receiver's landing. Moving receiver destinations
are captured when native receiving starts, matching the animation's fixed world
target. Neutral control continues during a checked native fall to the landing;
this shares the portal timeout. `teleportsCompleted` counts
confirmed transitions. A fresh search continues from the actual exit. If its
budget expires, the checked route tail can be retained without trying to walk
across the completed teleport. Lookahead never skips a pending portal.
Stop cancels further navigation; an already-started native animation
may finish.
Moving receivers with temporarily blocked landings hold entry neutrally. If their
motion temporarily disconnects the planned onward route, navigation waits and
replans for at most 15 seconds per interruption, within the overall 45-second
action timeout. It never follows an obsolete cross-island walking segment.

Floor conveyors and supported moving surfaces contribute native surface velocity;
wind is evaluated using current volumes, filters and direction. Travelator corner
velocity retains the entering surface, including across graph edges and remaining
route checks. The graph distinguishes arrivals with different prior surfaces while
sharing their geometry probes. Connectors from the chef use native surface history.
Native floor conveyors are routed **with their direction**, even when ordinary
walking could slowly overpower the belt. Their live world velocity determines
direction, including rotated tiles, borders and corner history. Stopped belts
permit both directions; sideways exits remain available when walking can counter
the cross-flow. Other directed edges are excluded when walking cannot counter
their cross-flow or make forward progress. Conveyor graph lanes additionally
check support around the chef's footprint over the union of adjacent belt tops
and floors. This keeps turns away from unsupported outer lips. Connectors from
the actual body retain native contact clearance so small drift can recover to a
padded lane. Steering preserves the next conveyor turn rather than skipping
several waypoints across a change in surface velocity.
Execution compensates using the chef's actual wind/surface velocities. Countertop
conveyors move the destination rather than the chef. Slipperiness affects momentum
checks and steering; unsupported sliding/override-gravity surfaces are excluded.
The final approach uses the same flow compensation and next-step checks. A running
floor conveyor is not a settled working stance: alignment uses neutral walking,
and the native axes normalize every nonzero input to full speed. The planner
chooses another supported, reachable side; if no stable side exists, that target
is withheld instead of repeatedly drifting and walking upstream during arrival.
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
expires; a separate 500 ms ownership lease permits renewal after frame jitter.
That ownership grace does not extend the checked movement window. Away from the
final stance, the controller may separately certify a short continuation window
(two preceding frame windows plus 20 ms, capped at 100 ms; already slower commands
are unchanged). It checks the entire extra travel plus one fixed physics step.
If the extended corridor is blocked, the shorter certified command remains usable.
Near arrival and during portal entry, the original frame window and braking horizon
remain in force. This prevents normal Update jitter from zeroing a command just
before the native controller reads it.
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
Neutral braking can begin one native movement window before the checked stance,
because the previous movement command can still integrate after an Update changes
input. That prediction never counts as arrival; the actual pose and settling
checks above must still pass.
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
loss of progress, excess duration, query overflow, planning failure, ownership
loss or round change ends the action. Focus loss, pause and temporary input
suppression (including respawn) suspend navigation with neutral input, retaining
the request and adjusting watchdogs before resuming.
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
`phase` distinguishes `walking`, `aligning`, `settling_ground`, `following_support`, `entering_portal`,
`waiting_portal`, `waiting_portal_geometry`, `waiting_portal_route` and `teleporting`; all are interrupted by Stop.

Read-only catalogues can be queried while the game is unfocused, provided the
other native round, pause, menu and player gates pass. Movement still requires
game focus and reports `game_unfocused` otherwise. The browser harness focuses
the game only after the user clicks Navigate, then revalidates the exact target.
Sequences remain internal to the direct-input lease. An autonomous action renews
its own bounded input commands under durable ownership; the decision maker does
not issue keepalive inputs. Closing/releasing its owning connection leaves the
accepted request intact; any client can Stop it. Idle connections may stay open (four
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
chef beyond GroundCast's contact distance. It independently checks a clear landing
below the body, within GroundCast's native downward search range. When the remaining
route provides continuous support, `following_support` can keep walking through
this brief hop. It checks the full landing corridor and an enlarged capsule envelope
covering the current height and upward velocity, the previous horizontal motion,
and the new command. A retained native contact additionally requires checking the
game's slope-relative movement basis and normal velocity. Missing floor, low
ceilings, sharp momentum changes, slippery surfaces, external flow, and proximity
to the final stance prevent this continuation. Other recoverable poses use neutral
input. This is distinct from the maximum step-up height; route probes keep
their original step bounds. A valid native proximity contact can continue a checked
route rather than braking off the stair merely because the body is above a tread.
Current capsule geometry detects temporary penetrations independently of the cached
ground point. An overlap is recoverable only when it is
with ground-layer colliders, the independently probed supported pose is clear,
and native `ComputePenetration` proves an upward resolution within step height,
or a shallow lateral ground resolution (at most one quarter of the native radius
plus the collision skin) with an independently checked landing and supported connector. Deep lateral contacts and unrelated walls are rejected.
Treads and wedges can overlap the capsule simultaneously.
The game resolves that contact under neutral input; the bridge never adjusts the
chef's position. Other blocking overlaps still stop the action. Settling forces
replanning and must finish within the existing two-second stall interval. This
does not allow airborne steering across holes.

The continuous-support height bound allows floating-point roundoff on flat
contacts and 2.5 mm of native sphere-query tangent error at sloped joins. Body
clearance is not a height-discontinuity allowance. Every accepted connector still
requires a clear capsule sweep. If native
motion displaces the chef from a previously valid connector, execution now
replans from the actual position before declaring the destination blocked.
Native sphere casts at internal flat-mesh seams can also report a slightly
tilted contact and a sub-2-mm foot depression. A separate centre ray corrects
that contact only when it hits the same collider's flat plane at the original
contact height. Missing support, slopes, other colliders and ledge heights do
not qualify; the ordinary continuity bound remains unchanged.

The [native headless simulator](native-simulator.md) runs this production
controller with the installed game’s actual physics and named kitchens. Use it
for repeatable traversal tests; the navigation unit fixtures remain available
for isolated geometry regressions.

When `SOUS_CHEF_NAV_TRACE=1`, `control.inputTrace` records actual native reads that
returned zero despite a nonzero navigation command. Each event includes the read
frame, command frame, command age, checked duration, and whether expiry or a native
gate suppressed it. These diagnostics distinguish command expiry from native
contact resolution; tracing does not prolong a command.

Standing-height fluctuations can now retain search progress when a fresh native
start-connector proof has exactly the same ordered nodes and floating-point
costs. This applies only to vertical start/pivot changes with unchanged horizontal
position, shape and movement rules, geometry, support/wind proofs, and cached
neighbour range decisions. Moving-surface starts keep strict invalidation. This
is an equivalence check, not a positional tolerance: changed costs or native
connector vetoes restart the search. Diagnostics count `startProofRevalidations`.

Walking overlap/sweep queries keep their original envelope but allow a shallow
horizontal box contact to consume at most one native chef contact offset (capped
at 0.01) beyond the existing skin. This handles a raised floor under a native-height
ceiling and the corresponding stable floor contact. The bottom exception requires
a ground-mask box; neither exception applies to hazards or side/slope contacts.
Lower ceilings and deeper penetrations remain blocked, and changing contact offset
invalidates cached geometry queries. See the `s_sushi_1_1` native pickup/placement
regression in [validation.md](validation.md).
