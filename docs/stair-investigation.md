# Native stair traversal investigation

The regression is the native `s_wizard_school_3_4` kitchen: Player 2 starts on
the lower deck, navigates to Chocolate upstairs, then to Egg on the lower deck
without a reset between legs. The original `60bb718` build completed zero of ten
round trips at requested 60/20 FPS. See the baseline in `native-simulator.md`.

## Findings from the installed game and traces

The installed `Assembly-CSharp.dll` was inspected locally, including
`GroundCast.FindGround`, `GroundCast.ProcessGroundHit`,
`ClientPlayerControlsImpl_Default.Update_Movement`,
`ProgressVelocityWrtFriction`, `ApplyGravityForce`, and `OnStepCollision`.
The original assembly SHA-256 remains
`8d548c632f48b07883e4e9861ddb834588f6edca9ff56920316daf5e33647159`.

Native movement normalizes the input axes. Reducing their magnitude cannot slow
walking. The game expresses movement in a basis derived from the current ground
normal and retains velocity along that normal. Stair lips therefore produce
vertical motion, including brief loss of native ground contact. GroundCast runs
in Update and accepts contacts within 0.3 units of the capsule; its cached result
can describe a different physics pose from the body the navigator sees.

The actual stair colliders are overlapping treads and rotated ramp boxes. The
captured `Step` and `Wedge` objects do not have the native `Steppable` component.
Their live transforms can change. Recognizing their names as steppable, ignoring
those colliders, or using a fixed level-specific waypoint list would not model
the observed geometry correctly.

The failures had several independent causes:

1. **Catalogue reads changed active navigation.** The running harness polls
   `available_actions`. That read previously refreshed the graph and applied a
   new plan even during a native stair hop, bypassing Tick's recovery checks.
   The stale settlement detail in terminal `not_grounded` traces was a clue.
   Removing this side effect produced five of five successful climbs and two
   complete round trips in the next five-run 60 FPS batch.
2. **The planner used ramp side contacts.** A sphere can be supported near a
   ramp's outside edge while the next native movement intersects the adjacent
   tread's side. Standability alone supplied too little lateral clearance.
3. **Slope continuity was stricter than native sphere-query precision.**
   Reconstructed supported feet and their reported normals sometimes disagreed
   by fractions of a millimetre at rounded ramp/tread joins. A 0.0001-unit
   tangent bound rejected these contacts even when the capsule sweep was clear.
4. **Recovery assumed all native depenetration points upward.** A shallow tread
   lip can have a predominantly sideways minimum separation vector. Rejecting
   that vector immediately stopped an otherwise recoverable landing.
5. **Native grounding and geometric clearance used inconsistent poses and
   tolerances.** A stale non-null ground contact resumed steering while the
   current body was too high above support. Conversely, a stale null contact
   stopped recovery when the body had landed 2–3 cm inside the allowed capsule
   skin. At 20 FPS these cases crossed the old thresholds in one Update.
6. **Route checking and command checking are different operations.** A valid
   polyline can produce an unsafe full-window heading near a ramp edge. There
   was no local alternative-direction search before stopping the action.
7. **Neutral recovery created avoidable pauses.** Native contact can remain stale
   while the capsule is above a connected stair corridor. Waiting for its contact
   distance to shrink produced pauses of up to 0.21 seconds in a later successful
   batch. Native contact must inform velocity prediction, while independent live
   geometry decides whether continuing movement is clear.
8. **Ownership and movement expiry were coupled too closely.** A captured 166 ms
   scheduling gap exceeded the 150 ms ownership lease. Movement had already
   expired safely, but ownership loss unnecessarily cancelled the whole request.

## Implementation

Catalogue reads now return the active action's existing observations; Tick owns
its planning and recovery lifecycle. Regression coverage keeps the harness poll
in the middle of an airborne recovery and verifies the action survives.

Ramp-box geometry reserves a full chef footprint laterally in planned nodes.
Actual-body connectors use the physical capsule/support checks when entering or
exiting that interior route; the planner's extra margin is not an invisible wall
that invalidates a safe native exit. Both use current collider transforms;
the implementation has no kitchen, collider-name, or coordinate checks.

Sloped contact continuity permits up to 0.0025 units of tangent-query error.
Flat-to-flat discontinuities and near-flat mesh seams keep their strict checks.
The full capsule sweep, support limits, hazards, and native Steppable requirements
still apply. Recorded box fixtures now expose their real oriented box geometry
to the production clearance check.

Landing recovery independently probes current support, even when native contact
is non-null. It retains recovery above the native contact range and through a clear
landing within the same collision skin used by its sweep. Shallow lateral penetration
is recoverable only for ground-layer contacts, within a quarter of the native
capsule radius plus the collision skin, with a clear resting pose above and a supported, unobstructed
connector to the native separation's projected landing. Deep penetration, walls,
missing floors, and unsafe landing connectors remain rejected. The bridge only
issues input; it does not relocate the chef or change native physics.

If the nominal full-window steering direction is unsafe, the controller tests
nearby directions in increasing angular order. Each alternative must advance
toward the current waypoint and pass the same full-window support/capsule checks.
If no direction passes, the action still stops. Regression coverage checks both
a safe local turn and an obstruction covering every candidate direction.

For short hops above a continuously supported corridor, `following_support` checks
both flat input and the game's slope-relative velocity, including its retained
normal component. It sweeps an enlarged capsule from the floor up through the
current body and upward-motion envelope. Previous horizontal velocity is checked
separately. Low ceilings, holes, sharp momentum changes, excessive frame intervals,
external flow, and slippery/sliding surfaces reject continuation. Near the final
stance it uses the existing neutral arrival checks. Penetration recovery stays
neutral; no body transform or physics velocity is changed by the bridge.

Walking ownership now lasts at least 500 ms, independently of the short checked
movement window. Inputs still expire at the original deadline; tests reproduce
166 ms of jitter, successful renewal, and eventual ownership revocation.

The simulator runner now includes per-leg motion summaries: native decision
frames, neutral recovery episode count/duration, local steering adjustments,
first/last body positions, maximum decision gap, and longest horizontal stationary
interval (at most 0.1 units/second during walking, excluding final alignment).
Neutral input duration is not itself a physical stall measurement; the raw
positions and velocities remain in each trace. Alignment now has explicit trace
records so dispenser-facing time is separate from stair motion.

## Evidence and remaining acceptance work

Local artifacts are ignored by Git because they contain game geometry:

- `.local/stair-observation-60`: five successful climbs; two successful round trips.
- `.local/stair-curvature-60` and `.local/stair-interior-60`: isolated geometry
  iterations, with remaining failures preserved.
- `.local/stair-landing-60`: eight of ten round trips; exposed stale grounding
  and the steering clearance boundary.
- `.local/stair-stable-60`: nine of ten round trips; exposed insufficient
  separation between planning and actual-body clearance.
- `.local/stair-reserve-20`: eight of ten round trips; exposed the inconsistent
  landing-skin threshold.
- `.local/stair-skin-20`: ten of ten complete round trips with the corrected
  landing-skin threshold.
- `.local/stair-skin-60`: nine of ten complete round trips. The failure was a
  safe bottom exit incorrectly rejected by the extra planning margin. This
  motivated keeping that extra margin out of actual-body connector validity.
- `.local/stair-skin-motion-summary.json`: both batches' reconstructed motion
  summaries from the deduplicated native traces.
- `.local/stair-airborne-60`: five of five round trips with the first continuation
  prototype; a stale native contact still caused a 0.21-second climb pause.
- `.local/stair-airborne-20`: nine of ten round trips; the remaining shallow tread
  penetration exposed a missing collision-skin allowance in lateral recovery.
- `.local/stair-continuous-20`: fifteen of fifteen round trips after checking the
  native slope velocity and applying consistent contact skin. The longest neutral
  recovery command lasted 0.083 seconds. Cross-rate acceptance runs follow below.
- `.local/stair-*-check.log` and `.local/stair-*-navigation.log`: automated checks.

Successful runs still include short neutral recovery episodes. Reliability and
smoothness require further native checks at multiple Update rates; the earlier
successful arrivals are not evidence that the full objective is finished.

## Cross-rate native results for `6269635`

The final implementation batch completed **50/50 round trips (100/100 legs)**.
Every leg ended with native selection, settled speed, and `readyForAction`; no
teleports, retries, or scene resets occurred between the legs of an attempt.
The private runtime used the original game assembly, Null graphics, automatic
native physics, and a 0.02-second fixed timestep. Requested Update rates do not
promise a constant measured frame rate under concurrent CPU load.

| Requested FPS | Round trips | Longest horizontal pause | Longest neutral recovery |
| --- | ---: | ---: | ---: |
| 20 | 10/10 | 102 ms | 94 ms |
| 30 | 5/5 | 61 ms | 72 ms |
| 60 | 15/15 | 81 ms | 54 ms |
| 120 | 5/5 | 65 ms | 81 ms |
| 60 — three round trips per load | 15/15 | 125 ms | 68 ms |

Reports and raw traces are in `.local/stair-final-{20,30,60,120,repeated}/`.
`.local/stair-final-validation.json` checks native runtime identity, per-leg
readiness, deck heights, unchanged station identities within each attempt, and
the runtime plugin against the built DLL (SHA-256
`ce0f1d63d2210f9cca44afaf65d04096699b30f1fdc943c7fe6d8077f7549223`).
`.local/stair-final-analysis.json` retains per-leg motion analysis. Maximum
backward excursion along the staircase was 0.170 units, without a stuck edge.

Automated validation passed 820 core assertions, 182 navigator integration
checks, 181 executor checks, and 79 Python tests. These fixtures remain separate
from the native game evidence.

The reliability criterion now passes in these samples. The strict smoothness
criterion still deserves investigation: the longest pause (125 ms) was a shallow
descending tread penetration followed by a 60 ms decision gap and a temporarily
zero native horizontal velocity. This is not evidence of perfectly continuous
motion; further input-timing diagnostics are needed before calling it flawless.

A concurrent teleporter investigation uses its own runtime under
`/private/tmp/sous-chef-teleporter-runtime` and ports 25712–25714. Later stair runs
use the existing private stair runtime on ports 24702–24704, through the same
installed BepInEx wrapper after checking those ports are free. The local helper
`.local/start_stair_isolated.py` avoids the launch CLI's process-name-only guard;
it retains the same native game, scene lifecycle, save isolation and physics.
The other investigation's process is not stopped or reset by the stair tests.
