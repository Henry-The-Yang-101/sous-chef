# Job focus and connection lifecycle — 2026-11-05

Bridge **0.8.7** suspends accepted autonomous work when the game loses focus,
pauses, or temporarily suppresses P2 input, including respawn. Logical input is
neutral while suspended. Returning to an input-ready kitchen resumes the same
request, repetitions and child actions, with watchdogs adjusted for the time
away and navigation revalidated. The game itself keeps simulating while
unfocused: food can cook and geometry can move.

Stop is the explicit cancellation path and works while suspended, including
from a new connection or browser tab. Closing/reloading a tab, missing browser
heartbeats, closing the harness, client `release()`, and socket disconnects no
longer cancel accepted actions, logistics jobs or tasks. Reconnection observes
the original request; it does not replay its command or start another copy.
Completed work still finishes normally, and ordinary action outcome failures
remain possible. Reassignment, round exit/change and plugin shutdown clear
work tied to the old chef/kitchen. Raw manual input retains immediate
disconnect cleanup and its existing short lease timeouts.

The harness displays the suspension reason beside the active job and keeps Stop
available. It no longer ties execution to a tab's lifetime or five-second
heartbeat window. Cancellation does not park or discard held food.

## Verification

`python3 scripts/check.py` passed against the installed game assemblies:
2,863 core/assembly assertions, 335 navigation checks, 784 action checks, and
111 Python tests. Core checks retain neutral manual disconnect behavior while
verifying that autonomous ownership survives disconnect/suspension. Harness
checks verify that browser closure/inactivity preserve a job until Stop.

The native lifecycle audit used an isolated, windowless Unity instance on
`s_sushi_1_2`, requested 60 FPS, separate ports 25052–25054, and the existing
`input-focus off/on` fixture. That fixture exercises the production
`game_unfocused` gates; it does not simulate a literal macOS window switch.
Measured execution uses the production logical-input executor, native food,
physics and clocks. It does not force poses, work progress or inventory.

| Case | Observed result |
| --- | --- |
| Focus loss during preparation travel | One request survived six seconds unfocused, resumed and finished one repetition. |
| Focus loss during chopping | The same request survived another two seconds unfocused and a pause/unpause transition while still unfocused. |
| Graceful client close, including `release()` | The infinite monitor remained running, retained its lease, and resumed after reconnect/focus restoration. |
| Abrupt socket loss | The original monitor survived; a new client could observe and Stop it, while a replacement claim was rejected. |
| Stop after reconnect | Both monitors ended as `cancelled/stopped`; Stop took about 17–23 ms, including one Stop while unfocused. |
| Focus loss/disconnect with held fish | The exact native item `-43538` remained held through suspension, reconnect and Stop. |
| Standalone navigation | Focus loss and client close retained the destination and lease; Stop worked from another client while suspended. |
| Finite standalone wait | A 1.5-second watchdog survived three seconds unfocused and reconnect; its deadline extended by about 3.20 seconds on resume. |

The localhost harness was also opened against this isolated runtime. An infinite
monitor remained running after the page reloaded while unfocused. The status
showed **Suspended while the game is unfocused**; clicking Stop changed it to
**Player 2 is idle / cancelled: Stopped.**

The tested 0.8.7 DLL SHA-256 is
`cd347ae4e82069c7b0bebc97a1c87183cb0ff4d46cf0de862400dc9621a288a0`.
Private evidence is under `.local/focus-lifecycle-audit-final/`, including
`result.json`, per-state receipts, independent cargo snapshots and harness
screenshots. The audit's own simulator and harness were stopped afterward.
The already-running interactive game and other runtimes were not replaced.

## Reproduction

```sh
python3 scripts/native_simulator.py --runtime .local/focus-lifecycle-native-final \
  start --port-base 25052 --level s_sushi_1_2 --fps 60
python3 scripts/control_lifecycle_audit.py \
  --runtime .local/focus-lifecycle-native-final --port-base 25052 \
  --output .local/focus-lifecycle-audit-final
python3 scripts/native_simulator.py --runtime .local/focus-lifecycle-native-final stop
```

Use free, non-default ports and a separate runtime. To use the change in the
interactive game, quit the running game instances, run `python3 scripts/install.py`,
relaunch with `python3 scripts/launch.py`, and restart the harness. Already-loaded
plugins keep their previous behavior until the game restarts.
