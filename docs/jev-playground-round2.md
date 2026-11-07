# Jev playground generalization round — 6 November 2026

Historical round at `0038a4a`. The [third round](jev-playground-round3.md) evaluates
the current task-boundary and scoped-context changes on fresh paired commands.
Descriptions of current prompts/tests below refer to this round's snapshot.

The revised interpreter completed **7 of 21 supported held-out requests without
clarification**, compared with **5 of 21** for the previous version. Both versions
blocked all eight unsupported or ambiguous requests. Neither accepted an incorrect
result in this sample. This is a modest improvement, with regressions as well as
gains; the current system still asks too often to call general voice control
reliably automatic. Preview remains the default.

This round saved **505 additional distinct playground request/response
observations**, including controlled prompt comparisons, paired command runs,
negative controls, dialogue and repetition probes. No Jev API/SDK was called from
Python, no key was used, and no game command was executed.

## Method

- Used the existing Chrome playground through Computer Use. Every hosted request
  was entered into its State and Questions editors, run with the visible Run
  button, and copied through the UI. The UI model was `jev-latest`; every response
  identified the actual model as `jev-1.14.0`.
- Committed the [corpus](../tests/fixtures/jev-generalization-corpus.json) in
  `0833c20` before testing its 29 holdout commands. It contains 12 development
  commands and 29 different holdout commands, with expected semantic results.
  Expected answers never enter provider requests.
- Used two synthetic kitchens: carrots/onions/rice with Players 1 and 3, and
  tomatoes/cucumbers/pasta/empty pots with Players 1 and 4 and a different sink
  ID. Included empty, busy and paused queues, exact native targets, counts,
  corrections, repeated occurrences, negation, unsupported constraints and
  conflicting ASR alternatives. These fixtures test interpretation, not native
  resource availability or delivery success.
- Compared the baseline at `0833c20` with the frozen revised compiler. The
  holdout required **157 fresh UI calls: 73 baseline and 84 revised**. No
  clarification answers or reused responses were supplied in the holdout.
- Stopped each command at completion, clarification or rejection. A correct
  result must match action, arguments, quantities, preparation, recipient,
  repetition, order, queue mutation and after-plan behavior. Asking a question
  is an abstention, not a successful complete interpretation.
- The release prompts and thresholds were frozen before the holdout. No prompt
  was changed in response to its failures. Source and corpus hashes are in the
  evidence. Subsequent verifier/dialogue/repetition probes used those same
  builders. Experimental development variants are explicitly labelled.

The [raw evidence](jev-playground-round2.json) retains 337 sessions, exact
requests, raw distributions, Noul values, model IDs, usage and timings. Its 563
response references include 58 explicitly attributed reuses of identical
**development** observations; those count only once in the 505 total. The
experimental variant named `final` is a discarded context experiment, not the
release. Production cases are named `release_*`.

## Paired holdout results

| Outcome | Previous | Revised |
| --- | ---: | ---: |
| Correct complete result on 21 supported requests | 5 | 7 |
| Clarification on a supported request | 16 | 14 |
| Incorrect complete result | 0 | 0 |
| Unsupported/ambiguous requests safely blocked, out of 8 | 8 | 8 |
| Explicit rejection among those eight | 2 | 4 |
| Clarification among those eight | 6 | 4 |

The revised seven comprise five task plans, one queue pause and one no-change
result. Four requests gained a complete result: burnt-food disposal with a busy
queue, standing equipment monitoring, preservation of the queue, and two raw
tomatoes for Player 4. Two lost automatic completion: a fire sweep and removing
the second pending job. The latter still needs explicit deletion confirmation;
its deletion score fell from .97 to .93, below the unchanged .97 gate.

The fire request reached the correct draft but its new equivalence probability
was only .68. Repeats scored .69 and .65. Serving reached a correct draft at .84,
just below .85; repeats stayed at .84 and .83. These were recorded as failures
to complete, without lowering thresholds or adding phrase-specific exceptions.

| Held-out utterance | Previous | Revised |
| --- | --- | --- |
| Please get a batch of plates washed. | Correct complete result | Correct complete result |
| Send out one finished plated dish. | Clarifies: `route` | Clarifies: `coverage` |
| Rescue the food that is ready on a cooker. | Clarifies: `task.1` | Clarifies: `task.1` |
| Deal with the fires. | Correct complete result | Clarifies: `coverage` |
| Bin the burned food. | Clarifies: `route` | Correct complete result |
| Keep an eye on the cooking and mixing stations until I stop you. | Clarifies: `coverage` | Correct complete result |
| Collect a batch of dirty dishes and hand them to me for washing. | Correct complete result | Correct complete result |
| Do three washing batches. | Clarifies: `route` | Clarifies: `task.2` |
| Player four needs two tomatoes, left raw. | Clarifies: `coverage` | Correct complete result |
| Bring me four cooked pasta portions. | Clarifies: `task.1` | Clarifies: `task.1` |
| Get two empty pots for me. | Clarifies: `task.1` | Clarifies: `task.1` |
| Give me one raw tomato and three chopped tomatoes. | Clarifies: `task.1.item.2.fields` | Clarifies: `task.1.item.2.fields` |
| Bring player four two—sorry, five—chopped cucumbers. | Clarifies: `task.1.item.2` | Clarifies: `task.1.item.2` |
| Wash a batch, put out the fires, then wash another batch. | Clarifies: `task.1.repeat` | Clarifies: `task.1.repeat` |
| Dispose of the burnt food, then deliver a finished dish. | Clarifies: `task.2` | Clarifies: `task.2` |
| Keep the queued jobs; don’t remove them. | Clarifies: `route` | Correct complete result |
| Hold off on starting the waiting jobs. Let the current task finish. | Correct complete result | Correct complete result |
| Let the queued jobs start again. | Clarifies: `coverage` | Clarifies: `coverage` |
| Drop only the second waiting job; keep the rest. | Correct complete result | Clarifies: `route` |
| Stop the current job and wash dishes now, but keep all the waiting jobs. | Clarifies: `route` | Clarifies: `task.1` |
| Go stand at the sink. | Clarifies: `route` | Clarifies: `task.1.destination_id` |
| Spend half a minute washing up. | Clarifies: `route` | Rejected |
| Watch the cookers while chopping cucumbers for me. | Clarifies: `route` | Rejected |
| Bring me two chopped mushrooms. | Rejected | Rejected |
| Bring me 11 raw tomatoes. | Rejected | Rejected |
| Give me two chopped tomatoes. (ASR alternative says ten.) | Clarifies: `route` | Clarifies: `route` |
| Bring me cucumber. | Clarifies: `task.1` | Clarifies: `task.1` |
| Move that over there. | Clarifies: `route` | Clarifies: `route` |
| Keep washing forever, then put out the fires. | Clarifies: `task.1.repeat` | Clarifies: `task.1.repeat` |

The correction resolved the intended quantity five but asked whether the item
list was complete. The repeated-tomato request correctly bound the first raw
entry and found the second entry, then stopped at chopped preparation .84. The
new quantity dependency therefore addresses one source of confusion but does
not solve the whole multi-entry path. Cooked pasta, empty utensils and ready-food
rescue still have weak task-family separation. Repeated tasks and sequential
plans also encounter low completion or repetition confidence.

These 29 constructed commands are a small, deliberately broad sample. Zero
wrong acceptances here does not establish a production error rate; the many
abstentions limit what it says about unattended execution. Later tuning should
use new development cases and a fresh holdout, not repeatedly optimize this one.

## General changes retained

1. **Separate intent from unsupported constraints.** A four-way Choice answers
   whether this is new work, an existing-queue edit, no change or unclear intent.
   Separate Noul questions check simultaneous duties, external deadlines/events
   and region destinations. A sufficiently strong constraint signal vetoes the
   request early. Low scores never authorize execution; field resolution, local
   contracts and whole-plan verification remain necessary.
2. **Scope routing context.** Routing sees whether active work exists, the
   pending count, pause state and append default. Existing task IR was competing
   with the new command's candidate plans. Later stages still receive exact
   queue entries, including the identities needed for pending-task removal.
3. **Resolve preparation before quantity.** These are now dependent requests.
   Quantity receives the resolved preparation, which distinguishes the same
   ingredient requested raw and chopped. On five paired development probes,
   the previously weak first count improved from .80 to .95; the other intended
   counts remained strong. This adds one request per item entry.
4. **Compare meanings explicitly.** Final coverage is a Choice between
   `equivalent`, `different` and `unclear`, with the separate ambiguity Noul
   retained. Both applicable gates must pass. Exact-target verification includes
   the selected target's label and operations. Full-plan confirmation remains
   bound to the exact displayed semantic plan.
5. **Keep contract labels and argument grounding.** Task choices include their
   registered labels, descriptions and limitations; the native-action boundary
   distinguishes identified-object operations from preparation/delivery. This
   does not introduce ingredient-specific or level-specific routing rules.
6. **Ground unfinished-command references.** Follow-up classification receives
   the locally constructed meaning of the unfinished draft, when available.
   This helps interpret references such as “make that three.” The .97 gate for
   these potentially revising follow-ups is unchanged.

Ordinary choices, `done` and coverage still use .85. Unlimited/simple selection,
destructive scope and explicit spoken answer binding still use .97. Final
coverage requires the `equivalent` option probability and the ambiguity Noul;
these scores are not multiplied or treated as calibrated correctness estimates.

The 12-command development corpus went from three to four complete correct
results, with both blocked cases still blocked. It informed tuning and is not a
second holdout. Replaying identical development requests saved repeated UI calls;
that reuse is recorded and excluded from unique observation counts.

## Changes tried and discarded

The archive includes all these comparisons, including weaker outcomes:

- A minimal verifier state removed contracts and other context. Some preparation
  scores improved, but valid monitoring fell from .87 to .19, while a wrong
  preparation's match score rose from .03 to .54. This was discarded.
- Shorter task-family descriptions helped serving but weakened corrected
  preparation and empty-utensil classification. The retained version keeps the
  full contract limitations and adds the contract label.
- Combining all unsupported constraints in one scope Choice made valid requests
  compete with unrelated constraints. Independent positive veto questions worked
  better in the development comparisons.
- Extending the compact queue summary to task discovery improved one busy-queue
  completion score from .72 to .87. A broader development check then weakened
  two preparation cases, including one to .81. The extension was discarded;
  only routing uses that summary.
- Rewording follow-up instructions without adding the unfinished meaning did not
  consistently help. The retained addition supplies context, without new
  phrase-matching rules or a lower confidence gate.

The initial Choice-verifier experiment accepted five of six positive controls
and blocked six wrong plans. That experimental prompt was not byte-identical to
production, so those figures are not presented as the final verifier's accuracy.

## Final verifier, dialogue and repeat probes

The exact production verifier was tested on **16 incorrect or ambiguous
proposals**: wrong quantities, preparation, recipients, units, repetition or
order; omitted or invented tasks; ignored negation; ingredient substitution;
dropped time/event constraints; serialized concurrency; excessive queue deletion;
conflicting speech alternatives; and an instruction inside the utterance to
ignore the comparison rules. **All 16 were blocked.**

The conflicting-speech example scored .97 for equivalence but only .56 for
unambiguous meaning. The independent ambiguity check was necessary to block it.
The concurrent-duty proposal's top `different` probability was only .51, so
selecting a leading option without its gate would also be insufficient evidence.

Only **3/7 correct verifier controls** passed both gates: pausing the queue,
repeated washing/fire/washing, and two burnt-food disposals. An accurate standing
assignment, cooked-pasta delivery, exact-target navigation and corrected
preparation asked for confirmation. These isolated probes reinforce the
remaining over-abstention problem; they do not measure whole-command compilation.

Eight new dialogue probes used requests captured directly from the real service
with a fake initial draft, followed by actual playground responses:

| Reply | Observed decision and local behavior |
| --- | --- |
| Yes, that whole plan is right. | Answer/confirmation 1.00; returns to Preview |
| Discard this command. | Cancel .95; discards the unfinished command |
| Forget this request. Bring me two raw onions. | New command .91; starts fresh compilation |
| Leave them raw. | Answer .87, raw 1.00; resumes at quantity with raw bound |
| Make that three batches, not one. | Correction 1.00; recompiles the original plus correction |
| After that, take the ready food off the heat. | Separate 1.00; remains queued behind the unfinished preview |
| Never mind, cancel my last request. | Cancel 1.00; discards the unfinished preview |
| No, the other one. | Top correction .85; below .97, so requests a complete restatement |

The seven clear replies passed their applicable gates; the vague reply did not.
Offline service replay verified those transitions and that no task entered the
game queue. For a correction, new command or preparation answer, the probe stops
at the next required provider request. It does not fabricate downstream provider
responses or count that as a completed new plan. Earlier development follow-ups
still scored .96 and abstained, so this is not a claim that all corrections work.

Finally, eight exact decision requests were each run three times in total:
washing, burnt disposal, raw tomatoes for Player 4, fire handling, serving,
concurrent duties, ASR conflict and removal. **All eight retained the same local
gate outcome across the three runs.** Scores varied: the conflicting-speech route
moved from .74 to .82 and .83, still below .85. These are repeated individual
stages, not 24 independent full-command tests or a guarantee of stability near
all thresholds.

## Timing and local verification

Across the 505 unique observations, provider-reported evaluation time was
**38.5 ms median, 83.0 ms p95, 19.4–208.1 ms range**. Recorded usage totalled
992,844 input tokens and 107,178 output tokens, including discarded experiments.
This is not a production cost estimate.

A simple held-out washing command took two requests and 66 ms of summed model
evaluation; raw tomatoes for Player 4 took ten requests and 392 ms. The mixed
preparation-plus-washing development command took fifteen requests and 608 ms,
then required confirmation. These sums exclude network, browser, audio
endpointing and user response time. Live Python end-to-end latency is untested.

Validation completed:

- **225 Python tests and 11 browser-logic tests passed.**
- Nine recorded-evidence tests check all 719 wire observations from both rounds,
  replay 41 current command cases with exact requests, replay 23 verifier probes,
  eight dialogue probes and sixteen additional repeated observations, and check
  the held-out corpus hash and lack of response reuse.
- Six correct complete drafts that stopped at coverage were explicitly confirmed
  only in offline regression tests; all preserved their expected values without
  another provider call. These assisted outcomes are excluded from holdout scores.
- Dependency tests cover preparation-to-quantity binding, routing versus exact
  removal context, target grounding, early constraint vetoes, and the need for
  whole-plan verification when an early constraint score is inconclusive.

The previous round's native harness UI verification is described in the
[round-one report](jev-playground-evaluation.md). This round changed interpreter
logic and prompts, not the UI. API connectivity, real microphone input and
native game execution remain untested as requested. Adding the API key to `.env`
and restarting remains the setup path; startup stays muted in Preview.

## Reproduce offline

```sh
python3 -m unittest discover -s tests -p 'test_jev_playground.py'
python3 scripts/jev_evaluate.py prepare experiment development
python3 scripts/jev_evaluate.py advance experiment development
python3 scripts/jev_evaluate.py report experiment development
```

The runner exports the next exact requests into `.local/jev-round2/batch.json`.
Only responses copied from the playground should be appended to each session's
`responses` list as `{ "request": ..., "response": ... }`. `advance` validates
and replays the entire prefix and refuses changed prompts. The gold corpus is
scored locally and never included in a request. For an individual command, the
[existing exporter](../scripts/jev_playground.py) also supports `start`, `record`,
`next` and explicit `answer` operations. None of these tools imports or calls the
provider transport.
