# Assemble an assigned dish

`assemble_dish` produces one dish for one exact native ticket. The caller owns
intent and allocation. The deterministic executor chooses among those resources,
uses ordinary P2 inputs, and reports success only when the selected plate's
composition and native plating step match the original recipe. Serving is a
separate task. There is no hosted Jev call or automatic order selection.

## Caller contract

```python
catalog = chef.available_actions()
# Select this ticket; do not assume the first ticket is the intended order.
order_id = selected_ticket['id']  # catalog['assemblyOrders']
chef.assemble_dish(
    order_id,
    bases=[plate_a_id, plate_b_id],
    components=[fish_id, seaweed_id, rice_id],
    equipment=[pot_id, burner_id],
)
```

`assemblyOrders` exposes opaque live ticket IDs, recipe IDs/names, plate type,
remaining seconds, and the native ingredient/mixing/cooking tree. Ingredient IDs,
quantities, cooking methods and processing boundaries are retained. Expired,
replaced or removed tickets stop the request with `assembly_order_unavailable`;
it never switches to an identical later ticket.

All resources are IDs from the current catalogue:

- `bases`: 1–24 alternative plates and optional preparation bases such as buns or
  tortillas. Include at least one compatible plate. A suitable partial plate is
  preferred, then distance. The selected plate stays pinned. Partial preparation
  bases can contribute their existing exact components.
- `components`: up to 24 exact prepared ingredient/container instances. These are
  **item IDs**, not dispenser selectors or station IDs. An assigned raw rice,
  flour or egg is usable when that is the recipe's required input; unchopped
  workable stock is not substituted for its finished ingredient.
- `equipment`: up to 24 assigned vessel and processing-station IDs. For example,
  assign both the bowl and mixer, and the pan and burner. Native compatibility
  chooses among these; unassigned equipment is never taken. Empty storage
  countertops are chosen by the existing safe-parking rules.

An object may appear in only one allocation group. A clean-stack allocation pins
its current top plate. A later top plate cannot replace it. Missing or temporarily
inaccessible assigned objects cause bounded periodic waiting. They never authorize
an unassigned replacement or dispenser spawn. Explicit allocation may reuse
objects previously handed off by preparation; ambient lookalikes remain untouched.

The generic client equivalent is `run_task('assemble_dish', order_id=...,
bases=..., components=..., equipment=...)`. Repetitions must be `1`. Wire grammar:

```text
task REQUEST assemble_dish 1 SELECTOR_ORDER BASE_COUNT BASE_IDS... COMPONENT_COUNT COMPONENT_IDS... EQUIPMENT_COUNT EQUIPMENT_IDS...
```

`SELECTOR_ORDER` uses the existing `s` + unpadded base64url selector encoding.
The Tasks panel exposes the same ticket and allocation fields. No resource is
selected implicitly. Native eligibility is rechecked on submission.

## Scheduler and proofs

The scheduler maintains a dependency tree for one dish, rather than independent
preparation demand lanes. Each cooking/mixing occurrence has its own target input
composition and vessel ownership. It uses existing prepared outputs first,
prefers more complete assigned assemblies, retains useful held cargo, and tries
other missing branches when one is blocked. A parent does not reserve the only
empty bowl before its mixing child can use it. A completed child's vessel may
continue into its parent stage when the native cooking method permits that.
An assigned ready output still counts against demand while another player holds
it or its route is blocked; the scheduler does not cook a duplicate replacement.

Cooking inputs are assembled off heat before installation. Mixer bowls are loaded
in place: the native client can reject reinserting a loaded bowl even though the
server's rule appears to allow it. A mixing addition proves the **underlying
input composition** against that exact mixing node; its transient progress does
not turn the partial mixture into an unrelated raw ingredient. Final completion
still requires the actual mixed/cooked native output, not just those inputs.

Ready owned batches are selected by their remaining spoil window and rescued
before other work. An unpressed child can yield to ready food. Vessel identities
survive removal from equipment. No native progress, ownership or recipe fields
are written by the executor. Inventory observations are reused per task pass;
blocked choices back off for 0.2 seconds rather than scanning every render frame.

Every addition requires two independent proofs:

1. Exact recipe membership: current composition plus this source advances the
   intended dependency without extra ingredients, duplicate quantities, a wrong
   preparation state, or a wrong cooking method.
2. Native physical compatibility: the source can transfer to this exact receiver,
   and the game's actual placement handler permits the press.

The first proof runs again **inside the primitive's final pre-press check**, after
navigation and target selection. The ticket, source/receiver identities and both
compositions are revalidated there. A compatible plate alone is not permission.
Transfers use the ordinary placement button, and require exact source consumption
plus the expected resulting receiver composition. Only a proven unchanged
suppressed transfer may retry; ambiguous changed outcomes fail without replay.

Hands recovery, moving-object pickup, navigation, pause, disconnect and Stop
semantics are shared with the existing task executor. Caught unrelated food is
parked, not added to this order. Assembly does not use human proximity, handoff
heuristics or successful button presses as dish-completion evidence.
Scheduling waits through an active native portal transit because its animation
can temporarily hide carried objects. It revalidates after arrival, before input.
Heat rescue checks the live station occupant, not just the cached inventory holder.

## Scope

Prepared-component assembly and recipe-derived mixing/cooking are implemented.
The task does not chop, dispense replacements, wash a missing plate, transport
resources across a gap, serve, or choose another order. Those remain separate
intent/allocation decisions. Optional recipe branches and unknown native node
kinds are withheld from the assembly catalogue rather than guessed. A physically
impossible allocation can remain waiting until the kitchen changes or Stop.

This is not certification of every DLC recipe, moving kitchen or crowded pickup
timing. Native input checks remain authoritative even when the structural recipe
proof succeeds. See the validation section below for the scenarios actually run.

## Validation

The final plugin and isolated runtime DLL have the same SHA-256:
`8e784901ccd7ca831bb4d2c7bc30fff4ba19406a69833cfe24ec146d4deb67b1`.
The recorded results and request identities are in
[dish-assembly-validation.json](dish-assembly-validation.json).

| Native replay | Level | Verified additions | Result |
| --- | --- | ---: | --- |
| Prepared fish, either assigned plate | `s_sushi_1_1` | 1 | Exact `Sushi_PlainFish` composition |
| Fish, seaweed and rice cooked in an assigned pot | `s_sushi_1_2` | 4 | Exact `Sushi_Fish` composition |
| Flour + egg, mix → pan → cook → portal → plate | `s_wizard_school_3_4` | 4 | Exact `Pancake_Plain` composition |

All three finished successfully under one request and lease per recipe. A browser
submission then selected between the two allocated plates and recognized the
already-completed pancake with **zero** additional transfers. The full repository
check passed: 2,885 core assertions, 391 navigation checks, 1,041 executor checks,
and 20 catalogue checks. After the final harness endpoint change, all 133 Python
tests passed; these include seven browser-form tests run through Node.

The action suite covers alternative and partial plates, partial assembly bases,
wrong/duplicate/unassigned ingredients, processing boundaries, ticket/base changes
during travel, lost resources, suppressed versus ambiguous transfers, combined
cooking, same-vessel mix-then-cook dependencies, already-prepared food temporarily
held by another player, portal cargo visibility, and exact final composition. Core
and Python tests cover the allocation grammar and typed client. Browser checks
cover the allocation form and required selection; the normal game installation
is unchanged.

Use an isolated native runtime and a new output directory per case:

```sh
python3 scripts/native_simulator.py --runtime .local/assembly-check start --level s_sushi_1_1 --port-base 36520
python3 scripts/assembly_audit.py --runtime .local/assembly-check --port-base 36520 --output .local/assembly-check-prepared --case prepared
python3 scripts/assembly_audit.py --runtime .local/assembly-check --port-base 36520 --output .local/assembly-check-cooked --case cooked
python3 scripts/assembly_audit.py --runtime .local/assembly-check --port-base 36520 --output .local/assembly-check-mixed --case mixed
python3 scripts/native_simulator.py --runtime .local/assembly-check stop
```

Fixtures provide assigned ingredients at counters. Measured assembly, transfers,
navigation and processing use production inputs and native clocks. Every accepted
job keeps one request ID and lease; an observation timeout is not a success.
