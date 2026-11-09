# Grounded movement — milestone 5

Implemented 8 November 2026. `navigate_to` now accepts registered station,
platform and floor destinations. “Walk to the chopping board at the bottom left”
is resolved against observed station copies and the game camera. “Go to the top
left” uses representative safe standing positions. The interpreter chooses the
meaning; native navigation decides whether movement can execute.

## Observation and identity

`semanticCatalog.movement` is a separate observed registry. It includes locations
beyond P2's current reachable tree, while the older `destinations` catalog still
means currently verified approaches. The registry contains an epoch, camera
orientation, P2's position, floor points, platforms, stations, support summaries,
observed bidirectional walking edges and coverage diagnostics.

Floor discovery samples actual Ground colliders in their local frame and checks
native support, capsule clearance and hazards. Raycasts reject holes in irregular
meshes. Multiple vertical surfaces remain separate points. A support comes from
the game's `IParentable.GetAttachPoint` hierarchy, the same ownership mechanism
used by `DynamicLandscapeParenting`; matching velocities never merge platforms.
Unparented floors retain their own transform and layer. Point IDs survive motion
and are not reused after a kitchen reset or chef reassignment.

Station references follow the exact object. Platform and floor references retain
a fixed support-local point. Thus “right raft” means the raft selected in the
interpretation snapshot, including after a queue delay or an exchange of sides.
Platform summaries include occupants, attached stations and observed transform
motion. Stations also report their own motion, including rotating counters.
Motion is observation, never a predicted opening time or crossing permission.

The model sees screen-relative `right` and `up`, a concise location description,
height, support identity, nearby stations and players. Bounds come from verified
playable standing geometry, excluding scenery. Raw world/local coordinates and
camera vectors stay local; the harness map uses the same camera projection.
Unknown orientation stays explicit. Height remains separate from screen position.

## Candidate selection

`harness/semantic_map.py` reserves each observed walking component, moving support
and half-metre height band. It then uses deterministic farthest-first sampling,
starting at each group's centroid representative. The initial floor budget is 18;
required group coverage expands it rather than dropping a deck or island. A hard
maximum of 180 returns an explicit coverage gap. Missing graph edges do not prove
disconnection. Named stations and physical platforms remain separate candidates.

Jev first chooses the binding type from the scene. Candidate suitability uses
independent Noul questions in batches of 18, with the existing .85 gate. Multiple
accepted destinations require an additional .97 equivalence gate before local
code ranks by verified route distance and stable ID. Distinct possible meanings
clarify. A floor menu with no acceptable point gets one refinement to 48 before
clarification. Nothing falls back to the closest unsuitable or reachable point.
Explicit clarification choices replay against the frozen candidate snapshot.
The existing whole-plan semantic verifier receives the selected spatial context.

This Noul policy avoids dividing a single Choice distribution among many equally
valid floor tiles. It is a provisional default: a comparative spatial Choice/Noul
benchmark and broader held-out language evaluation remain future validation work.
The confidence gates were not lowered for movement.

## Execution and lifecycle

Acceptance checks the kitchen epoch and exact object/support/local-point binding.
Dispatch resolves the registered ID again. Native execution rechecks standing
support, routes and the next input step throughout the action. Station movement
reuses native approach selection; points share the goal-directed destination
matcher, with a directly checked connector when possible. Discovery cannot mutate
the executor's physics or search state.

A closed passage, unfinished search or unsafe next step retains the destination
and reports `waiting_route`. Rechecks are paced at 250 ms while waiting. Zero input
renews ownership without releasing the navigation lease. Only a freshly checked
route resumes movement. There is no timeout that substitutes a different target,
and waiting is never success. Stop remains authoritative. Vanished references
cannot start; loss of a selected point's standing support waits for revalidation.

Point arrival requires position tolerance, the selected native support and height,
and sustained rest relative to the moving anchor. Old world coordinates cannot
satisfy arrival on a moving raft. Station arrival uses existing native approach
and selection checks. Movement emits no pickup, placement, use or wash action.
Standalone movement pauses preserved pending dispatch after arrival. An explicit
sequence such as “go to the sink, then wash dishes” continues to its next task.
Existing suspension, receipt, queue-edit and epoch fencing remain in force.

## Bounds and performance

Observation runs only while the map is being requested, at most once per frame,
with a nominal 2 ms / 48-operation slice. Native calls are indivisible, so this is
a soft time budget. Physics refresh is paced at 250 ms and sweeps at 500 ms after
completion. Discovery caps both registered points and seeds per collider at 4096.
Spatial buckets limit edge candidates, and only one native edge check is performed
per budgeted operation. Limits and incomplete sampling are reported in coverage.
No level names, permanent semantic regions or motion-cycle timings appear in the
implementation.

The original request bounds still apply: 24 questions, conservative 24 KB
state-plus-question / 48 KB request limits, and 48 attempts per compilation.
Large contexts fail explicitly instead of silently omitting relevant candidates.
Spatial observations are snapshots; accepted movement is not reinterpreted by Jev
on every map update.

## Verification

The committed [language observations](grounded-movement-language.json) contain
exact production candidate requests and observed `jev-1.14.0` Playground answers.
For two boards, bottom-left suitability was .99 versus .02. After rotating the
camera 180 degrees, the correct other board scored .92 versus .04. Each final
request used 1,724 input and 38 output tokens, with about 31–32 ms reported model
evaluation time. These two synthetic examples are calibration evidence, not a
general accuracy estimate or a full-plan test. An earlier rotated context caused
safe clarification; removing competing raw coordinates resolved that example.
No Python provider API/SDK call was made.

The [native audit](grounded-movement-native.json) records isolated headless Unity
2017.4.8f1 / PhysX runs from normal spawn, through fenced execution receipts:
floor arrival, empty-board approach, support-relative raft arrival and a blocked
other-raft wait followed by Stop. `scripts/movement_audit.py` chooses destinations
geometrically for these physical tests; it makes no Jev calls and writes no poses,
cargo or routes during measured execution. Native round-trip timings include the
catalog's existing work and transport, not just movement discovery.

| Native selection | Result | Arrival error | Catalog median | First sweep | Largest observed map slice |
| --- | --- | --- | --- | --- | --- |
| Sushi floor, top left | Arrived | 0.138 m | 31.9 ms | 1.18 s | 4.15 ms |
| Sushi empty board, leftmost | Positioned, no interaction | 0.117 m | 31.7 ms | 0.72 s | 2.08 ms |
| Rapids occupied moving raft | Arrived | 0.102 m | 28.5 ms | 0.52 s | 2.04 ms |
| Rapids other raft | Waited 35 s; Stop cancelled | None | 23.2 ms | 0.54 s | 6.10 ms |

Both kitchens produced 18 floor representatives, from 121 and 66 observed
standing points respectively. The largest measured slice exceeded the soft 2 ms
budget; native refresh/query calls cannot be interrupted mid-call.

454 analytical navigation checks pass, including both barrier orientations,
opening a blocked route without changing its destination, holes, overlapping
decks, moving support identity, neutral-wait ownership, suspension, Stop and stale
epochs. All 246 Python tests, 11 browser-JS tests, 30 native catalog-option checks
and 2,989 core/installed-API assertions pass. Mocked model answers test compiler
control flow and equivalent-choice handling; they are not language evidence.

`scripts/check.py` stops at the pre-existing actions test `HeightOptions` assertion
“lower receiving surfaces can be offered with their longer native arc.” The same
failure was reproduced on the untouched pre-milestone tree, in an isolated
baseline extraction. It is not counted as a movement pass.

Remaining validation boundaries: no full voice/typed-Jev-to-native run, no Python
provider connectivity test, no live two-player gameplay, no proven cross-raft
boarding in this audit, and no native moving-counter closed-to-open traversal
fixture. Barrier recovery is analytical; native raft waiting/arrival is separate
evidence. Discovery is bounded sampling, not proof that every playable point is
represented. The installed game plugin and Worker were not updated by this work.
