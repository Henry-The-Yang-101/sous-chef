# Native task fixes — 2026-11-05

These focused replays revisit the seven findings in the [140-kitchen audit](environment-task-audit.md). Each task ran in the native headless player with server snapshots, real kitchen physics and normal P2 inputs. P1 stayed still except where a test explicitly positioned a backpack wearer. An observation window expiring is recorded as a wait, not a delivery.

| Original failure | What the replay established |
| --- | --- |
| Ready egg and roast burned | In `s_treehouse_2_1`, the egg finished in a griddle on a counter, with zero fires. The original griddle could either be removed or emptied into a compatible griddle. In `s_keep_3_3`, the original tray came out of the oven with its roast still `Completed`. Two fresh three-burner sushi requests each rescued **and plated all three rice batches**. The choice of utensil now uses both the native transfer and the station’s placement check, within the same route observation. |
| Handoff succeeded on the wrong side of a shared floor | In `s_City_H17`, the flour was still in P2’s hands at 65 s; no false success. In `s_Night_3_3`, the frankfurter went by conveyor and the same item reached a belt 4.34 units from P1 in 8.36 s. Ground passes now check a route from P1 to the predicted landing and check access to the exact settled item. On the moving rafts in `s_rapids_2_1`, two repetitions delivered all four distinct chopped ingredients onto the receiving floor. |
| Ingredient pickup equipped a backpack | A loose pack did not offer its ingredient. With the native pack equipped on P1, ordinary pickup put Flour (`s_campsite_3_1`) and Sausage (`s_treehouse_2_2`) into P2’s hands. In `s_festivemashup_1_3`, a full fetch put dried fruit on the destination counter. Each original pack stayed on P1. |
| Water-gun kitchens could not wash | On the glass level (`s_resort_1_4`), the original dirty stack disappeared and a clean glass stack containing one glass occupied the return station. On the plate level (`s_resort_1_3`), P2 autonomously washed one dirty plate placed on a reachable counter, increasing the number of plates in clean stacks by one, and parked the gun. The ordinary return across the gap did not offer a wash from spawn. The conventional sink and extinguisher controls also passed. |
| Raspberry retried an unsafe throw | In the same 95 s window on `s_Day_2_1`, P2 retained the raspberry and waited: **4 internal steps, 0 repetitions**, down from 203 steps and 200 unsafe-stance entries. No delivery is claimed with P1 stationary. |
| Cargo disappeared during a walking handoff | On `s_moonfestival_1_1`, a fixture removed only the exact cucumber in P2’s hand during the approach. The same request delivered a different cucumber, counted one repetition, and the original was absent from the server snapshot. This reproduces the loss, not its unknown original cause. |
| Marshmallow pickup committed to an unreachable chopping board | On `s_campsite_1_1`, P2 waited for a board for 35 s with empty hands; no pickup or placement ran. On sushi 1-2 the normal mixed cooking/chopping control still completed, with native cooking observed while P2 chopped. |

During heat verification, two roast runs saved the food but lost the task while walking it through a portal. The full navigation trace showed native teleport completion followed by a turn back through the receiving trigger. The navigator now uses a supported, collision-checked outward step to clear the trigger before turning. Three fresh replays subsequently parked the original completed roast on a counter with zero fires. The ordinary portal audit also traversed all four entrances in that kitchen and all three in `s_wizard_school_3_4`.

A 95 s raft replay ended during the fourth pass. A fresh run with a 160 s allowance completed both repetitions in 75 s. Neither run issued a replacement request on timeout. In a separate backpack setup, P2 successfully dispensed flour but did not reach an arbitrarily selected camping counter within 45 s. The camping pickup tests isolate dispensing; the seasonal case independently checks delivery to a counter. This does not establish that all camping counters can be served with a stationary teammate in that pose.

## Reproduce

Start a separate [native simulator](native-simulator.md) for each DLC; use a new output directory for each run. For example:

```sh
python3 scripts/native_simulator.py --runtime .local/replay-fixes start --dlc 3 --level s_festivemashup_1_3 --port-base 36200
python3 scripts/task_audit.py --runtime .local/replay-fixes --port-base 36200 --output .local/replay-fixes-results --case backpack --case lost_handoff
python3 scripts/native_simulator.py --runtime .local/replay-fixes stop
```

Use `--case camp_backpacks` in DLC 5, `--case water_gun` in DLC 2, and `--case repeated_heat` in the base game. For the ordinary task replays, use [environment_task_audit.py](../scripts/environment_task_audit.py) with the level and ingredient above, and `--probe monitor_equipment` for the egg or `--probe take_ready` for the roast. Use `--ingredient DLC05_Marshmallow --preparation chopped` to isolate the board. The raft control is [raft_handoff_audit.py](../scripts/raft_handoff_audit.py) `repeat --cycles 2 --timeout 160`.

Local evidence is under `.local/env-fixes/`: `h/` (rice, egg, carnival), `i/` (water gun, dried fruit, forced loss, ordinary preparation), `k/` (board), `l/raft/` (raft control), `m/` (camping backpacks), `n/` (roast), and `portal/` (portal controls). Workers used a frozen build per batch and separate port ranges, runtime and save directories. Replays on the same worker required a fresh level generation.
