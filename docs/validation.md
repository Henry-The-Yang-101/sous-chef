# Validation record

Validated locally on 2026-10-31 / 2026-11-01 UTC using the native Steam macOS
Overcooked! 2 build on Apple Silicon. Game runtime: Unity 2017.4.8f1, x86_64,
CLR 2.0.50727.1433. Loader: BepInEx 5.4.23.6. Bridge: 0.1.0.

`Assembly-CSharp.dll` SHA-256:

```text
8d548c632f48b07883e4e9861ddb834588f6edca9ff56920316daf5e33647159
```

## Automated checks

`python3 scripts/check.py` passes:

- The real plugin compiles with warnings as errors against the installed game's
  .NET 3.5/Unity assemblies, without modern .NET runtime references.
- 151 explicit schemas resolve their declared types and fields. Custom FastList
  backing-array/live-count members are also checked.
- 458 assertions pass, including those field checks and tests of JSON escaping,
  non-finite numbers, rejecting opaque objects, identity reuse/retirement/epochs,
  real TCP greetings, gaps, reconnects, asset ordering, status invalidation,
  disappearing/reappearing assets, and bounded nonblocking publication.
- Six Python tests cover graph/held-item references, dangling references, parent
  cycles, inactive/disconnected/stale state, sequence regression and missing assets.

These checks do not emulate Unity physics or execute every gameplay adapter in
its corresponding level. They are complemented by the following real-game run.

## Live kitchen

Launched through the installed BepInEx wrapper with Steam running. The loader
reported one plugin, and the client received the expected fingerprint, session
and protocol greeting. Main-menu, local arcade selection and loading produced
inactive status without map data. Entering Arcade Couch / Sushi City produced
host snapshots only once the round began. This run used one local player with
two switchable chefs, not two independently controlled local players. It verifies
observation of two chef entities, but does not validate the intended two-player
couch co-op setup. Movement and pickup were performed through UI test inputs,
not through a bridge command or virtual controller.

The scene was `s_sushi_1_1`, alongside `InGameMenu`. Local recordings (ignored by
Git because they contain game geometry) are `.local/baseline.ndjson` and
`.local/pickup.ndjson`.

| Check | Observation |
| --- | --- |
| Baseline capture | 27 snapshots, 1,456–1,459 scene objects, 128 colliders, two chefs |
| Interaction capture | 60 snapshots, 1,456–1,460 objects, 128 colliders |
| Geometry | Readable collision-mesh asset with vertices and triangles |
| Graph | All recorded object/component references and parents resolved; no hierarchy cycles or missing geometry assets |
| Reads | No missing adapter fields or read errors in these recordings |
| Orders | Live fish/prawn sushi recipes, remaining-order times and score state present |
| Movement | Chef transform changed after movement/dash input |
| Carrying | Separate live probe confirmed a chef's authoritative carrier referenced the picked-up plate entity |
| Pause | Snapshots continued while the actual pause menu was open; Unity timeScale remained 1, motivating the explicit game pause-layer reader |
| Performance | Baseline median 21.539 ms, cold maximum 102.442 ms; interaction median 19.2925 ms, maximum 23.516 ms |

Times measure main-thread extraction, not worker serialization, end-to-end
latency, or an independently measured gameplay frame-rate change. The cold
capture includes initialization. Full scene breadth has real overhead; keep
5 Hz until the target level is profiled.

## Corrections from live testing

The first recordings exposed three unsupported custom FastList value types used
for plate return and limited-quantity supply. The final reader now reads the
live portion of that collection, and the installed-assembly audit verifies its
backing fields. Other refinements add ingredient definitions, client workstation
state, explicit pause/timer suppression, terrain capture, bounded asset caches,
and asset resending after disappearance or status recovery.

The final build was installed and relaunched successfully; a new session and
menu-only status stream were verified (`.local/menu-final.ndjson`). A second
kitchen run could not be completed because native UI automation stopped reliably
navigating the game menu and returned `noWindowsAvailable` for clicks. Consequently
those final gameplay-reader additions are build/audit-verified, not independently
replayed in a second live kitchen. The earlier recordings predate the explicit
`paused` field; their validator's `pausedSnapshots: 0` is not evidence that the
pause menu was absent.

## Remaining validation scope

Online multiplayer is outside the supported scope. Within couch co-op,
scene-to-scene round transitions, delivery/plate-return
progression, food-processing completion, fire, moving platforms, wind, portals,
cannons, piloting, Horde, DLC and terrain still need dedicated live scenarios.
No broad runtime coverage is claimed from member existence alone. The core
stream tests exercise simulated epoch changes and reconnects, not actual level
restarts. Per-collider collision-ignore pairs are explicitly unavailable in this
Unity API; unreadable meshes and bounded/truncated values remain explicit gaps.

BepInEx logged a warning about the missing `Application.isBatchMode` property
while probing this old Unity version. Startup still completed and the observer
worked. No bridge capture exceptions appeared in the tested run. The original
game assembly fingerprint remained unchanged. The test game was closed normally;
the side-by-side loader/plugin remain installed for the documented launcher.


## Player 2 direct control — version 0.2

The user manually created two simultaneous local players through Split Controller
and entered `MovingPlatform4`. The native assignments were Player.One and
Player.Two; the control endpoint identified P2 as pad `One`, side `Right`.
This supersedes the earlier single-player-only observation scope above.

Verified through direct bridge commands, without synthetic gameplay keystrokes:

- Movement: P2 changed from `[15.1099997, 0, 3.6650000]` to
  `[15.1099997, 0, 2.2000000]`, a displacement of about 1.465 units. P1 remained
  exactly at `[11.3999987, 0, 8.1350002]` in both snapshots.
- Exclusive ownership: a second connection's claim failed with `already_claimed`.
- Expiry: waiting beyond the last input's TTL revoked the claim; an ensuing
  release received `not_owner`, as expected for an already-expired lease.
- Pickup/drop: after aligning P2 with the plate counter, the authoritative held
  relationship changed from empty to `Plate 16 (2)` and back to empty on two
  distinct pickup presses. P1's position and empty hands remained unchanged.
- Explicit release restored an unclaimed status with `lastRelease: released`.
- Abrupt socket shutdown without a release command cleared ownership with
  `lastRelease: disconnected`. Subsequent observation showed zero horizontal
  velocity for P2 and unchanged P1 position.
- Requests were rejected when native input was suppressed, when paused, and
  after the round ended. Active-claim revocation at the precise pause transition
  was not captured in this live probe; lease revocation is also unit-tested.
- The new level's observed fields reported no field-read or component errors.

Local evidence is in `.local/control-probe-result.txt`,
`.local/control-actions-result.txt`, and `.local/control-pickup-result.txt`.
The first pickup attempt faced an empty counter and correctly picked up nothing;
we aligned with the actual plate before recording the successful relationship.

Automated validation now includes 489 C# assertions and 11 Python tests. New
checks cover finite/ranged inputs, diagonal normalization, strict command grammar,
exclusive leases, expiry, disconnects, sequence regression, main-thread control
request dispatch over a real TCP connection, and Python wire encoding/cleanup.
A final small hardening change seeds the native button edge tracker with neutral
at construction so a same-update claim/input cannot swallow its first press.

Still unverified: actual concurrent human P1 movement during bridge P2 movement,
completed chopping/washing/throwing and dash behavior, reassignment during a claim,
and special piloting/cannon control schemes. P1 isolation was verified with P1
idle; the implementation never replaces P1's input objects. Physical movement
already initiated (momentum/dash) is not forcibly cancelled when input is released.

## 0.3.0 dual-rate implementation

Built against the installed Unity/Mono game assemblies; 691 C# assertions and
13 Python tests passed. Added cadence tests across 60 Hz frame quantization and
long stalls, plus gameplay client tests for bounds-only records, gaps, epoch
replacement, status invalidation, stale reads and disconnects. Original transport,
identity, adapter audit and control tests continue to pass.

20 Hz gameplay / 5 Hz full are configured targets, not new live measurements.
The earlier 19–22 ms full-capture results above do not measure this new fast path.
Run `python3 scripts/profile_streams.py --seconds 15` in an active kitchen to
measure both streams concurrently. It reports capture/receive rates, extraction
median/p95/max, p95 intervals, skipped sequences, discovery age and approximate
JSON payload sizes. It does not measure rendering FPS or absolute end-to-end
latency. Live moving-platform, spawn/despawn and full/fast overlap comparison
remain necessary to validate accuracy and frame impact in representative kitchens.

## Player 2 navigation — version 0.4

Final live checks on 2026-11-01 used **Arcade → Couch**, with two simultaneous
local players joined through Split Controller. Only Player 2 received gameplay
commands. Its native assignment was pad `One`, side `Right`. The successful
scenario was `s_wizard_3_2`; this scene name does not imply Story mode.

| Check | Observation |
| --- | --- |
| Available destinations | Eight reachable significant objects from 12 candidates; one complete 388-node search; stable, distinct object IDs |
| Autonomous arrival | One `navigate_to` call chose the route to an ingredient crate 8.49 sampled route units away; observed `arrived` after about 1.43 seconds |
| Correct interaction | Native pickup, placement and grid selection all referenced the requested crate's Unity instance ID, 495866 |
| P2 movement | `[8.14629, 0, -9.33419]` → `[15.16718, approximately 0, -8.82981]`; settled horizontal velocity zero |
| P1 isolation | P1 remained exactly at `[15.866291046142578, 0, -4.9441938400268555]` across arrival, stop and disconnect observations |
| Explicit interruption | A second connection stopped a running navigation action; reply reported idle and unclaimed; settled P2 velocity was zero |
| Disconnect | Abrupt socket shutdown during navigation produced idle, `control_lost`, unclaimed, and `lastRelease: disconnected`; settled P2 velocity was zero |
| Invalid destination | An ID absent from the catalogue was rejected with `destination_unavailable` |
| Observation health | Full snapshot reported zero field errors; no bridge exceptions appeared in the loader log |

The stop reply took 4.21 ms in this one observation. This is command-response
latency, not a guarantee about all future calls or immediate removal of physical
momentum. Status polling during navigation was observational; it did not send
steering or renew the action's lease. Navigation sent no dash, pickup or use input.

The cold first catalogue refresh measured 59.55 ms and explicitly returned
`planning_budget_exceeded`, then became available after bounded cache warming.
Nineteen distinct successful refreshes observed during the checks measured
4.994–12.686 ms, median 5.946 ms. These measure complete planner refreshes in this
one kitchen, not rendering FPS, the state stream's sampling rate, or a hard
12 ms bound. The search budget excludes some setup/matching overhead.

The live investigation corrected the active-controls type check, full-graph
invalidation caused by scale rounding and distant kill-plane meshes, native
counter-grid approach selection, and final-approach flow compensation. An earlier
Arcade Balloon kitchen also exposed a false collision with a ceiling at the
chef's native height; the probe capsule now insets both vertical contact ends.
That exact Balloon scenario has not been replayed after the correction.

`python3 scripts/check.py` passes with 717 C# assertions, 18 Python tests and
the 151-type installed-assembly audit. Navigation tests cover shortest paths,
changing graph connectivity, search budgets, directed flow, action cancellation,
typed commands and bounded large-catalogue replies. They do not emulate Unity.

Local evidence, ignored by Git, is `.local/navigation-arcade-checks.ndjson` and
`.local/navigation-arcade-verified-snapshot.json`. The checked plugin is installed.
Moving/rotating barriers closing during a walk, wind, floor conveyors, ice,
moving platforms, moving people and all special traversal still require dedicated
Arcade Couch scenarios. The live result establishes basic autonomous navigation,
native target selection and interruption, not complete coverage of those mechanics.

### Additional Arcade Couch levels

`s_lunar_1_3` exposed repeated `planning_budget_exceeded` failures before any
movement. Its nearby kill-plane mesh caused full-map invalidation every 0.2
seconds. Readable mesh vertices/triangles are now compared before invalidating
probes; unchanged meshes retain their cache. Build/audit checks still pass.

After installing that correction, the next user-selected round was `s_mine_2_4`.
It exposed 17 destinations with a complete 587-node search. Explicit stop and
disconnect cancellation passed again, with P1 unchanged and P2 settling to zero
horizontal velocity. However, longer navigation still cancelled on intermittent
planning-budget overruns. A later request to approach a pot also started but
cancelled for that reason; one retry was refused with `input_suppressed`.
Arrival was **not** verified in this level. The original Lunar failure scenario
has not yet been replayed after the mesh correction. Planner timing stability
remains a known issue, rather than a passing cross-level navigation result.

Evidence: `.local/navigation-arcade-second-checks.ndjson`,
`.local/navigation-lunar-snapshot.json`, and `.local/navigation-lunar-retry.ndjson`
(the last file records the Mine round despite its original retry filename).

### Live harness, usable arrival and less conservative catalogues

The browser harness was exercised in Arcade Couch on 2026-11-01 with Split
Controller Player 2. Clicking a live destination started native navigation, and
the browser Stop button interrupted an active action. The early Mine checks
reported arrival using native target selection alone. User observation showed
that this could still leave the chef short of a useful position or facing the
wrong way, so those results do **not** establish action-ready arrival.

Arrival now requires the checked working stance, facing, settled motion and stable
native selection. A continuous navigation-then-pickup check in `s_balloon_1_5`
verified the result without a manual movement gap:

| Check | Observation |
| --- | --- |
| Requested target | `utensil_pot_01 (1)`, destination `d16`, Unity instance 547518 |
| Planned walk | 1.770 world units to the approach stance |
| Arrival | `idle`, reason `arrived`; native selection matched; pickup allowed |
| Position and facing | 0.1184 units from the stance; 0.028 degrees from the target |
| Actual action | A separate pickup press immediately after arrival put that exact pot in Player 2's authoritative held-item relationship |
| Sampled planner refresh | 9.842 ms total, 7.458 ms search, 367 nodes |

Navigation itself still sends no pickup or work input. The separate attempt to
return the pot exceeded the raw input lease and was rejected with `not_owner`;
replacement was not verified before the round ended. Only Player 2 received
gameplay commands from this test; concurrent Player 1 position was not held fixed
or used as an isolation assertion.

Local chefs and their carried descendants are now ignored as route obstacles.
Non-player solid colliders and hazards remain checked. Budget-limited searches
publish destinations whose shortest routes are already certified instead of
clearing the whole catalogue. Automated tests cover a blocked direct branch with
an available alternate route and partial-search certification. The exact reported
fire-blockage scenario and a pedestrian kitchen have **not** been replayed live.

The current round initially reported `no_safe_start` while Player 2 rested against
a counter; routes became available later without a test-issued movement nudge.
The native-radius contact escape reduces over-conservatism, but this observation
does not prove every legal wall-contact pose can seed a route. Discovery remains
bounded to 60 world units, walking cells to 58 units, and narrow or unusual
geometry may still be omitted. An empty catalogue is not proof that the entire
visible kitchen is physically unreachable.

The final `python3 scripts/check.py` run passes 730 C# assertions, 42 Python tests
and the 151-type installed-assembly audit, including the concurrent harness
sharing/metadata additions. Local evidence is `.local/final-continuous-result.json`,
`.local/final-continuous-route.json`, `.local/final-continuous-pickup.json` and
`.local/final-start-failure.json`. The full pickup snapshot records the authoritative
held-item relationship; the result file records the target identity and arrival
metrics together.

### Wall contact and final-approach stability follow-up

The next revision replaces the growing start capsule with consistent native
contact clearance and expands start discovery to a 5×5 neighbourhood. It also
retains a valid final stance near the target and adds positional hysteresis
(enter settling at 0.20 units, leave above 0.28). Native selection, facing and
velocity checks still gate arrival. A partial replan can retain an active route
only after rechecking the entire remaining route and its target stance.

Build/audit validation passes 738 C# assertions and 42 Python tests. New regression
checks cover braking drift without walking reversals, leaving and re-entering the
settling band, and resetting it between actions. Unity wall/corner contacts and
physical settling still require live testing; these unit checks do not emulate
PhysX or establish that all reported cancellations are resolved.
