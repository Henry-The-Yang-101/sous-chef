# Shared capability contracts — Milestone 1

Capability contracts define the shared task schema and validation rules for
the [scheduler](scheduler.md) and [Jev interpreter](jev-interpreter.md).

## Owning schema and adapters

[`clients/capabilities.json`](../clients/capabilities.json) owns all nine task
families and 19 existing action adapters (including independent Stop). Every
entry specifies version, description, closed parameter schema, required fields,
defaults, candidate sources, repetition unit/limits, start preconditions,
completion evidence, cancellation, resume support and limitations. Regions,
unrestricted container transfers, concurrent duties and checkpoint/resume are
explicitly unimplemented.

The Python client validates and encodes through
[`clients/capabilities.py`](../clients/capabilities.py). Existing convenience
methods retain their signatures. `run_capability(name, arguments, repetitions=1,
version=1)` selects only a registry adapter, never an arbitrary method. Unknown
fields, wrong types (including Boolean counts), invalid selectors/IDs, duplicate
allocations and overlong native wire commands fail before transmission. Native
parsing and start validation remain independent authoritative boundaries.

The registry generates native names, states and limits in
[`CapabilityRegistry.cs`](../src/Core/CapabilityRegistry.cs). Its version and
SHA-256 accompany the native hello and every actions catalog. Regenerate after
editing the registry, and increment versions when changing contract semantics:

```sh
python3 scripts/generate_capabilities.py
python3 scripts/generate_capabilities.py --check
```

`scripts/check.py` checks generation drift and shared wire fixtures. Every
adapter's fixture is both encoded by Python and parsed by the real C# parser,
checking the actual decoded arguments. The UI receives the same schema from
`/api/bootstrap`; task/action names, preparation states, recipients, quantities,
allocation limits and repetition choices derive from it.

## Support, applicability and eligibility

`GET /api/capabilities` returns `{registry, state}` without issuing game I/O.
`/api/state` includes the same dynamic `capabilities` view. Both use the existing
Host/Origin and phone-token checks. API keys and model services are absent.

Each dynamic entry separately reports:

- `support`: `implemented`, `not_implemented`, or `unknown` when the native
  manifest is missing/incompatible.
- `applicability`: `applicable`, `not_applicable`, or `unknown`, with evidence.
- `start_eligibility`: `eligible` or `blocked`, with a reason and an explicit
  requirement to validate actual arguments. Family eligibility is not approval
  of every argument combination.

Busy, claimed, paused, unfocused, disconnected and stale states never turn an
implemented task into an unsupported task. Missing/incompatible manifests block
future plan validation. Legacy manual controls continue using the native live
options; no old bridge is silently certified as the current contract version.

Native `available_actions()` now includes `semanticCatalog`: exact object IDs,
possible target operations, eligible recipient players, ingredient roots and
native work chains, utensil types, and live assembly tickets with recipe trees.
It observes through its own inventory and never starts work, cancels a task,
changes its request/iterator/reservations, presses input, or refreshes an active
route. Identity discovery is paced at one second; removals and ticket expiry are
observed on each capture. Round reset clears discovery state. Unsupported kitchen
or chef contexts publish no semantic candidates.

Structural candidates remain available while busy or suspended; existing
`taskOptions`, target `operations` and `jobOptions` still control immediate starts.
The UI merges live operation/contents evidence by exact identity and keeps Run
disabled during suspension or active work.

Preparation `raw` retains the root identity; `chopped` needs a complete native
work chain, and `empty` applies to observed utensils/plates. Cycles, missing or
unidentified outputs are unknown. Cooking/mixing compatibility requires recipe
and vessel evidence; it remains unknown in this structural catalog. Unknown
route coverage likewise never means unreachable. `assess_arguments()` exposes
these distinctions, rejects observed contradictions and always retains the need
for native start/physical validation.

## Typed plans and repetition

[`harness/plans.py`](../harness/plans.py) validates a complete draft and returns
immutable `Plan`, `NativeTask`/`NativeAction`, and `Repeat` values. It performs no
acceptance, queue mutation, focus change, network request or execution.

```python
from harness.plans import validate_plan

draft = {
    'schema_version': 'sous-chef.plan.v1', 'capability_version': 1,
    'command_id': 'command_42', 'expected_queue_revision': 0,
    'queue_edit': 'append', 'after_plan': 'continue_pending',
    'on_failure': 'pause_pending',
    'tasks': [{
        'kind': 'native_task', 'action': 'wash_dishes',
        'capability_version': 1, 'arguments': {},
        'repeat': {'mode': 'finite', 'count': 1},
    }],
}
plan = validate_plan(draft)  # Offline shape validation only.
# validate_plan(draft, capabilities=bridge.state()['capabilities']) additionally
# requires a fresh compatible native manifest and rejects known contradictions.
```

Plans contain 1–5 ordered occurrences; duplicate task types and ingredient entries
remain separate. Finite tasks accept 1–10 repetitions; `until_cancelled` maps to
the client's `None`. Unlimited work is only allowed at the tail, and appending
behind known unlimited work is rejected. Actions and assembly accept exactly one
iteration. Stop is rejected inside plans and retains its independent control path.

| Task | One repetition |
| --- | --- |
| `prepare_near_player` | The whole requested item list; each entry owes its quantity |
| `assemble_dish` | One exact ticket; always exactly one repetition |
| `press_button` | One exact control activation; always exactly one repetition |
| `take_ready` / `monitor_equipment` | One ready food collection and serving/parking |
| `wash_dishes` | One washing batch, not one plate |
| `deliver_dirty_dishes` | One dirty-dish batch handoff |
| `put_out_fire` | One reachable-fire sweep, not one flame |
| `toss_burnt_food` | One disposal plus surviving utensil return |
| `deliver_completed_dishes` | One verified dish delivery |

Assembly requires an exact ticket, 1–24 alternative bases, and at most 24
components and 24 equipment IDs. Groups are disjoint. Components are exact items,
not dispensers/station selectors. Missing resources/tickets are never rebound to
lookalikes. Compatible plate selection, native recipe arithmetic, allocation
binding and physical feasibility remain native responsibilities. Preparing missing
stock, chopping, washing, serving and choosing another order are separate work.
See [the complete assembly contract](dish-assembly.md).

Task-bearing queue-edit metadata supports append, insert-next and the three
replacement scopes. Queue-only commands, atomic acceptance/revision CAS, stable
task IDs, receipt reconciliation and actual dispatch belong to Milestone 2.
Input freshness checks here do not create hidden deadlines for accepted native
work. A validated draft is not an execution receipt or proof of feasibility.

## Verification

Run the complete check and an isolated native replay:

```sh
python3 scripts/check.py
python3 scripts/native_simulator.py --runtime .local/milestone1-native start --level s_sushi_1_2 --port-base 36741
python3 scripts/capability_audit.py --runtime .local/milestone1-native --port-base 36741 --output .local/milestone1-capability-audit
python3 scripts/native_simulator.py --runtime .local/milestone1-native stop
```

Tests cover registry/native wire parity, strict fields and bounds, exact assembly
allocation, Unicode/wire limits, immutable drafts, version drift, repeated task
and item types, unlimited placement, stale/legacy manifests, unknown feasibility,
read-only native observation and HTTP access controls. Browser-form tests check
registry-derived choices and retention through suspension; desktop and 390-pixel
phone layouts were also inspected in the actual harness with no console errors.

The native audit samples idle, busy, paused, unfocused, reconnected and resumed
states 20 times each. It verifies the same running request and lease, full task
support, exact candidates, and chef positions within 0.0001 world units (allowing
native floating-point settling). It then verifies explicit Stop. This uses an
empty-equipment monitoring assignment, so waiting is expected and is never
reported as task completion. Focus loss uses the native input-focus fixture.
Timings in the evidence are observations from this isolated run, not a baseline
comparison or a certification of sustained performance in moving kitchens.

The normal installed game plugin is not replaced by these checks.
