# Conveyor navigation — 2026-11-02

The navigator now treats native floor conveyors as directed routes. It follows
their live world velocity, including rotated tiles, border rules and the belt
that supplied entry into a corner. A stopped belt permits both directions;
checked sideways exits remain available. This is an intentional routing policy:
manual upstream walking can still be physically possible on a weaker belt.

## Reproduced problems and changes

In the mine loop, `s_mine_4_3`, the native chef walks at 6 units/second and the
belts move at 5. The previous feasibility check therefore accepted upstream
travel at only 1 unit/second, while the distance search could prefer that shortcut.
The new direction check rejects it in graph edges, connectors, shortcuts and
remaining-route validation.

In `s_sushi_1_4`, a request for Cucumber reached a stance on the moving belt,
neutralized walking to align, drifted away, then walked upstream to that same
stance repeatedly. The observed native relative speed was 2.5 units/second,
which could never satisfy the 0.2-unit/second settled-arrival check. The baseline
was stopped manually after 33.6 seconds; it had not arrived. The fixed planner
selects a reachable calm side at `[12, 0, -2.4]`. If no stable working side exists,
the station is withheld instead of promising an impossible settled arrival.

Native conveyor graphs now retain bounded search progress after exact checks of
speed, direction, border, enabled/active state, world pose, grid coordinates and
connected neighbours. Native entering-surface history also participates. Unknown
moving surfaces still require fresh searches. Starting navigation revalidates a
certified idle tree instead of discarding a distant route to rebuild it in one
frame. Conveyor lanes check footprint support around outer lips, and steering
preserves each turn through native changes in belt velocity. Actual-body
connectors retain contact clearance so small drift can recover to those lanes.

## Native verification

The [recorded results](conveyor-validation.json) include the runtime DLL hash,
individual arrivals and all direction probes. The production navigation source
is `1e2b118`. Tests used the installed Unity/PhysX game in an isolated headless
runtime, with native spawns after every reset and ordinary `navigate_to` calls.
No staged positions, modified belt fields, raw steering, physics replacements,
clock changes or failed-action retries were used.

Six native segment pairs passed: four mine belts covering `+X`, `-X`, `+Z`,
`-Z`, and both sushi belt directions. Each downstream segment was accepted;
each reverse segment was rejected as `conveyor_opposes_walking`.

| Kitchen / requested FPS | Route | Complete round trips |
| --- | --- | --- |
| Mine `s_mine_4_3` / 60 | Frying pan → Meat | **5 / 5** |
| Sushi `s_sushi_1_4` / 60 | Cucumber → SushiRice | **4 / 5** |

Every successful mine round trip observed movement on 25–26 distinct conveyor
colliders, including turns and opposing horizontal belt orientations. Successful
sushi trips observed 12–13. Arrival required native positioning, facing,
settling and target selection. The longest temporary heading against an observed
entering-belt velocity in the mine was 35.4 ms during a corner transition;
there was no sustained upstream attempt. The audit allows at most 150 ms for
that native transition and also requires actual conveyor coverage and arrival.

The failed sushi run stopped with `no_safe_start` after native contact with a
`Worktop` near `[20.049, 0, -3.918]`; it did not loop. Low-FPS traversal is **not
fully fixed**: at requested 20 FPS the mine completed 0/3 round trips and sushi
1/3, with the other attempts stopping as `no_safe_start` around contacts or floor
edges. Those failures remain visible in the report. Conveyor direction checks
passing does not establish reliable traversal of every contact at every FPS.

The full check passed 840 core/assembly assertions, 290 navigation checks,
267 action checks and 98 Python tests. Navigation doubles cover direction
reversal, stopped/rotated belts, lateral exits, drifting arrival rejection,
supported turns, contact recovery, retained waypoints and search invalidation
when native conveyor fields, neighbours or entry history change.

## Repeat

```sh
python3 scripts/native_simulator.py --runtime .local/conveyor-check start --port-base 26712 --level s_mine_4_3
python3 scripts/conveyor_audit.py --runtime .local/conveyor-check --output .local/conveyor-check-results --repeat 5 --fps 60
python3 scripts/native_simulator.py --runtime .local/conveyor-check stop
```

The runner writes every attempt, including failures, and exits nonzero if a route,
direction pair, conveyor-coverage requirement or sustained-upstream check fails.
Add `--fps 60 20` to include the known low-FPS cases. Default coverage is 60 FPS.
Local raw evidence is in `.local/conveyor-verified-60/`,
`.local/conveyor-direction-probes.json`, `.local/conveyor-audit-v6/`, and the
baseline `.local/conveyor-baseline-cucumber/`.
