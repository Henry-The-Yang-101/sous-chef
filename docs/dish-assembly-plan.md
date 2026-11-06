# Dish assembly implementation plan

`assemble_dish(order)` is one durable job for one exact live native ticket. The caller (eventually Jev) owns intent and resource allocation: it supplies the order token, alternative bases, component instances, and any processing vessels/stations. The executor never chooses another ticket or silently takes unassigned food. This does not introduce a Jev dependency.

## Milestones

1. Add the typed request, live order catalogue, recipe proof model and prepared-component assembly scheduler. Choose among assigned compatible bases; retain the chosen base; reobserve actual composition after each action. Add an authorization callback inside the primitive's last pre-press check.
2. Extend the scheduler with recipe-derived mixing/cooking dependencies, using only assigned vessels/stations. Native compatibility remains a physical constraint, never recipe authorization. Preserve cooking/mixing wrappers and quantities in recipe proofs.
3. Expose the request in the client/harness, add adversarial tests and isolated native replays, and document results and unsupported cases.

## Lessons carried forward from preparation

- Use exact authoritative identities, including clean-stack top plates. A replacement occupant is not the selected cargo. Lost allocations wait; they do not authorize taking another copy.
- Existing progress and useful held cargo outrank fresh work. Choose executable dependencies rather than blocking behind one inaccessible ingredient.
- Check required destinations before picking up cargo. Park unrelated held items through the existing recovery path and preserve the same request/lease.
- Before irreversible input, revalidate the ticket, source, destination, observed composition, recipe proof and native placement. Do not rely on an earlier route/catalogue snapshot.
- Only observed source consumption and the expected resulting composition count. Never replay an ambiguous pressed transfer. A source count or empty hands alone is insufficient.
- Protect ready processing batches before other work, observe native processing state, and preserve unfinished dependency ownership while moving equipment.
- Reuse inventory observations and back off while blocked. No unbounded per-frame recipe search or full-scene scans.
- Keep movement, portal, conveyor, moving-support and hands recovery in shared primitives. Assembly does not use handoff heuristics as completion evidence.

## Verification

Exercise alternative bases; wrong recipe/native-compatible ingredient; duplicate ingredients and processing state; partial dishes; foreign/caught cargo; moved/replaced/taken resources; ticket expiry during navigation and at the input edge; interrupted/suppressed and ambiguous transfers; blocked dependency selection; and combined processing. Run the repository checks and native headless scenarios against the built DLL. Record waits as waits, and leave the user's normal game installation unchanged.
