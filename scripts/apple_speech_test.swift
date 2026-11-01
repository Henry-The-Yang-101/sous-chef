// Native SpeechTranscriber smoke tester. Launched by test_apple_speech.py.
import Foundation
@preconcurrency import AVFoundation
import Speech
import Darwin

private func write(_ text: String) {
    FileHandle.standardOutput.write(Data(text.utf8))
}

private struct TestError: LocalizedError {
    let message: String
    var errorDescription: String? { message }
}

@main
struct AppleSpeechTest {
    static func main() async {
        do {
            try await run()
        } catch {
            FileHandle.standardError.write(Data("\nError: \(error.localizedDescription)\n".utf8))
            exit(1)
        }
    }

    private static func run() async throws {
        var localeID = "en-US"
        var audioFile: String?
        var checkOnly = false
        var arguments = CommandLine.arguments.dropFirst().makeIterator()
        while let argument = arguments.next() {
            switch argument {
            case "--locale", "--file":
                guard let value = arguments.next(), !value.hasPrefix("--") else {
                    throw TestError(message: "\(argument) needs a value.")
                }
                if argument == "--locale" { localeID = value } else { audioFile = value }
            case "--check": checkOnly = true
            case "--help", "-h":
                write("""
                Usage: python3 scripts/test_apple_speech.py [--locale en-US]
                       python3 scripts/test_apple_speech.py --check
                       python3 scripts/test_apple_speech.py --file recording.wav

                With no options, transcribe the default microphone. Ctrl+C stops.
                --check reports availability without downloading or opening the mic.

                """)
                return
            default: throw TestError(message: "Unknown option: \(argument). Use --help.")
            }
        }

        guard SpeechTranscriber.isAvailable else {
            throw TestError(message: "Apple SpeechTranscriber is unavailable on this Mac.")
        }
        guard let locale = await SpeechTranscriber.supportedLocale(
            equivalentTo: Locale(identifier: localeID)
        ) else {
            throw TestError(message: "SpeechTranscriber does not support \(localeID).")
        }
        let transcriber = SpeechTranscriber(locale: locale, preset: .progressiveTranscription)
        if checkOnly {
            let status = await AssetInventory.status(forModules: [transcriber])
            write("SpeechTranscriber available; locale: \(locale.identifier); assets: \(status)\n")
            return
        }
        if let request = try await AssetInventory.assetInstallationRequest(supporting: [transcriber]) {
            write("Downloading Apple's \(locale.identifier) speech model (first run)...\n")
            try await request.downloadAndInstall()
        }

        let analyzer = SpeechAnalyzer(modules: [transcriber])
        let interactive = isatty(STDOUT_FILENO) != 0
        let results = Task {
            for try await result in transcriber.results {
                let text = String(result.text.characters).trimmingCharacters(in: .whitespacesAndNewlines)
                if interactive {
                    write("\r\u{001B}[0J\(result.isFinal ? "" : "… ")\(text)\(result.isFinal ? "\n" : "")")
                } else if result.isFinal, !text.isEmpty {
                    write(text + "\n")
                }
            }
        }
        do {
            if let audioFile {
                let file = try AVAudioFile(forReading: URL(fileURLWithPath: audioFile))
                guard let end = try await analyzer.analyzeSequence(from: file) else {
                    throw TestError(message: "The audio file is empty.")
                }
                try await analyzer.finalizeAndFinish(through: end)
            } else {
                guard await AVCaptureDevice.requestAccess(for: .audio) else {
                    throw TestError(message: "Microphone access denied. Enable your terminal or Apple Speech Test in System Settings → Privacy & Security → Microphone.")
                }
                guard let microphone = AVCaptureDevice.default(for: .audio) else {
                    throw TestError(message: "No microphone found.")
                }
                let capture = try await CaptureInputSequenceProvider.providerWithSession(
                    from: microphone, compatibleWith: [transcriber]
                )
                let session = capture.captureSession
                try await analyzer.start(inputSequence: capture.analyzerInputs)
                await withCheckedContinuation { (ready: CheckedContinuation<Void, Never>) in
                    DispatchQueue.global(qos: .userInitiated).async {
                        session.startRunning()
                        ready.resume()
                    }
                }
                defer { session.stopRunning() }
                write("Listening — \(locale.identifier), \(microphone.localizedName). Ctrl+C to stop.\n")
                try await results.value
                return
            }
            try await results.value
        } catch {
            await analyzer.cancelAndFinishNow()
            results.cancel()
            throw error
        }
    }
}
