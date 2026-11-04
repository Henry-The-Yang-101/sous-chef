# Autonomous tasks

`ActionClient.run_task(action, repetitions=1, items=None, player_id=1)` owns one
request and control lease until all steps and repetitions finish. Use `None` for
unlimited repetitions; finite counts are 1–10. No model is invoked by the executor.
Each repetition executes a complete batch. Completion leaves P2 idle.

Environmental blocks wait indefinitely with neutral input and bounded periodic
revalidation. Completed steps are retained. Explicit Stop, release, disconnect,
pause, focus loss, reassignment, respawn and round changes still cancel and restore
native input bindings. Changed source identities wait for the original source before an unpressed step.
Changed cargo or unverified button outcomes fail safely with cargo retained. Waiting does not replay a successful step or disposal.

The existing interaction/logistics recovery window is also indefinite; time spent
recovering suspends finite watchdogs. Button outcome verification remains bounded.
New tasks and their child actions have no overall deadline.

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

The scheduler starts passive processing before short deliveries, performs other
requested preparation while equipment runs, and collects completed containers
before starting more work. Processing remains native and uses native progress/time.
It retains exact containers and stations between visits. Native remaining cooking
time and conservative estimates of travel/chopping prevent starting long work
just before a pot finishes. Full equipment defers new cooking units instead of
holding an ingredient that would prevent collecting an earlier unit. Ready
contents prefer a serving plate/utensil, leaving the processor available.

Delivery preference: a compatible counter within five walking units of the human,
then a checked pass/drop at the human, then a directed conveyor or returning
counter. Both chefs use the same navigator implementation in independent read-only
observers. Throw plans check actual throwability. Conveyor traversal follows the
native next receiver, including rotation/direction, and rejects loops/dead ends.
Moving-counter fallback requires an observed human-accessible stop in this round;
unknown motion waits for evidence. Transport delivery completes only after the
exact item reaches a human-accessible holder or the human's hands. Safe empty
countertops are used when putting equipment down; conveyor belts are not storage.

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
An already-held dirty stack can join the batch; unrelated cargo is never moved.

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
available burner, otherwise an available counter. Occupied burners are never
cleared. If no disposal/return route exists, it waits holding the original cargo.

## Monitor equipment

`monitor_equipment` waits for any accessible native cooker, heated station, or
mixer/blender to finish. It uses shared ready-food collection and serving/parking,
then completes one repetition. Use unlimited repetitions for ongoing monitoring,
including idle periods when equipment is empty. Cooking station variants use the
same native cooking handler instead of recipe/name-specific timing guesses.
