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
