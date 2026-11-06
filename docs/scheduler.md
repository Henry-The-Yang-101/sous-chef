# Local scheduler — Milestone 2

Implementation of the [voice-control design](jev-voice-control-flow-design.md).

## Native receipt protocol

The control hello and status advertise `receiptProtocolVersion: 1`. Status also
provides `kitchenEpoch` (changed on round or player assignment) and
`dispatchGeneration` (advanced by Stop). Existing clients remain compatible.

- `execute REQUEST EPOCH GENERATION OPERATION ...` wraps an existing validated
  action. Use a unique 32-character hexadecimal request ID.
- `receipt QUERY_ID REQUEST` reads its latest detached receipt, or null if unknown.
- `cancel REQUEST EPOCH GENERATION` is a fenced cancellation with its own receipt.
- The existing unconditional `stop ID` remains independent and advances the fence.

A matching ID/payload returns the retained receipt without reacquiring input.
Conflicting payloads are rejected. Fences are checked before input acquisition.
Stop/cancel requests have priority over queued ordinary commands. At most 256
receipts are retained, preserving active entries. Round/assignment changes clear
them; an absent or evicted receipt is **unknown**, never permission to replay.
Waiting and suspension are nonterminal. Navigation success requires native
arrival; task success retains the executor's completed repetition/step evidence.

Initial validation: plugin compiled against the installed Unity assemblies;
C# transport/core suite passed 2,541 assertions; Python action-client tests passed
13 tests. Native end-to-end scheduler verification is recorded below as added.
