# Sous Chef — Overcooked! 2 state bridge

A local observation and Player 2 control bridge for couch co-op in the native macOS Steam build of
Overcooked! 2. The target is two separate local players, each controlling their
own chef simultaneously. Online multiplayer is outside the supported scope.
Bridge 0.5 adds [first-layer interaction actions](docs/actions.md): pickup,
placement, ingredient addition, work, activation, disposal, typed waits, and
targeted throws to stations or another local player. Each checks its observed
outcome; container-to-container transfers remain a higher-layer task.
Player 2 can be created with the game's Split Controller option and controlled
directly through logical inputs, independently of Player 1. See
[Player 2 control](docs/control.md) for the API and lifecycle rules.
Bridge 0.4 adds [autonomous walking actions](docs/navigation.md): list reachable
meaningful destinations, call `navigate_to(destination_id)`, or interrupt with
`stop()`. The plugin handles routing and steering; callers never send waypoints.
Run `python3 -m harness.server` for a [live clickable navigation harness](docs/harness.md)
at [localhost:8765](http://127.0.0.1:8765). It shows dispenser food names and
walking/direct distances, with a QR code for phone access; add `--tunnel` for an
internet link that lets the game stay focused on your Mac.
It captures active kitchens, including paused rounds. Outside a round it sends
status messages without a map, so consumers cannot mistake the level selector
for a kitchen.

The bridge combines scene graphs and collision geometry with 151 explicit
schemas for chefs, food, attachments, processing, orders, score, timers,
environmental forces and special mechanics. It preserves references and
server/client provenance. Unsupported observations are reported rather than
guessed. Coverage of every DLC mechanic has not been live-verified.

## Run

Requires macOS, Python 3.12+, Steam, and a local installation of Overcooked! 2.
Scripts download checksum-verified .NET and BepInEx releases into `.tools/`;
there are no Python packages or NuGet dependencies to install.

Quit the game, keep Steam running, and run from this directory:

```sh
python3 scripts/install.py
python3 scripts/launch.py
```

The installer places the loader and plugin beside the `.app`. It does not edit
original game files or Steam launch settings. Set `OC2_GAME` or use `--game` for
a different `.app` path. In another terminal:

```sh
python3 clients/bridge.py
```

Enter a kitchen normally. The stream is UTF-8 NDJSON on `127.0.0.1:24702`, at
up to five snapshots per second. The CLI prints summaries; the socket carries
complete snapshots and shared collision geometry assets.

```sh
mkdir -p .local
python3 clients/bridge.py --record .local/kitchen.ndjson --frames 100
python3 clients/validate_recording.py .local/kitchen.ndjson
```

`--frames` counts snapshots and inactive statuses. Recordings can be large
because each snapshot contains the complete observed scene.

## Python consumer

```python
from clients.bridge import BridgeClient
from clients.world import World

with BridgeClient() as bridge:
    for message in bridge.messages():
        if message['type'] == 'snapshot':
            world = World(message)
            for chef, item in world.held_items():
                print(chef['name'], 'holds', item['name'])
```

`BridgeClient.current()` returns no world after an inactive status, disconnect,
or stale receive time. Use that freshness guard when retaining state across
consumer updates. See [the protocol](docs/protocol.md) for asset and identity
lifetimes, clocks, authority, and skipped-frame semantics.

## Configure, check, remove

After first launch, edit `BepInEx/config/dev.souschef.observer.cfg` beside the
game while it is closed. `Stream.Port` selects the port; `Capture.Hz` is clamped
to 1–30. Start at 5 Hz: broad main-thread capture has measurable cost. Match a
custom port with the client's `--port` argument.

Observation capture follows connected readers. With no observation clients,
both captures sleep; Player 2 control/navigation still work. A full-stream-only
client (including the harness) no longer pays for unused 20 Hz gameplay samples.
Gameplay-only readers still need full scene reconciliation at the configured
full rate. Connecting resumes capture automatically with fresh state and assets.

```sh
python3 scripts/build.py             # artifacts/plugin/SousChef.Bridge.dll
python3 scripts/check.py             # build, core tests, field audit, client tests
python3 scripts/install.py --uninstall
```

Uninstall removes manifest-owned, unchanged files and preserves other mods,
modified files, generated configuration, and logs. Launching the `.app` normally
bypasses this loader unless you separately configure Steam to use it.

## Organization

| Directory | Responsibility |
| --- | --- |
| `src/Core/` | JSON, stable identities, bounded TCP transport, graph search and action state |
| `src/Plugin/` | Round gate, main-thread scene/geometry capture, lifecycle |
| `src/Plugin/Adapters/` | Explicit field schemas and bounded readers |
| `src/Plugin/Navigation/` | Live physics reachability, destination catalogue, autonomous walking |
| `clients/` | Python observation/control/action APIs, world index, recording validator |
| `harness/` | Local browser destination list, action status and Stop control |
| `scripts/` | Pinned toolchain, build, reversible install, launch, checks |
| `tests/` | Core/transport tests, installed-game field audit, client tests |

Read [architecture and coverage](docs/architecture.md) and the
[validation record](docs/validation.md) before relying on an untested mechanic.
The [original investigation](overcooked2-bridge-investigation.md) provides context.
The [decision-model and voice study](docs/ai-stack-research.md) compares hosted and
local options for a future voice-directed sous chef, with latency evaluation criteria.
Game assemblies are local build inputs and are never included in this repository.
The bridge implementation is original. The harness bundles Project Nayuki's
MIT-licensed QR encoder; see [its attribution](harness/vendor/README.md).

### Try Apple's speech recognition

On macOS 27+, with Xcode 27's Swift tools installed, run:

```sh
python3 scripts/test_apple_speech.py
```

Allow microphone access when prompted, speak, and press Ctrl+C to stop. Provisional
text updates in place; final text stays on screen. This uses Apple's on-device
`SpeechTranscriber`, saves no microphone audio, and installs language assets if
needed. The launcher caches its native executable in `.local/apple-speech-test/`.
Use `--locale en-CA` to change language, `--check` to check availability without
opening the microphone, or `--file recording.wav` to transcribe an existing file.

### Faster gameplay observations

Bridge 0.3.0 adds a compact gameplay stream on **127.0.0.1:24704**, targeting
**20 Hz** by default, while retaining the complete **5 Hz** map stream on 24702.
Set `[Gameplay] Hz` to 1–30 in the BepInEx config while the game is closed.
The fast stream includes fresh transforms, velocities, supported gameplay fields
and collider bounds; full geometry and comprehensive discovery remain in the
original stream. Rates depend on live game performance.

```sh
python3 clients/bridge.py --port 24704 --frames 100
python3 scripts/profile_streams.py --seconds 15
python3 scripts/profile_streams.py --seconds 15 --stream full
```

Use `GameplayWorld` from `clients/world.py` to index fast samples. See
[the protocol](docs/protocol.md#gameplay-stream-bridge-030) for identity,
freshness, discovery delay and recovery semantics.
The profiler includes average rendering FPS from Unity frame counters. This
average can hide stutters; compare capture p95/max as well, in the same kitchen
and with the same clients connected.
