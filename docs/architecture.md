# Architecture and coverage

## Pipeline

`BridgePlugin` runs an end-of-frame coroutine. `KitchenGate` reads the actual
server/client `InRound` flag, preferring the server whenever present. It does
not infer gameplay from scene names. Pausing keeps the round active. Menus,
loading, selection and ended rounds produce only status.

`SceneCapture` traverses loaded scene roots including inactive descendants,
skipping UI (`RectTransform`) subtrees. It also follows the entity registry and
references from allowlisted gameplay fields. This reaches relevant persistent
managers without sweeping unrelated account/save/session managers. Parents and
references retain graph closure. Décor and animation bones remain in the graph;
consumers can filter them using components, geometry and activation.

`GeometryCapture` reads shapes, body/animation state, physics settings and cached
mesh/terrain data. `FieldReader` uses explicit cached reflection bindings from
`AdapterCatalog`. It does not traverse arbitrary properties or coroutine bodies.
Limits are 24 nested levels, 8,192 values per root field and 4,096 collection
entries. Truncation is visible. The game's custom FastList reader respects its
live count rather than exposing stale entries in unused backing capacity.

All Unity reads occur on the main thread. Frozen plain DTOs pass to a serializer
worker and independent socket writers. A single pending capture and single
latest encoded frame bound the backlog; slow consumers cannot block Unity.
Socket writers have a two-second timeout. Asset caches and identities are pruned.
The bridge makes no gameplay/input calls and adds no gameplay Harmony patches.

## Implemented observations

| Domain | Data retained |
| --- | --- |
| Scene | Loaded scenes, non-UI transforms, parentage, activation, layer/tag, static flags, native IDs, sampled topology changes |
| Collision/motion | Box/sphere/capsule/character/mesh/terrain shapes, bounds, triggers, material, rigidbodies, gravity and layer filtering |
| Chefs | Controller/team identity, movement/dash data, interaction candidates, carrying, collision and respawn state |
| Food | Ingredient/item definitions, recursive assembled order nodes, composition, plating, cooking/mixing stages and progress |
| Stations | Attachments, work/chop/mix/cook/heat, containers/stacks, dirty/clean plates, washing, return queues, delivery and disposal |
| Round | Orders and remaining time, score/tips/team statistics, recipes, round timer, timer suppression and pause |
| Environment | Wind volumes/velocity, conveyors, slippery surfaces, moving/dynamic phases and durations, animator states/transitions/parameters |
| Special mechanics | Portal/cannon state, piloting, fires/extinguishers, grid/Horde fields where present |

There are 151 explicit schemas. The check script resolves every declared member
against the installed assemblies. That proves member availability in this build,
not behavioral coverage of every mechanic. The catalog is the exact field list.

## Boundaries

- Sampling cannot observe actions/objects that begin and end between captures.
  The default 5 Hz favors breadth over event fidelity.
- Online client-only sessions, Horde, DLC, terrain-heavy levels and many special
  mechanics have not been live-validated. Client role lacks hidden server state.
- Unity 2017.4 lacks the public per-collider ignore-pair reader. Layer filtering
  is available. No navigation graph or exact reachability claim is provided.
- Unreadable meshes are explicit; mesh/terrain changes may lag two seconds.
  PhysX contact manifolds, solver internals and force history are not observed.
  Mesh triangles do not reproduce PhysX convex cooking exactly.
- Coroutine locals, queued iterator bodies, RNG and transient animator triggers
  are not read. Animation hashes, ordinary parameters and transforms are sampled.
- Prefabs have metadata only. Render meshes, textures, audio, UI and save/account
  records are outside map-state scope.
- Selected fields on a supported base class do not establish complete subclass
  coverage. Empty error lists mean successful selected reads, not completeness.
- Full captures incur CPU, allocation and network cost. Profile the actual level
  before increasing Hz. Reduce the rate first if overhead is excessive.

## Extending a mechanic

Inspect its data ownership and field meaning in the installed build first. Add
the smallest explicit schema exposing the necessary state and relationships.
Run the audit/tests, then record the mechanic in a real kitchen and check
progression, pause, reference closure and teardown. Document untested cases.
Do not serialize arbitrary unknown objects or invoke actions to discover state.

## Build and loader

The plugin targets the game's CLR 2.0/.NET 3.5 references using a pinned modern
C# compiler with `-nostdlib`. Building against modern .NET references alone would
be incompatible. Core tests run separately on .NET 8.

Tooling uses official [BepInEx 5.4.23.6](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.6)
and .NET SDK 8.0.408 downloads with checksum validation. Integration follows
[BepInEx installation](https://docs.bepinex.dev/articles/user_guide/installation/index.html)
and [Steam launch guidance](https://docs.bepinex.dev/articles/advanced/steam_interop.html).
`build.json` records the SDK version and reference fingerprints. Install checks
all conflicts before writes and tracks owned files for removal. Original game
assemblies are neither changed nor redistributed.
