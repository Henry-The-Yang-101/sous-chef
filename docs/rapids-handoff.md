# Story 2-1 handoff and loose-item navigation

The native story catalogue identifies level 2-1 as `s_rapids_2_1`
(`Text.Menu.Level07`). P2's chopping raft moves separately from P1's supplies.

The later [raft regression pass](raft-handoff-regressions.md) expands floor
targets across the receiving support, fixes moving release poses and repeat
stalls, and removes repeated arrival settling before work. The audit below
records the earlier 0.8.3 behavior and its bounded near-player landing policy.

## Changes

Handoff ranks currently usable choices: a counter accessible near the human,
then delivery beside/to the human, then a verified conveyor or moving counter.
An unfinished counter preview or incomplete human walking search cannot veto an
already feasible player delivery. Moving the chef without changing their held
geometry no longer discards bounded throw-planning progress. Every positive
preview and actual release still checks fresh physics and walking access.

When a catch or close drop is unavailable, tasks can throw to supported ground
beside the human. The shared native throw planner checks several landing spots,
their complete item footprint, release uncertainty, actual item collision shape,
native launch velocity, drag, gravity, wind, obstacles and hazards. It retains
the selected landing offset while walking, aiming and releasing. Floor receipt
requires the exact food to settle near the human on supported, safe ground; its
floor mask comes from the receiving human even without a prior close-drop plan.
Existing direct `throw_to_player` calls keep their native catch semantics.

A continually moving raft can leave a distant supplied ingredient out of the
partial catalogue. For an incomplete search, food on the chef's exact native
floor support can request the pickup's existing bounded active planner.
Certified sources take priority. The shared-floor check is only a planning hint:
the pickup still needs a fully checked walking route and fresh native selection
before pressing a button. A blocked pickup can reselect a source before pressing.

Unattached native pickup objects and their collider descendants are excluded from
walking geometry stamps, support probes, body overlap checks and movement
sweeps. Rolling food therefore cannot restart discovery. Food remains a pickup
target and participates in throw collisions. Attached objects, kitchen geometry,
NPCs and hazards retain their collision checks. Existing navigation worker
offloading is retained; throw preview keeps the existing bounded slice budget.

## Native audit

`scripts/rapids_handoff_audit.py` runs the following isolated 2-1 cases:

| Case | Checks |
| --- | --- |
| `default` | Supplied raw chicken is picked up, chopped and returned across the gap under one task request. |
| `busy` | P1 keeps their original held potato and faces away; chicken delivery uses the ground. |
| `mixed` | Three supplied ingredients (chicken, potato, chicken) are chopped and delivered as distinct native food objects. P1 picks up one earlier delivery after the landing area crowds; the same P2 request resumes. |
| `counter` | With P1 initially on the chopping raft, an accessible counter wins over floor/transport delivery. |
| `clutter` | A clear walking segment stays clear after three real loose ingredients are placed across it; P2 walks through to the destination with empty hands. |

Setup uses explicit native initial conditions: supplied ingredients on empty
holders, a busy P1's held ingredient, loose ingredients and supported chef poses.
The mixed case also simulates one external human pickup of already delivered
food within reach, using native carrying callbacks. It does not alter P2's task.
All measured P2 chopping, walking, pickup, aiming and release use production logical
inputs. No task progress, placement, ownership or motion is forced during those
measured actions. The runner accepts usable partial catalogues on the moving
kitchen instead of requiring the whole map to stop changing.

For an already-started isolated simulator:

```sh
python3 scripts/rapids_handoff_audit.py default --runtime .local/rapids-handoff-native --port-base 26442 --output .local/rapids-final/default
```

Replace `default` with each case above. Output includes control receipts,
independent full-state snapshots, fixture records, request/object IDs and the
loaded binary hash. Artifacts for this pass are under `.local/rapids-final/`.
The baseline reproduction is under `.local/rapids-baseline/`: P2 chopped chicken
then remained in `waiting_for_player_handoff` through the 60-second deadline.
All five cases passed against the same 0.8.3 binary. The mixed case verified
resumption after human collection. See `docs/rapids-validation.json` for the
binary hash and per-case results. `python3 scripts/check.py` also passed.

Executor regressions additionally verify translating-chef preview progress,
occupied human hands, supported centers with unsupported footprint edges,
obstructed arcs, hazardous landings, independent floor-receipt masks, moving
loose-item graph continuity and restoration of collision checks after attachment.
