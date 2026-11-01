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
host snapshots only once the round began.

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
