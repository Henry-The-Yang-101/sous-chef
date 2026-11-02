# Native throw corrections — 2026-11-02

The geometry rejection identified by the [baseline audit](throw-audit.md) is
fixed. Throws now use the actual native item collider and physical receiving
surface. Player passes also check the game's forward catch scan, which was
missing from the original prediction.

## Changes

- Box, sphere and capsule queries preserve the collider's center, orientation
  and scale. Ordinary ingredients no longer hit an arbitrary 0.6-unit radius
  limit or acquire an oversized sphere extending below their base. Nonfinite,
  degenerate and unsupported shapes still fail closed.
- Collision and wind filtering use the native detached item layer. Receiver
  bounds exclude colliders that cannot collide with that item, including tall
  chef-only blocks. Catch triggers do not become physical flight obstructions.
- Station targeting solves the descending crossing of the receiving top using
  the item's actual bottom. It accepts the native receiving footprint rather
  than requiring the whole item to fit in a tiny inset. Exact body sweeps still
  check walls and neighbouring stations.
- Player predictions search for a real catch window along the flight: native
  minimum flight time, free hands, catch layer, distance, velocity angle and
  `PlayerControls.ScanForCatch`'s forward PI/2 arc. The closest-point calculation
  mirrors the installed game's collider-specific helper. Stance search includes
  earlier catches, not just the descending ring at the receiver's foot height.
- Candidate work continues across Updates with a 25 ms slice and a two-second
  accumulated planning-work bound. One Update cannot run two slices for the same action.
  An unfinished cold search no longer becomes `no_reachable_throw_position`.
  Planning owns the normal control lease, keeps input neutral, and is cancellable.
- Walking stances reserve horizontal clearance for the navigator's 0.2-unit
  arrival tolerance. Generated throw points enforce that tighter bound rather
  than the station-arrival hysteresis. Lateral error tapers toward the receiver;
  fixed-force range error remains reserved at landing. Lateral candidates cover
  narrow release corridors. Aim and release recheck the actual item pose.

All movement, pickup, use and release still go through ordinary P2 controls.
There are no test-only position writes, replacement physics, custom throw forces,
forced item attachments or P1 inputs in these regressions.

## Native regression suite

The checked-in [current cases](../tests/actions/native-throw-regressions.json)
cover successful throws/passes for all eight original ingredient types, requested
20/60 FPS, elevated sources, native input comparison, empty hands, non-throwable
items, absent/occupied receivers, incompatible targets and interruption in flight.
Every successful delivery requires an exact server holder relationship in a fresh
snapshot, independently of the action receipt.

The final run passed **30/30 cases**, including **13/13 supported deliveries**
across Cheese_Raw, Meat, Lettuce, ChoppedBun, ChoppedCheese, Chocolate, Egg and
Flour. Requested rates were 20 and 60 FPS. Both stop and abrupt disconnect were
observed after real native launch, and ownership was released in each case.

The complete check passed **825 core/assembly assertions, 207 navigation checks,
233 action checks and 90 Python tests**. The exact native-tested DLL was installed
for the next ordinary game launch, with the previous plugin/manifest backed up in
`.local/throw-fix-final-20261102/installed-before/`. The isolated simulators were
stopped after verification.

The three old Chocolate-kitchen player-pass fixtures remain as safety cases.
With the stationary P1's current facing, the search finds no supported release
stance with a clear native catch window. They must retain cargo rather than fire
a pass outside the forward scan. Counter deliveries separately exercise those
same ingredients, so rejection is not used as their geometry-success test.

The constrained Flour counter case allows either certified delivery or refusal
with cargo retained. A released miss fails. The narrow corridor is a feasibility
boundary, not an expectation that every kitchen destination accepts a throw.

The original [baseline cases](../tests/actions/native-throw-cases.json) are kept
for comparison, including their original player-pass expectations. Use the current
regressions for the corrected native behavior.

Results, the exact runtime fingerprint, and local trace/snapshot paths are in
[throw-fix.json](throw-fix.json). Raw game captures remain private under `.local/`.

```sh
python3 scripts/native_simulator.py --runtime /tmp/sous-chef-throw-fix start --port-base 27112 --level s_wizard_5_5 --fps 60
python3 scripts/throw_audit.py --runtime /tmp/sous-chef-throw-fix --cases tests/actions/native-throw-regressions.json --output .local/throw-fix-retest
python3 scripts/native_simulator.py --runtime /tmp/sous-chef-throw-fix stop
```

Use an unused runtime/port range and a new output directory. The runner now uses
native raw input as an independent baseline; a successful planner pass no longer
consumes the cargo before that baseline can run.

## Scope

This validates the recorded kitchens and cases with the original Unity/PhysX
runtime. Wind, moving-receiver mathematics and unsupported-state guards also have
executor tests; this run does not certify every moving kitchen, wind phase or
human change of direction after release. Native receipt remains the success
criterion, and stop cannot recall a projectile already in flight.
