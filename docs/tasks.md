# Autonomous tasks

`ActionClient.run_task(action, repetitions=1, items=None, player_id=1)` owns one
request and control lease until all steps and repetitions finish. Use `None` for
unlimited repetitions; finite counts are 1–10. No model is invoked by the executor.
Each task owns its repeat policy under that lease. Serving counts verified dishes,
washing counts batches, and preparation tracks the demand for each requested
ingredient across the entire request. Completion leaves P2 idle.

Held items do not hide otherwise eligible tasks. A task uses an already-held
ingredient, empty utensil, dirty stack or tool when it supports the requested
work. Preparation prioritizes useful held food, including native chopped outputs,
and can switch to another unfinished requested ingredient after a catch. Raw
requests still require raw food; completed quantities and prior deliveries are
not counted again.

When cargo blocks the next step, P2 puts it on a reachable compatible empty
counter and resumes the same request. This also handles catches during walking
or chopping. Partially worked food stays on its board; a dirty stack caught while
washing joins the sink. Serving tasks can use a compatible plate already in hand.
If no safe counter is available, most tasks wait with the cargo and Stop remains
available. `deliver_completed_dishes` instead uses a checked floor drop after trying nearby
counters, as described below. Task discovery only observes state; it never moves items. Explicit
interaction and logistics commands retain their existing cargo preconditions.

Environmental blocks wait indefinitely with neutral input and bounded periodic
revalidation. Completed steps are retained. Stop explicitly cancels the request
and restores native input bindings. Focus loss, pause and temporary input
suppression (including respawn) suspend it with neutral input. Closing/reloading
the browser, a missing heartbeat, client release or socket disconnect does not
cancel an accepted job. Player reassignment, round changes and plugin shutdown
still clear work tied to the old chef/kitchen. Changed source identities wait for the original source before an unpressed step.
Cargo identity checks still protect placements, transfers and disposal.
Unverified button outcomes are never blindly replayed. Waiting does not replay a
successful step or disposal.

The existing interaction/logistics recovery window is also indefinite; time spent
recovering suspends finite watchdogs. Button outcome verification remains bounded.
New tasks and their child actions have no overall deadline.
Suspension preserves the active task/action's request, cargo and ownership.
Returning to the focused, unpaused kitchen revalidates navigation and resumes
with action watchdogs adjusted for the time away. The kitchen itself continues
running while unfocused, so native cooking and moving objects can change.
Stop still cancels while suspended; raw manual input leases are not retained.

The localhost Tasks panel uses `/api/task`, with the current `sessionId` and
`arguments` containing `action`, `repetitions` (integer or null), and preparation
parameters where required. The UI always lists all eight tasks, while
`catalog.taskOptions` contains only currently eligible tasks and controls whether
Start task is enabled. Preparation choices use the level-specific
`catalog.displayOptions.preparationOptions`; the live eligibility lists remain
separate. The server and executor both revalidate before starting. Stop works while
waiting, working, suspended or between repetitions, including from a new tab
or a reconnected client. Reconnection observes the original job without replaying
its command. There is no task queue or voice/Jev integration yet.

Wire: `task REQUEST ACTION 1..10|forever`; preparation additionally takes player
`1|3|4`, followed by 1–10 triples of base64url selector, preparation state and
quantity `1..10`. Task lines are bounded to 2048 ASCII bytes; legacy commands keep
their 256-byte parser bound.

## Prepare and give

`prepare_near_player` takes `items=[{'ingredient': 'chicken', 'preparation':
'chopped', 'quantity': 2}, {'ingredient': 'pot', 'preparation': 'empty'}]`.
Preparation states are `raw`, `chopped`, `cooked`, `mixed`, and `empty` (utensils
and plates). A finite request owes exactly `repetitions × quantity` of each
entry. A missing ingredient does not hold up the next repetition of another
entry. Native ingredient IDs
from the catalogue also work. Chopping follows the native output prefab;
cooking/mixing uses compatible empty containers on equipment, or stages the
ingredient while retrieving and installing an empty compatible container.
Processed requests also recognize later stages in that native work-output chain:
supplied chopped food can go directly into compatible equipment. Sources needing
less chopping take priority. Raw requests retain exact raw identities.

The localhost ingredient chooser is a dropdown populated from
`catalog.preparationOptions`: native dispenser roots for this kitchen, or observed
utensil types when `empty` is selected. The executor remembers exact objects it
successfully handed off until the round resets; later preparation requests and
repetitions leave those objects for the human and fetch a new dispenser item.
This reservation does not alter the explicit low-level pickup commands.

Storage/handoff destinations must have an empty native holder. An empty plate on
a counter is occupied: placing food there may transfer contents while retaining
the carried object. A movement stall before any button press selects an alternate
destination; if all alternatives are blocked, the same task waits and retries.

Preparation visits entries in list order, respecting quantity: two A and one B
normally give A, A, B, A, A, B. With A, B, C and no B available, P2 gives A, C,
A, C. B becomes eligible again when its source or equipment is available. A
useful catch or a pan about to burn can change the order. Finished loose food
and food on boards/counters are candidates at every dispatch, including after
a catch. A new raw potato cannot displace an already chopped potato for the
same chopped request. Items already handed to the human stay reserved.

Cooking and mixing reserve one unit of demand per occupied container. The
scheduler compares native time remaining with estimated travel and chopping
time. It can chop a second chicken while the first cooks, even with only one pan:
the second portion stays on its board until it can be loaded. Ready containers
are collected in order of time remaining before ruin. A pan removed or emptied
by another chef no longer owns that unit of demand. A blocked handoff can be
parked on a safe counter while other work proceeds; P2 remembers the exact meal
and comes back for it. Without safe storage it retains the cargo.

The action receipt's `details.preparation` lists delivered quantities, processing
and transport reservations, and any parked handoff per entry. A belt dispatch
reserves its unit; it counts after the recipient can collect it.
`completedRepetitions` is the number of full lists delivered. For example,
three A and zero B on a one-A/one-B request still means zero full repetitions.
The wire request and Stop behavior are unchanged.

Chopping boards are scheduled the same way. Food already on a board, such as food
the human threw there, is chopped in place and preferred to an equal-stage copy
elsewhere. Food needing a board is collected only when a reachable board can
receive it, so P2 never holds cargo while it alone could free a board. When every
reachable board holds food the remaining work cannot use now (a later repetition's
ingredient or unrelated food), P2 moves the nearest board's contents to a counter;
a board someone is using is left alone. If a board fills after a pickup or during
placement, P2 sets its cargo down on a counter and reschedules. Between
repetitions, the task continues with the same local sources the scheduler accepts.

An ingredient backpack is a supply only while worn by another chef. P2 approaches
from the dispensing side and checks the native pickup handler before pressing.

Loose food in front of a station can take the game's interaction selection from
it. If P2 is positioned at its target but the game selects a different loose
object, waiting cannot help. Before any press, the task sets down P2's cargo,
moves that object to a counter, and reschedules.

Delivery preference: a compatible counter within five walking units of the human,
then a checked pass/drop at the human, then a directed conveyor or returning
counter. Both chefs use the same navigator implementation in independent read-only
observers. Throw plans check actual throwability. Conveyor traversal follows the
native next receiver, including rotation/direction, and rejects loops/dead ends.
Moving-counter forecasts require an observed human-accessible stop, then motion
toward that stop or an observed complete return cycle. Unknown/one-way motion
waits for evidence. A floor pass needs a walkable landing from the receiving
chef’s side and a pickup route to the settled item. P2 skips a rejected release
stance while the receiver and support stay in the same relative positions.
Transport delivery completes only after the
exact item reaches a human-accessible holder or the human's hands. Safe empty
countertops are used when putting equipment down; conveyor belts are not storage.
An armed native throw is committed: releasing its use button also launches during
cleanup. The executor keeps observing that cargo if its final proof changes or a
native launch arrives early. A failed primitive handoff can still fulfill the task
when a fresh native route proves the exact item is collectible within five walking
units of the human. This never replays the throw or takes another ingredient.
In `prepare_near_player`, an unverified pass is never counted as delivered and
does not end the task: cargo still in P2's hands is offered again, and lost cargo
(including cargo lost while walking to a handoff) is prepared again. Other tasks
still stop on an unverified pressed handoff.

## Take ready food off heat

`take_ready` is offered only for accessible, nonempty heat equipment with
observable native cooking state. It waits for the selected food to finish, then
prefers a compatible clean plate or empty utensil. The `collect_contents`
primitive uses the normal native placement button and verifies both source
decrease and recipient increase while retaining their identities. If the station
cannot accept an available serving utensil, or time runs short before the press,
P2 removes the original cookware, parks it on a safe counter, and checks whether
it can plate the food there. Completed/overdoing food is ready; ruined contents
are excluded.

## Wash dishes

With an accessible sink, `wash_dishes` snapshots the currently obtainable dirty
stacks, merges them using native placement/pickup, carries them to the sink, and washes
until the sink empties. New dirty stacks are handled by the next repetition.
An already-held dirty stack can join the batch; unrelated cargo is parked before
collecting dishes or using the sink. With no reachable sink, P2 can pick up a
water gun, approach a checked spray position for each washable stack in the
batch, hold Use while the target is in the native spray, and park the gun.
A stack with no reachable spray position does not enable the task.

## Deliver completed dishes

`deliver_completed_dishes` monitors for a plated meal matching a current native order,
including its required plate type. It is available whenever P2 can reach a serving
window, even if no meal is ready yet. Empty, incomplete, mismatched and already
reserved plates, and plates carried by another chef, are excluded. A ready plate
already in P2's hands takes priority and goes straight to the serving window.
Among other ready dishes, P2 serves the one whose matching order has the fewest
seconds remaining before expiry. Walking distance breaks ties. This uses the
native ticket's actual remaining seconds, rather than its percentage bar; a more
urgent meal appearing before pickup can replace an unpressed selection.

Otherwise P2 uses normal `pick_up` and `place_at` actions to collect and serve the
exact dish. Unrelated held cargo is first placed on the nearest compatible empty
counter within five walking units. After a bounded nearby-counter search, if none
can be found or the checked alternatives cannot be used, normal placement drops
the cargo on a supported floor, away from its next pickup route. P2 turns away
from native placement handlers or takes a short checked walk to open floor
before dropping; this never uses the throw button. Serving windows, boards and
conveyors are excluded from storage. The floor release reuses the basic action's
landing/identity checks, then resumes the original dish pickup.

The task rechecks the selected dish and order before pressing. A dish taken by
someone else or a ticket expiring during travel returns it to monitoring. Native
serving immediately removes the plate from the window, so `place_at` verifies the
window's delivery message for the exact original plate instead of relying on an
occupied holder or a score change. Each verified serve completes one repetition;
use unlimited repetitions to keep delivering dishes until Stop.

## Deliver dirty dishes

`deliver_dirty_dishes` is offered only with obtainable dirty stacks, a sink the
human can reach, and a completed P2 route search proving no accessible sink.
An incomplete P2 search cannot authorize this option. It shares batch collection
with washing and the same player/conveyor/moving-counter delivery planner with
ingredient preparation. Dirty stacks use native drop/placement; they are never
treated as throwable ingredients.

## Put out fires

`put_out_fire` requires an observed native fire and obtainable extinguisher.
It picks up the tool, searches supported walking stances within the native spray
range, faces each fire, and holds normal use input while the native spray predicate
confirms coverage. It observes `ServerFlammable.OnFire()` to finish spraying.
It rescans after each fire, including newly spread fires, and finishes when no
remaining fire has a certified reachable spray stance. A selected fire blocked
during approach/spraying is retained and retried indefinitely. The tool returns
to its observed holder or an available counter when possible.

## Toss burnt food

`toss_burnt_food` requires obtainable food in the native ruined state. It captures
the current burner, picks up the exact food/utensil, and uses native disposal.
A surviving empty utensil returns to that burner, otherwise another compatible
available burner, otherwise an available counter. Incomplete route discovery cannot prematurely
select the countertop fallback. Occupied burners are never cleared; an unpressed
placement blocked by changed contents can select another allowed destination. If no disposal/return route exists, it waits holding the original cargo.

## Monitor equipment

`monitor_equipment` waits for any accessible native cooker, heated station, or
mixer/blender to finish. It uses shared ready-food collection and serving/parking,
then completes one repetition. Serving-tool travel is bounded by the native
remaining spoil window; a distant plate cannot delay removing a ready container. Use unlimited repetitions for ongoing monitoring,
including idle periods when equipment is empty. Cooking station variants use the
same native cooking handler instead of recipe/name-specific timing guesses.

## Examples

```python
chef.run_task('wash_dishes', repetitions=3)
chef.run_task('monitor_equipment', repetitions=None)  # Stop cancels the loop.
chef.run_task('deliver_completed_dishes', repetitions=None)
chef.run_task('prepare_near_player', items=[
    {'ingredient': 'chicken', 'preparation': 'chopped', 'quantity': 2},
    {'ingredient': 'pot', 'preparation': 'empty', 'quantity': 1},
])
```

The integer/null repetition parameter is ready for a future caller to choose;
there is no Score primitive or Jev dependency in these actions.

See [native verification and reproduction steps](task-verification.md) for tested
scenarios, fixture boundaries, evidence paths and remaining map coverage.
