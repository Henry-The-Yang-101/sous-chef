# Walking jitter — 2026-11-05

Two different causes were reproduced, including native story 4-2
(`s_rapids_4_2A`). The fixes keep live geometry checks, actual arrival requirements,
portal entry timing and the 500 ms stall-rerouting watchdog.

## Raft arrival

The raft moves the chef through `DynamicLandscapeParenting`, which reparents it
to an `IParentable` attach point. That transform motion is separate from
`RigidbodyMotion.GetVelocityXZ()` and `SurfaceMovable.GetVelocity()`. The old
arrival calculation subtracted the moving destination's velocity without including
the chef's inherited parent motion. A chef stationary relative to the raft was
therefore reported as moving past the target, and alignment timed out. Higher
actions can retry navigation after that failure.

The navigator now measures parent transport at the previous chef position and
includes it in relative velocity for braking, continuation limits and settling.
Reparenting starts a new parent sample. Observed displacement, native walking,
wind and surface motion still participate; this does not relax arrival thresholds.

The native fixture resets 4-2, walks to Lettuce and then the bin, and polls the
catalogue during each walk. It uses ordinary navigation requests and native spawns.
The empty bin's expected completion is `positioned_no_action`.

| Build / requested FPS | Successful walks | Alignment failures | Expired input frames |
| --- | --- | --- | --- |
| Before parent correction / 60 | 3 / 6 | 3 | 0 |
| Final / 60 | 6 / 6 | 0 | 0 |
| Final / 20 | 6 / 6 | 0 | 0 |

All three baseline bin approaches failed despite stable relative positions.
Every corrected bin approach completed, reporting zero relative speed.
Evidence: `.local/walk-jitter-native/raft-comparison.json`, the corresponding
`raft-initial-poll/`, `raft-fixed-60/` and `raft-fixed-20/` traces, and the local
`raft_walk.py` runner. Native assembly inspection is saved beside them.

## Short final-approach commands

On the stair kitchen's calm final approaches, native reads arrived 21–32 ms after
18–19 ms walking commands. The expired commands caused brief braking even while
the route remained valid. Three 60 FPS round trips recorded six such frames;
the checked-continuation change recorded zero over the same three round trips.

Final approaches can now extend their input window only after checking the whole
segment and limiting it before closest approach to the working stance, including
one extra physics step. Braking still uses the original frame horizon. Wind,
surface flow and slippery approaches retain their previous windows: an early
experiment exposed a mine belt-exit contact when flow changed during an extended
command. With that restriction, both mine and sushi round trips passed, including
all six directed-conveyor probes.

Evidence: `.local/walk-jitter-native/input-expiry-comparison.json` and
`final-conveyors/report.json`. These are native samples, not a claim that every
kitchen or scheduling pattern is free of pauses.

## Verification

The full check passed 2,885 core/assembly assertions, 422 navigation checks,
1,049 action checks, 20 harness checks and 133 Python tests. Regressions cover
parent translation/rotation, actual motion past separate targets, pending native
walking, bounded continuation, blocked extensions and unchanged environmental
timing. A separate regression caught and fixed standalone stall recovery releasing
its input owner: recovery now issues neutral input; completion/Stop still releases.

The final native build completed **22/22 walks**: twelve raft walks, four conveyor
legs, two portal legs with two confirmed teleports each, and four stair legs at
60/20 FPS. Final stair and raft samples had zero expired walking-input frames.
Portal entry retained its existing short windows and recorded five expired input
frames while still completing both legs; this change does not promise to remove
every native input pause. Final traces are under `final-portals/`,
`final-stairs-60/` and `final-stairs-20/` in the same runtime directory.

Final native plugin SHA-256:
`5b87fea80a3788d693ad3fd82279ae2c6fe8b7342d285755afd1e8e4035d646a`.
The normal interactive installation was not updated.
