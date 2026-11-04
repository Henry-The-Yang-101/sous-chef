# Local action harness

From the repository root:

```sh
python3 -m harness.server
```

Open [http://127.0.0.1:8765](http://127.0.0.1:8765). The server uses Python's
standard library and a bundled QR encoder, and uses the existing control endpoint
on port 24703. Override with `--port` and `--bridge-port` if needed. By default it
also listens on the Mac's network interfaces for phone access; `--local-only`
restricts it to loopback.

Start the game through `python3 scripts/launch.py`, add Player 2 with **Split
Controller**, and enter an unpaused **Arcade → Couch** kitchen. The page reconnects
automatically when the game starts or restarts. It never creates players or drives
the menus.

## Use

The **Kitchen map** is the default destination picker. It shows a simple top-down
x/z projection of observed floors, walls, counters and chefs; +z is up. Select
**Action target**, **Throw recipient** or **Supply delivery** in **Choose on map**.
Click a marker to select it, then use the corresponding Run/Throw button. Map clicks
never dispatch an action. For placing, select an action target and choose **Place
held item**. The throw and supplies forms also have **Choose on map** buttons;
choose the throw kind or job/source first, then pick its destination on the map.

Only targets in the current operation/source-compatible catalogue are enabled.
Other observed targets remain dimmed for context. Numbers group nearby or stacked
targets; clicking a group offers explicit choices with height and identity. This
also keeps markers usable at phone width. Moving platforms update from each sample,
and removed, stale or replaced targets cannot remain runnable. The detailed
**Show target list** and form dropdowns remain available as alternatives.

**Game FPS** plots up to 60 seconds of interval averages, calculated from changes
in the game's render-frame counter divided by its real-time clock. It measures
rendered game updates, including paused frames, independently of browser refresh,
HTTP timing and game time scale. It is not a per-frame stutter or 1% low metric.
The displayed Low is the lowest plotted interval average. History resets at round/
session changes, clock/frame regression or observation gaps. The installed bridge
exports frame counters in kitchens only, so the graph waits in menus/lobbies.

The **Throw held item** form uses the item already in Player 2's hands. Choose
**Player** or **Place**, choose the recipient/destination, then click **Throw to
player** or **Throw to place**. There is no separate source-item selector.
Only targets with a checked throw arc and reachable release position appear.
Raised or obstructed targets are withheld unless a valid release stance exists;
P2 can walk, climb or descend before releasing. Place labels include position
and height to distinguish similar counters on different decks.

Throw previews continue while the browser is focused, without acquiring a
control lease or moving the chef/item. The page reports when paths are still
being checked, the hands are empty, an action is running, or state is unavailable.
Clicking a throw brings the game forward before dispatch. Target/session/object
identity and current feasibility are checked again, and removed/replaced
selections clear. Both throw operations also remain in the per-target row menus.

The **Move supplies** form exposes the seven finite logistics jobs. Choose the
job, an observed item/ingredient/type/station where required, and its delivery
surface. The job selector contains only jobs with a currently usable source and
compatible reachable delivery. Ingredient/item/type choices depend on the job;
delivery choices then depend on the selected source. Full hands, items in use,
native pickup/placement restrictions, occupied surfaces and unconfirmed walking
routes all affect the options. Empty delivery counters are checked before pickup.
Return utensil chooses an observed reachable home or compatible empty station.
Source/destination compatibility and object identities are rechecked before dispatch;
removed selections clear rather than silently switching to another item.
Raw choppable foods such as Chocolate are included even when the game gives them
no ingredient-order definition. Their native chopping-output prefab identifies
the raw supply and its spawned items; processed ingredients retain separate IDs.
Assembly bases such as burger buns use their native `PreparationContainer`
ingredient definition, even when they have no ordinary ingredient component or
chopping output. Filled buns are distinct from empty supplies.
Parent and child phases remain visible through pickup and delivery. Stop and
browser-disconnect cleanup cancel the entire job.

If the job fields are disabled, the message below the form explains whether the
bridge needs an update, the game is paused, another action is running, or the
required items/destinations are missing. The page can be newer than the plugin
loaded by the game. After building new actions, **quit the game**, run
`python3 scripts/install.py`, and relaunch with `python3 scripts/launch.py`.
Refresh the page afterward. Restart `python3 -m harness.server` to expose the
bridge's version and implemented command list in **Bridge details**; these come
from the connected plugin, independently of its currently available actions.


- Select an action in a target's dropdown, then **Run**. **Navigate** sends the
  exact chosen destination to `navigate_to`. Other options include pickup,
  placement, ingredient addition, work, activation, disposal, waits and targeted
  throwing; see [first-layer actions](actions.md) for their semantics. Work and
  wait choices include their completion condition, such as **Work: one plate
  clean** or **Wait: cooked**, with the default 60-second timeout. Player rows
  offer **Pass held item** when the native capability is available.
- The plugin handles pathfinding, steering, replanning and final position/facing. On macOS,
  clicking brings Overcooked! 2 to the foreground using the OS application launcher
  so that its native input gate accepts the command. The harness sends a typed
  action request; the plugin owns the logical inputs and verifies the outcome.
- **Stop**, or Escape while this page is focused, interrupts the current bridge
  action. Stop is available even when another API client owns Player 2.
- Filter by food name, object name, type or ID. Food dispensers show the actual
  `PickupItemSpawner.m_itemPrefab.name` (for example, Flour, Egg or Cheese_Raw),
  rather than just `DispenserCrate`. Names are observed, never inferred from crate
  numbers. A missing prefab is shown as unavailable.
- A **Contents** line shows the item on a counter, cooker or chopping station,
  and the ingredients inside pots, pans, bowls and plates. Nested contents are
  included (for example, `pot (Rice, Tomato)`), along with plate-stack and dirty
  sink-plate counts. Contents are searchable, so filtering for an ingredient or
  utensil also finds the station holding it. Known empty holders say **Empty**;
  missing or stale observations say **Contents unavailable**. If the held item
  is known but its ingredients are not, its name stays visible with that qualifier.
  Dispenser supplies remain separate from items currently resting on a station.
- Each row shows the stable destination ID, object instance, live x/z position
  and two distances from Player 2, in game world units. **Walk** is the planner's
  route to a usable approach stance; **direct** is the horizontal straight-line
  distance to the object's pivot, which can include an intervening counter or wall.
  Walk can be shorter than direct because the chef stops beside an object rather
  than at its centre. Both update with the live catalogue. Targets without a
  walking route can still offer throws and waits; their walking distance is
  unavailable. This metadata requires
  the rebuilt plugin: quit the game, run `python3 scripts/install.py`, then relaunch
  through `python3 scripts/launch.py` after updating.
- The status names the actual target and shows walking, aligning, arrival or the interruption reason.
  `Arrived` includes the actions currently allowed by native predicates (pickup,
  place or use). An empty sink instead reports that the chef is positioned and
  facing it, with no action currently available. Navigation never performs the work.
  All action buttons are disabled while an action is active; stop it before choosing
  another. Interaction phases and terminal outcomes also appear in the status.
  Commands are never retried or redirected automatically.

## Open on your phone

Open **Open on your phone** on the Mac's localhost page and scan its QR code with
your phone's camera. Keep both devices on the same Wi-Fi. The phone shows the same
live list, distances, status, action selectors and Stop control, so Overcooked! 2 can stay
focused on the Mac. If the Mac has several network addresses, choose the one on
your phone's network in the connection selector. Guest Wi-Fi isolation or a local
firewall can prevent direct access.

For access from another network, install
[`cloudflared`](https://developers.cloudflare.com/tunnel/get-started/quick-tunnels/)
once (`brew install cloudflared` on macOS), then start:

```sh
python3 -m harness.server --tunnel
```

The harness starts a temporary Cloudflare Quick Tunnel, adds its HTTPS URL to
the QR panel automatically, and stops the tunnel when the harness exits. Select
**Internet** if it is not already selected. It requires no router port forwarding
or Cloudflare account. If you already have a forwarded URL, use
`--public-url https://your-forwarded-host.example` instead; this registers the
exact origin and encodes it but does not start forwarding itself.

QR codes are generated locally with no external image service. Each link contains
a random per-process access token in its URL fragment. The browser moves it into
session storage and removes the fragment; non-local viewers need that token to
read state or issue commands. A tunnel's loopback proxy connection does not bypass
authentication. Treat the QR/link as access to this Player 2 session; restarting
the harness invalidates it, so scan the new QR afterward. Ordinary Wi-Fi HTTP is
unencrypted; the optional tunnel uses HTTPS. The plugin's TCP endpoints remain
loopback-only.

## Live data and lifecycle

A single persistent connection polls the plugin up to **5 Hz**, shared across all
browser tabs. The page reads that cache every 250 ms; more viewers do not increase
game-side planning frequency. This is a target rate, bounded by game response time.
One additional read-only connection consumes the existing full observation stream
on port **24702** (`--state-port` to override), shared by all viewers. Contents
follow explicit attachment, workstation, cooking, stack and ingredient-container
fields from a fresh sample of the same bridge session, joined by exact Unity
object instance and name. They are not inferred from proximity or model children.
Host observations prefer server fields; client observations use client fields.
That same connection supplies FPS and map data. No additional game connection,
capture, physics query or increased polling rate is used for these features.
The separate Python process caches collider footprints and keeps a bounded FPS
history; HTTP viewers share the results. Browser SVG/marker rendering runs on the
existing page poll, skips hidden tabs and has no animation loop or external assets.
Boxes use their transformed footprint; readable mesh triangles preserve gaps and
separate platforms. Unreadable/unsupported shapes use bounds and are labeled as
approximate, and the display reports its 3,000-polygon limit if reached. This is
a map view of observed collision geometry, not a claim of walking reachability.
Every sample replaces the previous contents, including item removals and round
changes. A disconnected, missing or over-1.5-second-old contents sample cannot
describe a holder as empty. Content enrichment does not change navigation targets
or perform pickup, placement or cooking actions, and requires no plugin restart.
Destinations are replaced on each observation, including removals caused by moving
geometry. No options are shown when data is over 1.5 seconds old, the game is outside
a round or Player 2 is missing. During a budget-limited search, confirmed shortest
routes remain clickable while the count says **still checking**; uncertified
destinations are withheld until the planner reaches them. A partial catalogue is
not a claim that omitted locations are physically unreachable.

Clicking rechecks the bridge session, destination ID, object instance and current
reachable list. The plugin revalidates again at action start. A removed target or
new game session produces an error instead of acting on a stale row. If the game's
native input gate blocks control, the reason is displayed. Focus activation gets
a bounded 1.5-second wait and then the exact target is checked again. Menus, pause
and respawn still prevent movement. A rejected command is never retained to run
later when a round starts or an obstacle moves.

Local player chefs do not block the list or routes. Fire, walls, pedestrians and
other non-player solid geometry still do. Discovery covers a 60-unit vicinity;
the empty-list reason distinguishes missing/paused players, unsupported starting
positions and unfinished planning rather than silently implying a distance cutoff.

The owning browser tab sends heartbeats through its state requests. Closing or
reloading it releases its lease; if the close notification is lost, five seconds
without that tab's heartbeat releases it. Other tabs cannot keep its action alive.
Severe browser timer throttling may therefore stop an action conservatively.
Ctrl+C closes the harness's persistent socket, also cancelling its action. Losing
the bridge clears all options; reconnection observes only and never resumes motion.
The underlying plugin's input lease remains responsible for immediate socket-loss
handling. Network delays and Unity updates bound stop latency; native momentum is
not forcibly erased.

The HTTP server validates the exact configured Host/Origin and requires a
per-process page token for commands, plus authenticated reads for phone/tunnel
origins. There are no external page assets or runtime Python dependencies. Only
`--tunnel` starts an outbound service. This is a development harness; every viewer
with the QR token can control Player 2.

## Verification

```sh
python3 -m unittest discover -s tests -p 'test_*.py' -v
python3 scripts/check.py
```

Tests cover live removal, staleness, identity/session mismatch, command rejection,
disconnect/reconnect, browser ownership expiry, Stop, shutdown and the HTTP path.
They also cover dispenser metadata, phone authentication, forwarded Host/Origin
checks and phone command/heartbeat round trips. See
[the validation record](validation.md) for actual Arcade Couch browser tests.

Phone sharing was checked on November 1, 2026: both the Mac's LAN address and the
managed public HTTPS tunnel returned fresh game state with the QR token and
rejected unauthenticated state requests. Chrome loaded both links, and the LAN
page rendered without horizontal overflow at phone width. Apple's Vision QR
decoder read a screenshot of the displayed QR and recovered the exact access
URL. The full check passed 730 C# assertions, the 151-type installed assembly
audit and 42 Python tests. A physical phone camera/network was not exercised.

Contents checks added later the same day passed all 53 Python tests, including
nested ingredients, attachment de-duplication, plate stacks, dirty sink plates,
item movement, stale/missing data, authority selection and unchanged navigation
targeting. A browser preview using an actual recorded kitchen showed pans on
cookers, plates and a fire extinguisher on counters; filtering by `plate` found
the holding counters. The contents lines also fit phone width without horizontal
overflow. The live game was outside a kitchen during this verification.

FPS/map checks on November 2, 2026 passed 109 Python tests and JavaScript syntax
checks. Browser checks used actual recorded Moon Festival and Wizard kitchens to
verify action/placement/throw dispatch identities, source-compatible delivery,
nearby-target choice, stale-state clearing and a 390-pixel phone viewport without
horizontal overflow. Across recorded Sushi, Moon Festival and Wizard snapshots,
cached map extraction took median 1.2–2.2 ms in Python, with 5.4–10.8 KB map payloads.
These are off-process extraction measurements, not a live game FPS comparison.
The restarted localhost harness connected to the running bridge in its Arcade
lobby; the graph correctly waited for an in-round render counter. No plugin change
or game restart was needed.

## Autonomous tasks

The Tasks panel runs the seven actions described in [tasks.md](tasks.md), with
1–10 repetitions or Until stopped. Preparation supports multiple ingredient/utensil
rows, states and quantities. Only eligible tasks are selectable. While a task is
running or waiting, its phase, completed repetitions and waiting reason remain
visible; Stop cancels the parent and child together.
