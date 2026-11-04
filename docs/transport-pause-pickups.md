# Transport, pause and moving pickups

## Preparation while deliveries travel

Preparation records a confirmed conveyor/moving-counter placement as an in-flight
delivery and continues with the next ingredient or repetition. The exact object is
reserved for the human immediately, so later preparation cannot reclaim it.
Arrival checks run in the background at most five times per second, sharing one
human route observation per player for all shipments. A finite task reports
success only after its last preparation and all dispatched deliveries arrive.
An infinite task keeps preparing while transport runs. Stop cancels the task and
tracking, leaving already-dispatched objects in the kitchen.

## Pause and resume

Main-layer pause suspends autonomous navigation, primitive actions, logistics and
tasks. The existing lease and request remain owned while all input reads return
neutral. Unpause shifts finite deadlines, work/verification timers and navigation
watchdogs, then rechecks the route against current physics. It does not replay a
previous button edge. Stop, disconnect, round changes and reassignment still
cancel; unpause cannot revive a released owner. Manual raw-input leases retain
their usual cancellation behavior.

The native simulator accepts `pause on` / `pause off` through its mailbox. These
invoke the game's `TimeManager.SetPaused(Main, ..., fixtureKey)`, including native
physics/animation arbitration; they do not fake a paused control receipt. Native
validation records a chopping task remaining owned for 3.5 seconds of pause and
then completing under the same request. Stop while paused took about 15 ms.
Evidence: `.local/pause-native-audit/paused.json`, `resumed.json`, `stopped.json`.

## Moving pickups

Pickup pins the requested food object when its original source is a conveyor
station. Navigation uses that object's current attach station for its walking
stance, because attached cargo can have disabled colliders. It follows the new
holder when the food crosses between belt stations. Pickup can proceed as soon
as the grounded chef's native selection and pickup handler prove that the exact
food is in reach, including the native referral to incoming cargo before attach
ownership changes. Moving and rotating counters use the same native opportunity
check rather than requiring the chef to settle relative to the moving object.
Dispensers remain dispenser targets; a changed prefab blocks the original
request rather than silently substituting a different ingredient.

The check runs inside the existing interaction tick. It adds no navigation
searches to the plugin's idle loop; route search still uses the worker process.

## Reproducing the checks

Start an isolated native simulator, then run each case with its runtime path,
port base and separate output directory:

```sh
python3 scripts/task_transport_audit.py belt --runtime .local/transport-fixes-native --port-base 26332 --output .local/transport-final-audit/belt
python3 scripts/task_transport_audit.py pipeline --runtime .local/transport-fixes-native --port-base 26332 --output .local/transport-final-audit/pipeline
python3 scripts/task_transport_audit.py pause --runtime .local/transport-fixes-native --port-base 26332 --output .local/transport-final-audit/pause
```

The belt case loads native story 4-5 (`s_sushi_4_5`), stages P2's initial pose at
a certified holder approach, measures real cargo displacement, and verifies the
original food's ID in the server carrier after one ordinary pickup request.
There are no pose writes during the measured pickup.

The pipeline case also uses 4-5. An explicit `task-fixture in-flight ITEM_ID 1`
checkpoint adds already-moving native belt cargo to the running task's delivery
tracker. It changes neither food placement nor motion. The case verifies native
chopping of another ingredient while that shipment remains pending under the
same task request. This tests scheduler overlap after dispatch, not end-to-end
handoff selection/placement on the reported fruit DLC kitchen. Finite-task
arrival waiting and continued repetitions are covered by executor regressions.

The pause case preserves native chopping for at least 2.5 seconds of Main-layer
pause, unpauses with input focus still suppressed, then restores focus and
verifies completion under the original request. It also verifies Stop during a
second pause. Full receipts, independent object snapshots and the loaded binary
hash are written to each output directory.

Executor tests additionally cover translating and rotating counters, changing
dispenser contents, incoming belt referrals, disabled cargo colliders and holder
changes. The exact moving mine-dispenser encounter was not reproduced in native
testing; moving dispenser behavior is covered by these executor regressions.
