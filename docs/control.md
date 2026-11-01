# Direct Player 2 control

For autonomous walking and interruption, use [navigation actions](navigation.md).
For autonomous interactions and targeted throws, use [first-layer actions](actions.md).
The raw input API below remains available for low-level mechanics.

Create two local players manually using the game's Split Controller option,
then enter a kitchen. Player 2 can share the physical keyboard with Player 1.
No additional controller, virtual pad, keyboard event injection, or player-join
command is needed. The endpoint targets the actual local `Player.Two` ID,
never the second object in a list or the alternate chef in single-player mode.

## Python

```python
from clients.control import ControlClient

with ControlClient() as player2:
    print(player2.status())
    player2.claim()
    player2.hold(0.3, y=0.5)
    player2.hold(0.12, buttons=['pickup'])
    # Context exit releases ownership and restores the original P2 bindings.
```

The game must be focused, in an active unpaused local round, and accepting
input for the target chef. Axes use the game's logical MovementX/MovementY
convention, not an absolute world destination. Each is in [-1,1]; diagonals
are normalized to unit length. The native movement configuration can invert axes: in the tested kitchen,
positive Y moved toward decreasing world Z. Use observed positions to calibrate
a world-space controller; the bridge preserves the native convention.

Buttons are `pickup` (pickup/drop), `use` (work/interact/aim/throw according to
native context), and `dash`. Hold `use` for chopping/washing; throwing uses the
game's ordinary press/hold/release behavior. No direct object pickup, teleport,
forced interaction, menu, pause, or chef-switch command is exposed.
First-layer actions additionally use the native interaction-end protocol to
clean up their own sticky work when finishing or cancelling; raw input does not
invoke this cleanup for unrelated interactions.

```sh
python3 clients/control.py                         # status only
python3 clients/control.py --y 0.5 --seconds 0.3   # brief P2 movement
python3 clients/control.py --pickup --seconds 0.12
```

`set_input(x=0, y=0, buttons=(), ttl_ms=300)` sends a complete replacement input
state. Repeated held buttons do not mean repeated presses: send a neutral state
between taps. `hold()` renews every 100 ms and sends neutral afterwards. `release()`
restores native input. If you retain ownership yourself, renew before the TTL;
expired ownership requires another explicit claim.

## Ownership and lifecycle

Only one connection can claim P2 at a time. While claimed, bridge inputs replace
P2's movement, pickup, work/use and dash fields. Player 1 and other logical
bindings are untouched. Releasing restores the exact original input objects,
provided another mod has not replaced those fields in the meantime.

A claim without subsequent input lasts 500 ms. Input TTL is 50–1000 ms, measured
with a monotonic wall clock rather than scaled game time. Disconnect, expiry,
pause, input suppression/focus loss, player reassignment, round exit, or plugin
shutdown revokes control. Inputs are neutral immediately when sampled after a
lease expires/disconnects; restoration occurs on the Unity update thread.
Native physics momentum or a dash already started can continue after input stops.
The bridge does not rewind or cancel those native actions.

The game retains its normal control gating. This includes focus and suppressed
chef control; special modes that transfer input to a piloting/cannon control
scheme are not promised by this first control adapter. A rejected command does
not cause the client to reacquire automatically.

## Wire protocol

Separate IPv4 loopback TCP endpoint: `127.0.0.1:24703`. Configure `Control.Port`
in `BepInEx/config/dev.souschef.observer.cfg`; it must differ from the observation
port. Four connections maximum. The existing observation stream stays on 24702.
Its greeting advertises the installed `bridgeVersion`, `readOnly: false`,
`controlPort`, and `controlPlayer: 2`; observation protocol version remains 1.

Control replies are UTF-8 NDJSON. The greeting has `controlProtocolVersion: 1`,
`player: 2`, session ID, `mode: direct_logical_input`, and `maxHoldMs: 1000`.
Commands are bounded ASCII lines (LF terminated, at most 256 characters):

```text
status request_id
claim request_id
input request_id sequence ttl_ms x y button_mask
release request_id
```

Request IDs are at most 32 alphanumeric/underscore/hyphen characters. Sequence
numbers must be positive and strictly increasing within the claim. Button mask
bits: pickup=1, use=2, dash=4. Unknown commands, invalid axes, nonfinite numbers,
out-of-range TTLs, and unsupported mask bits are rejected. There is intentionally
no caller-specified player ID that could accidentally target P1.

Replies carry `type: reply`, `id`, `ok`, optional error, and a `control` record:
`available`, `reason`, `claimed`, `ownedByYou`, `lastRelease`, target chef/component
instance IDs and the assigned pad/side. Chef object instance ID correlates with
`objects[].unityInstanceId` in observations; the component ID correlates with the
`PlayerControls` component's instance ID.

Common errors: `outside_local_round`, `player2_absent_or_ambiguous`,
`player2_not_local`, `player2_not_ready`, `paused`, `input_suppressed`,
`bindings_changed`, `already_claimed`, `not_owner`, `stale_sequence`.

Workers parse requests and queue bounded work; Unity applies commands and
returns an acknowledgement from its update loop. An accepted command means the
input state was installed, not that a collision, pickup or other action succeeded.
Use observation snapshots to determine the actual outcome. Since bridge 0.4,
idle connections stay open so an autonomous action can finish without client
heartbeats. The four-connection limit, bounded dispatch and write timeouts, and
short raw-input TTLs still apply. `stop` interrupts either navigation or raw input
from any connection. Navigation rejects raw `input` with `action_in_progress`.

## Implementation

`src/Core/ControlLease.cs` owns the thread-safe lease, input state and strict
command parser. `ControlServer.cs` handles bounded transport and main-thread
dispatch. `src/Plugin/Control/Player2Control.cs` discovers the unique local P2,
substitutes its five logical input objects, preserves native gating/button
semantics, and restores originals. No Harmony patch or global input lookup
replacement is required. `clients/control.py` provides the consumer API.
