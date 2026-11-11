# Separate planning process

The plugin can start one persistent .NET 8 worker alongside Overcooked! 2. The
worker has a separate managed heap and reads only detached values. Unity object
access, native physics queries, eligibility predicates, input leases and action
execution remain in the game's main thread. Normal observation ports and public
JSON fields/rates are preserved.

## Work moved

- Full/gameplay snapshot and control-reply JSON formatting and UTF-8 encoding run
  in the worker. The game sends a private typed binary representation from its
  transport threads and receives final NDJSON bytes. Catalogue replies are frozen
  on the update thread, then encoded/written by their connection thread. Socket
  writes and worker waits never run inside the update dispatcher.
- Stable, incomplete idle walking searches can hand off Dijkstra expansion and
  route bookkeeping to the worker. The plugin produces native-checked adjacency
  batches under its existing search budget. The worker retains its graph/frontier
  and sends only newly settled nodes, costs and parents. A missing adjacency list
  pauses before the corresponding node is settled, preserving shortest-path and
  partial-catalogue certification. Already certified native results remain usable
  until the worker has reached an equally advanced frontier.
- Read-only throw previews batch the 36 calm trajectory seed endpoints for an
  angular ring. Native launch vectors and fixed-step parameters are captured in
  the plugin. Returned endpoints are reused only when those exact inputs still
  match. Candidate stances, final aiming, full trajectory/collision checks and
  native throw/catch predicates still run in the game. Wind-dependent seed
  calculations retain native positional overlap queries.

This does not move scene extraction or all physics planning out of the game.
Full-scene discovery and reading live adapter fields still have main-thread cost.
It also does not replace the existing sampled observations with an event log;
complete event coverage would require a separate audit of native lifecycle and
state changes. Moving those reads to arbitrary threads would weaken consistency.

## Continuity and failure handling

A worker search has its own connection and monotonically increasing generation.
A new round, invalidation or replaced starting state discards that connection and
its pending results. The existing exact geometry/chef/cargo/surface/wind/query
continuity checks apply before accepting results. Support dependencies accumulate
across batches, including earlier supports that were not visited in the latest
batch. Native conveyor continuity checks remain in force; portals and arbitrary
unversioned moving-surface rules retain native planning.

A new or changing starting state gets a native search slice immediately. The
worker handoff requires a successful continuity check on a subsequent refresh.
This matters while holding an animated object: repeatedly restarting an
asynchronous search could otherwise leave the destination list empty forever.
Completed native trees can simply be reused without doing redundant worker work.
Active route execution continues to validate native state on every update.

The plugin never waits for the worker on the main thread. Walking mailboxes hold
one job/response; adjacency batches contain at most 128 nodes, with a bounded
32,000-node production queue and the original 16,000-node search limit. Graph
queue saturation falls back to native planning. Throw work uses one shared
connection and a queue capped at 32 batches. Encoders retain their existing
single-pending/latest-frame backpressure. Protocol readers bound array sizes and
nesting; local connections authenticate with a per-launch random token.

Worker exit/socket failures preserve native planning and encoding fallbacks. The
worker watches its parent game process and exits with it; plugin shutdown stops
only the child it started. It never owns Player 2 input. Diagnostics expose worker
state/PID/error, encoded-message and throw-batch counters, and the current search
backend.

## Build and configuration

`python3 scripts/check.py` builds both components and tests the actual process
boundary. `scripts/install.py` installs the worker beside the plugin using the
same owned-file manifest checks. It also records the existing pinned dotnet host
path in `worker-dotnet.txt`; moving/removing that runtime requires rebuilding and
reinstalling. Worker files live in the adjacent `worker/` folder. The native
simulator copies the same files into its isolated runtime.

`[Worker] Enabled = false` in the BepInEx plugin config, or launching with
`SOUS_CHEF_WORKER=0`, selects the native backend for comparison. Worker startup is
asynchronous; the bridge remains usable through its fallback during startup or
if the runtime is unavailable.

## Validation

Automated checks launch the real worker and compare complete weighted-graph
routes and every published partial cost against the original Dijkstra search.
They cover incremental certification, generation reset, dead-worker fallback,
public JSON equivalence (including nonfinite values, full-range integers,
primitive arrays, packed transform records and UTF-16 surrogates), calm-flight
endpoint parity with the production integrator, retained support dependencies,
and changing-start destination discovery with the worker enabled.

Native simulator measurements use the original Unity 2017.4.8f1/PhysX player with
Null graphics. They can validate native integration and main-thread extraction,
but do not establish an improvement to on-screen rendering FPS. Compare the same
kitchen, held item, stream subscriptions and catalogue workload when evaluating
frame-time percentiles and destination-list/throw-preview completion.

Native integration checks on 2026-11-02:

- In `s_sushi_1_3`, the final handoff progressed through 0, 12, 25 and 34 certified
  destinations. Nineteen of twenty samples used the worker search backend; the
  catalogue was complete in the final sixteen samples.
- After native pickup of Cucumber, both the worker-enabled run and its native
  fallback retained all 34 destinations in every one of forty sampled catalogue
  requests. The worker completed 25 calm seed batches and 162 encoding jobs before
  the deliberate worker-exit test. This case's changing starting-state proof used
  native walking search, while encoding and calm seed batches remained external.
- Both observation runs sustained approximately 5 Hz with no skipped sequences.
  Worker-enabled full-capture median/p95/max was 19.007/24.983/100.900 ms; after
  terminating that child, fallback measured 23.486/25.755/26.191 ms. The worker run
  includes an initialization spike and precedes the fallback run, so these short
  ordered samples do not establish improved worst-frame latency or rendering FPS.
- The `unfocused-player` and `unfocused-place` fixtures both ended in native
  `throw_received` outcomes with all expected harness/input-gating checks passing.
  These use the original game and physics, not the analytical test stubs.

Private evidence: `.local/worker-native-final-search.json`,
`.local/worker-native-comparison.json`, `.local/worker-native-throw-audit/report.json`
and `.local/worker-check.log`. The failed earlier held-item handoff was corrected
before installation and covered by a changing-start integration regression.

Bridge 0.7.1 additionally handles stationary vertical numerical fluctuations by
revalidating native starting connectors and requiring identical ordered nodes and
costs before retaining work. It also shares wind-volume discovery across targets
within one synchronous catalogue pass. Moving work to the worker alone did
not noticeably reduce stutter in the tested Mine kitchen.
