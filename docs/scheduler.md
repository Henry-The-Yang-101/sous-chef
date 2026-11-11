# Local scheduler — Milestone 2

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

Pause dispatch holds future tasks. A native pause suspends and preserves the
active task. On bridge 0.8.10 or later, game focus does not affect dispatch or
execution.
Round, chef and session changes invalidate old work. Fresh plans can dispatch
in the new kitchen unless the player explicitly paused dispatch. An external
native Stop in the current kitchen clears the queue and holds dispatch.
Replacement is validated before cancellation and held behind a
native cancellation barrier. Stop journals invalidation before using a separate
priority connection; late sends carry the old native fence and cannot acquire
input. Immediate manual actions pause pending dispatch and use the same fence.
Automatic queue dispatch leaves window focus alone. Run now can foreground
older bridges that still report `game_unfocused`.

The UI supports single tasks and atomic plans of up to five steps, Add to queue,
Run now, remove/reorder, clear pending, pause/resume, Skip, and independent Stop.
It exposes active/waiting/suspended/reconciling status and the latest outcome.

## Verification

- Full `scripts/check.py` passed: plugin build and installed assembly audit,
  2,989 core assertions, 435 navigator checks, 1,169 action checks and 30 harness
  catalog checks. Navigator/action checks use analytical or mocked native state.
- Final Python suite: 175 tests (including the production JavaScript form
  regressions), covering atomic acceptance, CAS conflicts, capacity,
  dependencies, finite/unlimited ordering, waiting/failure, journal ownership,
  restart, lost acknowledgment, missing receipt, Stop race and HTTP protections.
  Additional cases cover a full queue during start rejection, disk-write failure,
  dependency retention, expired exact tickets, conflicting native IDs, and another
  Stop winning a replacement race.
- `scripts/scheduler_audit.py --runtime .local/milestone2-native --port 25313`
  passed against the actual windowless Unity game on `s_sushi_1_2`: two finite
  preparation tasks completed in sequence, with first acknowledgment discarded,
  harness restart, pause and focus loss preserving the same native request.
  Retrying a completed ID returned its receipt, conflicting payload was rejected,
  replacement waited for cancellation and then completed, Stop rejected a late
  fenced request, and round reset invalidated pending work.
- Chrome UI verified against that native harness: a two-step draft accepted
  atomically, pending steps survived reload, dependency-breaking edits were
  disabled, an unlimited monitor blocked append, and Stop returned the chef to
  idle with no pending work. Desktop and 390×844 phone layout were inspected;
  phone document width stayed within the viewport. This is responsive browser
  verification, not a physical phone test.
- This uses isolated runtime/ports. It does not install the plugin into the normal
  game or imply microphone/Jev integration (later milestones).

## Review boundaries

Receipt identity is bounded retention, not a claim of exactly-once physical
side effects. A lost/evicted receipt requires reconciliation or explicit
cancellation, never retransmission of an uncertain action. Queue progress does
not change the edit revision; material edits, Stop and context invalidation do.
A plan drafted against an old revision is rejected, while an exact duplicate
accepted command retrieves its stored acceptance without executing again.

Journal-write failures freeze dispatch and are visible in the queue. Stop still
uses the native control connection if persistence fails. Startup refuses a
journal already owned by another harness. No accepted-task deadline was added.
The browser's Escape key remains a local Stop shortcut while the page is focused;
this milestone does not install a system-wide hotkey, microphone service, or Jev.

`/api/plan` takes `{plan, context, focus?}` in addition to the existing token and
viewer ID. Context is the `queue.nativeContext` from `/api/state`. The plan uses
the existing closed schema and supports append, insert_next, replace_active,
replace_pending and replace_all. `/api/queue` takes `edit` with `operation_id`,
`expected_queue_revision`, and `operation`; remove adds `task_id`, reorder adds
all pending `task_ids` exactly once. Other edits are clear_pending, pause_pending,
resume_pending and skip_active. The Stop endpoint remains separate and does not
require a current queue revision. Native task clicks no longer use `/api/task`.
