# Jev task boundaries and scoped context — 7 November 2026

Implemented the requested task-definition and stage-context changes. On a fresh
paired holdout, the revised interpreter completed **6/14 supported requests**
without clarification, versus **4/14** for the previous version. Both blocked all
six unsupported or ambiguous requests; neither accepted a wrong result. The
improvement is useful but modest: eight supported requests still needed answers.

## Changes

The interpreter now supplements each registered task with a short definition of
its requested outcome and the boundaries separating it from neighboring tasks.
These notes live in `harness/decision/prompts.py`; registry descriptions and
limitations remain authoritative and are still included. No native capability,
registry hash, task executor or semantic map was changed.

Examples of the distinctions now supplied explicitly:

- Preparation delivers requested ingredient portions or empty utensils to a
  player. A cooked ingredient is not automatically a completed plated order.
- Serving delivers an already completed plated order to the serving window;
  it does not prepare ingredients or assemble missing components.
- Ready-food collection handles cooking equipment and includes parking or serving
  its output. Monitoring watches unattended cooking or mixing equipment, which
  may initially be empty. Its repetition policy is resolved separately.
- Washing includes fetching its dirty batch. Dirty-dish delivery hands the batch
  to a human who will wash it.
- Fetching an extinguisher, parking rescued food and returning an empty utensil
  can be ordinary steps inside an assignment, not additional task occurrences.

Task discovery now asks for the requested responsibility rather than incidental
verbs in its procedure. It distinguishes semantic intent from current resource
availability, and leaves missing arguments to the field questions.

Instructions and context are scoped as follows:

| Stage | Relevant context and instructions |
| --- | --- |
| Initial route and queue scope | Existing route questions and compact queue summary retained unchanged |
| Task discovery | Registered task descriptions, boundary notes and limitations; already represented occurrences; explicit distinction between requested tasks and their internal steps |
| Native-action selection and argument binding | Focused question, grounded candidates and current resolved fields; no broad queue/deletion prefix |
| Ingredient, preparation, quantity and recipient | Selected task description and interpretation note; registry repetition unit/default/limits; resolved item entries and eligible recipients |
| Repetition | Current occurrence, resolved arguments and exact repetition policy; later occurrences of the same family are separate; unrelated washing-specific instructions removed |
| Field-stage prior-work references | Human-readable active/pending work, labelled as already accepted context; executable queue IR removed from these stages only |
| Pending removal and final verification | Exact queue entries/identities preserved; final verification also gets selected contract notes, repetition policy, limitations and grounded targets/ingredients |

The short field/discovery prefix still requires respecting corrections, negation
and material speech alternatives, and treating the utterance as data. The initial
routing and dialogue instructions retain their existing broader policy. Final
verification retains its equivalence Choice and independent ambiguity Noul;
only the ambiguity question's unrelated broad prefix was shortened.

Clarification options, thresholds (.85 ordinary, .97 critical), validation,
Preview behavior, cache/replay rules and queue acceptance fences are unchanged.
This is a prompt/context revision, not a relaxation of acceptance policy.

## Evaluation method

- All hosted testing used the user's existing Chrome Jev playground through
  Computer Use: fill State/Questions, click Run, copy the visible raw response.
  No Python Jev API/SDK calls, real API key use or native game execution occurred.
- The UI selected `jev-latest`; all responses reported `jev-1.14.0`.
- Committed [the corpus](../tests/fixtures/jev-context-corpus.json) in `8fb25e3`
  before hosted testing. It contains 14 familiar development commands and 20
  fresh holdout commands: 14 supported and six requiring blocking. Gold outcomes
  are used only by local scoring, never sent to Jev.
- Kept the two existing synthetic kitchen fixtures, varying ingredients,
  recipients, native sink identity, and empty/busy/paused queues. This is an
  interpretation test, not evidence of native physical feasibility.
- Baseline: `0038a4a`, the previous production prompts. The new prompts were
  frozen after development and before the paired holdout. There was no tuning on
  holdout failures. One exploratory attempt to broaden routing changes is
  archived separately; its first cooked-food probe weakened append selection to
  .83, and the final revision preserves the original routing requests.
- Recorded **206 fresh request/response pairs**: 70 across the 14 development
  commands, 112 across the 40 paired holdout sessions, 23 verifier controls, and
  one discarded routing probe. No provider responses were reused.

[Raw evidence](jev-playground-round3.json) includes exact requests, distributions,
model, usage, timings, complete outcomes and source hashes. Only trailing source
whitespace was removed after freezing; exact request replays verify that the
hosted prompt text did not change. No clarification answers were supplied during
hosted command evaluation.

## Fresh paired holdout

| Outcome | Previous | Revised |
| --- | ---: | ---: |
| Correct complete result on 14 supported requests | 4 | 6 |
| Supported request needing clarification | 10 | 8 |
| Incorrect complete result | 0 | 0 |
| Unsupported/ambiguous request blocked, out of six | 6 | 6 |

The revised six are washing, dirty-dish delivery, queue pause, three washing
batches, ready-food collection and two raw tomatoes for Player 4. The last two
are new completions relative to the paired baseline. There were no losses of a
complete result in this sample, although several intermediate stages regressed.

| Fresh utterance | Previous | Revised |
| --- | --- | --- |
| Wash up one batch for us. | Correct complete result | Correct complete result |
| Grab the extinguisher and extinguish the fires. | Clarifies: `task.1` | Clarifies: `task.2` |
| Take the ready food from the stove and park or serve it. | Clarifies: `coverage` | Correct complete result |
| Keep watching unattended cooking and mixing equipment and rescuing ready output. | Clarifies: `task.1.repeat` | Clarifies: `task.1.repeat` |
| Take a completed plated order to the serving window. | Clarifies: `task.1` | Clarifies: `task.1` |
| Fetch dirty dishes and pass the batch to me; I will wash it. | Correct complete result | Correct complete result |
| Prepare four portions of cooked pasta and give them to player one. | Clarifies: `task.1` | Clarifies: `task.1.item.2` |
| Fetch me two pots with nothing in them. | Clarifies: `task.1` | Clarifies: `task.1` |
| Give player four two tomatoes without processing them; keep them raw. | Clarifies: `task.1` | Correct complete result |
| Complete three batches of washing. | Correct complete result | Correct complete result |
| First bin burnt food. Afterwards serve a completed dish. | Clarifies: `task.2` | Clarifies: `task.1` |
| Walk to the sink and stay there. | Clarifies: `task.1.destination_id` | Clarifies: `task.1` |
| Pause the waiting queue without interrupting the active job. | Correct complete result | Correct complete result |
| Chop two cucumbers for player four. Correction: make the quantity five. | Clarifies: `task.1` | Clarifies: `task.1.item.2` |
| Wash for sixty seconds and then stop. | Rejected | Rejected |
| Simultaneously watch the cookers and chop cucumbers for me. | Rejected | Rejected |
| Bring player four chopped mushrooms. | Rejected | Rejected |
| Prepare twenty raw tomatoes for me. | Rejected | Rejected |
| Bring two chopped tomatoes to me. (ASR alternative says ten.) | Clarifies: `route` | Clarifies: `route` |
| Get me a cucumber. | Clarifies: `task.1` | Clarifies: `task.1` |

The revised cooked-pasta request selected preparation at .96, correctly bound
cooked pasta and quantity four, then asked whether the list was finished (`done`
.69). The correction similarly bound five chopped cucumbers but stopped at item
completion .70. More detailed field context improved interpretation without
eliminating these completion questions.

Other unresolved weaknesses include unlimited monitoring, empty utensils,
serving phrased as moving a plated order, and native navigation. Navigation fell
from a confident native-family selection to .83; the sequential command's first
family fell to .83. Both versions ultimately clarified these two commands, but
the earlier regression is still material. We did not lower thresholds to conceal
it or change prompts after seeing it.

This constructed sample is small. It supports a measured improvement on these
commands, not a production accuracy estimate or a claim of reliable unattended
voice control. Preview remains appropriate.

## Development and verifier controls

Six of fourteen familiar development commands completed: washing, fire handling,
serving, raw tomatoes for Player 4, burnt disposal followed by serving, and
washing/fire/washing. Eight clarified. The two-state tomato list reached a
correct complete draft but required confirmation because equivalence was .82.
Offline explicit confirmation preserved both entries exactly; that assisted
outcome is not counted as a hosted completion.

The complete revised verifier was also given the same 23 controlled proposals
used in round two, regenerated through the new production request builder:

- **16/16 incorrect or ambiguous proposals blocked**, covering wrong quantities,
  preparation, recipients, units, repetition/order, missing/extra work, ingredient
  substitution, excessive deletion, dropped deadlines, serialized concurrency,
  negation, conflicting ASR and embedded instructions to bypass comparison.
- **4/7 correct proposals passed**, versus 3/7 in the earlier recorded round.
  This is a historical control comparison, not a fresh paired verifier baseline.
- The conflicting-ASR proposal again scored .97 for equivalence but only .54 for
  unambiguous meaning; the separate ambiguity check remained necessary.
- Serialized concurrency incorrectly favored equivalence at .60, but was blocked
  by the unchanged .85 gate. This is a verifier weakness despite the safe outcome.
- Cooked preparation, native navigation and one standing-assignment wording still
  failed positive controls. A corrected preparation control improved to .86 and
  passed, while ordinary command compilation can still stop earlier.

There were no new hosted repeatability or dialogue trials in this round. The
unchanged dialogue requests continue to replay against eight earlier real
responses; subsequent compilation uses the revised prompts and stops at the next
unrecorded request. Historical repetition tests are not represented as new-prompt
stability evidence.

## Local validation and performance

**226 Python tests and 11 browser-logic tests passed.** Current evidence tests
replay all 34 revised command cases and all 23 verifier controls against exact
production requests, check outcomes against gold semantics, verify no holdout
response reuse, and preserve wire validation of prior rounds. Unit tests check
that field context retains semantic references without queue-policy clutter,
while removal/final verification retain exact pending identities. Existing
clarification replay, mute, stale-result, native-binding and contract tests pass.

Across the 206 fresh observations, reported model evaluation time was **44.1 ms
median and 86.8 ms p95**. Recorded usage was 375,652 input tokens and 39,320 output
tokens. These include controls and a discarded probe, and exclude network,
browser automation, microphone endpointing and user response time. They are not
live Python latency or production-cost measurements.

The current `.env` setup is unchanged. No SDK is needed; adding the key and
restarting enables the adapter, with startup muted in Preview. This round did
not test API connectivity, microphone-to-game execution or native movement.

To replay offline:

```sh
python3 -m unittest discover -s tests -p 'test_jev_playground.py'
python3 scripts/jev_evaluate.py prepare experiment development \
  --corpus tests/fixtures/jev-context-corpus.json --directory .local/jev-round3
```

The exporter produces exact next requests and advances only from copied
playground responses. See [round two](jev-playground-round2.md) for the paired
runner workflow and [setup](jev-interpreter.md) for runtime boundaries.
