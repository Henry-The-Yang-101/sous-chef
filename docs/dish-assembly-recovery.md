# Assembly recovery follow-up

## Current scope after simplification

At the user's request, the final implementation returns to the earlier walking
geometry rather than expanding navigation to cover the remaining kitchen edge
cases. The experimental lift code was discarded. The new rounded-tread,
floor-overhang and shallow-counter-contact exceptions and their dedicated
fixtures were rolled back, as was repeated empty-plate reselection. The original
assigned-plate pinning contract remains.

Retained changes are general task behavior: exact recipe/allocation checks,
heat priority and whole-trip cooking budgets, early hand clearing, persistent
route retry history, and the assembly storage scheduler. General destination A*
and observed-motion arrival checks remain; no level names or kitchen geometry
are used by these policies.

The findings below are historical experiments, not claims about the simplified
binary. In particular, the pizza and contact-recovery successes used changes
that were subsequently removed. The interrupted replay 06 was stopped for this
scope change. Final simplified-build verification is recorded separately below.

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

## Native replay 03 and floor contact recovery

The next seven-kitchen replay independently verified completed pizza (60.47 s),
moving tomato pasta (55.05 s), chocolate pancakes (38.12 s), chicken burrito
(49.31 s), and cheeseburger with greens (52.47 s). No fire or ruined food was
observed in these seven runs. The fryer rescued its cooked chicken but then
stalled at a floor lip; marinara pasta also stalled, near a moving counter.
Both incomplete traces remain in `.local/assembly-recovery-03-*`.

The fryer pose was grounded on its native floor with 0.01225 units of centre
overhang. Live connectors can now recover inward from overhang bounded by the
existing contact skin. Cached lattice routes cannot use this exception, and
steps that increase overhang still fail. The captured geometry test covers all
three cases and retains the prior unsupported-corner regressions.

`scripts/check.py` passes (`.local/assembly-recovery-check-07.log`), including
444 analytical navigation checks. Native replay of this floor-edge correction
is pending. Trace-only native penetration details were added to distinguish
the separate moving-counter contact from a planning-margin error.

Replay 04 reproduced marinara's stall with a native separation depth of 0.02219
units against a moving worktop. Live connectors now permit outward movement
from a box contact within the existing skin, requiring the native separation
plane, non-increasing penetration at every sample, and unchanged support/hazard
checks. This does not change cached geometry or permit movement through/along
the contact. The captured regression rejects those directions, deeper contacts,
new routes into the overlap, and hazards.

Ordinary assembly pickups now use the same hand-clearing storage scheduler as
heat rescue. Previously, generic hand clearing could wait for a complete broad
map while the assembly scheduler could probe a concrete empty counter.
`scripts/check.py` passes (`.local/assembly-recovery-check-09.log`, including
451 navigation checks). Native replay of these changes is pending. Replay 04's
fryer case lacked enough reachable empty counters for its initial fixture;
no assembly was started, so that setup attempt is not a behavioral result.

Replay 05 completed marinara in 94.29 seconds with an independent exact recipe
match and no fire/ruined food. The long generic storage wait and `no_safe_start`
stall did not recur. Its recorded ready vessels were removed after 0.59 and
0.42 seconds. The fryer also recovered its floor contact and removed both
ready baskets after 0.44 and 0.58 seconds, but exposed an empty-base selection
problem: its chosen plate stayed on a distant moving cart while an alternative
allocated plate was near the cooking area.

Empty base selection is now reconsidered among the explicit allocation, with
a small preference for retaining the current plate. A plate containing any
food remains pinned. Tests move the original plate into another chef's hands
after component collection: an empty dish uses the other allocated plate;
a partially assembled dish waits without splitting its contents.

## Final simplified-build verification

Commit `924996b` restores the simpler geometry and plate policy described at the
top of this document. `scripts/check.py` passes: 2,895 core assertions, 435
analytical navigation checks, 1,150 executor checks, 20 harness checks, and 136
Python tests (`.local/assembly-simplified-check-02.log`). The first rollback check
still contained an assertion for the removed contact correction; that assertion
was then reverted with its implementation before the passing full check.

Four representative native replays all completed with independently matched
recipe trees on the same simplified binary:

| Level | Recipe | Seconds | Verified additions |
| --- | --- | ---: | ---: |
| `s_sushi_1_1` | `Sushi_PlainFish` | 2.06 | 1 |
| `s_balloon_1_5` | `Pasta_TomatoOnly_New` | 56.24 | 4 |
| `s_mine_2_5` | `Chicken_Burrito` | 46.11 | 5 |
| `s_wizard_school_3_4` | `Pancake_Chocolate` | 38.27 | 5 |

The monitor retained all 244 scheduler events, 606 control observations and 273
native food-state samples, with no missing scheduler records, lost requests or
leases, observation errors, fires, or ruined food. Long dependency waits were
reviewed against actual mixing/cooking progress; shorter route retries remained.
This is representative verification, not a measured 98% completion rate or an
all-kitchen guarantee. The known lift, gap, and difficult-contact limitations are
accepted within the simpler scope.

The built and tested isolated-runtime DLLs both hash to
`20d188e39dc8f88129334fc22a312e29525cb1394bc3d491bdbaef82a8bdf10a`.
The [result index](dish-assembly-recovery.json) records each case and trace path.
Only initial prepared ingredients were supplied by fixtures. No measured chef
positions or processing clocks were changed. The owned test runtime was stopped;
the normal installed game was not modified.
