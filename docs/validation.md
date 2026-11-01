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
