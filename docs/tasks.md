# Autonomous tasks

`ActionClient.run_task(action, repetitions=1, items=None, player_id=1)` owns one
request and control lease until all steps and repetitions finish. Use `None` for
unlimited repetitions; finite counts are 1–10. No model is invoked by the executor.
Each repetition executes a complete batch. Completion leaves P2 idle.

Environmental blocks wait indefinitely with neutral input and bounded periodic
revalidation. Completed steps are retained. Explicit Stop, release, disconnect,
pause, focus loss, reassignment, respawn and round changes still cancel and restore
native input bindings. Identity changes and unverified button outcomes fail safely
with cargo retained. Waiting does not replay a successful step or disposal.

The existing interaction/logistics recovery window is also indefinite; time spent
recovering suspends finite watchdogs. Button outcome verification remains bounded.
New tasks and their child actions have no overall deadline.

The localhost Tasks panel uses `/api/task`, with the current `sessionId` and
`arguments` containing `action`, `repetitions` (integer or null), and preparation
parameters where required. `catalog.taskOptions` contains only currently eligible
tasks. The server and executor both revalidate before starting. Stop works while
waiting, working or between repetitions. Browser disconnection closes ownership
as before. There is no task queue or voice/Jev integration yet.

Wire: `task REQUEST ACTION 1..10|forever`; preparation additionally takes player
`1|3|4`, followed by 1–10 triples of base64url selector, preparation state and
quantity `1..10`. Task lines are bounded to 2048 ASCII bytes; legacy commands keep
their 256-byte parser bound.
