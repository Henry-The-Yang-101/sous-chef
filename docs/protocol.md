# Protocol v1

The server binds IPv4 loopback only. Each line is one UTF-8 JSON object terminated
by LF. A connection receives `hello`, then the latest complete state and updates.
This observation socket is send-only. A separate [Player 2 control endpoint](control.md)
accepts commands. Four observation clients can connect; the Python client limits
individual lines to 64 MiB.

## Messages

| Type | Meaning |
| --- | --- |
| `hello` | Protocol/bridge version, process session ID, Unity version, game assembly SHA-256, configured capture rate, read-only flag |
| `asset` | Geometry payload keyed by `id`, sent before snapshots referencing it |
| `snapshot` | Complete observed active-round world, `inRound: true` |
| `status` | No usable world: `inRound: false`, reason `outside_active_round` or `capture_error`; no objects |
| `gap` | Sequence interval skipped, described by `afterSequence` and `beforeSequence` |

`sequence` increases across snapshots and statuses in one session. Consumers may
skip frames under load; new connections start at the latest frame. Full snapshots
allow recovery without replay. Inactive status heartbeats occur about once per
second. On disconnect, timeout or status, invalidate the current world. An open
socket does not prove freshness: the Unity render loop may stop advancing.

## Identity and authority

`sessionId` changes when the plugin starts. `levelEpoch` increments on a newly
observed round flow. Object `id` is an opaque session/epoch/serial string; use it
as the primary key. Unity instance IDs and `nativeEntityId` are diagnostics.
Native IDs can be absent and Unity can reuse instance IDs. The bridge also
checks reference identity and retires unseen objects. Reset indexes on epoch
or session changes.

`role` is `host` if a local server flow is available, otherwise `client` (or
`unknown` outside a round).
Component `authority` is inferred from the Server/Client type prefix, or is
`shared`: it labels provenance, not authority of every individual field. Host
snapshots retain both copies. Client-only captures lack hidden server state.

## Snapshot

- `flowId`: ID of the active flow object.
- `scenes`: loaded names, paths, and session-local handles.
- `paused`: the game's main pause layer, independent of Unity time scale.
- `clock`: render frame, plugin fixed-update count, Unity game/fixed/realtime
  clocks and deltas, time scale, and capture stage `WaitForEndOfFrame`.
- `objects`: observed graph, including inactive objects. Each record has identity,
  parent, scene, layer, tag, activation/static flags, transform, component inventory,
  colliders, and optional rigidbody state.
- `changes`: created/removed/reparented records compared with the previous
  *captured* frame. `semantics: sampled_diff`; this is not a lossless event log
  and may refer to a preceding frame the consumer never received.
- `geometryRevision`: conservative invalidation token advancing every capture
  because transforms, activation and shapes can change.
- `meshRevision`: cached mesh/terrain payload revision. Asset contents are polled
  every two seconds; transforms and ordinary shape dimensions are read each frame.
- `physics`: gravity, query settings and 32 named layers, each with its
  `collidesWith` layer indices. Per-collider collision-ignore pairs are unavailable.
- `coverage`: used schemas, component counts, unsupported types, missing fields,
  truncation counts, read errors and known limits.
- `diagnostics`: main-thread capture milliseconds and serializer coalescing count.

Unity reads are synchronous on the main thread at end of frame. This does not
establish an exact physics solver boundary. `fixedUpdateCount` counts plugin
callbacks, not independently verified solver steps.

## Coordinates and geometry

Unity uses Y up. Vectors are `[x,y,z]`, quaternion rotations `[x,y,z,w]`, and
matrices column-major arrays of 16 numbers. Transform records include world/local
position and rotation, local/lossy scale and `localToWorld`.

Collider bounds are world-space AABBs for broad-phase filtering. Actual box,
sphere and capsule dimensions are local; combine them with the object transform.
Character controllers include their dimensions, step offset, slope and skin
width. Activation, trigger, material and attached rigidbody are separate data.
Inactive colliders must not automatically become navigation obstacles.

Mesh assets contain flat local XYZ vertices and triangle indices when readable
(up to 250,000 vertices). Terrain assets contain normalized Z-major heights,
resolution and physical size (up to 1025²). Geometry data has status `observed`,
`unreadable` or `truncated`; bounds are not silently substituted for exact meshes.
Only collision meshes are captured, not render-only meshes or textures.

Retain only assets referenced by the latest snapshot, and clear after status or
new session. The server resends needed assets if they disappear/reappear, including
after capture-error recovery. Asset IDs are scoped to session/epoch context.

## Gameplay values

Components retain runtime `type` and `instanceId`. Known readers add, for example:

```json
{
  "adapters": {
    "ServerPlayerAttachmentCarrier": {
      "domain": "attachments",
      "fields": {"m_carriedObjects": []}
    }
  }
}
```

Inherited schemas can produce several adapters on one component. Nested data
records have `type` and a `fields` map with qualified member keys. Exact names
preserve distinctions between game stages and server/client copies. The catalog
is the definitive field list.

Scene references use `{objectId, component, componentInstanceId}` and resolve
within the snapshot. Asset/prefab references instead carry `kind: asset_reference`,
name, type and `assetInstanceId`; they are metadata, not spawned scene entities.

Null represents an absent/null value; non-finite floats also encode as null for
valid JSON. Failed/unavailable observations use explicit statuses: `missing_field`,
`unsupported_value`, `read_error`, `truncated`, or `cycle`. Unknown components remain
in the inventory with `inventory_only`. Schemas read selected fields; a supported
base class does not imply coverage of additional subclass fields.

## Consumer rules

1. Check version/fingerprint and inspect coverage for adapter drift.
2. Replace the world on each snapshot; sampled changes are not action history.
3. Resolve identities and attachments before deriving occupancy or ownership.
4. Distinguish inactive geometry, triggers, collision layers, hazards and food.
5. Invalidate on status/disconnect/staleness and handle unavailable reads explicitly.

`clients/world.py` supplies indexes, not planning/navigation inference.
`clients/validate_recording.py` checks reference closure, hierarchy cycles, asset
availability, monotonic sequences and adapter errors in recordings.
