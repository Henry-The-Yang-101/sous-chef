# Task regressions — 2026-11-04

Bridge **0.8.1** addresses the level 1-2 cooked-food reports, repeated preparation
in level 3-3, the ingredient chooser and redundant task observation work.

## Findings and changes

- Preparation uses a level-specific dropdown of native dispenser roots, plus
  observed utensils/plates for the empty-utensil state. IDs remain the API selectors.
- Successful handoffs reserve the exact delivered object until the round resets.
  Later preparation repetitions/commands leave it for the human. Explicit pickup
  primitives remain available.
- The native failure after removing rice was `outcome_not_observed`: a storage
  counter held an empty plate. Native placement moved rice to that plate and kept
  the original empty plate in P2's hand. Parking/handoff now require empty holders.
  The failure snapshot is `.local/task-heat-repro2/repeated_heat/failure.json`.
- A separate native run repeatedly steered right at an inaccessible counter
  approach. Before pressing a button, storage/handoff now tries alternative
  destinations following a movement stall. If none can execute, it retains cargo
  and waits, periodically reconsidering destinations. This is not evidence that
  pedestrians caused every observed stall.
- Ready-food selection watches all burners instead of waiting for the nearest
  unfinished pot. Ready batches are ordered by remaining spoil time. A short
  spoil window can require removing cookware first; the task then fetches a plate
  and collects from the actual off-heat holder instead of an item-only pot stance.
- A suppressed placement may replan only when authoritative held identity,
  contents, destination occupant and stack/dirty counts prove no transfer occurred.
  Ambiguous changed outcomes still fail safely instead of replaying a button.

## Native validation

Source `47d163c`; tested bridge DLL SHA-256:
`e029002dedff3019d18c4afa68df805dd636af1b4e730718fbd3ba42c0aa0144`.

`.local/task-regressions-081-final/report.json` records two passing regressions:

1. Story 1-2 (`s_sushi_1_2`): fill all three pots via native fetch actions, then
   issue one infinite take-ready request. It completes three repetitions, removes
   all three batches without ruining them and leaves **three filled plates**.
   The test explicitly stops the same request after observing completion counts.
2. Story 3-3 (`s_wizard_3_2`, native `Level15`): three raw-cheese preparations deliver
   three distinct native objects, leaving the earlier deliveries in the kitchen.

All measured actions use production logical input. Food is obtained from actual
dispensers; these regressions do not teleport actors or alter cooking progress.
The normal native kitchen scripts remain active. These runs establish the reported
task scenarios, not exhaustive certification of all pedestrian/contact timings.

`scripts/check.py` passes the core, navigation, action and Python suites.
Additional checks cover delivered-item reuse, one observation snapshot per task
pass, occupied-plate exclusion, and the unchanged-versus-ambiguous placement retry
boundary. Browser validation switches between native ingredient and utensil
dropdown choices and selects Cheese Raw in story 3-3.

## Observation cost

An isolated comparison used the installed 0.8.0 DLL and the fixed DLL, sequentially
in the same native 1-2 kitchen, with no rendering and a requested 120 FPS. Each
sample polls at 5 Hz: ten warm-up polls followed by forty measured polls.

| Measurement | 0.8.0 | 0.8.1 |
| --- | ---: | ---: |
| P2 navigation refreshes per idle poll | 7.00 | 1.03 |
| Median catalogue round-trip | 72.4 ms | 34.8 ms |
| Approximate native frame rate | 78.3 FPS | 97.6 FPS |

Evidence: `.local/task-bench-before.json`, `.local/task-bench-after.json`. This is
a small headless comparison; rendered-game gains vary. The task layer now shares
one inventory/navigation observation during each synchronous check and performs
P1 reachability searches only for handoff/human-only sink access. Existing worker
search/throw offloading remains in place; native physics queries remain in Unity.

To repeat task cases, use `scripts/task_audit.py` with `--case repeated_heat` and
`--case repeated_prepare` against an explicitly selected native runtime/port range.
To measure an idle runtime without taking control:

```sh
python3 scripts/task_catalog_benchmark.py --runtime .local/task-fix-native --port-base 26032 --output .local/catalog-benchmark.json
```
