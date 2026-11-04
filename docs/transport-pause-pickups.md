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
