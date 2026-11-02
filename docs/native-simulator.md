# Native headless simulator

This runs the **installed Overcooked! 2 Unity 2017 player**, with its original
PhysX simulation, chef controllers, asset bundles, kitchen scripts, and local
server/client lifecycle. It is a silent background process with no rendering or
window. It does not require menu input or random theme selection.

The former approximate CPU simulator has been removed. Navigation unit tests
remain for isolated regressions; this native runtime is the simulator for
complete level traversal.

## Start and repeat

Steam and the installed game/bridge launcher must be available. Close any other
Overcooked process first; the simulator uses the existing bridge ports.

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
catalogue. The current catalogue contains 52 base-game entries. DLC session
selection is not implemented.

`navigate` resets before each attempt, waits for reachability discovery to finish,
and makes exactly one normal `ActionClient.navigate_to` request. It accepts an
exact ingredient name or target label. A failed attempt is recorded, not retried
in place. The command exits 2 if any attempt fails. Each attempt's native action
trace and initial runtime state are written under `.local/native-player/runs-*`,
with a `report.json`. Use `--trace-dir PATH` to choose an output directory.

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
`--runtime PATH` selects another directory, but concurrent games are not supported.
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
Capture coroutines yield a regular frame in simulator mode because rendered
`WaitForEndOfFrame` is unavailable in batchmode.

Startup engages the native keyboard profile, splits its local user into two
chefs, creates an unsaved cooperative Party session, selects its native scene
entry, and invokes the same kitchen-loading message used by the game. Reset uses
that loader again. No replacement colliders, synthetic chefs, custom gravity,
position teleportation, or alternate contact solver are used.

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
