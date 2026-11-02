# Native throw audit — 2026-11-02

This is the baseline captured before the fixes. See the
[current implementation and regression results](throw-fix.md).

**Baseline: ordinary airborne throws were blocked.** All 13 functional fixtures
returned `unsupported_throw_geometry` before releasing an item. A nearby counter
handoff succeeded, and ordinary native use input threw the exact cheese rejected
by the planner. The 38 rejection/ownership checks passed.

## Runtime and evidence

This used a newly started, isolated native Unity process in
`.local/throw-audit-20261102`, on ports 26912–26914. Its installed game assembly
was `8d548c632f48b07883e4e9861ddb834588f6edca9ff56920316daf5e33647159`;
the loaded bridge DLL was
`8c0ab9bf72ba9eac9c608705710ae93f5aee4ac10bee3a36524ed7dcc0ba478a`.
The production C# source was unchanged from `cdc4f8d`. Unity reported 2017.4.8f1,
Null graphics, paused/muted audio, automatic physics and a 0.02-second fixed step.
Requested Update rates were 60 and 20 FPS.

The main fixtures use story 3-2 (`s_wizard_5_5`) and the Chocolate stair kitchen
(`s_wizard_school_3_4`). Each resets the kitchen, discovers current IDs, obtains
cargo through normal pickup/placement/work, and makes one measured request.
The runner records control traces, catalogues, and complete before/after snapshots.
Snapshot verification skips the initial cached capture and waits for a new one.
Rejected requests cannot inherit an earlier successful action's receipt.

Results and their local evidence paths are summarized in
[throw-audit.json](throw-audit.json). Raw captures and logs remain under the
isolated runtime; game geometry/assets are not included in this report.

## Results

| Case group | Result |
| --- | --- |
| `throw_to`: empty counters, raw/chopped cheese and egg; cheese at 20/60 FPS | 0/4 deliveries; all rejected before release |
| `throw_to_player(1)`: eight item types, including elevated sources; cheese at 20/60 FPS | 0/9 airborne passes; all rejected before release |
| Empty hands, each operation | 2/2 correctly returned `hands_empty` |
| Non-throwable pan/plate, each operation | 4/4 correctly returned `item_not_throwable` |
| Absent local players 3/4 with held cheese | 2/2 correctly returned `target_unavailable` |
| Client validation, malformed wire requests, unknown/stale IDs, ownership and cross-client stop | 30/30 checks passed |
| Native use-button comparison with the same rejected cheese | Exact item observed detached and `ServerThrowableItem.m_flying == true` |
| Nearby counter handoff | Exact cargo observed on the named counter; P2 empty-handed |
| Stop during the nearby handoff | `cancelled / stopped`, ownership released; already placed cargo remained on the counter |
| Abrupt disconnect during the nearby handoff | `cancelled / control_lost`, ownership released; exact cargo retained by P2 |

The 13 ordinary failures retained the exact cargo in P2's authoritative server
carrier and returned blocked receipts. There were no successful airborne receipts
or projectile releases in those cases. The audit retains `throw_received` as the
functional expectation, so its main command exits **2** while this defect exists.

## Why ordinary items are rejected

[`ItemBody.Radius`](../src/Plugin/Actions/ThrowFlight.cs) adds the collider's
distance from the item pivot to its enclosing radius, then rejects values above
0.6 units. The real, held native shapes produce these values:

| Item | Calculated radius |
| --- | ---: |
| Cheese_Raw | 0.7299 |
| Meat | 0.6720 |
| Lettuce | 0.7600 |
| ChoppedBun | 0.6339 |
| ChoppedCheese | 0.7023 |
| Chocolate | 0.6292 |
| Egg | 0.7600 |
| Flour | 0.9009 |

`ThrowPlanner.Prepare` therefore rejects every tested ingredient before stance
search, aim, release, or catch checks. The exact raw cheese successfully launched
through native use input, without changing its geometry or physics. This isolates
an overly restrictive geometry admission check; it does not establish how accurate
the trajectory model would be after that check is corrected.

The same helper is used by nearby ground-drop planning. Successful counter
placement bypasses it. Raising the limit alone has not been tested as a fix.

## Nearby handoff setup and coverage limits

The optional [handoff fixtures](../tests/actions/native-handoff-cases.json) use
ordinary P2 navigation and short movement inputs to approach a counter. Native
body collisions nudge the stationary P1 during setup; P1 inputs are never changed.
The initial approach can end in `no_progress`, so it is explicitly allowed as
fixture setup. It is not counted as successful autonomous navigation.
Measured handoff starts with both chefs stationary and current native selection.

Counter handoff succeeded in the standalone fixture and a separate replay.
This stages an item within P1's reach; it does not put the item into P1's hands.
The stop test confirms cancellation cannot undo an already applied button edge.
Setup is timing-sensitive: another replay left P1 1.6466 units from the counter,
outside the 1.6-unit limit, and fell back to the same geometry rejection before
the requested disconnect phase. That attempt provides no disconnect coverage.
A separate fresh replay reached the phase, disconnected the owning TCP socket
without sending release, and observed `control_lost` with the item still held.

Airborne catch angles, moving/occupied-handed receivers, wind compensation,
obstruction clearance, missed catches, and stop/disconnect during flight remain
**unverified** because the geometry guard prevents reaching those branches.
Nearby ground drops also remain unverified. Height differences and the lower
frame rate were exercised only through the initial rejection, not through flight.

## Repeat

Start a separate runtime with an unused port range, then run the checked-in cases:

```sh
python3 scripts/native_simulator.py --runtime /tmp/sous-chef-throws start --port-base 26912 --level s_wizard_5_5 --fps 60
python3 scripts/throw_audit.py --runtime /tmp/sous-chef-throws --cases tests/actions/native-throw-cases.json --output .local/throw-retest
python3 scripts/native_simulator.py --runtime /tmp/sous-chef-throws stop
```

Use a new output directory for each run. Optional proximity/interruption cases
use `tests/actions/native-handoff-cases.json`; inspect their recorded initial
positions before attributing a failure to the handoff itself.

The unchanged production build passed 823 core/assembly assertions, 207 navigation
checks and 205 action checks. All 90 Python tests, including the audit's stale
receipt, cached snapshot and exact object identity regressions, passed. These
checks coexist with the native functional failures above.
