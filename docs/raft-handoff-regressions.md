# Raft handoff regressions — 2026-11-04

Story 2-1 is `s_rapids_2_1`; story 4-2 is `s_rapids_4_2A`.
The changes use native floors, ingredient shapes and launch physics rather than
scene-specific production rules.

## Fixes

| Commit | Change |
| --- | --- |
| `71043e3` | Require a fully supported stationary throw stance, anchor it to its moving support, and measure arrival relative to that support. Rounded capsule contact over an open edge cannot certify a throw stance. |
| `da4b893` | Search distance-ranked landing seeds across P1's supporting floor. Distance from P1 is a preference; farther supported landings remain candidates. Preserve floor-local landing coordinates and verify receipt on the exact receiving support. |
| `0b3e264` | Try current-pose ground throws before walking and keep cached release poses local to the moving floor. Use 4 ms incremental throw-planning slices. |
| `cc6abdf` | Start work at a stopped chef's exact native selected station, including selection refreshing after placement, without repeating navigation and settling. Existing work identity, interaction and cleanup checks still apply. |
| `89ec25e` | Certify the actual landing footprint on the receiving floor rather than a tiny window around an aim seed. Revalidate ground throws at the actual stopped pose before arming, including full chef support, cargo footprint, hazards and swept flight. Counter and catch margins remain intact. |

The planner first seeks a usable landing from the current supported pose, then
considers checked walking releases. Seeds are ranked by distance from P1 and
stored relative to the floor; the full native trajectory determines the actual
landing. The grid normally uses 0.8-unit spacing and coarsens large supports.
Physics proofs are lazy. No model calls or new observation subscriptions were
added to the gameplay executor. Stationary support checks use box interiors
directly and reserve the extra native footprint rays for boundaries.

## Reproduction and verification

The original 0.8.4 build completed one potato/chicken pair on 2-1, chopped the
next potato, and retained it in `waiting_for_player_handoff` until the 100-second
audit deadline. P1 supplied no inputs and collected nothing. The trace and native
snapshots are in `.local/raft-handoff-baseline-repeat/`.

A far-player 4-2 baseline could not hand off one chopped potato within 35 seconds
(`.local/raft-handoff-before2-far/`). The floor-wide candidate change completed
that fixture in 3.45 seconds (`.local/raft-landings-far/`).

Combining faster work with the earlier throw fixes exposed a further stall after
two pairs. An intermediate floor-area change alone still stalled with five foods
delivered (`.local/raft-area-final-repeat/result.json`). This counterexample led
to revalidating prospective ground throws at the actual stopped pose instead of
eliminating narrow usable spaces with the walking-arrival margin.

The first complete fixed run delivered six distinct native foods over three
repetitions in 70.13 seconds under one infinite request and lease. All six foods
remained on P1's floor; P1 never moved or collected them. Actual native board
attachment to first work progress measured 0.212–0.293 seconds, versus 0.398
seconds in the baseline timing fixture. The reported 1–2-second delay was not
reproduced at that magnitude. Traces: `.local/raft-complete-repeat/`.

A second run completed the same three pairs in 90.90 seconds with six distinct
surviving deliveries and farther landings up to 4.57 units from P1. Across both
runs, twelve placement-to-first-chop observations ranged from 0.212 to 0.302
seconds, with a 0.245-second median. Both infinite requests remained active at
three repetitions until the audit explicitly stopped them.

The unknown original water-fall incident was not reproduced as a death. A native
4-2 open-edge fixture did reproduce admission of a throw approach 0.30 units
beyond the floor using rounded walking contact. The old navigator began moving
toward that point, then stopped when its world-space goal lost support as the
raft drifted. The fixed navigator rejects the same class of approach as
`unsafe_release_stance` before moving. These are distinct observations; the
fixture does not certify the user's unidentified exact incident.

Final suite results and timing are recorded in `raft-handoff-validation.json`.
They include original fixture records, independent before/after native snapshots,
20 Hz gameplay samples, 5 Hz catalogue/control traces, request identity and DLL
hashes. All measured P2 actions use production logical inputs. Initial supplies,
checked initial chef poses and the explicit synthetic throw-stance fixture are
recorded; no P2 pose, work progress, cargo or clock is forced during execution.

All **14 native cases** pass on DLL SHA-256
`2ab5a3442ffdf17ed25be0915fd3cfe9e789c926670c825c440c9f4c83363c69`,
from production source `89ec25e`:

| Case | Result |
| --- | --- |
| Infinite chopped potato/chicken, 2-1, twice | Three complete pairs and six surviving floor deliveries per request; no P1 inputs or collection. |
| Far P1, 4-2 | One chopped potato handed off in 3.65 seconds, versus the baseline 35-second timeout. |
| Normal handoff at 20 FPS, both layouts | One chopped potato delivered on each layout; no observed fall or respawn. |
| Controlled open edge, 4-2, 60 and 20 FPS | Rejected as `unsafe_release_stance`, no steering, over eight seconds observed with no fall or respawn. |
| Original four 4-2 preparation cases | Infinite raw lettuce/tomato, supplied raw chicken/potato, supplied chopped potato entering a fryer while chicken is missing, and the complete cooked-pair preparation case all pass. |
| Three cross-level regressions | Cooking overlaps chopping in 1-2; three distinct raw-cheese deliveries in 3-3; an empty native pot is delivered. |

The complete cooked-pair case stages P1's initial pose on P2's raft, as documented
in [preparation regressions](preparation-regressions.md). It isolates preparation
and local handoff; cross-raft nonthrowable plate transport remains unverified.

`python3 scripts/check.py` passes: 2,858 core/assembly assertions, 335 navigation
checks, 652 executor checks and 111 Python tests. The log is
`.local/raft-final-check.log`. Navigation/executor unit fixtures use analytical
native API doubles; the fourteen cases above use the real native player.

| Repeated 2-1 sample | Approx. FPS | Frame interval median / p95 / max | Gameplay capture p95 / max | Skipped stream sequences |
| --- | --- | --- | --- | --- |
| Original 0.8.4, stalled after one pair | 43.47 | 22.03 / 48.34 / 111.82 ms | 8.43 / 19.94 ms | Not recorded |
| Fixed confirmation, completed three pairs | 47.13 | 20.14 / 35.57 / 78.00 ms | 7.26 / 8.65 ms | 0 |

Performance figures are sampled averages across render-frame intervals, not an
exact per-frame profiler. The requested rate is an engine setting, not a promised
measured FPS. Native headless sessions, loading phase and host scheduling differ;
these comparisons do not establish rendered-game FPS or a controlled speedup.
The first successful repeat briefly overlapped an executor-test process; the
second run had no concurrent checks and supplies the comparison above. All owned
simulators were stopped. The normal game installation was not updated.

## Human-supplied food and board scheduling

The earlier `repeat` case pre-places every raw ingredient on a local counter.
On both raft levels P2 cannot walk to the potato and chicken dispensers, so the
human must throw raw food onto P2's raft. The `thrown` case models that: two raw
ingredients are in play, and each completed P2 handoff supplies the next. The
native spawn fixture stands in for the throw landing on a free board (`board`),
a standard counter (`counter`), the floor beside P2 (`loose`), or every board
before the request (`full`). P2's measured actions still use production inputs.

| Commit | Failure observed natively, and correction |
| --- | --- |
| `61fdedb` | On 4-2, raw chicken on P2's only board and raw potato on a counter: P2 collected the potato, then waited in `waiting_for_chopping_board` indefinitely (0.5 s into the run until the deadline). Food on a board is now chopped in place and preferred. Food needing a board is collected only when one can receive it. A board that fills after pickup or during placement makes P2 park its cargo and reschedule. Replannable steps whose precondition fails before a press re-plan. On 2-1, a repetition also waited 43 s while raw food lay on P2's raft, because repetition re-entry demanded a certified catalogue route; it now accepts the scheduler's local sources. |
| `26cfc64` | Loose food in front of a board took the native interaction selection (`m_TheOriginalHandlePickup` was the loose chicken, `m_iHandlePlacement` empty), so a positioned P2 held a potato for 96 s. Such a positioned child is now a stall; P2 parks its cargo and moves the loose object to a counter. In `full`, a later repetition's chicken on both boards blocked the remaining potato; P2 now moves the nearest board's contents aside, never a board in use. Ground-throw revalidation oriented the landing footprint along P2's facing rather than the planned throw, discarding valid landings (up to 118 times in one run) and causing 22–126 s handoff waits. A throw that bounced into the river ended the infinite request; preparation now re-offers held cargo or prepares lost cargo again. |

All **12 native cases** pass on DLL SHA-256
`7e2880287e2dd87f30918bc15199b893aa534a0a8b245febbb73eb8286668dc3` (source
`26cfc64`, requested 60 FPS). Each completed three infinite repetitions with six
distinct handoffs still present at the end, no respawn and no P1 input:

| Level | Layouts (runs) | Elapsed | Longest stationary handoff wait |
| --- | --- | --- | --- |
| 2-1 | `board`, `counter`, `full` ×2, `loose` ×2 | 40.7–80.3 s | 11.1 s |
| 4-2 | `board`, `counter`, `full` ×2, `loose` ×2 | 37.0–89.0 s | 3.3 s |

Two 4-2 runs waited about 5.5 s for a parking counter: Park's existing 5 s
exclusion after a blocked placement on the moving raft. Approximate FPS was
43–46 on 2-1 and 35–38 on 4-2. Evidence: `.local/thrown-audit3/` (final),
`.local/thrown-audit/` and `.local/thrown-audit2/` (intermediate failures), and
`.local/repro-board-out/` (baseline and diagnostic runs, including positioned-stall
snapshots in `loose21-f1/` and `loose21-f2/`).

A fresh build of the same source (DLL SHA-256 prefix `7b5a54155f699762`) also
passes the earlier suites: `repeat` on 2-1 (three pairs, 58.9 s), `far` and `safety`
on 4-2, all four [preparation cases](preparation-regressions.md), and all ten
default `task_audit.py` cases. Evidence: `.local/regressions-26cfc64/`.
`python3 scripts/check.py` passes 2,858 core/assembly assertions, 335 navigation
checks, 693 executor checks and 111 Python tests with no simulator running. While a
native simulator ran concurrently, the wall-clock-budgeted `HeightOptions` throw
check failed once; it passed on every rerun without that load.

## Repeat the audit

```sh
python3 scripts/native_simulator.py --runtime .local/raft-audit start --level s_rapids_2_1 --port-base 26602 --fps 60
python3 scripts/raft_handoff_audit.py repeat --runtime .local/raft-audit --port-base 26602 --output .local/raft-audit-repeat
python3 scripts/raft_handoff_audit.py far --level s_rapids_4_2A --runtime .local/raft-audit --port-base 26602 --output .local/raft-audit-far
python3 scripts/raft_handoff_audit.py safety --level s_rapids_4_2A --fps 20 --runtime .local/raft-audit --port-base 26602 --output .local/raft-audit-safety
python3 scripts/raft_handoff_audit.py edge --level s_rapids_4_2A --fps 20 --runtime .local/raft-audit --port-base 26602 --output .local/raft-audit-edge
python3 scripts/raft_handoff_audit.py thrown --layout full --level s_rapids_2_1 --timeout 125 --runtime .local/raft-audit --port-base 26602 --output .local/raft-audit-thrown
python3 scripts/native_simulator.py --runtime .local/raft-audit stop
```

`thrown` requires three repetitions and six distinct surviving handoffs before
its timeout; use `--layout board|counter|loose|full` and either level.

`repeat` requires three complete pairs and six distinct surviving floor deliveries.
`far` stages P1 at a far supported initial pose; `safety` uses a single chopped
potato with the chefs at their normal spawns. `latency` supplies one raw potato.
`edge` selects a clear native rounded-contact segment beyond an open floor edge,
stages a supported initial P2 pose, and invokes the production strict throw
approach through the headless-only `d0000` fixture. It requires rejection before
movement and observes eight further seconds for a fall or respawn. Edge fixture
selection/staging is paused, then normal moving-floor execution resumes before
the measured request. The controlled 4-2 open-edge case passes at 60 and 20 FPS.
A separate 2-1 search did not find a clear supported start for this fixture;
that search is not counted as a passing edge test. Normal handoffs pass on both
layouts at 20 FPS, and both three-pair runs exercise 2-1 at 60 FPS.
