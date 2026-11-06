# Native assembly decision audit

This audit expands beyond the original `s_sushi_1_1`, `s_sushi_1_2`, and
`s_wizard_school_3_4` assembly replays. It exercises the production P2 executor
inside an isolated, windowless native game. The normal game installation is
unchanged. P1 receives no scripted gameplay input.

The runner chooses a live ticket and explicitly allocates observed plates,
prepared ingredient instances, vessels, and processing stations. Fixtures supply
the initial prepared ingredients on empty counters, with P2's initially empty
hands available as one additional holder in small kitchens. They do not advance
mixing/cooking clocks or move the chef during measured execution. A separate
recipe selector can wait for a particular live ticket for regression replays.

<!-- selected-results:start -->
## Selected replay results

18 additional assembly kitchens were exercised beyond the original three-case suite. The latest retained observations contain 14 independently verified completions and 4 observation timeouts. The existing staircase kitchen is an additional regression, excluded from those counts.

The machine-readable [result index](dish-assembly-sweep.json) records the exact recipe, binary hash, sampled food clocks, decisions, waits, and local trace directory for every row. Successful earlier runs remain explicitly tied to their tested binary; this is not a claim that every kitchen ran on the last build.

| Kitchen | Recipe | Latest observed result |
| --- | --- | --- |
| `s_balloon_1_5` | `Pasta_TomatoOnly_New` | Verified, 53.44 s |
| `s_balloon_2_2` | `Pasta_MushroomOnly_New` | No completion within 140.45 s |
| `s_balloon_2_3` | `Pasta_Marinara_New` | Verified, 36.89 s |
| `s_mine_2_5` | `Chicken_Burrito` | Verified, 40.91 s |
| `s_mine_2_6` | `BeefBurgerCheese` | Verified, 62.43 s |
| `s_wizard_3_1` | `MargheritaPizza` | Verified, 50.52 s |
| `s_wizard_3_2` | `PeperoniPizza` | No completion within 140.48 s |
| `s_wizard_3_3` | `BeefBurgerWithGreensNCheese` | Verified, 57.70 s |
| `s_sushi_1_3` | `Sushi_Cucumber` | Verified, 18.68 s |
| `s_sushi_1_4` | `Sushi_Cucumber` | Verified, 28.30 s |
| `s_sushi_4_1` | `Sushi_Cucumber` | Verified, 21.28 s |
| `s_space_6_2` | `Pancake_Chocolate` | Verified, 31.28 s |
| `s_mine_4_3` | `BeefBurgerWithGreensNCheese` | Verified, 30.08 s |
| `s_mine_5_3` | `ChickenNuggetsAndChips_All` | No completion within 140.48 s |
| `s_balloon_5_2` | `BeefBurgerMax` | Verified, 24.10 s |
| `s_city_h2` | `SteamedSpecial_Carrot` | Verified, 42.77 s |
| `s_city_h3` | `SteamedSpecial_Carrot` | No completion within 140.45 s |
| `s_city_h4` | `SteamedSpecial_Prawns` | Verified, 36.92 s |
| `s_wizard_school_3_4` | `Pancake_Chocolate` | Verified, 39.12 s |

Across these selected traces: 1204 scheduler events, 4873 control observations, and 2183 native food-state samples. Earlier failed and interrupted development replays remain under their separate local sweep directories.
<!-- selected-results:end -->

## Evidence collected

Each case retains its initial and final full native snapshots, allocation,
`decisions.ndjson`, `kitchen.ndjson`, transitions, and report under the referenced
`.local/assembly-sweep-*` directory. Control observations include child actions,
navigation diagnostics, actor motion, held objects, resource reachability, request
identity, and lease ownership. A separate native stream samples food state and
fires approximately every 0.4 seconds. The scheduler's bounded event sequence is
checked for missing records.

All reported completions also have an independent Python comparison of the
saved native plate tree against the ticket tree. It preserves ingredient IDs,
duplicate quantities, cooking methods, and mixed/cooked nesting; it does not
trust the task's `compositionVerified` flag alone. Native processing state must
be complete. Sampling bounds the precision of heat-removal measurements.

Wait durations are review indexes, not automatic failure verdicts. Cooking timers,
missing routes, and absent allocated resources are distinguished from executable
work that the scheduler ignored. Observation timeouts are never counted as dish
completion. Environmental fire spawns are retained separately from ruined food
when interpreting the raw fire warnings.

The two long waits in otherwise successful earlier replays were checked against
their remaining work. In `s_mine_2_6`, the cooked beef stayed safely in P2's pan
while the completed route catalogue had no route to the assigned bun. In
`s_wizard_3_1`, both pizza additions were already complete before the wait for
oven access. Neither had another recipe addition to perform during that interval.
The steamer's roughly 10-second mixing and 12-second cooking waits likewise
match its native processing clocks.

In the selected `s_city_h4` replay, environmental fire first appears about two
seconds into execution, before any food starts cooking (about 22.8 seconds).
The spawned objects are `FireHazard`; no monitored food becomes ruined. The raw
warnings remain in the index instead of being silently treated as food fires.

The final burger replay retained its request and control lease across a short
`unsafe_update_interval` navigation pause, then completed in 57.70 seconds.
The final marinara replay completed in 36.89 seconds; its three ready batches
left their processors approximately 0.60, 1.01, and 1.81 seconds after the first
sample showing completion. The chicken burrito completed in 40.91 seconds with
no repeat of the rice-carrying fall. These are observed runs, not a performance
benchmark or a guarantee for other initial states.

## Reproduced defects and changes

- Native-rejected intermediate plating caused repeated collection/parking of
  ingredients and eventually stranded partial burgers. The executor now checks
  the native intermediate plate predicate before collection and completes a
  suitable assigned preparation base before plating it.
- A moving kitchen's repeatedly invalidated broad route catalogue hid a ready
  pasta pot from rescue. That pot burned in the initial run. Hot owned batches now
  invoke the bounded destination planner directly. Follow-up runs removed the
  pot before burning.
- The same incomplete catalogue caused useful held food to be parked and picked
  up repeatedly. Held valid additions and bound batches ready for their next
  processor can request a directed route; a completed catalogue reporting no
  route still withholds ordinary speculative work.
- A conveyor kitchen exposed a destroyed cached ingredient during the next heat
  check, terminating the task with `task_observation_unavailable`. Assembly
  inventory lookups now skip destroyed entries.
- A steamer replay stopped with `outcome_not_observed` after a pickup left the
  exact bowl on its counter and P2 empty-handed. Persistent finite pickups now
  return a retryable result only when authoritative source, holder, stack, and
  cargo observations prove the unchanged state. Dispenser and changed-source
  cases remain non-retryable.
- Regression coverage now holds the assigned plate with another chef while P2
  mixes, cooks, and rescues the food. Independent recipe dependencies continue
  while plate selection is pending; final plating still requires that allocation.
- A three-part pasta replay burned fish while P2 held an emptied prawn pan and
  waited for a storage route. Empty allocated utensils are now parked when useful
  work runs out, before another hot batch needs free hands. Heat-related storage
  can probe a candidate with the bounded navigator. The replay completed all three
  cooked components with no ruined food.
- A mine-floor inside corner supported the native sphere cast but not the chef's
  centre. Its rounded contact normal looked like a ramp. Horizontal, non-step
  boxes now require centre support when a route leaves their footprint. Coplanar
  joins, actual ramps, and native steps retain their semantics. Captured geometry
  tests and a full burrito replay verify the correction; a native staircase
  pancake replay also still completes.
- A fryer storage action spent its entire observation window retrying a broad
  kitchen search. Navigation recovery now focuses its bounded search on the
  requested station, using the same route and native-interaction proofs.
- After that fix, the fryer revealed a second wait: assigned ingredients were
  omitted from the incomplete route catalogue. Assembly now makes a second
  scheduling pass over its allocated resources after known/local work. Ordinary
  actions that spend two seconds recovering a route yield before input, briefly
  defer that resource, and let other branches run. A complete catalogue proving
  no route still blocks speculative collection. Heat rescue retains priority.
- The first broader resource probe exposed a pickup/parking loop around a safe
  cooked fryer basket when the plate route was still unknown. Source discovery
  now requires a usable receiving route before collecting stored food. Held
  cargo can still seek its destination. A failed transfer defers its receiver,
  so it does not incorrectly mark the food itself as the unavailable resource.
- Staging a processing vessel now honors temporarily rejected counters, using
  the same parking selector as empty-utensil storage. A failed counter is not
  immediately selected again while its deferral is active.

The runner itself was corrected to allocate from the full observed inventory,
not only immediately actionable logistics options. Earlier runs that omitted
required ovens or steamer baskets are retained as setup limitations, not
evidence that the executor ignored assigned equipment. A transient partial native
status-file read is retried before loading the next case.

## Remaining behavior and navigation issues

- `s_wizard_3_2`: the dough received all three required pizza additions. Both
  assigned ovens were on raised platforms and had no route in the catalogue;
  the uncooked pizza remained safely off heat. Assembly did not complete.
- `s_city_h3`: mixing completed and the bowl was removed. The three assigned
  steamers were on the upper floor and had no route in the completed catalogue.
  No further recipe branch was available.
- `s_balloon_2_2`: no assigned plate or processing vessel obtained a usable route.
  No ingredient addition occurred during the full 140-second observation.
- `s_mine_5_3`: the last replay picked up an empty fryer basket, then made 61
  staging attempts alternating between two counters whose routes could not be
  certified. No placement or ingredient addition followed the initial pickup.
  The immediate repeated-counter selection was fixed, but this continuing
  planning churn remains excessive and does not count as useful progress.
- `s_balloon_1_5`: the final replay completed the exact pasta recipe in 53.44
  seconds, but the tomato pan remained on heat for about 9.39 seconds after
  readiness. It entered `OverDoing` at about 45.21 seconds and was removed at
  50.82 seconds without becoming `Ruined`. Storage/navigation recovery delayed
  rescue, including a brief abandoned pasta-collection attempt between rescue
  attempts. This is a remaining heat-management risk despite valid final
  composition; the completion is not an all-clear behavior verdict.

The selected traces contain no scheduler sequence gaps, lost control leases,
changed request identities, native observation errors, or ruined food. Those
checks do not excuse the stalled work or delayed rescue above. The fryer setup
failure before sweep 14 is recorded separately in the index because no assembly
request was dispatched in that attempt.

## Verification and limits

`python3 scripts/check.py` passes at production source `16af0b0`: 2,885 core
assertions, 429 navigator checks, 1,149 action checks, 20 harness checks, and
136 Python tests (`.local/assembly-final-check-08.log`). Action assertion counts
vary with incremental discovery iterations. The final built plugin and isolated
audit runtime both have SHA-256
`7ba56b6b3705b06a07f6dce0473b05ead282085c161aba0ee0a2db6681da76dc`.

The result index identifies each native replay's binary separately. Core, navigator,
action, harness, and Python checks are separate from the native kitchen runs;
mocked action checks are not presented as gameplay evidence.

An earlier action-suite run failed the existing firefighting catalogue-availability
assertion while the native game was running concurrently
(`.local/assembly-probe-actions-03.log`). The subsequent complete suite passed
with the audit runtime stopped (`.local/assembly-final-check-07.log`). This
intermittent failure is retained; its precise cause was not established.

These are selected recipes in 18 additional base-game assembly kitchens, plus
the original staircase regression. They do not establish every recipe, initial
allocation, moving-kitchen phase, or concurrent human interaction. Prepared-food
fixtures isolate assembly and its combined processing dependencies. Failed
navigation remains a limitation even when the executor safely retains the
request and protects its food. Audit deadlines stop those requests explicitly.

## Reproduction

```sh
python3 scripts/native_simulator.py --runtime .local/assembly-check start --port-base 36720
python3 scripts/assembly_sweep.py --runtime .local/assembly-check --port-base 36720 --output .local/new-assembly-sweep
python3 scripts/assembly_sweep.py --runtime .local/assembly-check --port-base 36720 --output .local/new-pasta-regression --levels s_balloon_2_3 --recipe Pasta_Marinara_New
python3 scripts/native_simulator.py --runtime .local/assembly-check stop
```

Use a fresh output directory. A case's report records the actual runtime DLL hash;
separate rebuilds are not assumed to be byte-identical. The committed result
index records the final selected replay for each kitchen and its source trace.
