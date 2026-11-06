# Repeated preparation: native checks

Story 2-1 is `s_rapids_2_1`; story 4-2 is `s_rapids_4_2A`. The simulator runs
the installed Unity player and native physics. All cases use one accepted task
and lease. The audit distinguishes a proposed throw from a delivery: it records
the exact instance ID when the lane's delivered count advances.

| Case | Setup and required outcome |
| --- | --- |
| Catch and backlog (2-1) | Two chopped potatoes on P2's counters. While P2 is walking to pick one up, a fixture puts a chopped chicken in P2's empty hands. An indefinite chopped-potato/chicken order must deliver that chicken and **both original potatoes**, with no raw food supplied. The fixture reproduces the native carrier change, not the flight of P1's throw. |
| All available (4-2) | Finite order: raw lettuce, chicken, tomato, twice. All are obtainable. Require six distinct confirmed handoffs in exactly that order twice. |
| Skip and resume (4-2) | Same order, but with no accessible chicken. Require lettuce, tomato, lettuce, tomato before supplying either chicken. Both chickens must then arrive, and the request must stop at exactly two of each. |
| Single fryer (4-2) | P1 starts on P2's raft holding the spare empty fryer basket. Supply two **raw** chickens. During the one usable basket's native cooking interval, a snapshot must show the other raw chicken being worked on a board. Check the two delivered items' native contents at receipt: each must be cooked and unruined. |
| Blocked handoff (4-2) | Request an empty fryer basket and raw lettuce with P1 on the other raft. P2 must park the nonthrowable basket, give P1 lettuce, then retrieve and deliver the **same** basket after P1 moves within reach. |
| Ready before chopping (2-1) | Request potato before chicken, but provide one raw potato and two chopped chickens. Both exact chickens must arrive before any chopping starts. Run with the raw potato on a board, already held, and with the ready chicken arriving during raw placement. |
| Resume board (2-1) | Request potato before chicken, with raw potato on a counter and chicken already on a board. The first operation must work the occupied board; chicken must arrive before potato. |
| Raw catch during work (2-1) | Once chicken has native chopping progress, inject another raw chicken into P2's hands. Require continued progress on the original board and a confirmed chicken handoff. This verifies the original work survives; it does not require the caught raw item to remain held across native use release. |
| Bounded backup (4-2) | One usable fryer, two raw chickens and one raw potato. Chop potato while the first chicken cooks, then wait with that backup. Do not chop the second chicken before collecting the first batch. Load the prepared potato next and verify both cooked handoffs. |

The cooking and handoff cases stage P1 where needed. P2's movement, work,
placements, transfers and passes all use production inputs. No fixture advances
the cooking clock, writes chopping progress, retries an accepted task or moves P2.

To reproduce in an unused isolated runtime:

```sh
python3 scripts/native_simulator.py --runtime .local/scheduler-check start --level s_rapids_2_1 --port-base 26002
python3 scripts/preparation_scheduler_audit.py --runtime .local/scheduler-check --port-base 26002 --output .local/scheduler-results
python3 scripts/native_simulator.py --runtime .local/scheduler-check stop
```

Use `--case waiting_frames` for the separate frame-rate comparison. This case
runs the same two-ingredient indefinite order on 4-2 with neither source
available to P2. It samples the native render-frame counter over two ten-second
windows, with five status polls per second, the worker enabled, and no full-scene
or gameplay stream. The active comparison uses the `repeat --cycles 1` case in
`scripts/raft_handoff_audit.py`, which subscribes to gameplay telemetry on both builds.

## Results, November 5, 2026

All five behavior cases passed. The finite lists produced ABCABC when stocked
and ACACBB when B arrived late. After adding the unpressed-walk progress check,
the catch, single-fryer and blocked-handoff cases passed again. In the single-fryer
snapshot, raw chicken `-24904` was on the board while basket `508788` was cooking.
The first portion arrived on plate `510256`; the second arrived in the original
basket, with native state `Completed`. In the catch trial, all three receipts
matched the three supplied food identities. In the blocked-handoff trial, the
resumed basket was the same one P2 had parked.

The full repository check passed, including native API/worker checks, navigator
and action tests, and 131 Python tests. Full native traces and snapshots are
under `.local/preparation-scheduler/{tested,confirmed}/`; the two finite-order
cases are in `tested`. The original 2-1 timing fixture in that directory could
not start an empty-raft request on the precondition it used. The matched timing
runs below use 4-2, where P2 has local salad supplies and can accept the order.

| Matched native workload (60 FPS requested) | Before, `14b0cfe` | After, `aeac146` |
| --- | ---: | ---: |
| 2-1: chop and hand off one potato/chicken pair, gameplay telemetry enabled | 46.6 FPS | 46.7 FPS |
| Same pair: 95th percentile of sampled frame intervals | 37.5 ms | 37.5 ms |
| 4-2: waiting for two missing supplies, status only | 53.8 FPS | 52.0 FPS |

Both active runs delivered the two foods onto P1's floor. These were short,
sequential headless runs with a single simulator and the worker enabled, not
measurements of rendered gameplay or proof of a speedup. The active interval
percentile averages frames between telemetry samples. Raw comparisons are in
`.local/preparation-scheduler/{baseline,committed}-{active,waiting}/`.

Dispatch reuses one live inventory observation per decision, checks each listed
entry at most once per selection, and sleeps between unsuccessful selections.
Route-distance lookups use instance IDs instead of building target descriptions.
Native carrier, board, cooking and placement checks remain on Unity's thread.
The existing secondary process handles eligible graph expansion, throw-preview
batches and response encoding; choosing among at most ten entries adds no IPC
wait to the game loop.

## Ready-food and raw-stock priority follow-up

Ready handoffs now outrank new chopping across the entire request, even when
P2 already holds useful raw food. Selection also prefers native progress and
occupied boards over starting fresh copies, and loads prepared food into free
equipment before producing more. A prepared backup applies across entries,
preventing each ingredient from building its own queue behind the same full
equipment. Native compatibility, cooking deadlines and finite demand still
gate eligibility before priority is compared.

The three ready-food cases, ordered ABCABC, and single-fryer overlap passed in
`.local/ready-priority/audit/`. The efficiency changes passed ordered ABCABC and
single-fryer overlap again in `efficiency/`. Final checks in `final/` cover
existing board work, raw catch recovery, the bounded backup, ready food arriving
during placement, and single-fryer overlap. In the bounded-backup run, potato
`-45020` was chopped while basket `535360` cooked chicken; the first chicken
arrived on plate `536828`, and the cooked potato arrived in that basket.

The action regressions additionally cover retaining a partially chopped copy
when an identical raw copy is held, loading already prepared food before more
chopping, inaccessible ready food, and refusing new raw stock with a full pan
and a prepared backup. Repository checks and the action suite passed. Initial
efficiency-fixture failures are retained in `efficiency/`: they assumed a raw
object stayed on its board after conversion, required a catch to remain held
through native use release, and requested a third empty counter where only two
existed. The final fixtures use observed native identities and available holders.

Selection uses two bounded passes over at most ten entries, reusing the frame's
inventory and resolving each source once. Rechecking before work happens only
at an action boundary. There are no new per-frame scene scans or worker waits;
the existing secondary process continues handling graph searches, throw batches
and encoding. These behavior runs are not a matched live-game FPS benchmark.
