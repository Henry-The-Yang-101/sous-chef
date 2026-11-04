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
