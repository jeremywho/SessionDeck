import AVFoundation
import Foundation
import Speech

/// Voice to text without the keyboard: the microphone feeds Apple's speech recogniser, and every
/// partial transcript goes to the terminal as the difference from what was already sent, the same
/// way the typing proxy handles the keyboard's own dictation. One tap starts, one tap stops; Apple
/// ends a request after about a minute, and the button goes quiet when that happens.
///
/// Every start bumps a generation; a stop bumps it again. Results, errors and a pending permission
/// request all carry the generation they belong to, so nothing from an earlier recording can type
/// or stop a later one.
@MainActor
final class SpeechTyper: ObservableObject {
    @Published private(set) var listening = false
    @Published private(set) var transcript = ""
    @Published var problem: String?

    /// Called when listening ends for any reason; the owner resets the keyboard's baseline since the cursor moved.
    var onStopped: (() -> Void)?

    private let engine = AVAudioEngine()
    private var request: SFSpeechAudioBufferRecognitionRequest?
    private var task: SFSpeechRecognitionTask?
    private var generation = 0
    private var starting = false
    private var sent = ""
    private var unit: DeleteUnit = .scalar
    private var send: ((String) -> Void)?

    var isActive: Bool { listening || starting }

    func toggle(unit: DeleteUnit, send: @escaping (String) -> Void) {
        if isActive { stop() } else { start(unit: unit, send: send) }
    }

    private func start(unit: DeleteUnit, send: @escaping (String) -> Void) {
        problem = nil
        self.unit = unit
        self.send = send
        starting = true
        generation += 1
        let gen = generation
        Task {
            let ok = await Self.authorized()
            guard gen == generation, starting else { return }
            guard ok else {
                starting = false
                problem = "Allow the microphone and speech recognition for Session Deck in iOS Settings."
                return
            }
            do { try begin(gen) }
            catch {
                teardown()
                starting = false
                problem = "Could not start listening: \(error.localizedDescription)"
            }
        }
    }

    private static func authorized() async -> Bool {
        let speech: Bool = await withCheckedContinuation { c in
            SFSpeechRecognizer.requestAuthorization { c.resume(returning: $0 == .authorized) }
        }
        guard speech else { return false }
        return await withCheckedContinuation { c in
            AVAudioApplication.requestRecordPermission { c.resume(returning: $0) }
        }
    }

    private func begin(_ gen: Int) throws {
        guard let recognizer = SFSpeechRecognizer(locale: Locale.current) ?? SFSpeechRecognizer(), recognizer.isAvailable else {
            throw NSError(domain: "SpeechTyper", code: 1, userInfo: [NSLocalizedDescriptionKey: "speech recognition is not available right now"])
        }
        let session = AVAudioSession.sharedInstance()
        try session.setCategory(.record, mode: .measurement, options: .duckOthers)
        try session.setActive(true, options: .notifyOthersOnDeactivation)

        let request = SFSpeechAudioBufferRecognitionRequest()
        request.shouldReportPartialResults = true
        self.request = request

        let input = engine.inputNode
        let format = input.outputFormat(forBus: 0)
        input.removeTap(onBus: 0)
        input.installTap(onBus: 0, bufferSize: 1024, format: format) { buffer, _ in request.append(buffer) }
        engine.prepare()
        try engine.start()

        sent = ""
        transcript = ""
        starting = false
        listening = true
        task = recognizer.recognitionTask(with: request) { [weak self] result, error in
            Task { @MainActor [weak self] in
                guard let self, gen == self.generation, self.listening else { return }
                if let result {
                    self.apply(result.bestTranscription.formattedString)
                    if result.isFinal { self.stop() }
                }
                if let error {
                    // A normal end of listening also arrives here; only an unexpected one is worth a word.
                    let code = (error as NSError).code
                    if code != 216 && code != 301 && code != 1110 { self.problem = "Listening stopped: \(error.localizedDescription)" }
                    self.stop()
                }
            }
        }
    }

    /// The transcript so far, delivered as backspaces for what changed plus the new tail.
    private func apply(_ text: String) {
        transcript = text
        guard text != sent else { return }
        let bytes = ProxyTextView.delta(from: sent, to: text, unit: unit)
        sent = text
        if !bytes.isEmpty { send?(bytes) }
    }

    /// Stop whatever state the recording is in: a pending permission request, a running one, or a half-started one.
    func stop() {
        let wasActive = isActive
        generation += 1
        starting = false
        listening = false
        teardown()
        if wasActive { onStopped?() }
    }

    private func teardown() {
        task?.cancel()
        task = nil
        request?.endAudio()
        request = nil
        engine.inputNode.removeTap(onBus: 0)
        if engine.isRunning { engine.stop() }
        try? AVAudioSession.sharedInstance().setActive(false, options: .notifyOthersOnDeactivation)
    }
}
