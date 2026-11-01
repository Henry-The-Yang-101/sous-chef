# Sous Chef — Overcooked! 2 state bridge

A read-only, local bridge for the native macOS Steam build of Overcooked! 2.
It observes active kitchens, including paused rounds. Menus, the world map,
level intros and results screens produce status messages, never map snapshots.

The plugin combines a scene graph and collision geometry with explicitly
allowlisted gameplay adapters. Food composition, attachments, processing,
orders, environmental forces and moving geometry keep their original identities
and authority. Unsupported observations are reported rather than guessed.

Implementation and runtime validation are in progress. See `docs/` for the
protocol, coverage, and validation record as they are added.

## Layout

- `src/Core/`: Unity-independent wire encoding, identity, and bounded transport.
- `src/Plugin/`: BepInEx lifecycle, Unity capture, geometry, and gameplay readers.
- `clients/`: dependency-free Python consumer and recording tools.
- `scripts/`: pinned toolchain, build, installation, and validation commands.
- `tests/`: transport, identity, protocol, and installed-game compatibility checks.

The game assemblies are local build inputs and are never included in this repo.
The implementation is original; the investigation's reference projects are
design references, not vendored source.
