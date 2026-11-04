# Player 2 actions

Bridge 0.5 adds outcome-checked interactions on the existing control endpoint.
These run inside Unity, including navigation and alignment. No Python heartbeat
or model call drives their execution. Placement, ingredient addition, disposal
and throwing use Player 2's **currently held** item. Bridge 0.6 adds the finite
logistics jobs below. Ready container contents can be collected into a compatible held plate/utensil
with `collect_contents`; arbitrary container-to-container recipes remain separate.

| Python call | Scope and success evidence |
| --- | --- |
| `navigate_to(destination_id)` | Existing walking action; see [navigation](navigation.md). |
| `pick_up(target_id)` | Approach the specified source, tap pickup once, confirm the exact item, a plate removed from its stack, or the dispenser's ingredient composition in P2's hands. Requires empty hands. |
| `place_at(target_id)` | Approach, tap placement once, confirm the target holds the item, its plate stack increased, or its sink accepted dirty dishes. Occupied non-stack surfaces require an explicit ingredient-add action. Disposal requires `discard`. |
| `collect_contents(target_id)` | Collect ready contents into a held compatible plate/container using native placement. Verify recipient increase, source decrease and retained identities. |
| `add_ingredient(target_id)` | Add the held single ingredient to the target or its explicitly attached container. Confirm matching contents increased and P2 no longer holds that ingredient. Refuses container-to-container transfer. |
| `work_at(target_id, until='work_complete', timeout=60)` | Work until chopping completes, one plate is cleaned, or the sink empties. Detect expected replacement ingredient composition when chopping replaces an object. |
| `activate(target_id, desired_state='trigger')` | Trigger a native interaction and observe its P2-specific acknowledgement; or request `on`/`off` where an associated native toggle state is observable. Unsupported toggle state is rejected. Already-satisfied states need no button press. |
| `discard(bin_id)` | Dispose of the held item or empty its contents through a native rubbish bin; verify disappearance/deactivation or emptied contents. Preserves utensils according to native disposal behavior. |
| `wait_until(target_id, condition, timeout=60)` | Hold neutral input without navigating until the requested state is observed, or timeout. |
| `throw_to(target_id)` | Plan a native throw of the held item to a station/container; walk to a validated release stance, aim, press/release use, and confirm receipt. |
| `throw_to_player(player_id)` | Pass to another local chef (1, 3, or 4), accounting for current movement, available hands and native catch angle/distance. Confirm the item is held by that player. Never controls their inputs. |
| `stop()` | Interrupt either navigation, an interaction, a wait or raw input. |

`target_id` comes from `available_actions()['targets']`; these include discovered
objects without a current walking route, allowing throws across kitchen divisions
and waits for a route to reopen. `destinations` retains only checked walking
destinations. A listed target is not a promise that every action is compatible.
Starting and executing revalidate native predicates, ownership and target contents.
IDs remain scoped to the current game session and round.
Targets include `operations`, `workConditions`, `waitConditions` and
`activationStates` to describe compatible request combinations. A separate
`players` list binds logical player IDs to current chef object identities for
pass requests. The browser rechecks these identities and operations before
dispatch. Throw operations and their parameter enums now require a checked
native arc and a reachable release stance, including height differences. A
target above the current arc can remain available if P2 can walk to a higher
release stance. Downward throws use their actual longer flight time and range;
player catches use three-dimensional distance and the native forward scan.
See the [native height results](throw-heights.md) for upward/downward scenarios.

Each compatible target/player has a `throw` preview with `state` (`pending`,
`feasible`, or `unavailable`), `reason` and, when feasible, `plan`. Planning runs
in bounded read-only Update slices while the catalogue is being observed.
Pending choices are withheld; poll again to discover completed plans.
`throwOptionsComplete` reports whether the currently observed preview queue has
finished, independently of walking discovery's `complete` flag. Negative
previews are retried after two seconds. Movement, facing, cargo and receiver
changes invalidate cached proofs, and positive arcs/stances are rechecked when
listed. Execution plans again and revalidates at release because other chefs,
obstacles and wind can change. Nearby native counter/ground handoffs remain
available when their own checked handoff plan succeeds.

Ingredient containers use their native catch triggers, including rice entering
a pot below its solid top. Both a cooker and its current pot can be throw targets;
capacity, compatibility and clear-flight checks still apply. See the
[cooker regression checks](throw-cookers.md).

```python
from clients.actions import ActionClient

with ActionClient() as chef:
    print(chef.available_actions())
    chef.pick_up('d7')  # Replace with a current catalogue target.
    # Observe chef.status()['action'] until it reaches a terminal state.
    # Then e.g. chef.place_at(sink_id), followed by:
    # chef.work_at(sink_id, until='sink_empty', timeout=60)
```

The CLI waits for completion and keeps the owning connection open:

```sh
python3 clients/actions.py pick_up d7
python3 clients/actions.py work_at d12 --until one_plate_clean --timeout 20
python3 clients/actions.py activate d9 on
python3 clients/actions.py wait_until d4 cooked --timeout 60
python3 clients/actions.py throw_to d8
python3 clients/actions.py throw_to_player 1
```

## Logistics jobs (bridge 0.6)

These jobs retain one ownership lease through child pickup, navigation and
verified delivery. They have a 120-second overall deadline, including travel;
child navigation and interaction watchdogs still apply. An unrelated held item
causes `hands_full`. Failure preserves cargo and reports the completed delivery
count, chosen source/destination and the child receipt. Stop, disconnect and all
normal native control gates cancel the complete job.

Accepted jobs and interaction actions now wait **indefinitely** through a missing
target, unavailable walking route, or native pickup/placement/use readiness.
Input is neutral while blocked, with bounded route revalidation every 100 ms.
Environmental waiting suspends finite watchdogs and retains the ownership lease,
request, exact cargo and completed child steps. Recovery reports `graceSeconds:
null`, `indefinite: true`, elapsed time and interruption count.

Standalone actions retain identity checks and bounded verification after a button
press. Explicit stop/release, disconnect, focus loss, pause, respawn, reassignment
and round changes cancel immediately. New [autonomous tasks](tasks.md) additionally
compose these primitives, handle environmental changes before unpressed steps,
and repeat 1–10 times or indefinitely without model calls.

| Python call | Behavior |
| --- | --- |
| `move_item(item, destination)` | Move the exact observed item, or a station's current occupant, to the destination. Already-delivered items succeed without another pickup. |
| `fetch_ingredient(ingredient, destination)` | Resolve a native ingredient definition, prefer an existing reachable unused matching ingredient over a dispenser, then deliver one. A single ingredient delivered into a compatible container uses `add_ingredient`. |
| `get_plate(destination)` | Retrieve one observed clean, empty plate, including from a clean stack. Filled dishes and dirty stacks are excluded. |
| `get_utensil(utensil_type, destination)` | Retrieve an empty utensil of the requested native item-definition type. Deterministic aliases include `pan`/`frying pan`, `pot`/`cooking pot`, and `bowl`/`mixing bowl`. |
| `stage_item(item, handoff_location)` | Move an intact item to the specified handoff surface. It does not merge ingredient contents into a container. |
| `return_utensil(utensil)` | Return an empty utensil to its first observed station, otherwise a compatible empty cooker or counter. Native attachment restrictions are checked; occupied stations are not cleared automatically. |
| `clear_station(station, destination)` | Move the current occupant and verify the source station is free. Clean plate stacks are handled as a finite batch of original plate identities, up to 64. New arrivals or changed contents block further collection rather than extending the job indefinitely. |

Items and stations use current `d...` IDs. `available_actions()` now also returns
`items`, `ingredients`, `utensilTypes`, `deliveryDestinations` and `jobOptions`, plus explicit
`logisticsObservationErrors` where a definition/contents read is unavailable.
Item records include source identity, definition, empty/clean/in-use state and
route distance where known. Empty counters are included in walking discovery
even when P2 has empty hands, so deliveries can be checked before pickup.

`jobOptions` maps each logistics operation to rows of
`{"sourceId": "...", "destinationIds": ["d..."]}`. The source is an item ID,
ingredient ID, utensil type, or station ID, according to the job; `get_plate`
uses an empty source string. `return_utensil` includes its automatically chosen
destination. Only operations with at least one valid pair appear in `actions`.
The parameter enums contain that job's available sources/destinations; use the
pair rows to check their relationship rather than taking their Cartesian product.

Pairs require certified current walking routes, available pickup, compatible
native placement, and either free hands or the exact intended cargo already held.
In-use items, occupied/incompatible destinations and no-op station clear/returns
are withheld. An incomplete search withholds unconfirmed routes. Observation
previews use a read-only prospective carrier; they never move cargo or change
the actual chef carrier. Dispenser prefabs lack runtime attachment interfaces,
so empty-holder previews check their physical attachment definition and native
placement callbacks. Actual spawned cargo is checked again before delivery.
Request acceptance and arrival both revalidate; changing geometry or contents
can still block an already-started job safely.

Use a catalogue ingredient ID such as `i42` for an exact definition, or its
observed name. A raw name such as `tomato` also matches `Tomato_Raw`; it does not
silently substitute chopped/cooked states. Ambiguous names require the catalogue
ID. Utensil types come from native item definitions, not guessed object names.
Home locations are observed holder relationships, retained only within the round.
Ingredient identity includes `PreparationContainer.m_ingredientOrderNode` for
assembly bases such as buns. A bun with fillings cannot substitute for an empty
bun. Dispenser pickup verification uses this same identity when the prefab has
no runtime order composition, while the spawned base has one.

```python
chef.move_item(item_id, counter_id)
chef.fetch_ingredient('tomato', pan_or_counter_id)
chef.get_plate(counter_id)
chef.get_utensil('pan', cooker_id)
chef.stage_item(item_id, handoff_counter_id)
chef.return_utensil(pan_id)
chef.clear_station(board_id, spare_counter_id)
```

The CLI uses the same positional arguments:

```sh
python3 clients/actions.py fetch_ingredient tomato d12
python3 clients/actions.py move_item d7 d12
python3 clients/actions.py get_utensil 'mixing bowl' d12
python3 clients/actions.py return_utensil d7
```

Wire additions are `move_item request item_id destination_id`, `stage_item`
and `clear_station` with the same two-ID structure, `get_plate request destination_id`,
`return_utensil request item_id`, and `fetch_ingredient`/`get_utensil request selector destination_id`.
Selectors use `s` plus unpadded base64url UTF-8, bounded to 64 characters / 96 UTF-8
bytes; `ActionClient` handles this encoding. Control characters, malformed tokens,
invalid IDs and extra arguments are rejected before dispatch.

## Conditions and lifecycle

Work conditions: `work_complete`, `one_plate_clean`, `sink_empty`.
Wait conditions additionally include `occupied`, `empty`, `cooked`, `mixed`,
`reachable` and `ready`. `ready` means a native pickup, placement or use is
available **from the current pose**. `cooked`/`mixed` require the native
`Completed` processing state, rather than treating a burnt item as ready.
Waits do not reserve or perform the work. Counts can change through human work;
condition success reports the requested observed state, not exclusive credit.

Work/wait timeouts are 0.1–120 seconds, measured from request acceptance and
including travel. Other interaction actions have a 60-second overall bound;
walking retains its existing 45-second bound. A lack of processing progress for
five seconds ends work. Pickup/placement/activation get one button press and up
to two seconds to verify it; they never retry a contextual toggle blindly.

`control.action` reports `state` (`idle`, `running`, `succeeded`, `blocked`,
`failed`, `cancelled`), operation, request ID, target ID, phase, revision,
monotonic start/deadline, and outcome details. `control.navigation` still reports
walking and alignment, including child navigation. A child's arrival does not
release ownership or complete its parent action. Failed request preconditions
return `ok: false`; the action receipt represents the last accepted action.
For a standalone `navigate_to`, use the existing navigation receipt.

Raw input cannot override a running action. Disconnect, focus loss, pause,
reassignment, respawn, round change and explicit release cancel it. Target or
contents changes can block it. Cancellation never parks or discards food.
Native sticky work is ended using the normal client interaction-end message,
only when P2's current interaction matches the action's station; then input
bindings are restored. This narrow cleanup supplements logical-input execution
without directly changing progress, inventory or station state.

## Throw planning and limitations

The planner invokes the installed game's read-only
`ServerAttachmentThrower.CalculateThrowVelocity` for the actual direction. Its
returned vector is the exact velocity passed to the native velocity setter;
there is no assumed throw speed or inherited chef velocity. A temporary native
throw callback also records `observedLaunchVelocity` immediately after the
velocity setter. The callback is removed on completion/cancellation. Hold
duration does not charge throw strength; throwing occurs on **use release**.

The planner inverts the flight to form candidate release rings, also trying the
chef's current position. It checks live supported stances and walking routes,
the carried item's release offset and collider size, supported source-floor
heights, receiving height, gravity, live rigidbody drag, and the fixed physics
interval. Updrafts participate in inverse flight-time and release-position solving. Free-flight damping and gravity follow an independent
implementation of the integration in
[NVIDIA's PhysX 3.4 source](https://github.com/NVIDIAGameWorks/PhysX-3.4/blob/master/PhysX_3.4/Source/LowLevelDynamics/src/DyBodyCoreIntegrator.h).
Flight sweeps check obstructions; receiver-specific native predicates check
item compatibility. Candidate solving has a 25 ms budget after walking-graph
preparation. Exhaustion can return `throw_planning_budget_exceeded`.

Generated walking stances are internal, not caller-supplied coordinates. Before
release the executor rechecks the trajectory from the actual held-item pose and
requires tight aim. Player passes use a constant-velocity intercept and the
native catch cone, available hands and catch distance. For items with native
attachment wind receivers, collider overlap queries apply each volume's layer
filter and velocity. Native wind translates a detached item each fixed step;
leaving a volume additionally adds that source's velocity to the rigidbody.
The solver models both effects, including vertical wind and overlapping volumes.
Items without those native receivers do not gain invented wind forces.
Unsupported gravity/timestep/geometry and incompatible receivers are rejected.
No angle, force, transform, item ownership or velocity is directly changed to
make a throw succeed. Facing uses the same native turn helper as navigation.

For `throw_to_player`, a player within 1.6 world units on the same deck first
gets a nearby handoff attempt: native placement onto a usable empty counter,
or a checked ground drop within reach. Ground drops require predicted floor
support, compatible elevation and no detected fire/respawn/portal hazard along
the drop. The executor uses pickup/drop rather than use/throw and verifies the
item on that counter, in the player's hands, or stably supported nearby. An
occupied-handed player can still receive a staged nearby item. A blocked drop
falls back to throw planning; the action never drops blindly over an edge.

Throw success requires an observed holder relationship or the expected ingredient
appearing in the receiving container with its source consumed. Release alone
is not success. A missed catch reports `throw_missed`; an interrupted throw
already in flight records `throwReleased: true`. Stop cannot recall a projectile.
Wind volumes are sampled from current native state; future changes, native
Update/FixedUpdate ordering, human movement after release and changing collision
geometry can invalidate a prediction. The implementation is **not a live-verified perfect-throw guarantee**.

The [2026-11-02 native corrections and verification](throw-fix.md) replace the
oversized pivot sphere with native collider queries, use actual receiving
surfaces, and account for the chef's forward catch scan. Planning continues in
bounded Update slices and reserves clearance for walking arrival error. Requests
without a supported clear throw/catch window retain cargo and report a blocker.

## Wire additions

ASCII, LF-terminated requests on port 24703; replies remain NDJSON and control
protocol version 1. Existing command and lease bounds remain unchanged.

```text
pick_up request_id d7
place_at request_id d8
add_ingredient request_id d9
work_at request_id d10 work_complete 60000
activate request_id d11 on
discard request_id d12
wait_until request_id d13 cooked 60000
throw_to request_id d14
throw_to_player request_id 1
```

The final argument to work/wait is integer milliseconds. Unknown conditions,
states, player IDs, extra arguments and unbounded timeouts are rejected before
dispatch. There is no `transfer_contents` command.

## Validation

`python3 scripts/check.py` builds against the installed game assemblies, audits
observation fields and runs core, navigation, action executor, ballistic and
Python tests. Action fixtures run the production executor against explicit
native-state doubles; they check contextual-input outcomes and race handling.
Analytical geometry and free-flight tests do not reproduce the complete Unity
physics runtime. The game was not opened for validation of this addition.
