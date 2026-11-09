# Numerical movement coordinates — 8 November 2026

Removed generated location labels from Jev's candidate, player and support data,
and from the movement preview. Positions now use numerical `screen.x` and
`screen.y`, normalized to observed playable geometry: x increases rightward and
y increases downward, as in an ordinary screen/image. Height remains separate.
The native registry still owns its original world/support coordinates.

The prompt explains the coordinate frame, interior versus edge positions, player
references, elevation and moving identities. Each independent candidate question
repeats that candidate's numerical coordinates. Station and raft questions judge
the intended object/support; native route availability is a separate concern
unless explicitly requested. No coordinate thresholds assign semantic regions.
Confidence gates remain .85 for candidate/binding selection and .97 for equivalent
destination approval.

## Paired candidate results

Both sides of this comparison contain **no generated location labels**. The
baseline is the previous production wording with numerical right/up coordinates.
The final version uses standard x/y coordinates and the revised context/questions.
These are `jev-1.14.0` results copied from the signed-in TypeSafe Playground.

| Fixed case | Best intended candidate, baseline | Final | Candidate-stage outcome |
| --- | ---: | ---: | --- |
| Bottom-left board at corner | .98 | .99 | Correct |
| Same request, camera rotated 180 degrees | .85 | .99 | Correct |
| Bottom-left board among three interior boards | .05 | .97 | Correct; baseline clarified |
| Bottom floor area | .42 | .96 | Correct candidates; baseline clarified |
| Right moving raft, route blocked | .94 | .89 | Correct in both; confidence decreased |
| Board on upper deck at overlapping screen coordinates | .81 | .99 | Correct; baseline clarified |
| Board to the speaker's right | .86 | .97 | Correct |

Correct candidate approval increased from **4/7 to 7/7**, with no wrong candidate
approved in the final version. The ambiguous “that chopping board” and missing
sink controls approved nothing in either version. These are per-option Noul
scores, not calibrated probabilities of correctness.

Four fresh cases were run after freezing the candidate prompt: a sink under a
90-degree camera rotation (.96), a spoken correction to the top-right board
(.98), a left raft (.95), and unavailable camera orientation (no acceptance;
highest score .43). All four behaved as expected. The interior-board final
request was repeated once and again scored .97 for the correct board. Both
observations are retained.

## The remaining clarification

Actual binding-stage calls chose station, floor and platform with confidence
.95, .94 and .85 respectively; all clear the unchanged gate. Replaying these
real binding and candidate responses through `Compiler.movement` completes the
interior-board and raft choices correctly.

The floor command still clarifies at its **equivalence stage**. Its accepted
positions are all in the intended bottom area, but the original equivalence
question scored .58. Explaining that unspecified tiles permit local choice raised
this to .89, then .94. The final .94 remains below .97, so the code retains the
clarification instead of silently weakening the gate. A separate control with
two unresolved board identities scored .09 for equivalence and was rejected.
That control forces candidate approvals solely to isolate the equivalence gate.

This distinction matters: 7/7 candidate approval is **not** 7/7 completed movement
compilation. Full-plan verification and preceding task discovery were not evaluated
here. No headless simulator, game build, installation or provider API/SDK call ran.

## Reproducibility and unsuccessful trials

[Raw evidence](jev-coordinates-playground.json) preserves all 58 Playground
responses, their exact requests, fixed fixture worlds, intended answers and
variant names. It includes unsuccessful revisions: a longer explanation with
right/up axes confidently chose the wrong interior board; per-question numbers
and a percentage scale alone did not solve that problem. Those variants were
not shipped. The final candidate stage was frozen before the four held-out cases;
the later equivalence wording change does not affect their requests.

Export current production requests without any network activity:

```sh
python3 scripts/jev_coordinates_playground.py
python3 scripts/jev_coordinates_playground.py --stage binding
```

Targeted offline checks passed: 16 semantic-map/compiler tests and 11 browser-JS
tests. They verify numerical projections, absence of generated location labels,
exact production equality for the recorded final/held-out requests, and the real
binding/candidate/equivalence replay outcome. Earlier labeled observations remain
archived against their original requests, not relabeled as current-prompt evidence.

The nine paired final requests used 27,006 input tokens versus 21,071 for the
baseline (28% more); numerical evidence is repeated per question for clarity.
Median reported model evaluation was 35.88 ms versus 35.71 ms; those timings omit
browser/network overhead. This is a small synthetic diagnostic
evaluation, including tuning examples, not a general accuracy or latency benchmark.
