# First-layer Player 2 actions

Bridge 0.5 adds outcome-checked interactions on the existing control endpoint.
These run inside Unity, including navigation and alignment. No Python heartbeat
or model call drives their execution. All act on Player 2's **currently held**
item; fetching an item and container-to-container `transfer_contents` belong to a
higher layer and are not implemented here.

| Python call | Scope and success evidence |
| --- | --- |
| `navigate_to(destination_id)` | Existing walking action; see [navigation](navigation.md). |
| `pick_up(target_id)` | Approach the specified source, tap pickup once, confirm the exact item, a plate removed from its stack, or the dispenser's ingredient composition in P2's hands. Requires empty hands. |
| `place_at(target_id)` | Approach, tap placement once, confirm the target holds the item, its plate stack increased, or its sink accepted dirty dishes. Occupied non-stack surfaces require an explicit ingredient-add action. Disposal requires `discard`. |
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

Static inspection of this installed build establishes that
`ServerAttachmentThrower.CalculateThrowVelocity` uses horizontal `m_throwForce`
and vertical `force * tan(m_throwInclination)`. Hold duration does not charge
throw strength. The native input implementation throws on **use release**.

The planner inverts the flight to form candidate release rings, also trying the
chef's current position. It checks live supported stances and walking routes,
the carried item's release offset and collider size, gravity, drag, and the
fixed physics interval. Free-flight damping and gravity follow an independent
implementation of the integration in
[NVIDIA's PhysX 3.4 source](https://github.com/NVIDIAGameWorks/PhysX-3.4/blob/master/PhysX_3.4/Source/LowLevelDynamics/src/DyBodyCoreIntegrator.h).
Flight sweeps check obstructions; receiver-specific native predicates check
item compatibility. Candidate solving has a 25 ms budget after walking-graph
preparation. Exhaustion can return `throw_planning_budget_exceeded`.

Generated walking stances are internal, not caller-supplied coordinates. Before
release the executor rechecks the trajectory from the actual held-item pose and
requires tight aim. Player passes use a constant-velocity intercept and the
native catch cone, available hands and catch distance. Wind along the arc,
unsupported gravity/timestep/geometry and incompatible receivers are rejected.
No angle, force, transform, item ownership or velocity is directly changed to
make a throw succeed. Facing uses the same native turn helper as navigation.

Success requires an observed holder relationship or the expected ingredient
appearing in the receiving container with its source consumed. Release alone
is not success. A missed catch reports `throw_missed`; an interrupted throw
already in flight records `throwReleased: true`. Stop cannot recall a projectile.
Human movement after release and changing collision geometry can invalidate a
prediction. The implementation is **not a live-verified perfect-throw guarantee**.

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
