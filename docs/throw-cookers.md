# Rice throws into cookers

The planner recognizes native ingredient-container catch volumes in bridge
0.7.3. In `s_sushi_1_3`, rice is held at about 0.548 m and its native throw peaks
at about 1.013 m. The old planner required a descending arrival at the solid
pot's top, about 1.094 m after accounting for the rice collider. It therefore
withheld the cookers even though the game's surrounding catch trigger accepts
rice below that height.

Ingredient containers now aim through the receiving body's center and require
an actual native `Physics.ComputePenetration` overlap with an enabled catch
trigger on that receiver. Separate triggers are checked individually; neither
their bounding union nor unrelated child triggers can establish a catch.
Moving-receiver displacement and release-position uncertainty are included.
The native catch predicate still checks ingredient compatibility, capacity and
attachment state, and the full swept flight still checks obstacles before
receipt. Both cooker targets and direct pot targets use this proof.

Counter surface arrivals and player catch windows retain their existing rules.
Previews keep the existing 4 ms incremental search slices and two-second work
budget. The new checks reuse the already collected collider array, allocate no
flight-sample buffers, and reject distant samples before issuing a native overlap
query. There are no new scene-wide scans or changes to worker scheduling.

## Validation

The final 0.7.3 native build passed ten cooker cases and seven existing
regressions. Eight cooker deliveries were confirmed from authoritative server
contents; full-pot and incompatible-item requests were withheld and retained
cargo. Existing regressions covered raw/chopped cheese counters, a player pass
at requested 20 FPS, empty hands, a nonthrowable pan, interruption after launch,
and a player with occupied hands. The complete code check passed 2,840 core
assertions, 318 navigation checks, 289 action checks and 109 Python tests.

Two additional unstaged `s_sushi_1_4` attempts correctly offered the cooker, but
failed during the walking approach: `no_safe_start` at requested 60 FPS and
`alignment_failed` at 20 FPS. Neither released rice. The checked initial-pose
fixtures isolate catching from these approach failures and do not establish
successful autonomous navigation from that kitchen's rice dispenser.
The [machine-readable report](throw-cookers.json) retains both failed approaches
alongside the 17 passing cases, exact native DLL fingerprint and local evidence.
The live browser harness also exposed enabled `Throw held item` choices on all
three cooker rows while P2 held rice in `s_sushi_1_3`. All temporary native
runtimes and the isolated harness were stopped after verification. The existing
interactive game was left running with its previously loaded bridge; install
0.7.3 after quitting it to activate this change there.

## Reproduce

The checked-in [cooker cases](../tests/actions/native-throw-cookers.json) cover
two ordinary sushi kitchens plus a staged conveyor-kitchen fixture, requested
20/60 FPS, direct pot targets, a full pot, and an incompatible ingredient.
Each successful throw requires both the row operation
and action parameter choice to appear. Fresh snapshots independently confirm
the same server pot gains rice and the exact source cargo is consumed. A
receipt or an unrelated pot gaining food cannot pass that check.

```sh
python3 scripts/native_simulator.py --runtime /tmp/sous-chef-cookers start --port-base 27612 --level s_sushi_1_3 --fps 60
python3 scripts/throw_audit.py --runtime /tmp/sous-chef-cookers --cases tests/actions/native-throw-cookers.json --output .local/cooker-retest
python3 scripts/native_simulator.py --runtime /tmp/sous-chef-cookers stop
```

Use an unused port range and a new output directory. This uses the original
Unity/PhysX runtime, ordinary P2 pickup/navigation/throw input, and native pot
contents. The `s_sushi_1_4` fixtures explicitly stage P2 at a checked, supported
initial release pose before measuring the throw. Other cases use ordinary
actions throughout. No fixture changes throw forces or game clocks.

## Performance comparison

Short ordered native runs used the same `s_sushi_1_3` kitchen, held rice, 40
catalogue requests, and a ten-second full snapshot subscription. Walking discovery
was complete in all 40 requests. All throw previews were simultaneously complete
in 19/40 baseline requests and 30/40 initial fixed requests; negative previews retry after
two seconds. The final baseline catalogue withheld all three cookers; the fixed
build offered all three.

| Measurement | Before | After |
| --- | ---: | ---: |
| Full capture rate | 4.923 Hz | 4.937 Hz |
| Skipped sequences | 0 | 0 |
| Capture median / p95 | 21.292 / 28.001 ms | 19.529 / 26.954 ms |
| Catalogue request median / p95 | 42.836 / 63.917 ms | 45.653 / 61.944 ms |

These are short headless samples, not a rendering FPS benchmark or proof of
unchanged worst-case latency. The final run had no simultaneous build and its
maximum capture was 30.764 ms, versus 29.541 ms before. An earlier fixed run
overlapped compilation and included a 110.238 ms maximum. Positive pot proofs
also do useful work that the old height rejection skipped. Existing frame budgets
and counter/player paths are preserved; native atomic queries can exceed a slice.
Private measurements are in `.local/rice-cooker-{baseline,fixed,final}-profile/`.
