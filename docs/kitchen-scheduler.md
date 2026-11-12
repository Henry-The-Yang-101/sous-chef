# Native kitchen scheduler

Bridge **0.8.15** advertises `kitchenSchedulerVersion: 1`. The harness uses native
admission for task contracts on this bridge; older bridges retain their existing
FIFO behavior. Direct `run_task` and low-level action clients retain their
single-request execution contract.

## Jobs, stages, and concurrency

A finite preparation command is a group of independent one-portion jobs. Counts
and repetitions expand deterministically, after Jev has interpreted the request.
Two chopped fish plus two chopped prawns creates four jobs, each with its own
iterator, work progress, item reservations, and verified delivery. The group
succeeds only after every portion succeeds. Preparation still includes handoff
to the named player. It does not finish merely because chopping finished.

Stages use the existing native task primitives: fetching, placing, chopping,
loading, collecting, and handing off. Cooking/mixing does not consume chef time.
The chef can advance another portion while a vessel processes food. Each retained
job observes its processing deadlines while another owns input. The original
iterator collects its own hot food; another job cannot steal its vessel.

Every portion is `ready`, `blocked`, `running`, or `done`. Done jobs retain a
separate native outcome. Reasons and current stages are in the receipt's
`evidence.jobs`; the harness shows them in the queue. A group remains waiting
while its portions are blocked. Unlimited preparation retains bounded demand
lanes and yields to the same global dispatcher; it never expands an infinite
list. Washing retains native stack/batch semantics.

Native admission is bounded to 1,024 outstanding portion records. The harness's
existing ten-pending-command limit counts user commands, not their internal
stages. An oversized admission is rejected atomically, never partially run.

## Ordering and control

Plans accept `ordering: independent` or `ordering: sequence`. Omission preserves
the legacy sequence contract. The current manual UI defaults to independent
work and offers “Finish in list order.” Jev resolves explicit then/before/after
requirements separately and includes the ordering in full-plan verification.
Explicit sequencing between preparation ingredients uses separate preparation
task occurrences. Independent portion jobs inside an occurrence can interleave.

Dependencies are admitted only after successful predecessor receipts. `insert_next`
also depends on the active command group. Urgent work receives a priority boost;
it does not cancel earlier work or force a stack-ordered resumption. After a
handover, the dispatcher selects the best currently ready job.
An urgent plan resumes paused dispatch. A plan requesting a pause on completion
waits for all its independent commands, even when its last-listed command finishes first.

Pause dispatch holds new command groups. Already-started groups continue their
stages, including collection of cooking food, so pausing admission cannot strand
a hot vessel belonging to an in-progress command.
Stop cancels all jobs and standing duties. Removing an admitted pending command
forgets its native portions as well as its journal entry. Round/chef changes
invalidate all native identities. Scoped replacement retains other admitted work.
Manual primitives remain exclusive and use the existing executor.

## Priority and ownership

Priorities are recomputed every 0.2 seconds from native state. Blocked jobs do not
compete for input. The policy considers:

* Imminent ruin, ready processing output, and observed fires.
* Completed plated dishes matching live tickets, using actual expiry seconds.
* Missing recipe demand, allocating existing ready composition and requested
  portions once across tickets in expiry order. Native identities, multiplicity,
  and processing boundaries are preserved.
* Observed cooker/mixer duration in a prepared portion's deadline budget, so
  starting passive processing can outrank a short nonurgent handoff.
* Estimated remaining chef work, travel, switching overhead, completion of a
  useful portion, and bounded age within ordinary priority classes.
* Resource dependencies: an owner that can release a needed board or plate can
  inherit a blocked job's urgency. Washing receives order urgency when no clean
  plate is available.

Order urgency is advisory for an explicit preparation request. Ticket expiry
removes the association without cancelling the requested ingredients. Exact-ticket
assembly keeps its exact binding and fails visibly when that ticket disappears.
Estimated infeasibility lowers ordinary order priority without cancelling it.

These are online estimates, **not deadline guarantees or an optimal schedule**.
Native processing progress and travel estimates are authoritative observations;
remaining assembly/handoff budgets include conservative heuristic allowances.
The policy does not model future human actions or solve a complete multi-machine
schedule. Diagnostics expose the estimate and reason for each decision.

Reservations span jobs and include acquired food, processing vessels, and occupied
work boards. Verified chopping successors retain ownership. Empty or externally
changed boards release their reservation. Before each native action press, resource
ownership is checked again. Delivered preparation identities are shared across
jobs, preventing a later job from delivering the same portion twice. Per-job
completion evidence stays separate. Filled vessels leave the shared exclusion
history when emptied, allowing their reuse without crediting the same food twice.

## Duties and safe handovers

Admission enables standing serving, equipment monitoring, demand-driven washing,
and fire response until Stop or kitchen invalidation. They generate finite native
work when conditions permit, and remain observable after finite commands finish.
They retain autonomous background operation while waiting for new kitchen events.
The UI explicitly reports that kitchen duties remain active. Duty completions are
reported in `control.kitchenScheduler.dutyCompletions`.

An explicit pending serving/washing/monitoring/fire task suppresses the corresponding
automatic duty. This includes tasks waiting behind an explicit dependency, so
background work cannot consume the explicit task's count or bypass its ordering.
The harness synchronizes this policy before admitting dependent work.

Chopping and washing can release interaction with partial native progress intact.
Committed pickups, transfers, placements, throws, and portal transitions settle
before handover. Suspension retains original cargo; resumption parks unrelated
cargo and recovers the original identity. Small priority changes do not warrant
repeated switching. Route failures allow other ready work and use paced retries.

No safe storage or no usable route can still prevent useful progress. The scheduler
does not manufacture capacity, substitute a different exact target, or declare an
unverified interaction successful. Observation faults neutralize scheduler input,
hold dispatch, and expose the error; Stop remains available.

## Durability and protocol

`enqueue REQUEST EPOCH GENERATION URGENT task ...` admits a task without requiring
the chef to be idle. URGENT is 0 or 1. Existing request receipts deduplicate admission.
The harness journals intent before send and reconciles a lost acknowledgment by
receipt lookup, never by replaying the command. Native jobs continue across harness
restarts and disconnects. They are in-memory game state, not resumable after a game
restart.

`schedule_hold REQUEST EPOCH GENERATION HOLD [DUTIES]` controls future selections.
DUTIES is a bitmask: serving=1, equipment=2, washing=4, fire=8; omission means 15.
The command is fenced and idempotent. `forget_task` also handles admitted groups.
The existing independent Stop and dispatch-generation fence remain authoritative.

## Verification

Run `python3 scripts/check.py` and `node --test tests/harness_web.test.js`.
The coordinator tests cover independent admission, dependency success/failure,
unknown receipts, lost acknowledgments, restart, removal, native holds, urgency,
and suppression of duplicate duties. Parser/priority checks cover fenced admission,
invalid messages, blocking, completion preference, and switching overhead.

`scripts/kitchen_scheduler_audit.py --runtime ISOLATED_RUNTIME --port CONTROL_PORT`
uses a running isolated `s_sushi_1_2` game. It discards the first admission
acknowledgment, restarts the harness, observes fish preparation while rice cooks,
and requires three successful portions with distinct delivered identities and
one unchanged native request. It also pauses admission during the overlap and
requires the started group to finish safely. Add `--serve` to verify automatic
serving after assembling an exact ticket. It writes its evidence into the isolated runtime.

Native validation on 2026-11-12 also assembled an exact `Sushi_Fish` ticket in
`s_sushi_1_2` and observed one verified automatic delivery after the finite
assembly group completed. Local evidence: `.local/priority-native/serve-audit.json`.
These isolated probes do not establish microphone recognition or every moving
kitchen's scheduling performance. The normal installed game is not changed by
the audit scripts.
