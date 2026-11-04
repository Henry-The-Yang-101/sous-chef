# Preparation regressions — 2026-11-04

Story **4-2** is native scene `s_rapids_4_2A` (`Text.Menu.Level20`). These
changes use native ingredient identities, work outputs, placement predicates and
route proofs, without kitchen-specific production branches.

The subsequent [raft regression pass](raft-handoff-regressions.md) reruns all four
preparation cases on `89ec25e` and extends repeated chopped-food handoffs to three
whole pairs without human collection.

## Fixes

| Commit | Failure and correction |
| --- | --- |
| `3352fda` | The moving raft continually invalidated background discovery before it reached the free chopping board. Preparation can propose compatible equipment on the current support while discovery is incomplete; the active child still proves the route before interacting. |
| `0513ec4` | A chopped potato has a different native identity from its raw dispenser root. Processed requests follow the root's native work-output chain and prefer sources needing less work. Raw requests and ordinary logistics retain exact identities. |
| `ee7e6a1` | A missing ingredient at the front of a request monopolized the iterator. The scheduler selects available units across the full request, while retaining ready-food priority and the existing cooking-window checks. |
| `30e62aa` | A raw food's bounce could miss the primitive's precise receiving window despite leaving the exact food reachable by P1. The task reconciles that observed outcome with a fresh route within five walking units, without replaying the throw. |
| `a978143` | Releasing an armed native use button launches food even during cleanup after failed trajectory revalidation. Arming is now a commitment boundary; native launch callbacks and late revalidation both lead to outcome verification for the original cargo. |

The original board failure is recorded in
`.local/preparation-42-investigation/chop-before-*`: P2 retained the raw potato
in `waiting_for_chopping_board` for the observation window. The fixed board-only
run, `board-fixed-*`, completes chopping and delivery. Later native development
runs exposed `throw_missed` and `task_cargo_changed` during handoffs; their original
traces remain under `.local/preparation-42-run2/` and
`.local/preparation-42-final-results/`.

## Native verification

Source `a978143`, requested 60 FPS, actual installed Unity/PhysX player without
rendering. Tested runtime DLL SHA-256:
`7697211b12a7f9628e0b5de138bb9f80a571b6823abbf2313e9ff8d4fd7842ea`.

Four focused scenarios pass. The accepted summary is
`.local/preparation-42-summary.json`; it points to the original per-case reports,
control traces, fixtures and independent native snapshots.

| Scenario | Observed result |
| --- | --- |
| Infinite raw lettuce/tomato | One accepted request completes two whole repetitions and hands off four distinct native items, then the audit explicitly stops it. No initial food or chef-pose fixtures. |
| Supplied raw chicken/potato | Both floor ingredients are picked up, chopped on the native board and delivered in one completed request. Both chefs start normally. |
| Missing chicken, supplied chopped potato | With both chefs on their normal sides, P2 puts the floor potato directly into a native fryer before chicken exists locally. A server snapshot proves cooking with contents; the checkpoint then stops the request. No chopping step occurs. |
| Cooked chicken/potato | Potato begins cooking while chicken is absent. Raw chicken then arrives on the floor, is chopped and cooked, and both foods are delivered. The final snapshot contains cooked potato on a plate and unruined cooked chicken in its original fryer basket. |

The complete cooked-pair case stages P1's **initial** supported pose on P2's raft,
so nonthrowable cooked cargo has a currently executable handoff. It verifies
preparation, scheduling and delivery, without certifying plate transport between
the separated rafts. Other focused cases leave both chefs at their native spawns.
Food fixtures spawn the selected native prefab and invoke native detach. All
measured P2 movement, pickup, work, cooking, collection and handoff use production
logical inputs; P2 poses, processing progress and gameplay clocks are untouched.

An extended no-collection repeat run completed two pairs, then retained the same
request and cargo while uncollected food crowded the human's landing area.
Its 220-second audit deadline is recorded as a timeout in
`.local/preparation-42-verified-results/repeat_raw/`. This is separate from the
focused two-repetition regression; it does not establish unlimited deliveries
without human collection or available landing space.

`python3 scripts/check.py` passes the core/assembly, navigation, action and Python
suites. `.local/preparation-42-final-check.log` records 2,858 core assertions,
330 navigation checks, 644 action checks and 111 Python tests. Action check counts
can vary with the fixture's incremental discovery iterations.

Three existing native regressions also pass on the same DLL:
`.local/preparation-42-crosslevel/report.json` verifies overlapping rice cooking
and fish chopping in story 1-2, three distinct raw-cheese deliveries in story 3-3,
and delivery of an empty native pot. These exercise the unchanged preparation,
repetition and utensil capabilities outside the reported kitchen.

## Repeat

```sh
python3 scripts/native_simulator.py --runtime .local/preparation-audit start --level s_rapids_4_2A --port-base 26452 --fps 60
python3 scripts/preparation_audit.py --runtime .local/preparation-audit --port-base 26452 --output .local/preparation-audit-results
python3 scripts/native_simulator.py --runtime .local/preparation-audit stop
```

Select cases with repeated `--case` flags: `repeat_raw`, `chopped_pair`,
`cooked_floor`, `cooked_pair`. The runner requires isolated non-default ports,
checks the launch manifest's DLL hash, retains one accepted request and lease per
case, and stops failed/timed-out work before proceeding.

The existing headless-only supplied-food fixtures accept an optional final
`chopped` argument:

```text
task-fixture ingredient SUPPLY_INSTANCE_ID HOLDER_INSTANCE_ID [chopped]
task-fixture loose-ingredient SUPPLY_INSTANCE_ID X Y Z [chopped]
```

That argument selects the terminal prefab in the native work-output chain before
spawning. It does not alter a live item's work or cooking progress.
