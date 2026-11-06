# Assembly recovery follow-up

The cross-kitchen sweep left four stalled executions and one late heat rescue.
This follow-up fixes scheduler recovery first, then uses native traces to separate
navigation defects from kitchen mechanisms and resource access constraints.

## Implementation and verification plan

1. Retain route-failure history across alternate destinations, with bounded
   exponential backoff and early wake on changed position, occupancy, held item,
   support, or newly certified reachability.
2. Keep ready food ahead of ordinary additions during rescue retries. Clear hands
   and approach cooking food when its remaining time is within the estimated
   return trip. Preserve native cooking and exact-recipe checks at the input edge.
3. Replay the moving pasta kitchen and fryer stall. Inspect geometry invalidation
   causes, then inspect the pizza, steamer, and split-kitchen access failures.
4. Verify fixes on the failed scenarios and the previous heat, burrito, burger,
   and staircase canaries. Retain every scheduler decision and native food clock.

## Scheduler milestone

Ready-batch retry delays no longer dispatch cold ingredient pickups. Imminent
cooking completion can interrupt an unpressed ordinary step, store its cargo on
an empty counter, and approach the processor without removing undercooked food.
Heat-related hand clearing uses the assembly scheduler's rejected-route history.
Storage verifies that the exact held composition is unchanged before placement.

Route retries use 2, 4, 8, and then 16 second delays. Positions are compared in
the chef's support frame so raft translation alone does not reset the history.
Optional navigation traces now identify changed collider geometry for diagnosis.

`scripts/check.py` passed on this milestone (log:
`.local/assembly-recovery-check-01.log`), including focused scheduler tests for
rescue priority, clearing hands before completion, approach without premature
pickup, persistent retry history, and waking after occupancy or position changes.
Native validation is pending; these tests use mocked native state.

## Native findings and navigation milestone

The first moving-pasta replay completed in 49.71 seconds of decision observation.
Cold work did not interrupt heat rescue, but three alignment failures still
delayed tomato removal. During those failures, the chef's observed position was
stationary and the native cooker selected while the wind accumulator reported
roughly 2 units/second. Arrival now measures actual environmental displacement
relative to the target, retaining the Rigidbody walking-velocity guard, stable
arrival interval, and projected-footing check.

The first fryer replay staged both baskets and collected a component, then
stalled on the return route. Its moving carts repeatedly invalidated the broad
search. Station-directed searches now use A* with a consistent lower bound to
the candidate stance regions. Native edges and minimum-cost certification are
unchanged. Portal graphs retain Dijkstra, and changing the focus or restoring
the full catalogue starts the appropriate search rather than reusing a frontier
whose bound certifies a different target.

`scripts/check.py` passes with these changes
(`.local/assembly-recovery-check-03.log`). Tests compare sliced focused search
with Dijkstra, including a forced detour, and preserve moving-platform, wind
drift, pending-walking, and full-catalogue restoration checks. Native replays
of this navigation milestone are pending.

## Further failures retained and addressed

Replay 02 completed moving pasta in 44.04 seconds, independently matched the
recipe, and removed the observed ready tomato pan after 0.58 seconds without
overdoing. The fryer run exposed a further failure: P2 left its cooking basket
for a distant ingredient and could not return before it was ruined and ignited.
That failed trace is retained under `.local/assembly-recovery-02-fryer`.

Assembly now budgets the outward and return trip before dispatching ordinary
work during cooking. Nearby work can still proceed; an unknown or too-long
route causes P2 to approach the active batch instead. The same check protects
the native input edge. Recovery searches also retain their destination across
catalogue polls and release it when the owning action ends.

The pizza stairs were separately verified with ordinary native leftward input
from an explicit supported starting pose: P2 climbed from y=0 to y=1 without a
dash or position changes during measurement
(`.local/assembly-recovery-pizza-native-walk-01.json`). Planner probes rejected
real ramp/tread joins. The geometry correction requires a real adjoining ramp
and underlying support, admits the native rounded tread lip only alongside a
valid ramp contact, and preserves lateral ramp margins and capsule sweeps.
The recorded regression also rejects the staircase when its ramps are removed.
Existing unsupported-floor-corner and stair-side collision tests remain.

This milestone passes `scripts/check.py`
(`.local/assembly-recovery-check-06.log`). Native task replays of these additional
changes are pending; the direct walking diagnostic alone is not an assembly pass.
