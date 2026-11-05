# Held-item task recovery — 2026-11-05

Bridge **0.8.6** keeps autonomous tasks available with occupied hands. Useful
held ingredients take priority over collecting a replacement. Preparation also
uses native chopped outputs and can switch to another unfinished requested
ingredient caught during travel. Compatible held serving plates, extinguishers
and dirty stacks are reused by their respective tasks.

Unrelated cargo is put on a reachable compatible empty counter before the
interrupted step resumes. The same request, repetition progress and control lease
survive the interruption. Native chopping progress stays on the original board;
resuming revalidates its contents. Dirty dishes caught while washing join the
current sink. No available storage means a neutral wait with Stop still usable.
Discovery is read-only, and explicit primitive/logistics commands retain their
cargo preconditions.

## Corrections

- `c85bc16`: task availability checks can evaluate future pickups with empty
  hands without changing the actual carrier. Task steps reuse useful cargo or
  suspend while clearing it; preparation prioritizes eligible held ingredients.
- `52a23f0`: a native input release after a catch can empty the hands before
  parking starts. Recovery observes that departure and resumes the original work
  instead of repeatedly trying to place an item no longer held. Ingredient name
  aliases resolve through dispenser roots: raw and chopped fish share the name
  `SushiFish`, but are different native ingredients.
- `7970545`: a board changed while P2 clears its hands must be revalidated before
  work resumes. Preparation reschedules instead of chopping replacement food.

## Native verification

All **seven held-item scenarios pass** in the real Unity/PhysX player at requested
60 FPS. The tested 0.8.6 DLL SHA-256 is
`1d4e69b1d0265f200e016de7d90f2c253e403233a5070db85fb4764d509d3418`.
Reports, control traces, fixture records and independent native snapshots are in
`.local/held-item-certified/`. The compact accepted summary is
[`held-item-validation.json`](held-item-validation.json).
The standard installer recompiles the same sources; its installed DLL hash and
ownership-manifest verification are recorded separately in that summary.

| Scenario | Observed result |
| --- | --- |
| Held raw potato, story 4-2 | The exact held potato reaches a native chopping board, is chopped, and is delivered. |
| Held chopped potato, story 4-2 | That exact item is delivered directly without chopping or fetching another potato. |
| Unrelated held rice, story 1-2 | Rice survives on a counter while P2 collects, chops and delivers fish. |
| Chopped fish held for a raw request, story 1-2 | The chopped fish is parked; a fresh raw fish is collected and delivered without chopping. |
| Matching catch during source travel, story 1-2 | The exact caught fish is worked and delivered under the original request. |
| Catch for another requested ingredient, story 4-2 | A potato/chicken request switches to the caught chicken, then completes both deliveries. |
| Unrelated catch during chopping, story 1-2 | The caught rice survives on a counter; the exact original fish is worked and delivered without restarting the task. |

The localhost browser also shows **Prepare and give to player** and an enabled
**Start task** button while P2 is idle holding a native fish. Before/held snapshots,
the fixture and the catalogue are in `.local/held-item-ui/`.

All **eight existing native task regressions** also pass on the same DLL:
overlapping preparation, ready-food plating, firefighting, burnt-food disposal,
washing, finite/infinite monitoring and Stop, mixing, and an empty-pot delivery.
Their reports and native snapshots are in `.local/held-item-crosslevel/`.

Catch checkpoints invoke native `CarryItem` with a spawned level ingredient.
These reproduce authoritative carrier state, including arrivals during an active
task; they do not certify a P1 throw trajectory. All measured P2 movement, pickup,
placement, chopping and handoff use production logical inputs. P2 poses, work
progress and clocks are not forced. The chefs begin at native spawns.

The original failed work-interruption trace is retained in
`.local/held-item-verified/catch_working/`; the same-name raw/chopped counterexample
is in `.local/held-item-release/raw_rejects_chopped/`. These development failures
are separate from the final seven accepted cases.

`python3 scripts/check.py` passes: 2,858 core assertions, 335 navigation checks,
762 action checks and 111 Python tests. The action count can vary with incremental
discovery iterations. Additional action regressions cover dirty catches during
washing/collection, held plates and extinguishers, delayed native release during
parking, changed boards, storage exhaustion and Stop.

## Repeat

```sh
python3 scripts/native_simulator.py --runtime .local/held-item-native start --level s_rapids_4_2A --port-base 26632 --fps 60
python3 scripts/held_item_audit.py --runtime .local/held-item-native --port-base 26632 --output .local/held-item-results
python3 scripts/native_simulator.py --runtime .local/held-item-native stop
```

Use repeated `--case` flags to select scenarios. The runner requires matching
isolated ports, verifies the launch DLL hash, retains one accepted request and
lease per case, and stops failed work before proceeding.
