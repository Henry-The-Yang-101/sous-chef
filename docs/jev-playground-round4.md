# Jev clarification context and harness audit — 7 November 2026

With synthetic catalog certainty corrected to match native observations, the revised
interpreter completed **12/18 supported requests (67%)**, versus **7/18 (39%)** for
the previous prompts. Supported-command clarification fell from **11/18 to 6/18**.
Neither version accepted an incorrect plan. This is a small diagnostic comparison,
not a calibrated production success rate.

Explicit rejection did **not** improve overall: both rejected **4/6** deliberately
unsupported commands and unnecessarily clarified the other two. Both also clarified
the two genuinely under-specified/ASR-conflicted requests. We now score these
outcomes separately; a clarification on a clear unsupported request is not counted
as a correct rejection.

## Prompt and context changes

The shared policy for scoped Choice questions now says:

> Interpret the requested plan, not whether work has physically finished. Use ordinary paraphrases and the supplied contract defaults. Clarify only when unresolved meaning changes the action or a required field; alternative phrasings with the same executable meaning are not ambiguity. If a clear request has no supported match, choose unsupported when offered; never force the closest task.

This preserves clarification for missing preparation, recipient/scope uncertainty,
and material speech alternatives. Merely finding a related task is insufficient
when a required field remains unresolved. A clear unsupported request should reject.
The model can still trigger clarification by falling below a probability gate even
when its top choice is correct; choosing a clarification label is not the only cause.

General context changes address patterns across commands:

- **Draft progress:** `already_interpreted` and `items` contain requested work
  already captured, including full quantities and repetitions. They are not
  execution history. `done` ends parsing; it does not claim the chef finished.
- **Grounded discovery:** task/native-action selection receives observed ingredient
  and utensil identities, preparation applicability, eligible recipients and exact
  target labels/operations. Unknown applicability remains unknown. Native code
  checks actual availability, routes and compatibility before execution.
- **Field corrections:** changing an item quantity leaves the explicitly named
  recipient intact. Player numbers and item quantities are different fields.
- **Repetition:** ongoing responsibility wording differs from preserving an item's
  state. The default one-cycle option no longer competes merely because an ongoing
  command has no numeric count. This wording alone did not fix the development
  monitoring probe (.64 to .65, below the unchanged .97 gate).
- **Whole-plan comparison:** fetching, processing, delivery and cleanup already
  included by a task contract do not need extra task occurrences.

No semantic-region map, task executor or registry contract was added. No threshold
was lowered (.85 ordinary/done/coverage, .97 critical), no clarification option
removed, and no phrase-to-plan shortcuts added. Initial route questions are unchanged.

## Are the test requests actually supported?

Positive gold was checked against the registry, native wire encoding, complete
plan validation, recipient/ingredient/target catalogs, and native implementations.
The [corpus](../tests/fixtures/jev-clarification-corpus.json) records source references
and sufficient physical prerequisites for every positive case. Preparation quantities
are 1..10; repetition units are batches/sweeps/lists rather than individual plates;
serving requires a completed plated order; standalone movement uses an observed sink
identity and pauses pending dispatch. Queue requests refer to actual fixture entries.

Dirty-dish delivery has a significant prerequisite: the human must have sink access,
and complete route evidence must show P2 cannot reach a sink. That is implemented in
`TaskWashing.Sink(true)`, used by `TaskDirtyDelivery`; it is not a generic handoff task.
Preparation/serving/fire tests likewise assume their native resource and route
prerequisites. These are possible harness assignments under the documented conditions,
not evidence that they can start in every kitchen or in the currently running game.

The audit caught an old fixture defect: food `cooked`/`mixed` states were sometimes
marked applicable/not-applicable, while `SemanticCatalog.Preparation` always emits
`unknown` for these states. Utensil states have their own native applicability table.
We corrected the food flags and repeated **all 26 paired cases**, with production
prompts unchanged. Source audit and schema checks still do not prove real physical
execution. No live game or Python Jev API/SDK was exercised.

“Wash for sixty seconds and then stop” is deliberately **unsupported**. The harness
supports finite washing-batch counts and until-cancelled work, not timed washing.
It was rejected in both comparisons and the extra repeated control: all five
observations scored `external_deadline` at .97. Nothing was partially accepted.
The other negative cases deliberately test simultaneous high-level jobs, semantic
regions, unavailable ingredients, excessive quantities, and chopping an empty pot.
Missing preparation and conflicting ASR counts are separate clarification controls.

## Method and provenance

- Existing Chrome playground controlled through Computer Use; exact production
  State/Questions pasted, Run clicked, visible raw response copied. UI `jev-latest`
  reported `jev-1.14.0` on every observation. No API key or SDK calls.
- Corpus preregistered in `a12a462`; previous production baseline `363023b`.
  Fixture certainty correction committed in `a10cc09`.
- Sixteen development commands produced 79 observations, followed by one focused
  repetition probe. Production prompts were frozen before the first paired holdout.
  No changes to those prompts followed holdout results or the fixture correction.
- First paired comparison: 26 cases, 221 observations. Results 7/18 versus 11/18
  completed. These contexts overstated some preparation certainty, so these are
  preliminary results, not the headline comparison.
- Corrected-fixture repeat: same 26 cases, 224 observations. Results 7/18 versus
  12/18. Because these utterances were already evaluated, this is a replication
  with corrected context, not a second unseen holdout.
- Twenty-three verifier probes use the historical synthetic contexts for
  comparability; 16 wrong/ambiguous proposals were blocked, and 4/7 correct controls
  passed. These probes are outside the corrected-fixture command denominator.
- One additional timed-rejection control. **549 fresh observations total**;
  no response reuse and no supplied clarification answers. The 52 final-prompt
  command sessions across both comparisons replay against exact current requests.

The [raw evidence](jev-playground-round4.json) includes all requests, distributions,
model, timings, usage, complete outcomes and source hashes, including preliminary
and unsuccessful trials. Gold outcomes/audit notes are used only locally and never
sent to Jev. Model evaluation time median was 41.9 ms, p95 105.1 ms; these exclude
network/UI overhead and multi-stage compilation. Total reported usage was 998,847
input tokens and 94,212 output tokens.

## Corrected-fixture results

| Outcome | Previous | Revised |
| --- | ---: | ---: |
| Complete correct supported command | 7/18 | 12/18 |
| Supported command needing clarification | 11/18 | 6/18 |
| Supported command incorrectly rejected | 0/18 | 0/18 |
| Incorrect complete plan | 0 | 0 |
| Explicit rejection of unsupported command | 4/6 | 4/6 |
| Unnecessary clarification on unsupported command | 2/6 | 2/6 |
| Required clarification on ambiguous command | 2/2 | 2/2 |

| Utterance | Previous | Revised |
| --- | --- | --- |
| Collect some dirty plates and wash the batch. | Complete | Complete |
| Fetch an extinguisher and put out the reachable fires. | Complete | Complete |
| Remove ready food from a cooker, parking or serving what you take. | Complete | Complete |
| Keep monitoring unattended equipment until I cancel, rescuing its ready output. | Complete | Complete |
| Deliver one already finished plated order to the serving hatch. | Clarify | Complete |
| Pass the dirty batch to me so I can wash it. | Clarify | Clarify |
| Bring player one four pasta portions, cooked. | Clarify | Complete |
| Deliver two empty pots to me. | Clarify | Clarify |
| Take two raw tomatoes to player four. | Complete | Complete |
| Wash a batch three times. | Clarify | Complete |
| Throw away the burnt food before serving a completed plated order. | Clarify | Clarify |
| Move to the sink and wait there. | Clarify | Clarify |
| Hold the pending jobs; let the currently active job finish. | Complete | Complete |
| Prepare two chopped cucumbers for player four; actually, five cucumbers. | Clarify | Complete |
| Wash a batch, then extinguish the fires, then wash a batch again. | Complete | Complete |
| I need one tomato raw and three tomatoes chopped, brought to me. | Clarify | Complete |
| Remove pending job number two, leaving the other jobs as they are. | Clarify | Clarify |
| Bring me two chopped carrots and one raw onion. | Clarify | Clarify |
| Wash for sixty seconds and then stop. | Reject | Reject |
| Wash dishes while also chopping cucumbers for me. | Reject | Reject |
| Go to the upper-left area of the kitchen. | Reject | Reject |
| Bring me three chopped mushrooms. | Reject | Clarify (should reject) |
| Bring me twenty raw tomatoes. | Clarify (should reject) | Reject |
| Bring me two chopped pots. | Clarify (should reject) | Clarify (should reject) |
| Bring one cucumber to me. | Clarify (expected) | Clarify (expected) |
| Bring me three chopped tomatoes. | Clarify (expected) | Clarify (expected) |

The new completions are cooked portions, corrected quantity, three washing batches,
same-ingredient/different-state delivery, and plated-order serving. No previously
complete command was lost in this corrected comparison. Cooked/corrected lists
showed repeated improvements in `done` (roughly .73–.81 before versus .95–.98 after).
The three-batch completion moved .84 to .97 in the corrected run.

Some gains remain borderline: serving's verifier moved .83 to .87; the same-ingredient
verifier moved .84 to .86 and still clarified at .84 in the first run. Scores also
varied on identical, unchanged routing requests. Dirty delivery completed via the
simple-plan path in the preliminary revised run but failed discovery in the repeat.
Do not attribute every changed outcome solely to the new context, or claim consistency
from one pass at the gate.

Remaining supported failures are dirty handoff (task .71), sink navigation (correct
target .81), selective pending removal (deletion evidence .96 versus .97 required),
empty-pot quantity (correct count .82), the ordered burnt-food/serving request
(incorrect top `done` .59, safely blocked), and a two-ingredient list (correct draft,
coverage .76). These utterances have valid native/queue representations; they are
interpreter failures, not deliberately unsupported requests.

Rejection tradeoff: the revised compiler now reaches and rejects the oversized
quantity. The unavailable ingredient instead stalls at task selection (.72) where
the previous version reached ingredient selection and rejected. Chopped pots still
clarify before local preparation validation. All remain blocked, but the latter two
are missed explicit rejections. More context alone has not solved this distinction.

A useful next general change would be to separate *task-family recognition* from
*catalog-fit validation*, so a missing ingredient can reach its grounded unsupported
check without competing with task families. Another is clearer final comparison for
complete item lists. These are follow-up hypotheses, not changes fitted to this
holdout. Semantic regions would add new supported requests, but would not directly
fix these count, parsing-progress, correction, and verifier failures.

## Local verification

231 Python tests and 11 JavaScript tests passed. Coverage includes exact production
replay, all historical wire observations, complete-plan field equality, verifier
controls, timed rejection, corpus contract/catalog audits, preservation of unknown
native applicability and cached clarification behavior. Provider connectivity,
microphone accuracy and live native execution remain untested in this work.
