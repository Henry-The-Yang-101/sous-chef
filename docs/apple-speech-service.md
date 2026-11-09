# Apple speech service (milestone 3)

Implemented 6 November 2026. This is local command capture and inspection;
Jev interpretation and voice-driven plan acceptance belong to milestone 4.
No speech result from this milestone can run a native task.

## Using it

Run the harness normally with `python3 -m harness.server` and open **Voice
commands**. Voice starts muted on every harness restart. The helper is built
lazily when you enable the microphone or refresh devices, using Xcode command-line
tools and macOS 27. Its binary is cached by source hash
under `.local/apple-speech-service/`. A missing SDK or unsupported OS affects
only voice; existing manual controls and the scheduler remain available.

1. While muted, optionally refresh devices/locales and select the input device,
   recognition locale, submission mode, fast-results setting, pause duration,
   maximum command duration and energy threshold. Save settings explicitly.
   Settings are shared across tabs for this harness session.
2. Enable the microphone. Permission, recognition preparation, asset downloading,
   unsupported locales and device failures are visible. Model assets may need an
   Apple download; microphone audio stays on the Mac.
3. Hold **Hold to talk**, then release to finalize. The button supports pointer,
   touch, Space and Enter. Keyboard focus must be on the button. A phone controls
   the Mac's microphone; it does not capture phone audio.
4. Inspect the assembled transcript and its per-segment alternatives, confidence
   spans, audio ranges, command ID and timing. The default destination is **Local
   inspection only**. **Local fake model transport** additionally echoes each
   immutable command through the server's dispatch gate; it uses no network and
   produces no executable plan.
5. **Mute voice / Jev off** flushes current audio, drafts, inspection history and
   fake responses. The UI shows muting until capture stops or the helper exits.
   Accepted native work and the scheduler queue continue. **Stop** additionally
   cancels native/queued work using the existing independent control path and
   invalidates unfinished speech without changing the mute preference.

Optional automatic mode opens a turn when audio energy crosses the threshold
and finalizes after the configured audio pause (800 ms initially). It retains up
to 250 ms of local pre-roll, never across an already committed boundary. This is
an energy detector, not a semantic voice detector: game audio or conversation can
open a turn or delay its boundary. Push-to-talk remains the dependable initial
mode. The input energy display helps adjust the threshold. Neither Apple detector
events nor transcript silence are used as the sole endpoint signal.

Changing tab, losing browser focus, pointer cancellation or losing the PTT
heartbeat discards an unfinished held turn. It does not cancel accepted game
work. Automatic capture belongs to the harness, not to the tab, so reloads do not
stop it. All tabs observe shared state. Mute and Stop remain independent controls.

## Ownership and protocol

- `scripts/apple_speech_service.swift`: persistent on-device helper, PCM capture
  from the selected device in a speech-compatible format, audio boundary policy, bounded
  input buffering, Apple result consumption, lifecycle/error events.
- `scripts/apple_speech_service.py`: source-hashed build and exec launcher, with
  microphone purpose strings embedded in the executable. Diagnostics use stderr.
- `harness/voice/assembler.py`: generation- and sequence-checked transcript
  intervals, provisional replacement, preserved finalized ranges, one immutable
  command per drained turn, segment alternatives capped at three without a
  Cartesian product. Missing confidence metadata stays unknown.
- `harness/voice/service.py`: one global helper lifecycle, bounded transient
  command inspection, PTT ownership/lease, device and finalization watchdogs,
  server-side settings and generation validation.
- `harness/voice/gate.py`: mute/dispatch/result-adoption serialization and the
  initial local fake transport. A future transport must start each individual
  request synchronously in its nonblocking `start` method, return a cancellable
  future, wait outside the gate, and adopt results through the generation fence.
  All retries, clarification calls and compiler stages must use the same gate.

`POST /api/voice` uses the existing Host/Origin, page-token and viewer checks.
Its `voice` object has an `operation`, the current `generation` (except that mute
always wins), and operation-specific `settings` or `turnId`. Operations are
`devices`, `configure`, `arm`, `mute`, `begin`, `heartbeat`, `finalize`, `cancel`,
and `clear`. State is included in the existing `/api/state` response. Snapshot
instance IDs/revisions prevent delayed browser responses from rolling back mute.
No natural-language/model API or client-side API key is introduced.

Helper stdin/stdout uses NDJSON protocol version 1. Every event has
`speech_generation`, increasing `event_sequence`, and `emitted_at_ms`. Commands
are `devices`, `configure`, `arm`, `mute`, `reset`, `begin`, `finalize`, `shutdown`.
Events include `ready`, `status`, `audio_level`, `turn_started`,
`transcript_update`, `turn_boundary`, `finalize_completed`, `device_changed`,
and `error`. Transcript events carry turn identity, audio range, finalization
watermark, text, alternatives and available confidence/time spans. An unchanged
provisional result can become immutable at the terminal stream barrier; its
committed segment records `finalization_source: drained_result_stream`.

### Finalization barrier

Each command owns one analyzer session. The helper process and loaded model
resources persist (`modelRetention: processLifetime`), while successive sessions
supply an explicit consumer barrier:

1. PTT release or the audio pause detector captures the end of the last input
   buffer assigned to the turn. Future audio belongs to another turn.
2. Finish this turn's bounded input stream and await `analyzeSequence(_:)`
   for the last audio sample the analyzer consumed. Pass that sample time to
   `finalizeAndFinish(through:)`.
3. Await the result-consumer task until the result stream is drained. This covers
   delayed result consumption and finalization without a replacement final event.
4. Emit `finalize_completed` with cutoff, finalization watermark, last consumed
   transcript-event sequence and `results_drained: true`.
5. Python validates that evidence against its interval buffer before committing.
   Finalization tasks commit in turn order while subsequent audio can accumulate
   in a separate bounded turn. Empty turns produce inspection receipts, not model
   requests. Repeating the same words intentionally creates distinct command IDs.

This uses terminal result-stream draining instead of guessing that an analyzer
range callback or a return from `finalize` means Python has consumed everything.
Apple documents that finalization can precede application result consumption and
that finishing terminates result streams while allowing remaining results to be
read: [finalize](https://developer.apple.com/documentation/speech/speechanalyzer/finalize%28through%3A%29),
[SpeechAnalyzer lifecycle](https://developer.apple.com/documentation/speech/speechanalyzer).

### Failure, bounds and privacy

The helper permits at most three outstanding turns and 256 queued audio buffers
per turn. Python caps each turn at 256 transcript segments and 16,000 characters
including retained alternatives. Default duration is 30 seconds, configurable
from 1–60 seconds. Overflow rejects the entire command rather than submitting a
truncated instruction. Finalization has a 15-second watchdog; absent input has a
5-second watchdog while listening. PTT renews every 750 ms and is discarded after
a 3-second lease loss. These are initial implementation settings, not measured
latency promises or accepted-task deadlines.

Mute increments the generation immediately under the dispatch lock, cancels
pending model futures, clears queued helper commands and requests capture stop.
A helper that does not acknowledge mute within two seconds is terminated;
acknowledgment waits for its exit. Pipe writes are bounded, atomic and nonblocking
so they cannot stall this watchdog. Device loss, capture format change, helper
crash, invalid protocol and finalization failure invalidate drafts and close the
gate. Re-arming starts fresh; there is no audio or command replay. Stop and voice
operations do not hold the game I/O or scheduler lock and do not foreground the
game.

Inspection retains the latest finalized turn in memory for up to ten minutes.
Mute, Stop and Clear discard it. Transcripts are never journaled and no raw
audio is written to disk.
Authorized phone views can read the same transcript state through the existing
harness sharing connection. This is separate from model dispatch; the implementation
has no Jev SDK, remote model transport, audio upload or verbose transcript logging.

## Verification record and remaining acceptance work

Per the user's instruction, **no tests, browser execution, microphone capture,
or game-sound measurements were run** for this milestone. Static checks only:

- The Swift 6/macOS 27 helper compiled with the installed Xcode SDK.
- Python source compiled and JavaScript passed `node --check`.
- `git diff --check` passed.

Behavioral acceptance remains unverified: real overlapping/revised Apple results,
empty and unchanged-final turns, result-consumer races, long pauses, repeated
commands, PTT touch/keyboard cancellation, mute during preparation/finalization,
device loss, helper restart, desktop/phone layout, and actual endpoint timing under
game sound. The existing design's evaluation matrix remains the source of that
follow-up work. The local fake transport is wired, but no fake-transport test run
is claimed.

Inspection records last above-threshold audio sample to cutoff, helper boundary to
result-stream completion, and Python boundary receipt to immutable commit. These
provide measurement points; energy activity is not proof of the last audible
speech sample. Microphone evaluation must independently label that sample and
compare the proposed 400/800/1,500 ms pause settings and fast-results modes.

### Microphone crash fix (9 November 2026)

On macOS 27.0.1, the deprecated `AnalyzerInput.buffer` getter trapped on the
first captured buffer. The helper now gets PCM directly from AVFoundation and
wraps the audio in `AnalyzerInput` after metering and assigning it to a turn.
A local harness smoke test using the built-in Mac microphone verified device
discovery, metering, transcription in both capture modes, and mute followed by
re-arming. The test used local inspection with synthesized test phrases.
