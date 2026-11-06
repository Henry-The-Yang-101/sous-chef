// Persistent, local-only NDJSON microphone service. No model/network client.
import Foundation
@preconcurrency import AVFoundation
import Speech
import CoreMedia
import Darwin

private struct ServiceError: LocalizedError {
    let message: String
    var errorDescription: String? { message }
}
private struct Settings: Decodable, Sendable {
    var locale = "en-US"
    var deviceId = ""
    var mode = "push_to_talk"
    var pauseMs: Double = 800
    var maxCommandMs: Double = 30000
    var fastResults = false
    var energyThreshold: Double = 0.015
}
private struct Control: Decodable {
    let protocol_version: Int
    let type: String
    let speech_generation: Int
    let turn_id: String?
    let settings: Settings?
}

@MainActor
private final class Turn {
    let id: String
    let generation: Int
    let start: Double
    let analyzer: SpeechAnalyzer
    let input: AsyncStream<AnalyzerInput>.Continuation
    var startup: Task<Void, Error>!
    var results: Task<Void, Error>!
    var lastResult = 0
    var lastSpeech: Double?
    var end: Double
    init(id: String, generation: Int, start: Double, analyzer: SpeechAnalyzer,
         input: AsyncStream<AnalyzerInput>.Continuation) {
        self.id = id; self.generation = generation; self.start = start
        self.end = start; self.analyzer = analyzer; self.input = input
    }
}

@MainActor
private final class SpeechService {
    var generation = 0
    var sequence = 0
    var settings = Settings()
    var armed = false
    var capture: CaptureInputSequenceProvider?
    var captureTask: Task<Void, Never>?
    var prepareTask: Task<Void, Never>?
    var finishTask: Task<Void, Never>?
    var deviceObserver: NSObjectProtocol?
    var sessionObserver: NSObjectProtocol?
    var turns: [String: Turn] = [:]
    var active: Turn?
    var locale: Locale?
    var timeline: Double = 0
    var lastMeter: Double = 0
    var format: AVAudioFormat?
    var preroll: [(AnalyzerInput, Double)] = []

    @discardableResult
    func emit(_ type: String, _ fields: [String: Any] = [:], generation eventGeneration: Int? = nil) -> Int {
        sequence += 1
        var event = fields
        event["protocol_version"] = 1
        event["type"] = type
        event["speech_generation"] = eventGeneration ?? generation
        event["event_sequence"] = sequence
        event["emitted_at_ms"] = Date().timeIntervalSince1970 * 1000
        if let bytes = try? JSONSerialization.data(withJSONObject: event, options: [.sortedKeys]) {
            FileHandle.standardOutput.write(bytes)
            FileHandle.standardOutput.write(Data([10]))
        }
        return sequence
    }

    func inventory() async {
        let savedGeneration = generation
        let locales = await SpeechTranscriber.supportedLocales.map(\.identifier).sorted()
        guard generation == savedGeneration else { return }
        let devices = AVCaptureDevice.DiscoverySession(deviceTypes: [.microphone, .external],
            mediaType: .audio, position: .unspecified).devices
        emit("ready", ["devices": devices.map { ["id": $0.uniqueID, "name": $0.localizedName] },
                       "locales": locales, "available": SpeechTranscriber.isAvailable])
    }

    func command(_ line: String) async {
        do {
            let command = try JSONDecoder().decode(Control.self, from: Data(line.utf8))
            guard command.protocol_version == 1 else { throw ServiceError(message: "Unsupported protocol") }
            guard command.speech_generation >= generation else { return }
            if command.speech_generation != generation {
                generation = command.speech_generation
                cancelCapture()
            }
            switch command.type {
            case "devices": await inventory()
            case "mute":
                cancelCapture()
                emit("status", ["status": "muted"])
            case "configure":
                cancelCapture()
                if let value = command.settings { settings = value }
                emit("status", ["status": "muted"])
            case "arm", "reset":
                cancelCapture()
                if let value = command.settings { settings = value }
                let current = generation
                emit("status", ["status": "preparing"])
                prepareTask = Task { [weak self] in
                    guard let self else { return }
                    do { try await self.prepare(current) }
                    catch { if self.generation == current && !Task.isCancelled { self.fail(error.localizedDescription) } }
                }
            case "begin":
                guard armed, settings.mode == "push_to_talk", active == nil else {
                    throw ServiceError(message: "Microphone is not ready for push-to-talk")
                }
                try startTurn(id: command.turn_id ?? UUID().uuidString, start: timeline)
            case "finalize":
                guard let turn = active, turn.id == command.turn_id else { return }
                boundary(reason: "push_to_talk")
            case "shutdown": cancelCapture(); exit(0)
            default: throw ServiceError(message: "Unknown helper command")
            }
        } catch { fail(error.localizedDescription) }
    }

    func transcriber(_ locale: Locale) -> SpeechTranscriber {
        var reporting: Set<SpeechTranscriber.ReportingOption> = [.volatileResults, .alternativeTranscriptions]
        if settings.fastResults { reporting.insert(.fastResults) }
        return SpeechTranscriber(locale: locale, transcriptionOptions: [], reportingOptions: reporting,
            attributeOptions: [.audioTimeRange, .transcriptionConfidence])
    }

    func prepare(_ current: Int) async throws {
        guard SpeechTranscriber.isAvailable else { throw ServiceError(message: "On-device speech is unavailable on this Mac") }
        guard let chosenLocale = await SpeechTranscriber.supportedLocale(equivalentTo: Locale(identifier: settings.locale)) else {
            throw ServiceError(message: "Unsupported speech locale: \(settings.locale)")
        }
        guard current == generation, !Task.isCancelled else { return }
        let module = transcriber(chosenLocale)
        if let request = try await AssetInventory.assetInstallationRequest(supporting: [module]) {
            guard current == generation, !Task.isCancelled else { return }
            emit("status", ["status": "downloading"])
            try await request.downloadAndInstall()
        }
        guard current == generation, !Task.isCancelled else { return }
        guard await AVCaptureDevice.requestAccess(for: .audio) else {
            throw ServiceError(message: "Microphone permission denied. Enable Sous Chef Voice or the launching app in System Settings → Privacy & Security → Microphone.")
        }
        guard current == generation, !Task.isCancelled else { return }
        let devices = AVCaptureDevice.DiscoverySession(deviceTypes: [.microphone, .external], mediaType: .audio, position: .unspecified).devices
        guard let device = settings.deviceId.isEmpty ? AVCaptureDevice.default(for: .audio) : devices.first(where: { $0.uniqueID == settings.deviceId }) else {
            throw ServiceError(message: "Selected microphone is unavailable; select a connected input device")
        }
        // Apple's provider converts the selected device's sample rate/channel
        // layout into a format compatible with the speech module.
        let provider = try await CaptureInputSequenceProvider.providerWithSession(from: device, compatibleWith: [module])
        guard current == generation, !Task.isCancelled else { provider.captureSession.stopRunning(); return }
        capture = provider; locale = chosenLocale; armed = true
        timeline = 0; lastMeter = 0; format = nil
        let deviceID = device.uniqueID
        deviceObserver = NotificationCenter.default.addObserver(forName: AVCaptureDevice.wasDisconnectedNotification,
            object: nil, queue: .main) { [weak self] notification in
                guard let disconnected = notification.object as? AVCaptureDevice, disconnected.uniqueID == deviceID else { return }
                Task { @MainActor in
                    guard let self, self.generation == current else { return }
                    self.emit("device_changed", ["message": "Selected microphone disconnected; unfinished audio discarded"])
                    self.cancelCapture()
                }
            }
        sessionObserver = NotificationCenter.default.addObserver(forName: AVCaptureSession.runtimeErrorNotification,
            object: provider.captureSession, queue: .main) { [weak self] _ in
                Task { @MainActor in
                    guard let self, self.generation == current else { return }
                    self.fail("Audio capture failed; unfinished audio discarded")
                }
            }
        captureTask = Task { [weak self] in
            do {
                for try await input in provider.analyzerInputs {
                    guard let self, self.generation == current, self.armed, !Task.isCancelled else { break }
                    try self.consume(input)
                }
                if let self, self.generation == current, self.armed, !Task.isCancelled {
                    self.fail("Microphone stream ended; unfinished audio discarded")
                }
            } catch {
                if let self, self.generation == current, !Task.isCancelled { self.fail(error.localizedDescription) }
            }
        }
        provider.captureSession.startRunning()
        emit("status", ["status": "listening", "device_id": device.uniqueID, "device_name": device.localizedName])
        await inventory()
    }

    func cancelCapture() {
        armed = false
        prepareTask?.cancel(); prepareTask = nil
        captureTask?.cancel(); captureTask = nil
        capture?.captureSession.stopRunning(); capture = nil
        if let observer = deviceObserver { NotificationCenter.default.removeObserver(observer) }
        if let observer = sessionObserver { NotificationCenter.default.removeObserver(observer) }
        deviceObserver = nil; sessionObserver = nil
        for turn in turns.values {
            turn.input.finish(); turn.startup?.cancel(); turn.results?.cancel()
            Task { await turn.analyzer.cancelAndFinishNow() }
        }
        finishTask?.cancel(); finishTask = nil
        active = nil; turns.removeAll(); preroll.removeAll(); timeline = 0; format = nil
    }

    func fail(_ message: String) {
        cancelCapture()
        emit("error", ["message": message])
    }

    // Replaceable audio-based endpoint policy. This is energy detection, not
    // semantic speech detection: game sound can hold it open. PTT is the default.
    func energy(_ buffer: AVAudioPCMBuffer) -> Double {
        let count = Int(buffer.frameLength)
        guard count > 0 else { return 0 }
        var sum = 0.0
        for channel in 0..<Int(buffer.format.channelCount) {
            for frame in 0..<count {
                let index = frame * buffer.stride
                let sample: Double
                if let data = buffer.floatChannelData { sample = Double(data[channel][index]) }
                else if let data = buffer.int16ChannelData { sample = Double(data[channel][index]) / 32768 }
                else if let data = buffer.int32ChannelData { sample = Double(data[channel][index]) / 2147483648 }
                else { return 0 }
                sum += sample * sample
            }
        }
        return sqrt(sum / Double(count * Int(buffer.format.channelCount)))
    }

    func consume(_ input: AnalyzerInput) throws {
        if let previous = format, previous != input.bufferFormat {
            emit("device_changed", ["message": "Microphone audio format changed; arm voice again"])
            cancelCapture(); return
        }
        format = input.bufferFormat
        let start = timeline
        let duration = CMTimeGetSeconds(input.bufferDuration) * 1000
        guard duration.isFinite, duration > 0 else { throw ServiceError(message: "Invalid input audio timing") }
        timeline += duration
        let rms = energy(input.buffer)
        let speech = rms >= settings.energyThreshold
        if timeline - lastMeter >= 100 {
            lastMeter = timeline
            emit("audio_level", ["audio_end_ms": timeline, "rms": rms, "speech_detected": speech])
        }
        if active == nil, settings.mode == "automatic", speech {
            let beginning = preroll.first?.1 ?? start
            try startTurn(id: UUID().uuidString, start: beginning)
            for (buffer, at) in preroll { try feed(buffer, start: at) }
            preroll.removeAll()
        }
        if let turn = active {
            try feed(input, start: start)
            turn.end = timeline
            if speech { turn.lastSpeech = timeline }
            if timeline - turn.start > settings.maxCommandMs {
                throw ServiceError(message: "Command exceeded the duration limit; entire command discarded. Use shorter turns.")
            }
            if settings.mode == "automatic", let last = turn.lastSpeech, timeline - last >= settings.pauseMs {
                boundary(reason: "audio_pause")
            }
        } else if settings.mode == "automatic" {
            preroll.append((input, start))
            preroll.removeAll { timeline - $0.1 > 250 }
        }
    }

    func feed(_ input: AnalyzerInput, start: Double) throws {
        guard let turn = active else { return }
        let time = CMTime(seconds: (start - turn.start) / 1000, preferredTimescale: 1_000_000_000)
        switch turn.input.yield(AnalyzerInput(buffer: input.buffer, bufferStartTime: time)) {
        case .enqueued: break
        default: throw ServiceError(message: "Speech processing fell behind; command discarded without truncation")
        }
    }

    func startTurn(id: String, start: Double) throws {
        guard let locale, armed, turns.count < 3 else {
            throw ServiceError(message: "Speech is still finalizing; command buffer is full")
        }
        let module = transcriber(locale)
        let analyzer = SpeechAnalyzer(modules: [module], options: .init(priority: .userInitiated, modelRetention: .processLifetime))
        let (stream, continuation) = AsyncStream<AnalyzerInput>.makeStream(bufferingPolicy: .bufferingOldest(256))
        let turn = Turn(id: id, generation: generation, start: start, analyzer: analyzer, input: continuation)
        turns[id] = turn; active = turn
        emit("turn_started", ["turn_id": id, "start_ms": start])
        turn.results = Task { [weak self] in
            for try await result in module.results {
                guard let self, self.generation == turn.generation, self.armed, !Task.isCancelled else { return }
                let text = String(result.text.characters)
                let range = [turn.start + CMTimeGetSeconds(result.range.start) * 1000,
                             turn.start + CMTimeGetSeconds(CMTimeRangeGetEnd(result.range)) * 1000]
                var spans: [[String: Any]] = []
                for run in result.text.runs {
                    var span: [String: Any] = ["text": String(result.text[run.range].characters)]
                    if let confidence = run.transcriptionConfidence { span["confidence"] = confidence }
                    if let time = run.audioTimeRange {
                        span["audio_range_ms"] = [turn.start + CMTimeGetSeconds(time.start) * 1000,
                            turn.start + CMTimeGetSeconds(CMTimeRangeGetEnd(time)) * 1000]
                    }
                    if span.count > 1 { spans.append(span) }
                }
                let finalized = CMTimeGetSeconds(result.resultsFinalizationTime) * 1000
                turn.lastResult = self.emit("transcript_update", ["turn_id": id, "audio_range_ms": range,
                    "text": text, "alternatives": Array(result.alternatives.prefix(3)).map { String($0.characters) },
                    "confidence_spans": spans, "is_final": result.isFinal,
                    "finalized_before_ms": turn.start + (finalized.isFinite ? max(0, finalized) : 0)])
            }
        }
        turn.startup = Task { try await analyzer.start(inputSequence: stream) }
    }

    func boundary(reason: String) {
        guard let turn = active else { return }
        active = nil; preroll.removeAll()
        let cutoff = turn.end
        emit("turn_boundary", ["turn_id": turn.id, "cutoff_ms": cutoff, "reason": reason,
            "last_speech_ms": turn.lastSpeech as Any? ?? NSNull()])
        turn.input.finish()
        let boundaryTime = ProcessInfo.processInfo.systemUptime
        let previous = finishTask
        finishTask = Task { [weak self] in
            // Serialize commits, while capture continues into the next bounded turn.
            await previous?.value
            guard let self, self.generation == turn.generation, self.armed, !Task.isCancelled else { return }
            do {
                try await turn.startup.value
                if cutoff > turn.start {
                    let end = CMTime(seconds: (cutoff - turn.start) / 1000, preferredTimescale: 1_000_000_000)
                    try await turn.analyzer.finalize(through: end)
                    try await turn.analyzer.finalizeAndFinish(through: end)
                } else {
                    await turn.analyzer.cancelAndFinishNow()
                    turn.results.cancel()
                }
                // This awaited iterator termination is the consumer barrier.
                // It also covers unchanged provisional text with no final callback.
                if cutoff > turn.start { try await turn.results.value }
                guard self.generation == turn.generation, self.armed, !Task.isCancelled else { return }
                self.emit("finalize_completed", ["turn_id": turn.id, "cutoff_ms": cutoff,
                    "finalized_before_ms": cutoff, "results_drained": true,
                    "finalization_ms": (ProcessInfo.processInfo.systemUptime - boundaryTime) * 1000,
                    "last_result_sequence": turn.lastResult])
                self.turns.removeValue(forKey: turn.id)
            } catch {
                if self.generation == turn.generation, !Task.isCancelled { self.fail(error.localizedDescription) }
            }
        }
    }
}

@main
private struct Main {
    @MainActor static func main() async {
        let service = SpeechService()
        await service.inventory()
        await Task.detached {
            while let line = readLine() {
                if line.utf8.count > 32768 { break }
                await service.command(line)
            }
        }.value
        service.cancelCapture()
    }
}
