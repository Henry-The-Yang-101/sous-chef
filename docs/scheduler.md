# Local scheduler — Milestone 2

Implementation of the [voice-control design](jev-voice-control-flow-design.md).

## Native receipt protocol

The control hello and status advertise `receiptProtocolVersion: 1`. Status also
provides `kitchenEpoch` (changed on round or player assignment) and
`dispatchGeneration` (advanced by Stop). Existing clients remain compatible.

- `execute REQUEST EPOCH GENERATION OPERATION ...` wraps an existing validated
  action. Use a unique 32-character hexadecimal request ID.
- `receipt QUERY_ID REQUEST` reads its latest detached receipt, or null if unknown.
- `cancel REQUEST EPOCH GENERATION` is a fenced cancellation with its own receipt.
- The existing unconditional `stop ID` remains independent and advances the fence.

A matching ID/payload returns the retained receipt without reacquiring input.
Conflicting payloads are rejected. Fences are checked before input acquisition.
Stop/cancel requests have priority over queued ordinary commands. At most 256
receipts are retained, preserving active entries. Round/assignment changes clear
them; an absent or evicted receipt is **unknown**, never permission to replay.
Waiting and suspension are nonterminal. Navigation success requires native
arrival; task success retains the executor's completed repetition/step evidence.

Initial validation: plugin compiled against the installed Unity assemblies;
C# transport/core suite passed 2,541 assertions; Python action-client tests passed
13 tests. Native end-to-end scheduler verification is recorded below as added.

## Coordinator and journal

`harness/scheduler.py` owns one active entry and ten pending entries. Whole typed
plans enter through `POST /api/plan` with the observed native context and queue
revision. Edits use `POST /api/queue`; both retain Host/Origin/token checks.
Command IDs deduplicate atomic acceptance, task IDs identify queue entries, and
unique attempt IDs identify native dispatches. The last 512 operation receipts
and 50 outcomes (plus referenced dependencies) are retained in SQLite.
Dispatch intent is fsynced before send; a process lock prevents two harnesses
from owning the same journal. The default is `.local/queue-CONTROL_PORT.sqlite3`;
`--queue-journal` selects another path. Browser reload does not own queue lifetime.

A sequence records dependencies between its steps. Reordering/removal/replacement
cannot remove an unmet prerequisite while retaining its dependents. Failures
pause dispatch. Skip explicitly cancels independent active work, waits for native
cleanup, and advances; it never records success. Unknown receipts hold the queue
until a later receipt proves the outcome or an explicit Stop/replacement cancels
it. Only a proven start rejection can create a fresh attempt, with a new ID.
There is no accepted-task timeout or automatic restart of partially executed work.
Unlimited work is allowed only at the tail of the entire resulting queue.

Pause affects future dispatch. Native pause/focus loss preserves an active task.
Round/chef/session changes invalidate the old queue. External native Stop also
invalidates it. Replacement is validated before cancellation and held behind a
native cancellation barrier. Stop journals invalidation before using a separate
priority connection; late sends carry the old native fence and cannot acquire
input. Immediate manual actions pause pending dispatch and use the same fence.
Automatic queue dispatch does not foreground the game. Only explicit Run now
may request focus. The future voice compiler can call the same acceptance API.

The UI supports single tasks and atomic plans of up to five steps, Add to queue,
Run now, remove/reorder, clear pending, pause/resume, Skip, and independent Stop.
It exposes active/waiting/suspended/reconciling status and the latest outcome.

## Verification

- Python suite: 166 tests, including atomic acceptance, CAS conflicts, capacity,
  dependencies, finite/unlimited ordering, waiting/failure, journal ownership,
  restart, lost acknowledgment, missing receipt, Stop race and HTTP protections.
- `scripts/scheduler_audit.py --runtime .local/milestone2-native --port 25313`
  passed against the actual windowless Unity game on `s_sushi_1_2`: two finite
  preparation tasks completed in sequence, with first acknowledgment discarded,
  harness restart, pause and focus loss preserving the same native request.
  Retrying a completed ID returned its receipt, conflicting payload was rejected,
  Stop rejected a late fenced request, and round reset invalidated pending work.
- This uses isolated runtime/ports. It does not install the plugin into the normal
  game or imply microphone/Jev integration (later milestones).
