# Autonomous tasks

`ActionClient.run_task(action, repetitions=1, items=None, player_id=1)` owns one
request and control lease until all steps and repetitions finish. Use `None` for
unlimited repetitions; finite counts are 1–10. No model is invoked by the executor.
Each repetition executes a complete batch. Completion leaves P2 idle.

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
If no safe counter is available, the task waits with the cargo and Stop remains
available. Task discovery only observes state; it never moves items. Explicit
interaction and logistics commands retain their existing cargo preconditions.

Environmental blocks wait indefinitely with neutral input and bounded periodic
revalidation. Completed steps are retained. Explicit Stop, release, disconnect,
focus loss, reassignment, respawn and round changes still cancel and restore
native input bindings. Changed source identities wait for the original source before an unpressed step.
Changed cargo or unverified button outcomes fail safely with cargo retained. Waiting does not replay a successful step or disposal.

The existing interaction/logistics recovery window is also indefinite; time spent
recovering suspends finite watchdogs. Button outcome verification remains bounded.
New tasks and their child actions have no overall deadline.
Pausing suspends an active autonomous task/action with neutral input, preserving
its request, cargo and ownership. Unpausing revalidates navigation and resumes
with action watchdogs adjusted for the pause. Stop and disconnect still cancel
while paused; raw manual input leases are not retained.

The localhost Tasks panel uses `/api/task`, with the current `sessionId` and
`arguments` containing `action`, `repetitions` (integer or null), and preparation
parameters where required. `catalog.taskOptions` contains only currently eligible
tasks. The server and executor both revalidate before starting. Stop works while
waiting, working or between repetitions. Browser disconnection closes ownership
as before. There is no task queue or voice/Jev integration yet.

Wire: `task REQUEST ACTION 1..10|forever`; preparation additionally takes player
`1|3|4`, followed by 1–10 triples of base64url selector, preparation state and
quantity `1..10`. Task lines are bounded to 2048 ASCII bytes; legacy commands keep
their 256-byte parser bound.

## Prepare and give

`prepare_near_player` takes `items=[{'ingredient': 'chicken', 'preparation':
'chopped', 'quantity': 2}, {'ingredient': 'pot', 'preparation': 'empty'}]`.
Preparation states are `raw`, `chopped`, `cooked`, `mixed`, and `empty` (utensils
and plates). Each repetition prepares the entire list. Native ingredient IDs
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

The scheduler starts passive processing before short deliveries, performs other
requested preparation while equipment runs, and collects completed containers
before starting more work. Processing remains native and uses native progress/time.
Missing ingredients defer only their own units; other available requested food
continues. On moving supports, an incomplete background catalogue may propose a
local board or processor for the active action planner to verify before use.
It retains exact containers and stations between visits. Native remaining cooking
time and conservative estimates of travel/chopping prevent starting long work
just before a pot finishes. Full equipment defers new cooking units instead of
holding an ingredient that would prevent collecting an earlier unit. Ready
contents prefer a serving plate/utensil, leaving the processor available.

Chopping boards are scheduled the same way. Food already on a board, such as food
the human threw there, is chopped in place and preferred to an equal-stage copy
elsewhere. Food needing a board is collected only when a reachable board can
receive it, so P2 never holds cargo while it alone could free a board. When every
reachable board holds food the remaining work cannot use now (a later repetition's
ingredient or unrelated food), P2 moves the nearest board's contents to a counter;
a board someone is using is left alone. If a board fills after a pickup or during
placement, P2 sets its cargo down on a counter and reschedules. Between
repetitions, the task continues with the same local sources the scheduler accepts.

Loose food in front of a station can take the game's interaction selection from
it. If P2 is positioned at its target but the game selects a different loose
object, waiting cannot help. Before any press, the task sets down P2's cargo,
moves that object to a counter, and reschedules.

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
waits for evidence. Forecasts never replace the final accessibility check. Transport delivery completes only after the
exact item reaches a human-accessible holder or the human's hands. Safe empty
countertops are used when putting equipment down; conveyor belts are not storage.
An armed native throw is committed: releasing its use button also launches during
cleanup. The executor keeps observing that cargo if its final proof changes or a
native launch arrives early. A failed primitive handoff can still fulfill the task
when a fresh native route proves the exact item is collectible within five walking
units of the human. This never replays the throw or takes another ingredient.
In `prepare_near_player`, an unverified pass is never counted as delivered and
does not end the task: cargo still in P2's hands is offered again, and lost cargo
(for example, food that bounced into the river) is prepared again. Other tasks
still stop on an unverified pressed handoff.

## Take ready food off heat

`take_ready` is offered only for accessible, nonempty heat equipment with
observable native cooking state. It waits for the selected food to finish, then
prefers a compatible clean plate or empty utensil. The new `collect_contents`
primitive uses the normal native placement button and verifies both source
decrease and recipient increase while retaining their identities. If no serving
utensil is available, P2 removes the original pot/pan/food and parks it on a safe
counter. Completed/overdoing food is ready; ruined contents are excluded.

## Wash dishes

`wash_dishes` requires an accessible sink and obtainable dirty stacks (or plates
already in that sink). It snapshots the currently obtainable stacks, merges them
using native placement/pickup, carries the whole batch to the sink, and washes
until the sink empties. New dirty stacks are handled by the next repetition.
An already-held dirty stack can join the batch; unrelated cargo is parked before
collecting dishes or using the sink.

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
chef.run_task('prepare_near_player', items=[
    {'ingredient': 'chicken', 'preparation': 'chopped', 'quantity': 2},
    {'ingredient': 'pot', 'preparation': 'empty', 'quantity': 1},
])
```

The integer/null repetition parameter is ready for a future caller to choose;
there is no Score primitive or Jev dependency in these actions.

See [native verification and reproduction steps](task-verification.md) for tested
scenarios, fixture boundaries, evidence paths and remaining map coverage.
