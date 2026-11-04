# Autonomous tasks

`ActionClient.run_task(action, repetitions=1, items=None, player_id=1)` owns one
request and control lease until all steps and repetitions finish. Use `None` for
unlimited repetitions; finite counts are 1–10. No model is invoked by the executor.
Each repetition executes a complete batch. Completion leaves P2 idle.

Environmental blocks wait indefinitely with neutral input and bounded periodic
revalidation. Completed steps are retained. Explicit Stop, release, disconnect,
pause, focus loss, reassignment, respawn and round changes still cancel and restore
native input bindings. Identity changes and unverified button outcomes fail safely
with cargo retained. Waiting does not replay a successful step or disposal.

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
cooking/mixing uses compatible empty containers already on equipment.

The scheduler starts passive processing before short deliveries, performs other
requested preparation while equipment runs, and collects completed containers
before starting more work. Processing remains native and uses native progress/time.
It retains exact containers and stations between visits.

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
