# On-device speech recognition while playing

Research follow-up, **1 November 2026**. Moonshine Medium Streaming did not meet
the user's needs. This replaces the original study's default speech-recognition
recommendation. Sources and runtime implementations were inspected; no new models
were downloaded, no microphone audio was captured, and no co-running benchmark
was performed. The target machine is an M4 Pro with 24 GB unified memory, running
macOS 27.0.1.

## Revised selection

**First: Parakeet Ultra through FluidAudio/Core ML, with its encoder on the Apple
Neural Engine.** Compare Apple's **SpeechTranscriber/SpeechAnalyzer** if a
proprietary system model is acceptable. For an independently implemented local
alternative, compare **Qwen3-ASR-0.6B at 8-bit precision**, then **1.7B** if its
accuracy improvement justifies the extra work.

For a native continuously streaming recognizer, compare **Nemotron 3.5 ASR
Streaming 0.6B**. Its current Core ML distribution and chunk configuration need
care: the published upstream low-latency modes are not all shipped in the main
Apple conversion. **Voxtral Mini 4B Realtime 2602** is a capable larger streaming
alternative, but would put more work on the GPU alongside the game and any local
decision LLM.

The ranking is about deployment fit, not an established accuracy leaderboard
across these models or a measured response-time win on this machine.

## Parakeet Ultra: first candidate for short commands

Moondream's Ultra is a post-training of NVIDIA Parakeet TDT 0.6B v3. It retains
the architecture, tokenizer, and 25-language coverage. Its author reports
improvements over v3 on English, multilingual, background-noise, and long-form
evaluations. The weights are CC BY 4.0. These are upstream evaluations, not tests
of this user's microphone or kitchens. [Upstream model](https://huggingface.co/moondream/parakeet-ultra)

FluidAudio provides a Core ML conversion with an int8 encoder of about **595 MB**,
roughly **630 MB total model download**, and ANE placement by default. The runtime
author reports similar speed to v3 and better WER on its own paired evaluations.
Those speed results do not establish M4 Pro gameplay latency; download size is
also not resident-memory usage. [Conversion/runtime details](https://github.com/FluidInference/FluidAudio/blob/main/Documentation/ASR/ParakeetUltra.md)

Ultra uses an **offline encoder**. FluidAudio's sliding-window manager repeatedly
decodes overlapping windows; it is explicitly distinct from its cache-aware
streaming engines. For Sous Chef, start with VAD-segmented short utterances or
push-to-talk, then immediate final decoding. Add partial transcripts only if they
improve the interaction. Do not leave a long-form window's buffering policy in
the command path. [Runtime distinction](https://github.com/FluidInference/FluidAudio/blob/main/Sources/FluidAudio/ASR/Parakeet/Streaming/StreamingAsrManager.swift)

The hardware advantage is placing the large encoder on the ANE while the game
uses the GPU. CPU preprocessing/decoding, unified-memory traffic, and power/heat
remain shared. Core ML placement is a policy to verify, not proof of zero
contention. This is the preferred first configuration when the decision model
also uses Metal/MLX.

## Apple SpeechTranscriber: useful native comparison

Apple's newer SpeechAnalyzer/SpeechTranscriber is designed for on-device live
transcription, conversational audio, and low latency. It provides provisional
results that can be revised and finalized. Assets are downloaded and managed by
the system. The model runs outside the application process; this does **not**
make its memory or compute free for the machine. It is a proprietary platform
model, not downloadable open weights. [Apple's technical presentation](https://developer.apple.com/videos/play/wwdc2025/277/)

This is worth comparing on the installed macOS version rather than assuming old
Siri/dictation recognition represents the current API. Check supported locale,
asset availability, finalization behavior, and recognition of actual gameplay
commands. A small Swift helper can return transcripts to the existing Python
engine without moving task execution into Swift.

## Qwen3-ASR: small speed option and larger accuracy option

Qwen publishes **0.6B and 1.7B** ASR variants under Apache 2.0, with multilingual
coverage and context hints. The upstream toolkit's documented streaming path is
vLLM; use an Apple runtime rather than following CUDA deployment instructions on
this Mac. [Qwen model/toolkit documentation](https://huggingface.co/Qwen/Qwen3-ASR-1.7B)

The `moona3k/mlx-qwen3-asr` runtime has native MLX support and committed benchmark
artifacts. Its maintainer reports these **warm, idle-machine** medians on an
**M4 Pro, 48 GB**, for completed audio:

| Model configuration | 2.5-second clip | 10-second clip |
| --- | ---: | ---: |
| 0.6B, 8-bit | 100 ms | 230 ms |
| 0.6B, 4-bit | 90 ms | 170 ms |
| 1.7B, fp16 | 360 ms | 730 ms |

These are decode/inference timings, not last-spoken-word-to-final-command timings.
The same implementation reports a noticeable loss from quantizing everything to
4-bit on the harder English set; 8-bit is the first comparison when recognition
quality is the concern. A mixed-precision export is another option. The 1.7B
quality advantage is workload-dependent. [Benchmark protocol and results](https://github.com/moona3k/mlx-qwen3-asr/blob/main/docs/BENCHMARKS.md)

This runtime's live method uses a growing bounded audio window and text-prefix
rollback, not a cache-aware transducer. Its documented default input chunk is
**2 seconds**: retune that and final flushing for short commands before calling
it low latency. Context can include a small list of kitchen terminology. Skip
forced alignment and speaker diarization in the command path. [Streaming and
context interface](https://github.com/moona3k/mlx-qwen3-asr)

An MLX recognizer shares the GPU with the game and a local decision model. With
hosted Jev decisions, this is a more attractive tradeoff than when both ASR and a
local decision LLM continuously compete for that GPU.

## Native streaming alternatives

**Nemotron 3.5 ASR Streaming 0.6B** is a cache-aware streaming recognizer with
upstream lookahead/chunk modes including 80/320/560/1120 ms. Those numbers describe
algorithmic streaming settings, not total response latency on a Mac.
[Transformers' supported settings](https://huggingface.co/docs/transformers/v5.13.1/en/model_doc/nemotron3_5_asr)

The current FluidAudio model distribution documents **560 ms and larger** tiers;
the English Nemotron runtime has also moved to a **2240 ms default** for
throughput. Do not inherit that default for voice commands or promise the
upstream 80 ms mode from that distribution. The multilingual documentation,
published model card, and current loader are not fully synchronized, so pin and
verify a complete model/runtime combination. Separate community 80/320 ms
exports exist, but their caches/checkpoints are not interchangeable with older
FluidInference exports. They need conversion and live-quality validation before
becoming the default. [English runtime](https://github.com/FluidInference/FluidAudio/blob/main/Documentation/ASR/Nemotron.md),
[multilingual distribution](https://huggingface.co/FluidInference/Nemotron-3.5-ASR-Streaming-Multilingual-0.6b-CoreML),
[low-lookahead export](https://huggingface.co/mochiexists528/nemotron-3.5-asr-streaming-0.6b-coreml-lookahead)

**Voxtral Mini 4B Realtime 2602** is natively streaming, Apache 2.0, with a
documented **480 ms** quality/latency starting setting. MLX Audio supports actual
incremental microphone input, plus bounded streaming-session steps. A 4-bit
variant exists. It is a viable experiment if continuous live transcription is
important, but a larger GPU workload than a 0.6B transducer. Do not substitute
current hosted Voxtral 3 API claims for this downloadable 2602 model's behavior.
[Model/settings](https://huggingface.co/mistralai/Voxtral-Mini-4B-Realtime-2602),
[Apple runtime](https://github.com/Blaizzy/mlx-audio/blob/main/docs/models/stt/voxtral-realtime.md)

NVIDIA's newly released `nemotron-labs-unified-asr-0.6b` is also promising: its
author reports better streaming accuracy than Nemotron 3.5 at matched latencies.
An inspected model card does not establish a validated Apple runtime. Track it
as a port/evaluation candidate rather than a ready-to-use Mac recommendation.
[Model card](https://huggingface.co/nvidia/nemotron-labs-unified-asr-0.6b)

## How to choose while the game is running

Keep one recognizer loaded. Use a lightweight VAD and a short audio pre-roll,
flush promptly at push-to-talk release or a confirmed speech boundary, and avoid
running a second heavyweight recognizer in parallel. Buffering, endpointing, and
provisional-text commitment can dominate inference time.

First compare **Ultra/Core ML**, **Apple SpeechTranscriber**, and **Qwen 0.6B
8-bit** on the same 30–50 representative commands, with the game and actual
decision engine active. Include negation, self-corrections, plate counts, short
urgent instructions, accents, and realistic background game sound. Measure:

- Correct task, target, and scope after transcription.
- Last audible speech sample → final usable transcript, p50 and p95.
- Recognition compute time separately from endpointing and buffering.
- Game frame times, audio underruns, resident memory/pressure, and sustained load.

If the smaller candidates still miss the user's words, add Qwen 1.7B and Voxtral
4B to that comparison. Aim initially for **200–400 ms after the end of a short
utterance to a usable final transcript**; this is an engineering target, not a
measured guarantee. Judge stop recognition and task cancellation separately.
