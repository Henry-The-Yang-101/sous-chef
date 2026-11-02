# Native throw height tests — 2026-11-02

The new height suite passed **16/16 cases** in an isolated instance of the
original Unity/PhysX simulator, at requested 20 and 60 FPS. It includes 12 exact
item deliveries and four raised-target refusals with cargo retained. Each option
assertion checks both the target/player's `operations` and the action parameter
enum after its preview finishes. Receipts alone cannot pass a delivery: fresh
snapshots must show the exact item in the intended server holder.

| Scenario (each at 20 and 60 FPS) | Native result |
| --- | --- |
| Lower deck to raised right counter | Withheld; no reachable throw position. Current-pose probe rejects the receiving height. |
| Lower deck to raised left chopping board | Withheld; no clear supported release arc. Current-pose probe rejects the receiving height. |
| Level Cheese counter delivery | Offered and delivered; flight approximately 0.649 s. |
| Chocolate to lower counter | Offered and delivered; 1.883 m below release, flight approximately 1.194 s. |
| Flour to lower counter | Offered and delivered with the same measured height/time relationship. |
| Egg from lower deck to lower counter | P2 first climbs approximately 1.835 m to a reachable release stance, then delivers downward. |
| Egg to chef upstairs | P2 climbs approximately 1.33 m onto the stairs before passing; the predicted catch is about 0.4 m above release. |
| Lettuce from upper deck to lower chef | Current-pose sweeps meet the recipient's body before an acceptable native catch window. P2 descends approximately 1.4–1.6 m to a safe release stance and completes the pass. |

The final build also passed the existing **30/30 throw regressions**, followed
by **4/4 additional catalogue checks** for occupied hands and unsupported player
catch windows: **50/50 native cases in total**. Those four checks explicitly
confirm blocked passes are absent from both row operations and parameter enums,
then request them directly to verify the native guard retains cargo. The full
code check passed **825 core assertions, 207 navigation checks, 254 action checks
and 91 Python tests**. The action suite includes a target disappearing during a
pending preview and a nonthrowable nearby counter handoff.

The exact native-tested DLL was installed for the next normal game launch,
with the previous plugin/manifest backed up in
`.local/throw-height-final-20261102/installed-before/`. All simulators started
for this audit were stopped. The report records the matching installed/runtime
DLL fingerprint and the full verification log path.

These figures describe the held item's root height at release/catch, while
climbs/descents describe the chef's foot height. Station receiving height uses
the physical counter top and the item's native collider bottom. Player catches
use the native three-dimensional root distance, forward scan, velocity angle,
free hands and minimum flight time; a horizontal intercept alone is insufficient.

The native launch in these kitchens gains approximately 0.465 m above release.
A receiver on a 2 m raised platform cannot be reached directly from the lower
deck. A reachable higher stance can make a target available, but its complete
arc must also clear walls, barriers, neighbouring stations and the recipient.
Downward delivery has substantially more flight time and horizontal travel than
an otherwise similar level throw. There is no flat-distance approximation or
fixed height cutoff in option selection.

## Availability and planning

The catalogue now runs separate read-only instances of the production throw
planner. Only a valid arc with a certified walking route enables `throw_to` or
`throw_to_player`. Work is spread over Updates with a 4 ms search slice, stopping
at the first feasible stance. The two-second planning-work bound counts elapsed time inside search
slices rather than idle time between Updates. Normal execution
retains its 25 ms slices and chooses the least walking among valid candidates.
Individual native geometry queries/candidate work are atomic and may exceed a
slice; this is not a hard frame-time guarantee.

Pending/unknown plans stay out of the operation and parameter choices. Per-row
`throw` previews expose `pending`, `feasible`, or `unavailable`, plus the plan
or reason. `throwOptionsComplete` is separate from walking discovery's
`complete`. While observed, negative results are retried after two seconds.
Changing chef pose, facing, held item or receiver invalidates cached proofs;
positive arcs and routes are checked again when listed. Execution independently
replans and revalidates before releasing the item. Nearby counter/ground
handoffs retain their own physical checks.

The planner never issues input or moves anything during previews. Most native
height cases use only normal P2 actions. The two player scenarios explicitly
stage chefs at checked, supported initial poses using the simulator-only
`stage-player` fixture, preserving native physics and catch settings. Those
writes are recorded separately and happen before measured action execution.
P1 receives no input, no item is forcibly attached, and no gravity, throw force,
collision layer, timestep or game clock is replaced. `probe-throw` records a
read-only current-pose decision independently of alternate-stance search.

The [machine-readable report](throw-heights.json) records exact runtime/DLL
fingerprints, preview plans, current-pose diagnostics, actual release plans,
native launch callbacks, holder identities and private local evidence paths.
The [cases](../tests/actions/native-throw-heights.json) are reproducible with:

```sh
python3 scripts/native_simulator.py --runtime /tmp/sous-chef-throw-height start --port-base 27212 --level s_wizard_5_5 --fps 60
python3 scripts/throw_audit.py --runtime /tmp/sous-chef-throw-height --cases tests/actions/native-throw-heights.json --output .local/throw-height-retest
python3 scripts/native_simulator.py --runtime /tmp/sous-chef-throw-height stop
```

Use an unused runtime/port range and a new output directory. This verifies the
recorded kitchens and poses; moving kitchens, wind phases and a human changing
direction after release still require their own native scenarios. Unknown or
uncertified routes remain withheld, and execution can safely reject a listed
option if the world changes.
