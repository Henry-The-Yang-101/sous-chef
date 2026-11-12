# Assemble an assigned dish

`assemble_dish` produces one dish for one exact native ticket. The caller selects
the ticket and may restrict resources. The deterministic executor plans the work,
uses ordinary P2 inputs, and reports success only when the selected plate's
composition and native plating step match the original recipe. Serving is a
separate task. The voice compiler can select the ticket with Jev before
submitting the same exact native task.

## Automatic orders

```python
chef.assemble_dish(selected_ticket['id'])
```

Omit all three resource groups to make the whole order from the observed kitchen.
The executor fetches the required quantities from ingredient dispensers or loose
stock, follows native work-prefab chains for chopping, mixes/cooks each recipe
branch, removes ready batches from heat, and plates the exact result. It can wash
one needed plate at a sink and clear occupied workspaces using safe storage.
Conveyor arrivals and newly freed resources are reconsidered while waiting.
Serving remains a separate task.

Preparation is part of the recipe dependency scheduler. Existing prepared and
partly prepared food satisfies demand before new stock is fetched. A spawned
portion and each verified chopping successor stay owned by the same request.
Chopping and washing yield at native work boundaries when a cooking batch needs
rescue; partial progress stays in the game. No ingredient names or recipe-specific
procedures are hardcoded.

## Explicit resource contract

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
  bases can contribute their existing exact components. With an empty selected
  plate, a suitable assigned preparation base is completed before final plating.
  Cookable preparation bases, such as dough, can also supply a processing vessel
  without appearing a second time in the equipment allocation.
- `components`: up to 24 exact ingredient/container instances or ingredient
  dispenser IDs. Raw stock is chopped through its native work chain. A dispenser
  authorizes fetching only the missing recipe quantities; its current prefab is
  checked again before pickup. Chopping boards and safe storage are selected
  locally. Existing prepared ingredients are used directly.
- `equipment`: up to 24 assigned vessel and processing-station IDs. For example,
  assign both the bowl and mixer, and the pan and burner. Native compatibility
  chooses among these; unassigned equipment is never taken. Empty storage
  countertops are chosen by the existing safe-parking rules.

An object may appear in only one allocation group. A clean-stack allocation pins
its current top plate. A later top plate cannot replace it. Missing or temporarily
inaccessible assigned objects cause bounded periodic waiting. They never authorize
an unassigned replacement or dispenser spawn. Allocated dispensers explicitly
authorize spawning; verified work successors replace their original raw items. Explicit allocation may reuse
objects previously handed off by preparation; ambient lookalikes remain untouched.

The generic client equivalent is `run_task('assemble_dish', order_id=...,
bases=..., components=..., equipment=...)`. Repetitions must be `1`. Wire grammar:

```text
task REQUEST assemble_dish 1 SELECTOR_ORDER BASE_COUNT BASE_IDS... COMPONENT_COUNT COMPONENT_IDS... EQUIPMENT_COUNT EQUIPMENT_IDS...
```

`SELECTOR_ORDER` uses the existing `s` + unpadded base64url selector encoding.
The manual Tasks panel exposes the same ticket and optional allocation fields.
Leaving every allocation field empty selects automatic resources (`0 0 0` on the wire). Native
eligibility is rechecked on submission.

## Voice commands

Try “make the next order” or name the recipe, such as “make the fish sushi.”
After selecting assembly, Jev receives the pending kitchen tickets sorted from
soonest to latest expiry. Ties preserve kitchen order and untimed tickets come
last. “Next order” selects the first ticket, and the normal queue default is
append.

Voice commands use automatic resources and bind only the exact ticket.
A lack of prepared components no longer rejects the command. The compiler
rechecks the ticket before acceptance; the executor checks ingredients,
quantities, reachability and native interactions throughout execution.

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
before other work. A short rescue retry never dispatches ordinary ingredient
collection. Before leaving a cooking batch, the scheduler budgets the outward
and return trip plus storage/interaction time. It clears hands and approaches
the batch early when another trip would run too long. Nearby work can proceed
when that budget fits. An unpressed child can yield to ready food. Vessel identities
survive removal from equipment. No native progress, ownership or recipe fields
are written by the executor. Inventory observations are reused per task pass;
dependency waits are paced at 0.2 seconds rather than scanning every render frame.
Repeated route failures back off for 2, 4, 8, then 16 seconds, waking earlier when
position, support, occupancy, cargo or certified reachability changes. Assembly
pickups share this storage and route-history policy when clearing their hands.

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
When moving geometry invalidates the broad catalogue, rescue uses the bounded
destination planner instead of waiting for that catalogue to finish. Useful held
components likewise retain their next recipe addition while its directed route is
proved. Native navigation and placement checks still apply before input.
Independent mixing/cooking continues while the allocated plate is unavailable.
Empty allocated cookware is parked before it can delay another batch's rescue.
If no known/local work is executable, the scheduler probes other allocated
resources against an incomplete catalogue. Stored food is collected only when
its receiving route is usable; ordinary stalled route recovery yields before
input so another recipe branch can run.

`details.assembly.decisions` contains the last 96 scheduler events with monotonic
sequence numbers, native time, stage, and exact source/destination instance IDs.
It records selections, final authorization checks, verified transfers, replanning,
waiting transitions, heat rescue, and completion. Authorization events are checks,
not a count of button presses.

## Scope

Automatic ingredient collection, chopping, recipe-derived mixing/cooking, sink
washing and plating are implemented. Explicit resource mode retains the caller's
allocation boundaries. The task does not serve or choose another order. Unsupported
gap transport and specialized wash tools still require help. Optional recipe branches and unknown native node
kinds are withheld from the assembly catalogue rather than guessed. A physically
impossible allocation can remain waiting until the kitchen changes or Stop.

This is not certification of every DLC recipe, moving kitchen or crowded pickup
timing. Native input checks remain authoritative even when the structural recipe
proof succeeds. See the validation section below for the scenarios actually run.

## Validation

The full-order audit starts with the level's actual stock, without prepared-food
fixtures, and independently compares the finished native plate to the recipe:

```sh
python3 scripts/full_order_audit.py --runtime .local/full-order-dev --port-base 38720 --output .local/full-order-fish --level s_sushi_1_1 --recipe Sushi_PlainFish
```

Native full-order replays on November 12, 2026 used only order IDs, ordinary
Player 2 inputs and native progress clocks. No prepared ingredients were supplied.
Each finished under one request/lease and passed the independent plate proof:

| Recipe | Level | Verified additions | Local evidence |
| --- | --- | ---: | --- |
| `Sushi_PlainFish` | `s_sushi_1_1` | 1 | `.local/full-order-fish-01/report.json` |
| `Pasta_Marinara_New` | `s_balloon_2_3` | 6 | `.local/full-order-pasta-06/report.json` |
| `Pancake_Plain` | `s_wizard_school_3_4` | 4 | `.local/full-order-pancake-01/report.json` |

Additional pizza probes did not complete: `s_wizard_3_2` prepared dough and all
three toppings but could not establish an oven route; the observed starting
inventory in `s_wizard_3_1` exposed only the tomato dispenser. These waits are
not successful dish proofs (`.local/full-order-pizza-01/` and
`.local/full-order-pizza-02/`). Navigation and currently obtainable stock remain
physical prerequisites; automatic resources do not bypass them.

The action suite additionally checks explicit raw stock, repeated quantities,
multiple work successors, wrong/missing ingredients, occupied boards, sink
washing, heat interruption during chopping, partial alternative plates and
bounded blocked-route retries. Existing exact-allocation, transfer, navigation,
pause and Stop regressions remain in the suite. `scripts/check.py` builds against
the installed game's real assemblies and runs the core, navigation, action,
catalogue and Python/browser checks.

Earlier explicit-allocation replays for prepared fish, cooked rice sushi and
mix-then-cook pancakes remain reproducible with `scripts/assembly_audit.py`.

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
