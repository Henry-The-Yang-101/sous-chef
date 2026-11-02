# Headless stair motion simulation

Run without launching, restarting, or connecting to Overcooked:

```sh
python3 scripts/simulate_navigation.py
python3 scripts/simulate_navigation.py --trace-dir .local/navigation-simulation
```

Add `--verbose-trace` to retain the controller's bounded execution history in
each frame. This produces substantially larger files. The suite also runs in
`python3 scripts/check.py`.

This test compiles the production `NavigationActions` and `WalkPhysics` against
the same game-interface test doubles as the navigation integration tests. Its
output drives a dynamic capsule through 69 recorded boxes from the Chocolate
stair route in `s_wizard_school_3_4`. The fixture includes the lower floor, stair
treads and wedges, upper landing, counters, and barriers. It preserves collider
transforms, ground-layer membership, and the player's collision-layer filtering.

Unlike the static geometry tests, this simulation closes the control loop:

- Navigation receives the current body pose and issues time-limited input.
- Native-style locomotion normalizes input, projects it into the ground tangent
  basis, retains normal velocity, and accumulates the game's two gravity forces.
- Movement updates run at 60 Hz and capsule integration at 50 Hz, independently
  of the render/controller rate. Ground observation can precede or follow the
  controller decision. The delayed-order case consumes the previous input and
  ground sample in native movement before the current controller decision.
- Solid oriented boxes resolve capsule contacts, including simultaneous tread
  and wedge penetration. Partial correction tests residual contact across frames.
- The next controller tick receives the resulting pose and velocity. A positive
  scenario must reach `arrived` from one request, without retries or position edits.

The wall-clock search budget is disabled in this deterministic simulation (the
production default remains 12 ms and the graph's node cap is unchanged), so CPU
load cannot turn the same simulated frame into a different reachability result.

The 12 cases include jittered 20/30/60/120 FPS with both ground-update orders,
the captured multi-contact state, slower contact resolution, initially missing
stairs, and stairs disappearing during an active route. Contact-state cases seed
the recorded penetration into an already-started action before any physics
correction; they assert neutral settling immediately. Missing/retracting stairs
must stop rather than report arrival.

The first closed-loop run reproduced `route_blocked`. Replanning from the actual
pose before abandoning a displaced connector allowed it to complete. Additional
regressions distinguish multiple upward ground contacts from wall obstruction,
and use numerical error alone in the continuous-support height bound: the old
0.025-unit allowance could certify small discontinuities differently depending
on the segment's sampling phase. The delayed 20 FPS case also caught an overbroad
settling rule: braking on proximity distance alone repeatedly returned the chef
to the same tread. Settling now requires lost native contact or proven capsule
penetration. A landing search uses GroundCast's downward probe range, not the
unrelated maximum step-up height.

## Limits

The contact solver is an independent CPU model, **not Unity PhysX**. It uses the
recorded static stair geometry plus explicit removal cases; it does not recreate
the kitchen's complete animated staircase cycle, multiplayer synchronization,
native dispenser selection, or cooking. The navigation goal is the recorded
Chocolate approach position, not a simulated pickup. A passing suite establishes
repeatable controller behavior under these conditions, not universal in-game
correctness. No new live verification is implied by these results.
