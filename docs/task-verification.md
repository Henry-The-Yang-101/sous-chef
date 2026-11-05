# Task verification — 2026-11-03

Bridge 0.8.0 implements the seven task actions in [tasks.md](tasks.md). Each
action has its own implementation commit. Later commits address shared recovery,
native utensil discovery, processing-state interpretation and scheduling.
Voice/Jev integration is deferred.

## Automated checks

`python3 scripts/check.py` builds against the installed game with warnings as
errors and passes 2,853 core/assembly assertions, 318 navigation checks, 391 action
checks and 111 Python tests. Navigation/action checks use analytical geometry and
mocked state; the native audits below supply real Unity/PhysX evidence.

Coverage includes finite/infinite repetition, indefinite route/target interruption,
identity retention, cancellation, persistent ownership with neutral input expiry,
independent P1/P2 reachability, native utensil definitions, processing-stage
readiness, and counter-motion forecasts that reject unknown/one-way motion.

## Native task audits

Ten accepted task scenarios passed using the installed Unity headless player.
Source: `01154b2`. Tested runtime DLL SHA-256:
`996081cab452ec8ca459e676d43775a8dd620220f08d029379668b2d28957699`.

| Scenario | Native evidence |
| --- | --- |
| Prepare different ingredients | Rice begins cooking; a fresh native snapshot confirms cooking still progressing while P2 chops fish; both items are handed off. |
| Give an empty utensil | A real empty pot is retrieved and delivered. Its type comes from its cooking-step asset, despite lacking item properties. |
| Take ready food off heat | P2 waits for rice, collects contents into a serving plate, and parks it. The original pot remains empty. |
| Monitor a mixer | Flour/egg preparation is removed from a finished native mixer. The bowl's unused cooking handler does not mask mixing readiness. |
| Wash dishes | Two original dirty stacks containing three plates are merged, carried to the sink, and washed. Original dirty objects disappear and the sink empties. |
| Deliver dirty dishes | In `MovingPlatform4`, supported initial poses swap the chefs' islands. Only the human has a certified sink route. P2 delivers the exact two-plate dirty stack to a checked counter near P1. |
| Put out fire | Native fires, including spread during execution, are extinguished; a fresh server snapshot has no remaining fire. |
| Toss burnt food | After separately extinguishing any fire, native disposal empties the exact pot and returns it to its original burner. |
| Two repetitions | One request washes two separate batches, waiting for the second batch to appear. |
| Infinite monitor / Stop | An idle monitor retains one request/lease until Stop; measured API Stop took about 12 ms. |

Native scenarios use `s_sushi_1_2`, `s_sushi_1_4`,
`s_wizard_school_3_4` and `MovingPlatform4`. Dirty dishes, ignition and ruined
progress use explicit initial-condition fixtures invoking native spawning/state
methods. The dirty-delivery case also uses supported initial chef poses. All
measured movement, pickup, placement, work, spraying and disposal use production
logical inputs. P1 inputs and native gameplay clocks are unchanged.

Original reports retain failures from development. The final accepted-case summary
is `.local/task-verification-summary.json`, assembled from:

- `.local/task-audit-verified/report.json` and per-case traces/snapshots;
- `.local/task-burnt-verified/report.json`;
- `.local/task-dirty-delivery-verified/report.json`.

One initial burnt-food request was rejected before acceptance while readiness was
changing after firefighting. The runner now waits for current eligibility and may
retry only `task_not_available` preconditions. Accepted tasks are never replayed;
every trace requires a single request identity and continuous ownership.

## Interruption recovery

Four additional native cases passed: 900 ms target and route outages at requested
60 FPS, a 900 ms target outage at requested 20 FPS, and Stop during recovery.
The three outages deliver the exact original ingredient once, without another job
request. Stop retains the cargo and took about 10 ms. Evidence:
`.local/task-recovery-final/report.json`. These use the same recovery/ownership
implementation, before the later station-readiness and scheduling refinements.

## Localhost and limits

Browser validation starts an infinite monitor through the Tasks form, observes its
waiting phase and repetition count, and cancels it through Stop. Hidden preparation
fields do not block other tasks' form submission. The current task remains visible
while its controls are disabled, without placeholder object coordinates.

Direct counter/player handoffs and the moving-kitchen dirty-delivery case have
native coverage. Directed conveyor fallback and learned moving-counter return
forecasts have implementation/unit coverage but are not exhaustively certified
across native maps. Existing navigator contact limitations remain: a task waits
instead of silently abandoning its cargo when a route cannot currently execute.

## Repeat the checks

```sh
python3 scripts/native_simulator.py --runtime .local/task-audit-native start --level s_sushi_1_2 --port-base 25932
python3 scripts/task_audit.py --runtime .local/task-audit-native --port-base 25932 --output .local/task-audit-results
python3 scripts/native_simulator.py --runtime .local/task-audit-native stop
```

Select individual scenarios with repeated `--case` flags. Each scenario reloads
its kitchen and records control receipts and independent native observations.
Fixtures are opt-in headless-only mailbox commands: `task-fixture dirty COUNT
[COUNTER_ID]`, `task-fixture ignite OBJECT_ID`, and `task-fixture burnt ITEM_ID`.
They are never invoked by production tasks.

## Dish delivery — 2026-11-05

`deliver_dishes` passed seven scenarios in the native Unity player on
`s_sushi_1_1`, at requested 60 FPS. Implementation: `db43e24` (including `2bacd23`
and `b66d209`). Tested DLL SHA-256:
`bc36c0e5c3b2879ec75d5335117f00b7ec9d089e07e906bf931dd7ea5604f3da`.
The committed [report](dish-delivery-audit.json) records all seven results; full
receipts, fixture records and independent snapshots are under
`.local/deliver-dishes-final-v3/`.

| Scenario | Native evidence |
| --- | --- |
| Already-held ready dish | P2 serves its held plate with no additional pickup. |
| Wait for a plated dish | The same accepted request waits with zero repetitions, then collects and serves a plate filled later. |
| Unrelated held ingredient | P2 parks the exact ingredient on a nearby counter, then collects and serves the dish. |
| Occupied counters, held ingredient | P2 drops the original ingredient away from the pickup route, then collects and serves the dish; the original cargo remains alive on the floor. |
| Occupied counters, held empty plate | P2 keeps one child action while seeking a usable floor release, drops the original plate, and completes the dish pickup and serve. |
| Two repetitions | P2 serves its initially held plate and a second map plate, counting exactly two native successful deliveries. |
| Infinite idle monitor / Stop | The idle request retains its lease until explicit Stop cancels it and releases control. |

Every delivery trace checks one task request identity and uninterrupted ownership.
Fresh snapshots independently check the native server's successful-delivery count,
P2's empty hands, and the exact parked or dropped cargo. Setup uses existing native
ingredient/holder fixtures plus `task-fixture plate-food PLATE_ID SUPPLY_ID`, which
fills an empty plate using the supply's finished native work-output definition.
Measured walking, pickup, parking, floor placement and serving use production
logical inputs. Production tasks never invoke fixtures.

The counter case exposed a native placement opportunity just short of the
navigator's conservative alignment proof. The primitive now accepts the exact
native placement predicate while stopped and aligning at that station, retaining
all cargo and occupant checks. Floor cases also verified that dropping toward the
next pickup can obstruct walking, and that a counter's native placement selection
can persist during turning. Floor placement now prefers an unobstructing landing
and can take a short checked walk to open floor without neutralizing that walk.

`python3 scripts/check.py` passed the installed-game build with warnings as errors,
2,869 core/assembly assertions, 359 navigation checks, 845 action checks and 119
Python tests. Regression coverage includes incomplete/empty/wrong-plate recipes,
reserved or human-carried plates, source changes, a newly caught ready plate,
ticket expiry, exact serving evidence, indefinite floor-release recovery and Stop
during floor positioning. The localhost Tasks panel also displayed the new label,
current phase and repetition count during a native job. The ordinary installed
game was not reloaded or replaced for this verification.

```sh
python3 scripts/native_simulator.py --runtime .local/dish-check start --level s_sushi_1_1 --port-base 40032
python3 scripts/dish_delivery_audit.py --runtime .local/dish-check --port-base 40032 --output .local/dish-check-results
python3 scripts/native_simulator.py --runtime .local/dish-check stop
```

### Expiry priority follow-up — 2026-11-05

Dish selection now compares the actual seconds remaining on the native matcher's
selected ticket. Its returned lifetime fraction is not used for sorting. Held
ready plates retain direct-serving priority; walking distance breaks expiry ties
among plates to collect. An earlier-expiring meal appearing before pickup can
replace the unpressed selection.

The [expiry audit report](dish-delivery-expiry-audit.json) records three passing
native scenarios: expiry priority, already-held serving and two repetitions.
On `s_sushi_1_1`, P2 chose a prawn plate 7.20 walking units away with 59.39 seconds
remaining, ahead of a fish plate 0.76 units away with 148.80 seconds remaining.
A fresh snapshot confirmed one successful delivery and the later plate left
filled. The headless-only `serving-order PLATE_ID SECONDS` fixture adds a matching
native ticket with an explicit initial remaining time; all measured execution
uses production inputs. Full evidence: `.local/dish-expiry-audit/`.

`scripts/check.py` passed, including regression checks for absolute time versus
lifetime percentage, distance ties, held-dish priority and a more urgent meal
appearing during an unpressed pickup. Repeat the native checks with
`scripts/dish_delivery_audit.py --case expiry --case held --case repetitions`.
