# Navigator regression fixture

Run `python3 scripts/check.py` for the plugin build against the installed game,
the core/API checks, this fixture and the Python tests. To run this fixture alone:

```sh
.tools/dotnet/dotnet run --project tests/navigation/Navigation.Tests.csproj
```

The project links the production `WalkPhysics` and `NavigationActions` classes.
`Stubs.cs` provides only their Unity/game API surface; the fixture supplies
analytical floors and injectable collision queries. No game process, input
connection or installed plugin is touched.

Coverage includes non-Steppable ledges, native stair markers, continuous ramps,
directed portal chains/cycles across decks, activation cones, receiver animation
endpoints, disabled links, blocked landings and connectors, cached portal geometry
with live flow, cooldown waits, native transport with disabled controls, bounded
timeouts, wrong exits, and route retention after transport with an incomplete search;
independently confirmed native flat-mesh seam contacts and early neutral arrival
braking without accepting predicted arrival;
native-size spherical contact at measured wizard-stair wedge lips, steep ramps,
step-component cache reconciliation, conveyor-corner history across graph edges,
intermediate conveyor/wind samples, changing
flow on cached edges, moving search boundaries, arrival under wind and external
pushes, shared platform translation/rotation, fallback from an unreachable
retained side, preserved reachable-side preference, slow-frame movement windows
and rejected overshoot. The core suite separately checks input expiry while
retaining ownership and renewal after frame jitter. Open-corridor continuation
and unchanged final-approach windows have separate production-controller checks.

These doubles do not emulate PhysX, native step projection, friction or Unity
update ordering. Building against the installed assemblies checks API compatibility;
real level traversal and low-FPS/native-input behavior still need live validation.

Native conveyor regressions also cover upstream edge rejection on weaker belts,
live reversals, stopped belts, rotated directions, checked lateral exits,
non-drifting arrival stances, padded turn support, contact recovery and preserved
turn waypoints. Exact conveyor-field, neighbour and native-entry-history changes
invalidate saved searches. See [the native conveyor report](../../docs/conveyor-navigation.md)
for the repeatable simulator audit and its remaining traversal failures.

`fixtures/wizard-stair-corner.json` contains eight measured floor/tread/wedge box
transforms from the live Chocolate stair failure in `s_wizard_school_3_4`.
`StairGeometry.cs` independently replays solid oriented-box contacts and continuous
capsule sweeps. The regression checks the blocked strip between valid endpoints
and preserves a front-on route. The pre-fix controller fails this regression.
This replay still is not Unity PhysX; live execution remains a separate check.
Ground-settling regressions cover neutral input, automatic continuation, missing
floor rejection, and timeout without native ground reacquisition. Landing-continuation
coverage includes stale native contacts, slope normals, low ceilings in the airborne
body envelope, momentum changes, and missing support between route points. Shallow
lateral contact recovery retains the capsule skin allowance and rejects blocked
landings, deep contacts, and unrelated walls.

`wizard-stair-contact.json` captures the mid-stair initial penetration that made
all 25 starts fail. Its regression proves upward native resolution is eligible
for neutral settling, the resolved pose restores usable starts, and an unrelated
wall overlap is not eligible. GroundCast's stale point is not used as proof that
the current body is resting.
