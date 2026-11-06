# Rice refill and handoff preference

The 1-3 report exposed two separate limits. Preparation used the time until the
first pot **finished** as the deadline for all further work. Nearby supplied rice
could therefore wait alongside an empty pot. Separately, pickup navigation used
a counter-height sight ray for low loose ingredients; its vertical and spherical
range limits could reject food that had safely settled on the floor.

Preparation now budgets against time before ruin, reserving travel back to the
pot and two seconds for collection. Already-ready batches retain priority.
Low loose items use a ray at the food's height, with the same obstacle checks,
walking proof and native pickup verification. Raised items and grid stations keep
their existing height checks.

Handoff selection now compares walking with checked throws, including when a
counter is walkable. Its score combines predicted travel/action time with a
spacing cost of `0.6 * abs(distanceInTiles - 3)`. Thus the reward peaks three tiles
away and falls on either side. The native grid supplies tile spacing when
available; standard spacing is 1.2 world units. Ground landing seeds use the same
preference, retain a close ring for narrow platforms, and still require supported
food footprints and a pickup route from P1's side. A short support margin follows
the touchdown velocity relative to the receiving platform, rejecting throws that
can slide past its edge. It checks floor support without treating an object that
can stop sliding food as a missing floor. Spacing is a preference, not
a requirement that can strand the task.

Selection checks at most four counter throw previews plus the player and floor
options. Comparison runs at 10 Hz with a 0.4-second grace period for pending
proofs before selecting an already-feasible handoff. Existing flight planning
remains sliced and uses the secondary worker. This adds no synchronous worker
wait or unbounded search to the frame loop.

## Native validation

`scripts/rice_handoff_audit.py` runs against an explicitly isolated Unity runtime:

```sh
python3 scripts/native_simulator.py --runtime .local/rice-check start --level s_sushi_1_3 --port-base 26202
python3 scripts/rice_handoff_audit.py --runtime .local/rice-check --port-base 26202 --output .local/rice-results
python3 scripts/native_simulator.py --runtime .local/rice-check stop
```

The fixtures supply food exactly once. P2's movement, work, transfers, cooking
progress, throws and delivery verification use production inputs and native
physics. No fixture advances a cooker, moves P2 during the task, retries an
accepted request or credits an intended handoff.

| Case | Required outcome |
| --- | --- |
| Late counter/floor rice, 1-3 | Supply rice while P2 returns with the second batch, near the first pot's completion. Fill all three pots before the first handoff, consuming an exact supplied rice item. |
| Floor-only rice, 1-3 | Same timing, with only loose floor rice supplied. The exact loose item must fill the spare pot. |
| Efficient spaced handoff, 1-3 | Give P2 chopped cucumber at the initial spawn. A walkable counter exists. Require an actual throw, the exact item's confirmed delivery, and a settled position near the three-tile ring. |
| Repeated raft deliveries, 4-2 | Deliver raw lettuce, chicken and tomato twice in ABCABC order, checking all six actual receipts. This caught a missed chicken before adding the post-impact support margin. |

All three passed on November 5, 2026. Counter rice `-27680` and floor rice
`-54158` were consumed into the spare pots in their respective runs. Cucumber
`-45456` was thrown with zero planned walking and settled 3.83 world units
(about 3.2 standard tiles) from P1. Its exact identity was confirmed as delivered.
This measures saved walking, not a claim that every throw has lower elapsed time.

Evidence is in `.local/rice-handoff/low-pickup/`, `floor/`, `runout/` and
`final-handoff/`. The original build
failed the matched late-rice assertion in `before-late/` and chose walking in
`before/handoff/`. Intermediate throw trials in `after/` and `settling/` exposed
the low-food pickup issue. The final implementation fixes pickup geometry and
checks supported runout without simulating post-impact contact dynamics. The
raft rerun delivered all six items, and the 2-1 ready-during-placement case still
delivered both ready chickens before chopping potato. Cooking overlap and the
blocked, parked-basket handoff also passed in `regressions/`. Action regressions cover
the three-tile score, the rice timing window, thin floor food versus elevated
food, unsupported runout, and narrow-platform landings. Full repository checks passed, including
the native API/worker checks, navigation/action suites and 131 Python tests.

These runs do not benchmark live rendered FPS. The normal installed game was not
restarted or modified by the isolated audit.
