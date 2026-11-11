# Jev interpreter (milestones 4–5)

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
Voice assembly needs the current plugin. Run `python3 scripts/install.py` and
restart the game and harness after updating the repository.

## Behavior and boundaries

- Ordinary work appends. Once Jev selects append, the final check verifies the
  requested work and any conditions on it. In Automatic mode a verified plan
  goes straight to the end of the queue. Queue-only edits, scoped replacement,
  independent Stop, repetitions and a single unlimited tail use the scheduler.
- Complete simple plans use a candidate Choice with a companion coverage Noul.
  Other commands discover task occurrences until explicit `done`; preparation
  discovers item occurrences in the same way. Repeated families/ingredients stay
  distinct. Quantity is resolved after preparation so repeated ingredients in
  different states have separate count context. Every final plan has a Choice
  comparing equivalent/different/unclear meanings and an independent ambiguity
  Noul; both gates must pass.
- Intent routing uses a queue summary and separate early vetoes for concurrent
  duties, external deadlines and persistent area constraints. These vetoes do not replace
  full-plan verification. Later stages retain exact queue entries and target
  bindings for scoped edits and native arguments.
- Task selection includes explicit boundaries between preparation, plated-dish
  serving, ready-food collection, monitoring and other registered assignments.
  Field questions receive the selected contract's interpretation and repetition
  policy. Already accepted work is provided as labelled semantic references;
  exact queue identities remain available for removal and final verification.
- Task-family discovery defers ingredient, recipient and target catalog fit to
  grounded argument questions. Short paired examples clarify task boundaries,
  parsing progress, counts, queue edits, follow-ups and final plan coverage.
  Draft-progress context explains that `done` ends parsing and that
  full quantities are already represented. Corrections preserve unaffected fields;
  clarification is reserved for meaning that changes the executable plan.
- Counts are locally extracted candidates, never Score values or generated JSON.
  Contract defaults apply only to omitted fields. A plain ingredient handoff uses
  raw when the catalog confirms only raw and rules out chopping. Other unspecified
  preparations ask for clarification. Counts outside native limits are not clamped.
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
- After selecting dish assembly, Jev gets the pending orders sorted from
  soonest to latest expiry and chooses an exact ticket. The plugin allocates
  compatible observed resources locally. Missing components or an expired
  ticket stop the plan. See [dish assembly](dish-assembly.md).
- Grounded movement uses observed station, platform and floor bindings. See
  [milestone 5](grounded-movement.md). Concurrent
  high-level responsibilities and event/timed work are not approximated as FIFO.
  Existing exact-target native action contracts can be selected when grounded.

The default gates are .60 for ordinary choices, `done` and coverage, and .80
for unlimited work, complete simple-plan selection, destructive edits, explicit
spoken clarification answers and early rejection of unsupported constraints.
Destructive edits also require separate deletion/interruption evidence or a
direct scoped answer.
These are policy thresholds, not calibrated probabilities of correctness.
Clarifications remain frequent, including correct
drafts. Preview remains the recommended mode; automatic acceptance is an explicit
experimental opt-in.

## Bounds and diagnostics

The dedicated worker reuses a TLS connection. Requests time out after 15 seconds,
with at most two application retries for transient failures, bounded by 90 seconds
and 48 attempts per compilation. Retry-After is respected; it is never shortened
to fit the deadline. Auth/schema failures are not retried. There are no SDK retries,
redirects, images, extended-context headers, or automatic provider calls on startup.
Clarifications expire after 90 seconds, previews after 60. User thinking time is
separate from model time. Resumed work retains the total request count. Each new
command starts with its own utterance and the current kitchen and queue. Replies
and corrections can still refer to the single unfinished command they address.

UI diagnostics report request attempts, last latency and actual input/output token
usage. No raw audio is sent or recorded by this interpreter. Finalized text and
relevant semantic choices are sent only after enabling Jev.

The top of the Jev section shows the current round's reported input tokens,
previously saved rounds (newest first), and their saved total. Cost estimates use
\$0.042 per million input tokens. On round end or clean harness shutdown, the
harness writes one line per nonzero round to `.local/jev-input-tokens.csv`.
The CSV contains counts only, with no header. An in-flight response goes toward
the round that sent it. If no usage is returned (for example, on a timed-out
request), the harness has no count to add. Install bridge 0.8.11 or later to keep
the round identity stable through Player 2 reassignment.

## Verification

`python3 -m unittest discover -s tests -p 'test_*.py'`

`node --test tests/harness_web.test.js`

Offline tests cover typed compilation, repeated tasks/items, corrections, limits,
clarification replay, invalid provider data, explicit queue scope, mute before
actual transmission and during backoff, preview acceptance, stale manual edits,
round changes, target identity and unlimited queue policies. Recorded playground
responses validate against their recorded wire requests and serve as mocked
choices for control-flow regressions. Exact playground replay rejects responses
from older prompts. These checks
do not establish live Python API connectivity, native execution or microphone
accuracy.

Live Jev requests in an isolated native game tested “Make the next order” and
“Make the fish sushi order” with finalized transcript fixtures. Both reached
preview and then completed through the task queue. Independent plate checks
matched the selected tickets, including the cooked rice in the second recipe.

`scripts/jev_playground.py` exports exact production requests and replays copied
playground responses without any network code. Its kitchen is explicitly synthetic.
The corpus runner, `scripts/jev_evaluate.py`, audits expected outcomes against
native contracts and catalogs. It scores explicit rejection separately from
clarification.
