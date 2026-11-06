# Automatic ingredient preparation

Story **4-1** (`s_sushi_4_1`, `Text.Menu.Level19`) has one cucumber crate.
Its two automatic belt sources alternate rice/seaweed and prawn/fish. Previously,
the preparation menu offered only cucumber, although live inventory could observe
the other foods. Waiting to observe a particular spawn would also miss variants
that were temporarily absent.

`PreparationIngredients` now reads native `TriggerAttachedSpawn` tables as well
as dispenser roots and `PickupItemSwitcher` variants. Both preparation catalogues
and the executor's raw-to-worked identity matching use these roots. No global
prefab registry, recipe guesses, or kitchen-specific production branches are used.
Only actual obtainable cargo enters source selection. Processed copies, food
already handed off, and food in another chef's hands retain the existing checks.

Native belt testing also exposed two pickup races. A selected item could reach
the bin during pickup, leaving the task waiting for an object that had been
destroyed. A moving item could also leave pickup range before the native press,
ending verification without cargo. The primitive now distinguishes destroyed
exact cargo from temporarily unavailable sources. Preparation reselects from
current inventory after an unsuccessful pickup, reconciling anything already in
its hands before issuing another input. The original task request and lease stay
in place; unsuccessful pickups never count as deliveries.

## Repeatable native test

```sh
python3 scripts/native_simulator.py --runtime .local/prepare-belt-fixed start --level s_sushi_4_1 --port-base 28453
python3 scripts/automatic_ingredients_audit.py --runtime .local/prepare-belt-fixed --port-base 28453 --output .local/prepare-belt-default --fps 60
python3 scripts/automatic_ingredients_audit.py --runtime .local/prepare-belt-fixed --port-base 28453 --output .local/prepare-belt-seafood --fps 60 --seafood-side --case fish_raw --case prawn_raw
python3 scripts/native_simulator.py --runtime .local/prepare-belt-fixed stop
```

Repeat with `--fps 20` and separate output directories. Each case resets the
kitchen, checks both ingredient catalogues against all five expected foods, and
independently reads the native automatic spawn tables and sole cucumber crate.
Requests use the dropdown's native ingredient IDs. One accepted request must
retain ownership through completion. Fresh server snapshots verify the exact
delivered object in its counter, P1's hands, or P1's native pickup selection after
a nearby ground drop, plus chopped identity and cooked rice contents. Food,
processing progress, gameplay clocks, and physics are unmodified.

The default cases collect rice and seaweed, chop cucumber, and cook rice from the
normal spawn. `--seafood-side` explicitly stages **both chefs' initial poses** on
the right side: P1 at `[35.5, 0, 5.5]`, P2 at `[34.8, 0, 3.6]`, validated against
native support. Fish and prawn pickup/handoff then use ordinary production input.
The fixtures and both original poses are recorded in each result.

## Verified results

Production source `527ede8` passed **12/12 native preparation cases**, six at each
requested Update rate. The runtime used the installed Unity/PhysX player with
`graphicsDevice: Null`, native automatic physics, and a 0.02-second fixed timestep.
Runtime DLL SHA-256:
`82bd3f20aabc771207cc24afe869d9327a25ab1fbd150af58e6644dad08cfc56`.

| Case | 60 FPS | 20 FPS | Initial positions |
| --- | --- | --- | --- |
| Raw belt rice | Pass, 2.35 s | Pass, 1.97 s | Native spawn |
| Raw belt seaweed | Pass, 5.91 s | Pass, 5.80 s | Native spawn |
| Chopped cucumber | Pass, 4.24 s | Pass, 5.03 s | Native spawn |
| Cooked belt rice | Pass, 18.20 s | Pass, 18.32 s | Native spawn |
| Raw belt fish | Pass, 3.85 s | Pass, 5.45 s | Seafood-side fixture |
| Raw belt prawn | Pass, 3.93 s | Pass, 10.28 s | Seafood-side fixture |

The final 20 FPS prawn case observed `pickup_item_destroyed`, retained the same
accepted request, and delivered a later native spawn. The 20 FPS seafood handoffs
used nearby ground drops; independent snapshots prove P1's native pickup selector
points at the exact delivered food and that it is no longer held by P2. The first
version of the audit incorrectly required a catch for these valid ground drops;
its rejected records remain in `.local/prepare-belt-verified-seafood-20/`. The
corrected verifier was rerun in a fresh round for each seafood case.

Per-case traces, catalogues, initial poses and full native snapshots are under
`.local/prepare-belt-verified-default-{60,20}/`,
`.local/prepare-belt-verified-seafood-60/`, and
`.local/prepare-belt-verified-seafood-20-recheck/`. A compact summary is in
[automatic-ingredients.json](automatic-ingredients.json).

The full `scripts/check.py` run passed 2,877 core/assembly assertions, 359
navigation checks, 897 action checks, 20 harness-display checks and 131 Python
tests. See `.local/prepare-belt-retry-check.log`.

The same production source was also checked in seven kitchens with automatic
food sources. Active native spawn-table variants all appeared in the ingredient
menu, including random tables with several possible outputs:

| Native scene | Observed ingredient choices |
| --- | --- |
| `s_sushi_4_1` | Cucumber, rice, seaweed, prawn, fish |
| `s_sushi_5_1` | Flour, chocolate, honeycomb, egg |
| `s_city_h8` | Meat, flour, prawn, fish, carrot |
| `s_dynamic_Stage_01` | Lettuce, tomato, cucumber; fish, rice and seaweed added when their sources activate in phase 2 |
| `s_city_h12` (DLC 5) | Flour, meat, carrot, prawn |
| `s_treehouse_3_2` (DLC 5) | Dough, pepperoni, tomato, cheese, olive, chicken |
| `s_chinatown_1_5` (DLC 3) | Flour, prawn, carrot, meat |

The dynamic case explicitly advances native phase prerequisites; it verifies
source activation and cache refresh, not recipe completion. Loading ordinary
`s_wizard_5_5` afterwards showed only cheese, meat, lettuce and bun, with no food
types leaking from earlier kitchens. The DLC sessions rebuild the same source;
their separate DLL hashes and native source tables are recorded in the JSON
summary and `.local/prepare-belt-catalogues/`. Phase evidence is in
`.local/prepare-belt-phase/`.

One additional full preparation request in `s_city_h8` collected a naturally
spawned carrot, chopped it and delivered `Chopped_Carrot` via a ground throw near
P1, from normal chef positions at requested 60 FPS. This verifies processing an
automatic-source ingredient without any dispenser or chef/food fixture. Its
original selector survived the native raw-to-chopped identity change; the
independent snapshot confirmed the exact chopped carrot. Evidence:
`.local/prepare-belt-chopped-carrot/`.

## Scope of the traffic-kitchen test

The existing navigator did not certify a route across the road from P2's normal
left-side spawn to the right-side seafood belt. An ordinary chopped-fish request
kept its lease and waited for an obtainable ingredient for the full 150-second
test deadline. The native seafood staging isolates pickup/handoff from this
separate road-routing limitation; it does not establish autonomous seafood
chopping across the road. The right side has no chopping boards.

Original failure evidence is retained in `.local/prepare-belt-60/fish_chopped/`
(unreachable seafood), `.local/prepare-belt-seafood-60/prawn_raw/` (destroyed
pickup), and `.local/prepare-belt-final-default-60/rice_raw/` (missed pickup).
The latter two preceded the corresponding recovery fixes.

Unit regressions cover all configured variants before any spawn, noninteractive
sources, deduplication and nonfood exclusion, switching crates, source removal,
round reset, processed-stage matching without a raw copy, raw aliases that share
a processed name, handoff reservations, and both pickup recovery cases. The
live localhost browser was also inspected: its preparation selector listed
cucumber, rice, seaweed, prawn, and fish during an active task.
