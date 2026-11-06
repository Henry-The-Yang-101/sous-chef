# Unattended equipment monitoring

`monitor_equipment` discovers the native drivers that advance food without a
chef working there: cooking stations, mixing stations, and cooking regions.
Native cooking-step definitions cover fryer baskets, pots, pans, ovens,
grills/griddles and heated cookers; mixing drivers cover bowls and blenders.
The task does not identify equipment by prefab name, recipe, or utensil name.
Portable food with a progress handler is not itself a processing station, and
ordinary chopping boards are excluded.

Readiness follows the station's processing stage. For example, a bowl in a
mixer uses mixing completion even when its unused cooking handler reports
zero-progress cooking. A bowl currently baking uses cooking completion instead.
Monitoring continues to scan all accessible processors, then removes or plates
ready food through the existing observed native interactions.

## Moving-kitchen failure

A native story 4-2 (`s_rapids_4_2A`) fryer replay before this fix accepted a
monitoring request, recognized the ready basket and picked it up. It then stayed
at `waiting_for_safe_counter` until the 75-second audit timeout. The rotating
raft repeatedly invalidated the kitchen-wide walking catalogue. Local storage
proofs were previously enabled only during preparation requests.

All running tasks can now use those same checked local storage routes on their
current support. Monitoring and ready-food removal may also propose local
equipment while global discovery is incomplete; the active child navigator
still has to prove and walk the route. A complete catalogue with no route still
withholds the target. Native placement vetoes, occupied counters, conveyor
exclusion and the normal pre-press checks remain in force.

Optional plating no longer waits for kitchen-wide discovery before rescuing
ready food, or after safely parking its container. A currently obtainable
compatible plate is used when its travel fits the spoil window; otherwise the
finished container is removed and parked safely.

## Verification

`python3 scripts/check.py` passed the plugin build, API audit, core, navigation,
action and harness checks, and 131 Python tests. Action regressions exercise
server-side cooking/mixing drivers, cooking regions, correct stage readiness,
local storage during incomplete discovery, placement vetoes, and the exclusion
of cold portable containers and active-work boards.

The isolated native runtime passed:

| Case | Observed result |
| --- | --- |
| Two fryer batches on the moving 4-2 raft | One finite monitoring request completed two repetitions; both original baskets were emptied, both batches were cooked on plates, and no fire remained. |
| Ordinary pot monitoring | Cooked rice was removed; unlimited empty-equipment monitoring retained its request and lease, and Stop cancelled it. |
| Mixing | A native Flour/Egg batch finished mixing and its original bowl's contents were removed from the mixer. |
| Repeated heat removal | One ongoing request rescued and plated all three native rice batches without ruining them. |

Fixtures only supplied chopped potatoes to P2's hands for the fryer case.
Ingredient insertion, cooking, walking, food removal and parking used production
actions and the native game. These are representative processor checks, not a
replay of every kitchen or DLC variant. The user's normal running game was not
modified or restarted by these tests.

Local evidence is under `.local/equipment-monitor/`: `baseline/`,
`verified/report.json`, `fryer-recheck/`, and `check.log`.

To reproduce in a separate runtime while the normal game is open:

```sh
python3 scripts/native_simulator.py --runtime .local/equipment-monitor/runtime start --level s_rapids_4_2A --port-base 38420 --fps 60
python3 scripts/task_audit.py --runtime .local/equipment-monitor/runtime --port-base 38420 --output .local/equipment-monitor/verified --case fryer --case monitor --case mixer --case repeated_heat
python3 scripts/native_simulator.py --runtime .local/equipment-monitor/runtime stop
```
