# Sous Chef — Overcooked! 2 state bridge

A read-only local bridge for couch co-op in the native macOS Steam build of
Overcooked! 2. Online multiplayer is outside the supported scope.
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
| `src/Core/` | JSON, stable identities, bounded TCP transport |
| `src/Plugin/` | Round gate, main-thread scene/geometry capture, lifecycle |
| `src/Plugin/Adapters/` | Explicit field schemas and bounded readers |
| `clients/` | Python consumer, world index, recording validator |
| `scripts/` | Pinned toolchain, build, reversible install, launch, checks |
| `tests/` | Core/transport tests, installed-game field audit, client tests |

Read [architecture and coverage](docs/architecture.md) and the
[validation record](docs/validation.md) before relying on an untested mechanic.
The [original investigation](overcooked2-bridge-investigation.md) provides context.
Game assemblies are local build inputs and are never included in this repository.
The implementation is original; reference projects were not vendored.
