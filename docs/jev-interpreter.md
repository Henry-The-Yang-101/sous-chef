# Jev interpreter (milestone 4)

Implemented 6 November 2026. Provider calls from Python have **not** been run.
The adapter follows the [System One API](https://docs.typesafe.ai/api/system-one)
and uses only Choice and Noul, pinned to `jev-1.14.0`.

## Start using it

1. Open `.env` in the repository root (copy `.env.example` if it does not exist).
2. Set `JEV_API_KEY=your_key` (or supply it in the server environment).
3. Restart with `python3 -m harness.server`.
4. Use **Enable typed Jev commands**, or enable the Mac microphone with its
   transcript destination set to **Jev interpreter**. A configured key selects
   that destination by default on startup.

Startup is muted and acceptance starts in **Preview**. Review and accept each
proposed plan, or explicitly switch Acceptance to **Automatic**. A key alone
never starts microphone capture or sends a request. No SDK installation is needed.
The `.env` file is ignored by Git; the key is absent from browser state and logs.
`TYPESAFE_API_KEY` is also accepted. Environment values override the file.

## Behavior and boundaries

- Ordinary work appends. Queue-only edits, scoped replacement, independent Stop,
  repetitions and a single unlimited tail use the existing scheduler.
- Complete simple plans use a candidate Choice with a companion coverage Noul.
  Other commands discover task occurrences until explicit `done`; preparation
  discovers item occurrences in the same way. Repeated families/ingredients stay
  distinct. Every final plan has a separate coverage and ambiguity check.
- Counts are locally extracted candidates, never Score values or generated JSON.
  Contract defaults apply only to omitted fields; unspecified preparation asks
  for clarification. Counts outside native limits are not clamped.
- A clarification retains the original draft and cached decisions. Buttons bind
  exact options; spoken/typed replies distinguish an answer, cancellation and new
  commands. Buffered follow-ups are checked for corrections before accepting the
  unfinished command. Ordinary separate commands stay FIFO.
- If all fields are resolved but the final semantic check is uncertain, a button
  shows the complete interpretation, including quantities, recipient and queue
  effect. Explicitly confirming it binds that exact plan; restating or discarding
  it remains available. Confirmation still leads to Preview in Preview mode.
- The inbox permits three pending utterances. Five tasks and ten preparation
  entries are the compilation limits. Oversized contexts, missing candidates and
  incomplete/uncertain coverage produce no partial acceptance.
- Mute and Stop invalidate unfinished results. DNS/TLS, model waits and backoff
  run outside game/queue locks. The actual nonblocking request write passes the
  mute gate, including each retry. Already accepted work is preserved by mute.
- Queue revision, command generation, kitchen/session/chef, capability hash and
  exact target identities are rechecked. Moving an object does not itself change
  its binding. Native start validation still owns physical feasibility.
- Exact assembly allocation generation remains unsupported: use the existing
  manual assembly controls. Semantic-region movement is milestone 5. Concurrent
  high-level responsibilities and event/timed work are not approximated as FIFO.
  Existing exact-target native action contracts can be selected when grounded.

The provisional gates after playground tuning are .85 for ordinary choices,
`done` and coverage, and .97 for unlimited work, complete simple-plan selection,
destructive edits and explicit spoken clarification answers. Destructive edits
also require separate deletion/interruption evidence or a direct scoped answer.
These are policy thresholds, not calibrated probabilities of correctness.
The [playground evaluation](jev-playground-evaluation.md) found frequent
clarifications even for correct interpretations. Preview remains the recommended
mode; automatic acceptance is an explicit experimental opt-in.

## Bounds and diagnostics

The dedicated worker reuses a TLS connection. Requests time out after 15 seconds,
with at most two application retries for transient failures, bounded by 90 seconds
and 48 attempts per compilation. Retry-After is respected; it is never shortened
to fit the deadline. Auth/schema failures are not retried. There are no SDK retries,
redirects, images, extended-context headers, or automatic provider calls on startup.
Clarifications expire after 90 seconds, previews after 60. User thinking time is
separate from model time; resumed work retains the total request count. Recent
command context is bounded and expires after ten idle minutes or mute/Stop.

UI diagnostics report request attempts, last latency and actual input/output token
usage. No raw audio is sent or recorded by this interpreter. Finalized text and
relevant semantic choices are sent only after enabling Jev.

## Verification

`python3 -m unittest discover -s tests -p 'test_*.py'`

`node --test tests/harness_web.test.js`

Offline tests cover typed compilation, repeated tasks/items, corrections, limits,
clarification replay, invalid provider data, explicit queue scope, mute before
actual transmission and during backoff, preview acceptance, stale manual edits,
round changes, target identity and unlimited queue policies. Recorded playground
responses also replay through the exact production request builder. These checks
do not establish live Python API connectivity, native execution or microphone
accuracy.

`scripts/jev_playground.py` exports exact production requests and replays copied
playground responses without any network code. Its kitchen is explicitly synthetic.
Playground observations, tuning, fixed-prompt holdout results and limitations are
recorded in the [evaluation report](jev-playground-evaluation.md) and its
[raw request/response evidence](jev-playground-evaluation.json).
