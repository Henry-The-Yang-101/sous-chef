# Native headless simulator

This runs the **installed Overcooked! 2 Unity 2017 player**, with its original
PhysX simulation, chef controllers, asset bundles, kitchen scripts, and local
server/client lifecycle. It is a silent background process with no rendering or
window. It does not require menu input or random theme selection.

The former approximate CPU simulator has been removed. Navigation unit tests
remain for isolated regressions; this native runtime is the simulator for
complete level traversal.

## Start and repeat

Steam and the installed game/bridge launcher must be available. The default
runtime uses the existing bridge ports and requires other game processes to be
closed. Use an isolated runtime and port range to test concurrently (below).

```sh
python3 scripts/native_simulator.py start --level s_wizard_school_3_4
python3 scripts/native_simulator.py navigate Chocolate --repeat 5
python3 scripts/native_simulator.py reset
python3 scripts/native_simulator.py levels
python3 scripts/native_simulator.py load s_wizard_3_1
python3 scripts/native_simulator.py stop
```

`start` builds the current source and automatically starts the native process.
The default level is the Chocolate stair kitchen, `s_wizard_school_3_4`.
`reset` reloads that exact kitchen **in the same process**, including chefs,
items, orders, and scripted objects. It waits for the new scene and actual round;
old round state cannot satisfy the wait. Native loading/intro takes several
seconds. `load` selects a named two-player kitchen from the native session's
catalogue. `start --dlc ID` selects an installed cooperative DLC session; the
default is `-1` (base game). `all-levels.json` inventories the installed session
prefabs and their two-player scene variants (164 entries on the tested install).

`navigate` resets before each attempt, waits for reachability discovery to finish,
and makes exactly one normal `ActionClient.navigate_to` request. It accepts an
exact ingredient name or target label. A failed attempt is recorded, not retried
in place. The command exits 2 if any attempt fails. Each attempt's native action
trace and initial runtime state are written under `.local/native-player/runs-*`,
with a `report.json`. Use `--trace-dir PATH` to choose an output directory.
Each leg also reports a `motion` summary with neutral ground-recovery episode
count/duration, checked landing continuation and steering adjustments, first/last
native positions, maximum decision interval, and longest horizontal stationary
interval while walking (speed at most 0.1 units/second; arrival alignment excluded).
These measurements distinguish neutral commands from physical pauses; use the raw
trace for their positions, velocities, and native contact details.

For teleporter validation, require confirmed native transitions so an alternative
walking route cannot pass the test. `--then TARGET` adds a return leg in the same
round; each leg uses one ordinary navigation request, with no retries. The minimum
applies to every leg. Only the next attempt resets the kitchen.

```sh
python3 scripts/native_simulator.py load s_wizard_3_3
python3 scripts/native_simulator.py navigate utensil_frying_pan_01 --then Meat --min-teleports 2 --repeat 3
```

Reports include each leg's target, arrival checks and `teleportsCompleted`, plus
the attempt's total. An arrival with too few transitions fails as
`insufficient_teleports`. A failed later leg fails the entire attempt.
The default expected outcome is `arrived`. For an empty station, explicitly use
`--arrival positioned_no_action`; this still requires native positioning and the
requested number of transitions, while acknowledging there is no current pickup,
placement or use action at that station.

For timing-dependent failures:

```sh
python3 scripts/native_simulator.py navigate Chocolate --repeat 5 --fps 20
python3 scripts/native_simulator.py fps 60
python3 scripts/native_simulator.py status
```

FPS is a requested Update rate, not a guaranteed measured rate. The engine retains
its native fixed timestep, normal update ordering, physics solver, and the
navigator's production planning budget. Results can vary with scheduling and CPU
load. This is not a deterministic lockstep simulation or an arbitrary fast-forward
engine. Normal round timers and gameplay continue while it runs.

## Run other jobs

The usual clients and localhost harness connect to the same process:

```sh
python3 -m harness.server
python3 clients/bridge.py --record .local/native-kitchen.ndjson --frames 1
```

Use `ActionClient` in `clients/actions.py` for pickup, work, ingredient delivery,
and other supported actions. Re-query live destination IDs after a reset.
The CLI's `navigate` runner is a convenient stair regression; it does not restrict
what the existing action API can execute.

## Isolation and implementation

The simulator has its own BepInEx core/config/plugin copy and save directory under
`.local/native-player/`; it does not replace the normal installed bridge build.
For concurrent games, supply **both** a different `--runtime PATH` and an unused
`start --port-base N`. The three ports are snapshot `N`, control `N+1`, gameplay
`N+2`. Configuration, logs, saves, process ownership, and mailbox commands remain
inside that runtime. The default launch guard still rejects accidental concurrent
interactive/default-port launches. No command stops a process belonging to a
different runtime.

```sh
python3 scripts/native_simulator.py --runtime /tmp/portal-audit start --port-base 25712 --level s_wizard_3_3
python3 scripts/portal_audit.py --runtime /tmp/portal-audit --port-base 25712 --output /tmp/portal-results
python3 scripts/native_simulator.py --runtime /tmp/portal-audit stop
```

The audit loads every installed two-player scene in its corresponding DLC
session. It inventories scene portal components (including inactive ones),
selects ordinary `navigate_to` destinations whose planned routes include untested
entrances, and records the native transitions and final arrival. Timed portal
phases are observed without changing transforms or advancing the game clock.
A walking-only arrival cannot cover a portal; every observed entrance must be
followed by a successful arrival. Failures and uncovered entrances remain in the
report, with per-route NDJSON traces. Each result records the runtime's bridge
DLL hash. `--resume` skips recorded scenes; use a new output directory for a new
validation run. `--level NAME` (repeatable) and `--dlc ID` select subsets.

For the seasonal cannon island, use `--cannon-setup --level s_festivemashup_1_5`.
This fixture boards using the normal `activate` action, confirms Player 2 is
loaded in a ready cannon, then fires the native teammate launch trigger. It
waits for native flight and grounded control to return before testing the portal
route. It never writes positions or controls Player 1. The setup is recorded
separately and does **not** establish autonomous cannon pathfinding. A stale
cannon occupant reference after landing cannot authorize another launch. Story-only throne rooms are identified
separately. An inactive player portal is not silently counted as tested.
Additional explicitly selected fixtures:

- `--portal-phase-setup` advances each native phase prerequisite until dormant
  portals activate. It preserves native phase events, order transitions and
  animations; it does not prove recipe completion. Initial cutscenes use their
  native skip event in every simulator run.
- Carnival `s_Day_3_4`: use `--cannon-setup --cannon-aim-down --portal-index 0`
  for the left island. Add `--cannon-transit --portal-index 1` for the right.
  Aiming supplies native pilot input for one second and retains the game's limits.
- `--stage-portal-index N` explicitly places the stationary Player 2 at a checked
  supported entrance approach **before** measured navigation. The before/after
  pose is recorded as `explicit_supported_portal_initial_pose`; this is not a
  successful route from spawn. It is needed to isolate the portals in
  `s_dynamic_stage_03` from its currently rejected stair connection.
- `--portal-points` uses the production point navigator and normal control lease
  to reach the selected portal's supported exit. This is useful for platforms
  without reachable stations. Simulator-only IDs start with `d000`; ordinary
  interactive launches reject them. Actual native transitions still determine
  coverage; arriving on the same island without teleporting cannot cover an edge.

`probe-walk Ax Ay Az Bx By Bz` is a read-only mailbox diagnostic that writes
production standability/segment results to `probe.json`.

Files are private local runtime artifacts, not distributable game assets.

The opt-in runtime requires `SOUS_CHEF_NATIVE_SIM`, `-batchmode`, and `-nographics`.
Its BepInEx entrypoint is `BootFlow.Awake`, since the normal Application static
constructor does not reliably start plugins without graphics. It mutes and pauses
Unity audio before the game frontend loads. Status reports `graphicsDevice: Null`,
`audioPaused: true`, and `audioVolume: 0`.

Two scoped Harmony prefixes redirect native saves into the simulator directory
and bypass only the window-focus portion of native button gating. Player-control
suppression, dialogs, menus, ownership, pause checks, input leases, and Player 2
restrictions remain. Normal interactive launches do not enable these patches.
Scene animators use `AlwaysAnimate` in the native headless runtime so camera
culling cannot freeze timed kitchen geometry or intro animation events. This
changes animation visibility policy only; native animation clips, timers, and
physics still execute normally.
Capture coroutines yield a regular frame in simulator mode because rendered
`WaitForEndOfFrame` is unavailable in batchmode.

Startup engages the native keyboard profile, splits its local user into two
chefs, creates an unsaved cooperative Party session, selects its native scene
entry, and invokes the same kitchen-loading message used by the game. Reset uses
that loader again. No replacement colliders, synthetic chefs, custom gravity,
or alternate contact solver are used. The optional staged initial-pose fixture
is the sole test-only position write; measured navigation never writes positions.

Lifecycle commands use a local file mailbox, with request acknowledgements and
explicit errors. `status.json`, `player.log`, and `BepInEx/LogOutput.log` provide
startup diagnostics. A timeout is an error, not a successful load. `stop` waits
for the owned process to exit; normal process state checks reject stale PID files.

## Verification and known navigation failure

On 2026-11-02, the native Chocolate kitchen loaded automatically, and repeated
resets restored both chefs to their original positions without restarting the
process. Three single-request climbs at requested 60 FPS produced two arrivals
and one `no_safe_start`; three at requested 20 FPS arrived. Arrival included the
native Chocolate interaction being selected. This verifies the runtime and also
reproduces a remaining navigation defect; it does **not** establish that stairs
are fixed.

The failed run stopped near the lower stair edge at approximately
`[16.3075, -1.8356, 4.3698]`, after the current-body sweep rejected the native
`Wedge` while GroundCast still reported the previous flat-floor contact. The full
trace is in `.local/native-verified-60b/attempt-2.ndjson`. Root-cause analysis can
now use repeated native runs rather than another approximate-physics adjustment.

A later final-build smoke run also captured `not_grounded` on the stairs and a
`control_lost` after a measured 313 ms frame gap. These are reported as failures;
the simulator preserves the production lease and grounding checks. See
`.local/native-final-runs/`. A full native observation contained two chefs and
180 colliders with no adapter errors. Exact loading of `s_wizard_3_1` was also
verified in the same process.

## Chocolate stair round trips — 2026-11-02

The baseline below precedes the fixes described in the
[stair investigation](stair-investigation.md), which records the native causes,
implementation, iteration results, and remaining acceptance work.

The current source at `60bb718` was rebuilt and run in the native headless
`s_wizard_school_3_4` kitchen. **Stair traversal fails this validation.**
Player 2 spawned at `[21.8963, -1.8356, 2.9158]`; Chocolate's approach is on
the upper floor at `[14.4, 0, 9.6]`. Egg's approach is on the spawn floor at
`[19.2, -1.8356, 2.4]`, so the return request requires descending the stairs.

```sh
python3 scripts/native_simulator.py start --level s_wizard_school_3_4
python3 scripts/native_simulator.py navigate Chocolate --then Egg --repeat 5 --fps 60 --trace-dir .local/stair-roundtrip-60-20261102
python3 scripts/native_simulator.py navigate Chocolate --then Egg --repeat 5 --fps 20 --trace-dir .local/stair-roundtrip-20-20261102
python3 scripts/native_simulator.py stop
```

Every attempt reset to the native spawn. Each leg used one normal navigation
request, without manual steering or retries. The return leg ran immediately in
the same round only if Chocolate was reached. Failed climbs therefore did not
count as descent attempts. FPS values below are requested Update rates.

| Requested FPS | Climbs arriving at Chocolate | Descents arriving at Egg | Complete round trips |
| --- | --- | --- | --- |
| 60 | 1 / 5 | 0 / 1 | 0 / 5 |
| 20 | 1 / 5 | 0 / 1 | 0 / 5 |

| Attempt | 60 FPS climb / return | 20 FPS climb / return |
| --- | --- | --- |
| 1 | `next_step_unsafe` / not attempted | `not_grounded` / not attempted |
| 2 | `no_safe_start` / not attempted | `not_grounded` / not attempted |
| 3 | `arrived` / `not_grounded` | `arrived` / `not_grounded` |
| 4 | `next_step_unsafe` / not attempted | `not_grounded` / not attempted |
| 5 | `not_grounded` / not attempted | `not_grounded` / not attempted |

The native execution traces identify three distinct failure observations:

- At 60 FPS, attempt 1 rejected a `Step 3` support transition at
  `[15.0293, -0.4446, 6.3179]`; attempt 4 rejected a `Wedge` transition near
  the outside lower stair edge at `[16.3686, -1.3076, 4.9870]`. Both stopped
  with `next_step_unsafe` and `support_discontinuity`.
- Attempt 2 at 60 FPS stopped at `[16.0251, -1.6068, 5.0816]` with
  `no_safe_start`; all 25 start connectors were rejected by a current-body
  sweep against `Step`. This is a contact-check failure; the trace alone does
  not establish whether the native chef was physically snagged.
- Both descents lost native ground contact near the top of the staircase:
  `[15.2373, 0.1492, 6.3866]` at 60 FPS and
  `[15.4492, 0.0018, 6.2163]` at 20 FPS. They stopped with `not_grounded`
  after approximately 0.76 and 0.72 seconds, respectively.

Even the two successful climbs contained neutral `settling_ground` decisions:
eight updates across three episodes at 60 FPS, and three updates across two
episodes at 20 FPS. Both arrivals did satisfy native Chocolate selection,
positioning, zero relative speed, and pickup readiness. Arrival alone therefore
does not establish smooth stair movement. Neither batch reported a lease,
test-timeout, or navigation-timeout failure.

Local evidence consists of each directory's `report.json` and five
`attempt-*.ndjson` traces, plus `.local/stair-current-catalog.json`,
`.local/stair-roundtrip-20261102-analysis.json`, and
`.local/stair-roundtrip-20261102-status.json`. The runtime reported Unity
2017.4.8f1, `graphicsDevice: Null`, `physicsAutoSimulation: true`, and the
native 0.02-second fixed timestep. The simulator was stopped after testing.
