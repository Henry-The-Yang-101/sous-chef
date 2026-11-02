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

The first live follow-up used `s_sushi_1_3` (Arcade Couch). Player 2 started
at approximately `[13.4, 0, 9.8]` against the counter corner, and the planner found
six start connectors and 19 destinations. Navigation to the cucumber dispenser
completed in 0.676 seconds with one walking-to-aligning transition and 0.20
seconds of alignment; stance error was 0.0905 and facing error zero.

The longer rice-dispenser route then reproduced `unsafe_alignment_stance` after
3.31 seconds. Its arrival check projected the preceding full-speed walking
velocity toward the counter even on a non-slippery floor. The follow-up fix
scales retained locomotion by native surface slippiness while continuing to
check wind/platform drift and native settled velocity before arrival. Build/audit
checks still pass. Evidence: `.local/contact-navigation.ndjson` and
`.local/contact-before.json` / `.local/contact-after.json`.

After loading the arrival projection fix, a different Arcade Couch kitchen passed
both dispenser checks: orange in 0.927 seconds and flour in 4.201 seconds. Each
recorded exactly one walking → aligning → idle sequence, with alignment lasting
0.17–0.23 seconds, native target selection, zero facing error and pickup allowed.
The flour arrival settled at 0.221 units of stance error within the hysteresis band.
Neither route required a second command.

A deliberate 0.30-second Player 2 movement into the flour dispenser counter left
the chef resting at approximately `[6.018, 0, -9.4]`. The catalogue still exposed
23 destinations and ten valid start connectors. One navigate command from that
wall contact to the dried-fruit dispenser completed in 0.391 seconds, with one
alignment phase, 0.087-unit stance error, zero facing error and pickup allowed.
No pickup or dash buttons were sent during these follow-up checks. This verifies
the tested contact/arrival cases, not every wall shape, slippery surface or cause
of cancellation. Evidence: `.local/contact-final-navigation.ndjson`,
`.local/wall-contact.json`, and `.local/wall-contact-result.json`.

### Stairs and connected decks

The reported case was captured in `s_festivemashup_1_3`. Its upper deck is at
1.85 units and its two staircases have approximately 0.50-unit risers, with
`StepHeightMax` observed as 0.65. The old planner's footprint/midpoint checks
used 0.22/0.15-unit height tolerances and straight capsule sweeps through risers.
From the upper deck it exposed only ten upper-deck destinations. Evidence is
`.local/stairs-before.json`; native GroundCast inspection also confirmed its
58-degree slope limit.

The update uses native-height supported step transitions, resolved surface keys,
step-shaped clearance sweeps and horizontal waypoint completion with bounded
stepping lag. The build passes 768 C# assertions, 53 Python tests and the
151-type installed-assembly audit. New graph fixtures cover two staircases,
shortest ascent, descent via the alternate staircase, a mid-stair start with
both decks reachable, blocked staircases, distinct stacked-floor keys and rejected
unsupported deck drops. These graph tests do not emulate Unity physics; live
staircase traversal is recorded separately when verified.

### Interruptions beside holes and narrow connections

The reported interruption was captured in `s_wonderland_1_5` as `route_blocked`,
with `planning_in_progress` on the subsequently observed catalogue. Player 2 was
grounded when the snapshot was taken; this does not establish its pose at the
exact cancellation update. Evidence: `.local/hole-control-before.json` and
`.local/hole-before.json`.

Inspection found that flat graph edges checked only midpoint ground plus a body
sweep, while execution checked the full footprint along the segment. Near holes,
those checks can disagree even when the centre remains supported. Graph edges
now use the same sampled support and clearance kernel, with padded clearance
independent of the chef's position; dynamic flow is not baked into cached geometry.
Waypoint advancement now validates the direct connection instead of cutting a
corner solely because the chef is nearby. Optional lookahead into a hole beyond
a turn falls back to checking the actual next step, never unchecked movement.

Regression checks cover a missing footprint between supported endpoints/midpoint,
unsupported endpoints, invalid lengths, and retaining versus advancing a corner
waypoint. Full build/audit checks pass. The update is installed; live gameplay
verification is left to the user's requested manual test. It does not add jumping
or traversal over genuinely unsupported gaps. Cold searches may need more cache
warming because flat edges now perform the same stricter checks as execution.

### Capture overhead and unused streams

The 2026-11-01 FPS investigation measured the original plugin in an active
`s_wonderland_1_4` round. Both streams ran even with no observation readers.
Full capture median/p95/max was 33.720/63.489/79.803 ms; gameplay capture was
8.314/18.063/25.483 ms. A separate overlapping frame-counter sample averaged
108.29 rendering FPS. That average does not describe the visible periodic stalls.

The fix retains small records as key/value arrays, removes primitive-array
boxing during encoding, caches qualified reflection names and reuses reader
scratch storage. It suspends captures without subscribers and skips the fast
sampler for full-only consumers, including the harness. No observation fields,
configured sampling rates, navigation checks or control capabilities were removed.

After installing and relaunching, the user entered `s_sushi_1_3` and reported
the game was **noticeably smoother**. The kitchens differ, so these timings are
not a controlled before/after speedup or proof of a specific FPS gain:

| Updated build | Full capture median / p95 / max | Gameplay capture median / p95 / max | Average rendering FPS |
| --- | --- | --- | --- |
| Both streams | 18.275 / 25.157 / 104.858 ms | 5.060 / 9.450 / 29.447 ms | 148.2 |
| Full only | 23.886 / 25.193 / 26.189 ms | Suspended | 172.2 |

Both-stream sampling sustained approximately 5.00 / 19.85 Hz with no skipped
sequences. The larger maximum shows that capture stalls are not eliminated in
every case. Twelve subsequent full snapshots passed the recording validator:
1,738–1,739 objects, 144 colliders, resolved references/parents/geometry assets,
zero read errors and no unsupported values. A read-only navigation catalogue
request returned 19 destinations and a complete search without an observation
error. No test-issued movement was used for this investigation.

Closing and reopening the gameplay reader across 2.063 seconds advanced its
sequence by only two, rather than about 41 samples, and returned a fresh render
frame in the same epoch. The user's full-stream harness stayed connected.
Automated transport tests additionally cover all readers disconnecting while
idle, fresh state/assets on resubscription, and rejection of old in-flight frames.

`python3 scripts/check.py` passes 759 C# assertions, 53 Python tests and the
151-type installed-assembly audit. The installed DLL matches the checked build;
the loader log contains no capture, serialization or control failures from this
run. Local evidence: `.local/fps-before.json`, `.local/fps-before-clocks.json`,
`.local/fps-after-both.json`, `.local/fps-after-full.json`,
`.local/fps-after-validation.ndjson`, `.local/fps-after-catalog.json` and
`.local/fps-reconnect.json`. `profile_streams.py --stream full` measures just the
full subscription; its new `averageRenderFps` field uses Unity frame counters.

### Additional allocation reductions

A follow-up removes the full UTF-16 string copy previously used to append the
NDJSON newline. Both observation and control responses now put that delimiter
directly into the final UTF-8 buffer. Byte-equivalence tests cover Unicode,
escaping, empty input and a large payload. For the same 1 MiB ASCII payload,
the .NET 8 test measured framing allocations falling from 3,145,784 to
1,048,608 bytes. This measures framing only, not complete serialization or Unity
FPS; the original string and final byte buffer still exist.

Gameplay discovery now reuses its component-selection scratch list, while
copying each retained component array independently. Repeated references queue
an object only once per sample. All references are still emitted and the queue
membership resets each sample; no state is cached across samples by this change.

The follow-up passes 775 C# assertions, 53 Python tests and the 151-type adapter
audit against the installed game assemblies. Sampling rates, fields, navigation
checks and command behavior are unchanged. Additional live FPS improvement has
not been measured. Check log: `.local/fps-followup-check.log`.

### Navigator code-audit regressions

The code-audit follow-up addresses six gaps: native step eligibility, flow on
intermediate support samples, destination-relative settled motion, fallback from
an unreachable retained side, full slow-frame movement windows and search-radius
filtering after the chef moves. Navigation input now expires at the checked
motion deadline independently of its longer control-ownership lease.

`python3 scripts/check.py` passes the real plugin build, 800 core/API assertions
(including the 151-type installed-assembly audit), 56 navigator integration checks
and 53 Python tests. The added test project links the production physics/planning
and execution classes against small Unity/game doubles. Fixtures cover ledges,
stairs, ramps, changing conveyors, interior wind, moving search boundaries,
drifting/externally pushed chefs, shared platform translation and rotation,
retained-side fallback and 10-FPS overshoot. Input-lease tests separately verify
neutralization at the motion deadline and renewal after frame jitter.

These are code and analytical-geometry regressions. They do not emulate PhysX,
native step projection, friction or Unity update ordering. No game process was
controlled and no installed plugin was replaced during this follow-up; live level
traversal and low-FPS behavior remain unverified.

### First-layer interactions and targeted throwing — 2026-11-01

Bridge 0.5 implements pickup, placement, single-ingredient addition, work,
activation, disposal, typed waits and targeted throws to stations or other local
players. `transfer_contents` remains a higher-layer operation and is not exposed.
The Python API, CLI and browser action selector use the same native executor.

Validation builds the plugin against the installed assemblies and audits the
151 observation schemas plus the additional action fields, native processing
enum names and sticky-interaction cleanup bindings. The core/API suite passes
812 assertions; navigation passes 56 checks. The action project links the real
executor and planner against Unity/game doubles, including Unity's destroyed
object/null equality behavior. Its 117 checks cover exact pickup/placement
outcomes, consumed ingredients, last-plate stack destruction, changed targets,
washing scopes, desired-state activation, cancellation, bounded waits, processing
state, fixed-step damped ballistics, obstructions, generated release stances,
moving-player catch constraints and missed catches. Python passes 60 tests.

The browser selector was inspected with a local mocked kitchen, including
station work, nonwalking throw targets and player-pass options; no browser errors
were reported. The temporary preview server was stopped afterward.

No game was opened or controlled for this addition, as requested. No installed
plugin was replaced. Throw physics and native interaction behavior remain
unverified in a live kitchen; the analytical fixtures do not establish perfect
throws under arbitrary human movement or changing native collision geometry.
Check log: `.local/first-layer-final-check.log`.

### Logistics jobs and native wind-aware handoffs — 2026-11-01

Bridge 0.6 adds `move_item`, `fetch_ingredient`, `get_plate`, `get_utensil`,
`stage_item`, `return_utensil`, and `clear_station`. Jobs retain one lease across
native child pickup/delivery, bind concrete source identities, preserve unrelated
cargo, and report partial progress on blockage/cancellation. Ingredient and
utensil selection uses native definitions; plate fetching requires observed clean,
empty plates. Clear-station plate batches retain original plate identities.

Static inspection of `ServerAttachmentThrower.ThrowItem` and
`CalculateThrowVelocity` establishes the native launch setter path. The planner
calls that calculator and observes actual post-setter velocity through a scoped
native throw callback. Inspection of `ServerAttachmentWindReceiver.FixedUpdate`,
`OnWindVolumeExited`, `RigidbodyMotion.Movement`, `WindVolume` and `TimeManager`
establishes positional wind drift, layer filtering/time multipliers and the
exit-velocity addition; these are included in the fixed-step simulation and
inverse source-height/release-position search. Nearby handoffs prefer checked
counter placement or supported ground drops, including when the human has full
hands. Predicted and observed outcomes remain distinct.

`python3 scripts/check.py` passes the installed-profile build and audit,
815 core/API assertions, 56 navigation integration checks, 172 action checks
and 65 Python tests. New fixtures cover multi-step ownership/cancellation,
blocked destinations, source substitutions, clean/filled plates, utensil homes,
finite plate batches, native-calculator overrides, actual-launch observation,
crosswind compensation, vertical wind, elevated receiving surfaces, wind exit
velocity, layer time multipliers and nearby drop/hazard checks. Browser controls
were inspected with a local mocked kitchen and reported no JavaScript errors;
the preview tab/server were closed afterward.

No game was opened or controlled and no installed plugin was replaced. Native
Update/FixedUpdate/trigger timing, changing wind fields, future human movement
and collision behavior still require live validation before claiming perfect
throws. Check log: `.local/logistics-wind-final-check.log`.

### 2026-11-01: stair execution root-cause capture

The previously reported Chocolate arrival was manually assisted and is not a
successful autonomous stair validation. The opt-in trace (`e504810`) reproduced:

- `next_step_unsafe` at the outside edge of the first wedge. Captured geometry
  contains a body-blocked strip near x=16.42..16.46, z=4.4 between standable floor
  and wedge endpoints. The old coarse segment checker skipped this strip.
- `no_safe_start` when subsequent connectors swept from a body contacting the
  wedge; retrying from a settled pose could resume.
- `not_grounded` at the upper landing with foot y=0.3501 and upward velocity
  0.9946, after native ground distance reached 0.2919. GroundCast rejects contacts
  farther than 0.3 from the body, so the old controller cancelled an ordinary
  native stair hop immediately.

The new solid-box corner regression fails against `e504810` and passes with
support-transition refinement. All 92 navigation checks, 815 core assertions,
181 action checks, assembly audit, and 66 Python tests pass. Live validation of
this correction is pending installation; analytical results alone do not prove
execution. Conveyor routing has not been revalidated live in this kitchen.

The first live test of `293dfdf` passed the previously blocked entrance and retained
an airborne interval, but ended `route_blocked` on a later wedge. The trace shows
movement resumed with native ground distance 0.2384, before the chef settled onto
the tread. GroundCast's proximity flag is not resting contact. The follow-up
waits with neutral input until native distance falls within collision skin, and
adds a regression for this exact early-contact condition (93 navigation checks).
Single-request live completion is still pending this follow-up installation.

The next live run (`168ec7a`) exposed the remaining initial-contact failure at
[15.38606, -0.99343, 5.78242]. Independent solid-box replay finds a clear supported
pose at y=-0.91567, but all 25 initial sweeps hit the current wedge because the
moving native capsule temporarily penetrates it. GroundCast still described the
previous body position [15.45501, ..., 5.55534]. A new measured fixture reproduces
both the rejected starts and their recovery after native contact resolution.
Settling now uses current geometry and native `ComputePenetration`; it permits
neutral waiting only for a clear landing or a verified upward supporting-ground
contact, rejects other overlaps, and remains bounded. All 99 navigation checks
pass; another single-request live climb is required before declaring completion.

### 2026-11-02: closed-loop headless stair validation

Historical record: this approximate simulator was subsequently removed in favor
of the [native headless simulator](native-simulator.md). The results below describe
the earlier implementation, not a currently available test suite.

At the user's request, further game restarts and live attempts stopped. The last
live build (`be20fde`) still stopped on simultaneous tread/wedge contact; none of
its attempted climbs is counted as successful autonomous validation.

The new CPU motion harness reproduces a complete controller failure, rather than
only probing standable points. It compiles production navigation code and drives
a capsule through 69 recorded colliders with native-style ground projection,
gravity, fixed-step motion, input expiration, and stale/current ground ordering.
The pre-correction closed-loop run stopped `route_blocked` at frame 58. After
strict continuity bounds, multi-contact settling, and replanning displaced
connectors, all 12 scenarios pass: eight jittered 20/30/60/120 FPS climbs, captured
contact recovery, slow contact resolution, missing stairs, and retracting stairs.
Positive cases arrive from one request; negative cases stop issuing input.

The solver is an independent numerical model, not Unity PhysX. Native interaction
selection and the complete animated kitchen are not simulated. These latest
changes have not been revalidated in Unity PhysX; no game reopening is required
to run `python3 scripts/simulate_navigation.py`.

The final suite additionally varies whether native movement consumes the previous
frame's input before the controller and ground observer run. This exposed and
removed the overbroad proximity-distance settling rule from the earlier attempts.
It also separated native downward landing-probe range from the step-up height
limit. With these corrections, the final 12/12 cases pass, including delayed
20 FPS movement. The headless search disables the wall-clock budget to avoid CPU
load changing a simulated outcome; production retains its default 12 ms budget.

Final validation for code commit `879f74b`: the installed-assembly build/audit,
815 core assertions, 104 navigation checks, all 12 headless scenarios, 181 action
checks, and 66 Python tests pass. The game had been closed; the verified plugin
was installed for its next launch and its SHA-256 matches the built artifact.
The game was not reopened. Live execution of this final correction remains
unverified, as requested; earlier manually assisted arrivals are not counted.

### 2026-11-02: actual Unity headless kitchens

Added `scripts/native_simulator.py` and an opt-in native bootstrap. The installed
Unity 2017.4.8f1 player loads the exact `s_wizard_school_3_4` kitchen, creates two
local native chefs through keyboard splitting, and resets/reloads through the
game's own level messages. Rendering reports `Null`; audio is paused with volume
zero. The simulator uses isolated BepInEx configuration and a redirected save
directory, and retains native physics, level scripts, and production navigation
budgets/leases. Normal installed bridge files are not replaced by its launch.

One process completed ten successive named loads/resets and also loaded
`s_wizard_3_1`. The 52-entry base-game catalogue comes from the native two-player
scene directory. A full headless snapshot contained two chefs and 180 colliders,
with zero coverage errors. The CLI waits for the new scene generation and actual
round, then waits for complete action discovery before issuing a single request.

Native Chocolate climbs at requested 60 FPS produced two arrivals and one
`no_safe_start` in `.local/native-verified-60b/`; three at requested 20 FPS arrived
in `.local/native-verified-20/`. Final-build smoke traces additionally captured
`not_grounded` and `control_lost` (313 ms frame gap) in `.local/native-final-runs/`.
The native interaction was selected on successful arrivals. The failed 60 FPS
trace rejects a Wedge at the lower stair edge while cached ground still describes
the prior flat-floor pose. These results prove that the approximate suite's
12/12 success did not cover every native failure. **Stair navigation remains
unresolved.** Failures are retained and exit nonzero, with no retries or manual
assistance hidden in a successful result.

The simulator itself has verified startup, same-process reset, named kitchen
switching, silent/null graphics state, full observation, action execution, invalid
level rejection, and graceful shutdown. Its native runner is opt-in; ordinary
`scripts/check.py` uses deterministic tests and does not launch the game.

Validation for implementation commit `182fba1`: the real plugin builds and the
151-type installed-assembly audit passes; 815 core assertions, 104 navigation
checks, 12 approximate simulation scenarios, 181 action checks, and 74 Python
tests pass. The eight new lifecycle tests cover reset generations, stale/dead
processes, command acknowledgements/rejection, PID reuse, startup cleanup,
pending-command preservation, and single-request navigation after discovery.
The final native process was shut down through the simulator CLI and no
Overcooked2 process remained. No normal-game reinstall or push was performed.

### 2026-11-02: remove the approximate simulator

Removed the CPU simulator script, project, motion/contact implementation, its
exclusive full-route fixture, and its invocation from `scripts/check.py`. Removed
the simulator-only planning-budget override and GroundCast stub mode; the native
production planning budget remains 12 ms. Shared geometry fixtures and navigation
unit tests remain. Active documentation now points to the native simulator.
Historical validation records above are preserved as evidence of earlier results.

After removal, `python3 scripts/check.py` passes the real plugin build, 151-type
API audit, 815 core assertions, 104 navigation checks, 181 action checks, and 74
Python tests. No game process was launched for this cleanup.

### 2026-11-02: native teleporter pathfinding and execution

Navigation now models directed native player portals, including receiver animation
endpoints, activation cones, supported entry/exit connectors, cooldown/occupancy
waits, native disabled-control phases, multiple hops and different deck heights.
Walking shortcuts cannot cross a transport edge. Completed transport consumes that
edge before replanning, and a budget-limited search can retain the checked route
tail. Connector geometry is cached with live flow checks and geometry invalidation.

Validation used the actual headless Unity kitchen `s_wizard_3_3`, with the production
12 ms search budget and ordinary action requests. Each attempt reset once, walked
to `utensil_frying_pan_01` through two portals, then returned to `Meat` through two
more portals without resetting. Every leg required `--min-teleports 2`, native
selection, actual arrival pose and settled speed. There were no in-place retries,
manual movement, synthetic physics or direct teleport calls.

| Requested FPS | Successful round trips | Successful navigation legs | Confirmed native transports |
| --- | ---: | ---: | ---: |
| 20 | 3 / 3 | 6 / 6 | 12 |
| 60 | 3 / 3 | 6 / 6 | 12 |

Final code: `264c69d`. Reports and full native traces are in
`.local/portal-verified-20/` and `.local/portal-verified-60/`. The final build also
passes `python3 scripts/check.py`: installed-game build/API audit, 815 core
assertions, 159 navigation checks, 181 action checks and 76 Python tests.
The subsequent runner outcome regression also passes; the full Python suite now
contains 77 passing tests.
The runner now records individual legs and completed native transports, and fails
an ordinary walking-only arrival when portal traversal is required.

Earlier runs exposed the solid frame behind the trigger centre, missed route-tail
consumption, repeated connector probe cost, delayed lateral movement at portal
turns, flat-mesh sphere-contact seams and late counter braking. Their failures
remain in `.local/portal-native-*` and `.local/portal-final-20/`; they were not
counted as successful validation. The final implementation stages and brakes
before entering, checks flat seams with an independent same-collider ray, and
starts neutral arrival braking before the preceding native movement integrates.
None of these predictions waive actual arrival or collision checks.

A second kitchen, `s_wizard_5_5`, completed one native transition to
`workstation_plate_return`, with native selection, 0.10-unit stance error and zero
relative speed. The empty station correctly completed as `positioned_no_action`.
The first strict `arrived` expectation therefore failed; the explicit
`--arrival positioned_no_action --min-teleports 1` regression passed after a fresh
reset. Its report is in `.local/portal-verified-second-kitchen/`. A supplementary
Meat pickup followed by a two-portal frying-pan approach also positioned correctly
without a currently available native action; its strict arrival report remains
in `.local/portal-carry-report.json` and is not counted in the matrix above.

The final headless process was shut down, and the tested source was built and
installed into the normal game's owned bridge installation. No game was relaunched
and no commits were pushed. These runs cover two base-game kitchens, including
both directions through four portals in the multi-hop kitchen. They do not
establish every kitchen or DLC works, nor resolve the separately recorded
Chocolate stair failures.


## FPS follow-up — 2026-11-02

The earlier demand-driven streaming and serialization optimizations remain in
place. With the usual harness connected, the new investigation measured
`MovingPlatform4` at 5.00 full snapshots/s: median/p95/max capture time
31.312/39.084/39.350 ms and approximately 4.16 MB per snapshot. Average render
FPS was 122.72; this average conceals individual capture stalls.

The first follow-up (`65c89cb`) packs transform values into one detached buffer
while preserving their exact JSON layout, reuses planner-local seed/aim
trajectory buffers, scans other carriers once per inventory observation, and
avoids calculating an already validated throw route cost twice. Regression
checks compare exact JSON and every trajectory position/time/velocity; changing
cargo ownership is re-observed on the next inventory refresh. The .NET 8
allocation checks measured 1,000 transform records at 496,000 → 200,000 bytes
and 100 warmed flight simulations at 402,400 → 0 bytes. These are allocation
measurements, not Mono rendering benchmarks.

The first updated live run was a different kitchen, `s_mine_5_3`: 5.00 full
snapshots/s, median/p95/max capture time 14.776/20.543/21.006 ms, approximately
1.89 MB per snapshot, and average rendering FPS 69.38. The different scene
prevents interpreting this as a before/after speedup. Twelve snapshots passed
the reference/geometry validator with 1,205–1,206 objects, 123 colliders, zero
read errors and no unsupported values.

That run exposed a second source of overhead: idle catalogue warming repeatedly
restarted incomplete searches between harness polls. Four read-only catalogue
samples showed 14.069–15.923 ms per rebuild, and catalogue revisions advanced
16–18 times between requests about 0.4 seconds apart. The follow-up `b59bab0`
spaces speculative idle warm passes at least 100 ms after the last refresh.
Explicit requests still refresh immediately; active navigation and all safety
checks are unchanged. Initially incomplete catalogues may finish more gradually.

The complete check passes 830 core assertions, 210 navigator integration checks,
265 action checks, 93 Python tests and the 151-type installed-assembly audit.
Analytical physics tests do not substitute for native-game validation. Both
changes were locally committed and installed through the owned installer; no
push was performed. Evidence is in `.local/fps-nov2-before-active.json`,
`.local/fps-nov2-after.json`, `.local/fps-nov2-catalog-after.json`,
`.local/fps-nov2-validation.ndjson` and `.local/fps-nov2-final-check.log`.


After the final install, `s_mine_2_4` sustained 4.998 full snapshots/s with no
skipped sequences; capture median/p95/max was 26.375/28.128/29.409 ms and average
rendering FPS was 149.69. This is another different scene, so the FPS numbers
must not be presented as a controlled speedup. Non-replanning status polling
measured 8.22 catalogue rebuilds/s with the harness connected, confirming that
idle warming no longer runs at render cadence. The sampled catalogue was still
`planning_in_progress`; this fix does not claim to solve every incomplete search
or certify all destinations. No navigation observation error was reported.
The installed DLL matches the checked build, and no capture/serialization/control
failures appeared in the loader log. Final evidence:
`.local/fps-nov2-final-profile.json` and `.local/fps-nov2-final-cadence.json`.


## Continuing catalogue searches — 2026-11-02

`242cf9f` retains Dijkstra frontier/settled work between idle updates when exact
continuity checks pass. Initial and continued idle searches target 3 ms of graph
work per slice. Geometry and starting-state changes invalidate progress; support
speeds and wind values are re-read before reuse. Arbitrary moving-surface rules
and portal readiness keep the conservative fresh-search fallback. Active
navigation and sampled-destination certification remain unchanged. These are
search budgets, not bounds on setup, matching, native queries or total frame time.

Tests compare sliced and uninterrupted shortest paths, frontier certification,
node limits and repeated continuation after completion. A 100-node chain finishes
in twenty five-node slices with exactly 100 expansions; restarting would repeatedly
visit only the first five nodes. Production-physics tests cover geometry additions,
movement/removal, chef pose/speed/step changes, surface mode/speed changes, wind
changes and moving-surface fallback. Stable progress resumes on the next render
frame, and a catalogue request cannot duplicate a background slice in that frame.

`dfa1a6f` ends throw shooting iterations at an exact direction/correction fixed
point, keeping full final flight and native/collision validation. Tests verify
that calm aiming converges before eight iterations and still rejects a native
catch veto. Existing height, wind, release uncertainty and arrival tests pass.

The first live install observed Player 2 holding Pasta in `MovingPlatform2`.
The catalogue was complete in 47 of 51 status samples; four samples were
invalidated on focus loss. No search reuse was observed in this dynamic kitchen,
so this run does not establish a native static-kitchen completion speedup.
Full capture sustained 5.004 Hz; median/p95/max capture time was
33.372/35.081/89.445 ms, with average rendering FPS 96.96. There was no controlled
same-scene before/after measurement. Evidence:
`.local/progressive-live-status.json` and `.local/progressive-live-profile.json`.

That live run exposed browser focus invalidating the catalogue despite permitting
throw previews. `1a51d56` preserves and warms the read-only catalogue under the
same observation gates while still revoking input ownership and active actions.
Its warming entry point explicitly refuses to tick an input-owning action.
Search diagnostics now expose generations, continuation counts and failure reasons.
The final build passes 840 core assertions, 239 navigator integration checks,
267 action checks, 93 Python tests and the 151-type installed-assembly audit.
Check log: `.local/progressive-search-final-check.log`.

## Separate worker process — 2026-11-02

Bridge 0.7.0 adds a persistent .NET worker for detached JSON/UTF-8 encoding,
certified stable incomplete walking searches and calm throw seed batches. Native
physics, live predicates, active execution and input ownership remain in Unity.
The [worker report](worker.md) records the process-boundary tests, native
catalogue/held-item/throw checks, deliberate worker-loss fallback and performance
measurement limits. No on-screen FPS improvement is claimed from Null-graphics
simulator measurements.

### First on-screen worker check and standing-height follow-up

The user's Mine 4-6 run (`s_mine_4_6`) with Player 2 holding Tortilla showed no
noticeable subjective improvement; stutter remained, particularly at round start.
The 18-second observer sample averaged 123.21 render frames/s and 4.994 full
snapshots/s, with capture median/p95/max 19.956/26.615/27.947 ms, zero skipped
sequences and no read errors. Only 16 of 86 harness observations were focused,
so this is not a continuous foreground benchmark. The worker stayed healthy,
encoded 159 additional messages and completed 181 additional calm seed batches.
Walking catalogue completion was observed in 71/86 samples; throw preview
completion in 16/86. Walking searches used the native fallback throughout because
starting-state or geometry changes invalidated their continuity.

A separate stationary sample held horizontal position constant while chef Y
varied between approximately -1.97e-6 and -2.68e-6. It advanced from search
generation 914 to 952 across fifty samples. Median planning cost was 14.166 ms;
median full capture remained 20.439 ms. The window was unfocused. Evidence:
`.local/worker-live-mine.json`, `.local/worker-live-continuity-probe.json` and
`.local/worker-live-stationary.json`. These findings do not establish that every
source of stutter belongs to the plugin or that the worker removed the remaining
native capture/planning cost.

Bridge 0.7.1 allows vertical-only start changes to reuse a tree only when all
existing geometry, actor, cargo, dimension, speed, surface, wind and query proofs
still hold, cached neighbour range decisions are unchanged, and fresh native
starting connectors reproduce exactly the same ordered node identities and
floating-point costs. Horizontal movement still invalidates. Moving-surface start
changes retain strict invalidation. Target approaches and action execution remain
freshly checked. No positional tolerance or cost rounding was introduced.

Throw preview preparation also shares wind-volume discovery within a synchronous
catalogue pass. Flags, collider geometry, velocity, item filtering and native
receiver predicates are still read afresh. Discovery is discarded at the end of
the pass, including on exceptions; work outside that pass performs its normal
fresh discovery. Items without wind-receiver components avoid the shared scan.

The follow-up passes 2,840 core assertions, 308 navigator integration checks,
274 action checks, 98 Python tests and the 151-type installed-assembly audit.
Tests include exact connector/cost equivalence under vertical jitter, strict
rejection of horizontal/meaningful vertical movement and fresh collision vetoes,
and one wind discovery per pass with fresh discovery on subsequent/outside passes.
A native Mine simulator run completed and reused its catalogue, but did not
reproduce the persistent held-item height fluctuation; on-screen benefit of this
follow-up remains to be verified after installation. Check log:
`.local/standing-start-check.log`. Native general-catalogue evidence:
`.local/standing-native-results.json`.
