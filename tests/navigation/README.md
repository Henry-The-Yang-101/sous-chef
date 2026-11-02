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
native-size spherical contact at measured wizard-stair wedge lips, steep ramps,
step-component cache reconciliation, conveyor-corner history across graph edges,
intermediate conveyor/wind samples, changing
flow on cached edges, moving search boundaries, arrival under wind and external
pushes, shared platform translation/rotation, fallback from an unreachable
retained side, preserved reachable-side preference, slow-frame movement windows
and rejected overshoot. The core suite separately checks input expiry while
retaining ownership and renewal after frame jitter.

These doubles do not emulate PhysX, native step projection, friction or Unity
update ordering. Building against the installed assemblies checks API compatibility;
real level traversal and low-FPS/native-input behavior still need live validation.
