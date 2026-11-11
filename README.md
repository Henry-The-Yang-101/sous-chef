# Sous Chef — Overcooked! 2 state bridge

A local observation and Player 2 control bridge for couch co-op in the native macOS Steam build of
Overcooked! 2. The target is two separate local players, each controlling their
own chef simultaneously. Online multiplayer is outside the supported scope.

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

Observation capture follows connected readers. The harness discovers its compact
feed through the full stream's greeting. Only its kitchen view runs during
normal harness use. Full readers trigger detailed snapshots, and gameplay
readers trigger both full scene reconciliation and the faster gameplay samples.
With all observation sockets closed, Player 2 control and navigation run on
their own. Reconnecting supplies fresh state and assets.

```sh
python3 scripts/build.py             # artifacts/plugin/SousChef.Bridge.dll
python3 scripts/check.py             # build, core tests, field audit, client tests
python3 scripts/install.py --uninstall
```

Uninstall removes manifest-owned, unchanged files and preserves other mods,
modified files, generated configuration, and logs. Launching the `.app` normally
bypasses this loader unless you separately configure Steam to use it.

## Native headless kitchens

Run a silent, windowless instance of the installed game with an exact kitchen and
repeatable resets. This uses the game's own levels, chef controllers, and physics:

```sh
python3 scripts/native_simulator.py start --level s_wizard_school_3_4
python3 scripts/native_simulator.py navigate Chocolate --repeat 5
python3 scripts/native_simulator.py stop
```

The existing harness and action clients also work with this process. See the
[native simulator guide](docs/native-simulator.md) for level selection, resetting,
frame-rate tests, saved traces, and current limitations.

## Organization

| Directory | Responsibility |
| --- | --- |
| `src/Core/` | JSON, stable identities, bounded TCP transport, graph search and action state |
| `worker/` | Separate .NET planner/encoder process; see [worker architecture](docs/worker.md) |
| `src/Plugin/` | Round gate, main-thread scene/geometry capture, lifecycle |
| `src/Plugin/Adapters/` | Explicit field schemas and bounded readers |
| `src/Plugin/Navigation/` | Live physics reachability, destination catalogue, autonomous walking |
| `clients/` | Python observation/control/action APIs, world index, recording validator |
| `harness/` | Local browser target list, action selectors, status and Stop control |
| `scripts/` | Pinned toolchain, build, reversible install, launch, checks |
| `tests/` | Core/transport tests, installed-game field audit, client tests |

See [architecture and coverage](docs/architecture.md) for supported observations
and known limits. The [Jev interpreter](docs/jev-interpreter.md) adds typed or
finalized voice commands with clarification and a reviewable plan preview. Set
`JEV_API_KEY` in `.env`, restart the harness, and enable Jev.

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
