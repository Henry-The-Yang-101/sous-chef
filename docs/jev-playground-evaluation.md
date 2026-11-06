# Jev playground evaluation — 6 November 2026

This is the **historical first round**, recorded against the implementation at
`8df24b5`. See the [second-round generalization report](jev-playground-round2.md)
for the current prompts, 505 additional observations and the paired 29-command
holdout. The results and raw evidence below are retained unchanged as history;
they are not current-prompt scores.

Milestone 4 is implemented and works as a bounded interpretation and review
flow. The current prompts often select the right actions and arguments, but
still ask for clarification frequently. **Use Preview. These results do not
establish reliable unattended automatic acceptance.**

## Method and evidence

- Used the user's existing Chrome tab at the [TypeSafe playground](https://console.typesafe.ai/playground), through Computer Use.
- Entered the exact `state` and `questions` emitted by the production Python
  compiler. No separate demonstration prompt or hand-written approximation was
  used. Selected `jev-latest`, the version exposed by the UI; every copied raw
  response reported **`jev-1.14.0`**, matching the adapter's pinned version.
- Clicked Run and copied raw responses through the UI. Python only constructed
  requests, validated responses and replayed them offline. **No Python Jev API
  call, SDK call or real game command was made.**
- Used a synthetic kitchen with carrots, onions, rice, a sink, Players 1 and 3,
  and the actual shared capability registry. Some queue-scope probes included a
  synthetic active task and pending queue. This does not test native feasibility.
- Saved **214 distinct request/response pairs across 41 command utterances and
  eight dialogue probes**. Copied
  prefixes reused during offline replay count once. Earlier records retain the
  exact earlier prompts; they are tuning evidence, not final-prompt scores.
- After freezing prompts and thresholds, ran eight new utterances once each,
  stopping at completion, clarification or rejection. No clarification answers
  were supplied in that holdout. The earlier 13-case cohort informed tuning and
  is explicitly not a holdout.
- Captured the service's actual clarification/follow-up requests using a local
  fake transport, then ran those eight requests in the same playground. Their
  initial drafts were synthetic fixtures; the dialogue responses are actual
  provider observations. They are additional probes, not part of the holdout.

The [machine-readable evidence](jev-playground-evaluation.json) includes complete
requests, distributions, Noul values, model IDs, token usage, evaluation times,
source hashes, final-case outcomes and provenance. At this round's commit,
`tests/test_jev_playground.py` replayed its 21 final command cases, eight verifier
probes and eight dialogue probes against those exact builders. The current suite
still validates these archived wire responses and replays the second round's
cases against current production prompts. Changing a prompt makes that replay
fail instead of silently using stale answers.

## Frozen-prompt holdout

| New utterance | Result without an answer supplied | Evidence |
| --- | --- | --- |
| Wash the dishes one time. | Correct preview: wash one batch | 2 requests; coverage .95, unambiguous .97 |
| Throw away burnt food twice. | Correct preview: two repetitions | 5 requests; count .99, done .92, coverage .94 |
| Keep washing dishes until I ask you to stop. | Correct preview: wash until cancelled | 2 requests; unlimited candidate 1.00, coverage .87 |
| Prepare two raw onions for player three. | Correct complete draft; asks for full-plan confirmation | 9 requests; onion .98, raw/count/recipient 1.00; final coverage only .64 |
| Take the cooked food off the heat, then wash the dirty dishes. | Clarifies first task; no partial plan | Correct top family `take_ready`, but .84 is below .85 |
| Don’t do anything; leave the queue alone. | Clarifies; changes nothing | Wrong top route `queue_only` only .50, stop scope .48; both blocked |
| Wash dishes for twenty seconds. | Clarifies; changes nothing | Route .62; unsupported duration is not silently reduced to one batch |
| Wash dishes, and keep taking food off the heat at the same time. | Rejects unsupported concurrent duties | Unsupported route .85 |

Thus **3/8 reached a correct complete plan, 4/8 asked for clarification, and 1/8
was rejected**. Of the five supported work requests, three completed without a
question. No incorrect plan passed in this sample. The small sample and many
abstentions make it unsuitable for claiming an overall accuracy percentage.

The preparation example is a useful failure: ingredient, preparation, quantity
and recipient were all correct, while the verifier's confidence remained low.
The harness presents the entire proposed meaning for explicit confirmation. An
offline replay verified that confirming this exact plan produces two raw onions
for Player 3, once, appended; it does not trigger more provider requests. That is
a user-assisted result and is not counted as an autonomous holdout success.

## Final tuning cohort

The final production prompts were also replayed against 13 earlier examples:
five complete results (four changes plus one no-change), seven clarifications,
and one unsupported rejection.

| Case | Observed result |
| --- | --- |
| Wash dishes | Correct one-batch plan |
| Wash two batches of dirty dishes | Correct two-batch plan; repeated verifier run .95–.96 coverage |
| Keep monitoring the cooking equipment until I cancel | Correct unlimited monitoring plan |
| Pause dispatch after the current task | Correct `pause_pending`; active task remains running |
| This kitchen looks ridiculous | No change |
| Please give player one three chopped carrots | Correct draft; full-plan confirmation because coverage .56 |
| Bring me one raw onion and two chopped carrots, then wash a batch of dishes | Correct two-task draft with both distinct items; confirmation because coverage .78 |
| Bring me one raw carrot and two—no, four—chopped carrots | Stops at the first quantity: correct top count 1, confidence .78 |
| Please serve one completed dish | Correct top task, confidence .78; asks |
| Prepare two chopped strawberries for me | Stops at task discovery, confidence .77; no invented ingredient |
| Give me twelve chopped carrots | Rejects out-of-range quantity; never clamps to ten |
| Do not clear the queue | Asks; no deletion |
| At the same time, wash dishes and rescue food from the cookers | Asks; no conversion of concurrency into FIFO |

Offline replay of explicit full-plan confirmation also preserved the exact
three-carrot and mixed-item plans. No clarification answers were invented in
the provider recordings. Earlier correction experiments did confirm a quantity
and obtained the corrected value four, but did not finish the full plan; those
are partial tuning observations.

## Deliberately incorrect plans

Ran the final production coverage verifier on six known incorrect interpretations.
All fell well below the .85 gate and produced clarification rather than acceptance.

| Deliberate error | Coverage Noul |
| --- | ---: |
| Wrong quantity | .02 |
| Wrong preparation state | .02 |
| Wrong recipient | .01 |
| Omitted second task | .05 |
| Clearing a queue despite negation | .10 |
| Treating one plate as one washing batch | .20 |

Two positive controls illustrate over-abstention: an accurate standing assignment
scored .86 and passed; an accurate pause request scored .84 and asked for
confirmation. These probes test the verifier in isolation, not the compiler's
ability to construct every plan or its resistance to every misleading input.

## Clarification and follow-up probes

| Reply to an unfinished command/question | Provider decision | Local behavior |
| --- | --- | --- |
| Yes, that is exactly what I meant. | Answer 1.00; full-plan confirmation .99 | Shows the confirmed plan in Preview |
| Chopped, please. | Answer .94; chopped 1.00 | Resolves preparation and resumes compilation |
| Cancel that unfinished command. | Cancel .97 | Discards only the unfinished command |
| Actually, bring me two raw onions instead. | New command .98 | Starts a fresh interpretation; original is not accepted |
| Never mind that unfinished command. | Cancel .99 | Discards the unfinished preview |
| Actually, make that two batches. | Correction .79 | Asks for complete restatement |
| Then deliver the dirty dishes. | Separate .92 | Asks for complete restatement |
| Instead, the other thing. | Correction .95 | Asks for complete restatement; does not guess the other task |

Five of eight dialogue decisions passed their applicable gates. The .97
follow-up gate blocked two otherwise clear replies as well as the deliberately
unclear one. This is another usability limitation; a response's leading choice
alone is not enough to accept it. The final local flow presents a normal
clarification with expiry and restatement input, without accepting either
unfinished interpretation. No thresholds were relaxed after these probes.

The offline replay checks the exact service requests and these state transitions.
For the new-command and preparation-answer probes, it stops at the next required
provider request; subsequent task compilation was not observed in those probes.
Answers introducing a value outside the supplied clarification choices require
a complete restatement; they are not silently inserted into the bound draft.

## Changes driven by the runs

1. Made one repetition and append defaults explicit. Initially even “Wash dishes”
   produced simple coverage .68 and `done` .87; after tuning, the final simple
   request and separate verification complete correctly in two requests.
2. Split high-level task discovery from native-action discovery. Preparation
   otherwise competed with its own fetch/pick-up/move child operations. Added
   an explicit native-action branch instead of deleting native capabilities.
3. Gave ingredient choices meaningful keys and clearly described already-covered
   quantities. A three-carrot entry accounts for all three carrots; it is not
   three item entries. Repeated ingredients with different preparations remain
   distinct occurrences.
4. Fixed local number parsing so “player one three chopped carrots” contains
   separate one and three candidates. Merged omitted counts into the actual
   default candidate, avoiding competition between `unspecified` and `count_1`.
   Invalid numbers remain unsupported; they are never rounded or clamped.
5. Sent eligible recipients and resolved item arguments at the stage that needs
   them. Separated item quantities from repetition of the entire item list.
6. Removed correlation UUIDs/revisions from semantic verification while retaining
   them locally as acceptance fences. Added readable exact plan meanings and
   relevant preparation/recipient context.
7. Added fingerprint-bound whole-plan confirmation for low final coverage. A
   confirmation binds the displayed interpretation and retains revision, identity,
   expiry and native contract checks. In Preview mode it still requires Accept.

An earlier staged run successfully kept both washing occurrences in “Wash dishes,
take ready food off the heat, then wash dishes again,” with final coverage .97.
That used an earlier prompt revision and is retained as exploratory evidence;
offline compiler tests independently verify occurrence preservation. Three
option-order reversals also retained the intended leading route (including a
low-confidence negated no-change); this is a small sensitivity probe, not an
order-invariance guarantee.

The final gates are .85 for ordinary choices, completion and coverage; .97 for
unlimited/simple-plan selection, destructive scope and explicit spoken answer
binding. Lowering ordinary completion gates made some correct drafts usable;
it did not remove the separate full-plan verifier or local validation. The
holdout was run after these choices. The probabilities remain uncalibrated.

## Performance and local validation

Across the 214 distinct recorded pairs, provider-reported `evaluation_time_ms`
had a **39.9 ms median, 84.3 ms p95 and 22.7–147.4 ms range**. Recorded usage
totalled 378,290 input tokens and 39,020 output tokens. These include exploratory
prompt versions and are not a production cost estimate.

A simple final command needed two requests. One-item preparation needed nine;
the mixed preparation-plus-washing example needed thirteen. Their summed model
evaluation times were about 76–121 ms for the two new simple cases, 518 ms for
the held-out preparation and 519 ms for the mixed tuning example. These numbers
exclude browser overhead, transport/network latency, microphone endpointing and
user thinking time. No end-to-end latency claim is supported yet.

Local validation completed:

- 220 Python tests, including 39 interpreter tests and six recorded-playground
  regressions. The latter cover 21 complete case replays, eight verifier probes
  and eight dialogue probes.
- Eleven browser-logic tests, including exact native preview arguments and
  disabled acceptance during follow-up interpretation.
- Chrome UI test with a fake-only local transport: enable typed commands without
  microphone capture, submit, confirm a full interpretation, verify an empty
  queue during Preview, explicitly accept, and mute while preserving the accepted
  task. A 390×844 phone viewport had no horizontal overflow. The temporary server
  and tab were closed; the user's playground was left open.

Final local review also made pending-task removal name its exact ordinal and
task in both confirmation and preview. That description change has an offline
regression test; it was not a new hosted removal trial. All recorded final-case
requests still match the production compiler exactly.

The adapter's HTTP framing, keep-alive, gate-at-write behavior, retries and stale
results were tested with mocked streams/transport. Actual Python provider
connectivity, real microphone input and native game execution remain untested,
as requested. More held-out paraphrases, realistic ASR alternatives and live
end-to-end latency measurements are needed before relying on Automatic mode.

## Reproduce without the API

```sh
python3 -m unittest discover -s tests -p 'test_jev_playground.py'
python3 scripts/jev_playground.py start example 'Wash dishes'
```

The exporter writes exact state/questions under `.local/jev-playground/`. Paste
them into the playground, run, copy its raw JSON response to a local file, then:

```sh
python3 scripts/jev_playground.py record example path/to/copied-response.json
python3 scripts/jev_playground.py next example
```

For a deliberately supplied clarification answer, use `answer example OPTION_KEY`.
The exporter never imports or invokes the provider transport. See
[interpreter setup](jev-interpreter.md) for enabling the real adapter later.
