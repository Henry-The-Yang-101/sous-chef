# Native teleporter coverage — 2026-11-02

The installed two-player inventory contains **164 scene entries: 140 kitchens
and 24 story scenes**. Nine kitchens contain player-sending teleporters.
All **28 directed entrances** in those nine kitchens passed native traversal
followed by a verified arrival on the combined build (`7e74ddf`).
The other 131 kitchens had no player-sending portal components in the active and
inactive scene inventory. Food-only senders are not walking edges.

There is one access limitation: **`s_dynamic_stage_03` still has no certified
route from the normal spawn to its portal platform**. Its stair connection is
rejected for body clearance/support discontinuity. Both portals passed after an
explicit supported initial-pose fixture; this establishes portal traversal,
not spawn-to-platform navigation. The audit reports the setup rather than
counting it as a successful walking route.

## Final combined-build cases

| Session | Kitchen scene | Entrances covered | Successful navigation requests | Native teleports | Setup |
| --- | --- | ---: | ---: | ---: | --- |
| Base | `s_wizard_5_5` | 2 / 2 | 2 | 2 | Native spawn |
| Base | `s_wizard_3_3` | 4 / 4 | 2 | 4 | Native spawn |
| Base | `s_wizard_school_3_4` | 3 / 3 | 4 | 6 | Native spawn; moving receiver |
| Base | `s_dynamic_stage_04` | 2 / 2 | 2 | 2 | Native phase transitions |
| Seasonal (3) | `s_festivemashup_1_5` | 1 / 1 | 1 | 1 | Native cannon boarding/flight |
| Campfire (5) | `s_city_h10` | 8 / 8 | 4 | 8 | Native spawn |
| Horde (7) | `s_keep_3_3` | 4 / 4 | 2 | 4 | Native spawn |
| Carnival (8) | `s_Day_3_4` | 2 / 2 | 2 | 2 | Separate left/right native cannon fixtures |
| Base | `s_dynamic_stage_03` | 2 / 2 | 3 | 2 | Native phase transitions, staged pose, supported point goals |
| **Total** | **9 kitchens** | **28 / 28** | **22** | **31** | |

Every measured route used the production navigator and normal Player 2 control
lease. Station goals used live destination IDs. The final dynamic kitchen used
the existing production point navigator because the portal platforms have no
reachable stations. Its first request stayed on the same platform and counted
zero teleports; only the two later native transitions covered entrances.
Cannon and phase fixtures do not establish autonomous cannon piloting or cooking.

A separate **20 FPS** regression completed **3/3 round trips, 6/6 legs and 12
native teleports** in `s_wizard_3_3`. Each leg required at least two transitions;
walking-only arrivals could not pass. FPS is the requested Update rate. Native
physics retained its 0.02-second fixed timestep.

## Evidence and reproduction

[teleporter-audit.json](teleporter-audit.json) preserves the complete scene
inventory, per-case source IDs/positions, DLL hashes, setup kinds and counts.
Full local reports and per-route NDJSON traces are under
`/tmp/sous-chef-teleporter-results/`:

- `merged-kitchens/`: the six ordinary/phase-selected kitchen cases.
- `merged-carnival-left/`, `merged-carnival-right/`, `merged-festive/`:
  native cannon setup cases.
- `merged-dynamic/`: the explicit initial-pose and point-goal case.
- `merged-20fps/`: the additional low-FPS round trips.
- `report.json`, `final-base/`, `dlc2-final/`, `intros/`: the scene inventory
  sweep and follow-up inspection of inactive portals and skipped intros.

All final cases succeeded on fresh loads; earlier failing runs remain in their
original directories. They exposed receiver-only exits, elevated native landing
fallbacks, moving animation targets, floor-contact recovery, flush frames,
shallow platforms, inactive phases and the separate stair limitation above.

Use an isolated runtime and unused ports. The other agent's simulator used a
different runtime and port range and was not stopped or modified.

```sh
python3 scripts/native_simulator.py --runtime /tmp/portal-audit start --port-base 25712 --level s_wizard_3_3
python3 scripts/portal_audit.py --runtime /tmp/portal-audit --port-base 25712 --output /tmp/portal-standard --level s_wizard_3_3 --level s_wizard_5_5 --level s_wizard_school_3_4 --level s_dynamic_stage_04 --level s_city_h10 --level s_keep_3_3 --portal-phase-setup
python3 scripts/portal_audit.py --runtime /tmp/portal-audit --port-base 25712 --output /tmp/portal-left --level s_Day_3_4 --cannon-setup --cannon-aim-down --portal-index 0
python3 scripts/portal_audit.py --runtime /tmp/portal-audit --port-base 25712 --output /tmp/portal-right --level s_Day_3_4 --cannon-setup --cannon-aim-down --cannon-transit --portal-index 1
python3 scripts/portal_audit.py --runtime /tmp/portal-audit --port-base 25712 --output /tmp/portal-seasonal --level s_festivemashup_1_5 --cannon-setup
python3 scripts/portal_audit.py --runtime /tmp/portal-audit --port-base 25712 --output /tmp/portal-dynamic --level s_dynamic_stage_03 --portal-phase-setup --stage-portal-index 1 --portal-points
python3 scripts/native_simulator.py --runtime /tmp/portal-audit stop
```

These are native Unity 2017.4.8f1 runs with `graphicsDevice: Null` and
`physicsAutoSimulation: true`, not analytical substitutes. The installed game
assembly hash is recorded in the JSON. The combined build also passed 823 core
assertions, 207 analytical navigation checks, 181 action checks and 84 Python
tests. Those checks complement the native evidence; they do not replace it.
