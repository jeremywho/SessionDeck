import AVFoundation
import Foundation
import Speech

/// Voice to text without the keyboard: the microphone feeds Apple's speech recogniser, and every
/// partial transcript goes to the terminal as the difference from what was already sent, the same
/// way the typing proxy handles the keyboard's own dictation. One tap starts, one tap stops; Apple
/// ends a request after about a minute, and the button goes quiet when that happens.
@MainActor
final class SpeechTyper: ObservableObject {
    @Published private(set) var listening = false
    @Published private(set) var transcript = ""
    @Published var problem: String?

    private let engine = AVAudioEngine()
    private var recognizer: SFSpeechRecognizer?
    private var request: SFSpeechAudioBufferRecognitionRequest?
    private var task: SFSpeechRecognitionTask?
    private var sent = ""
    private var unit: DeleteUnit = .scalar
    private var send: ((String) -> Void)?

    static var isSupported: Bool { SFSpeechRecognizer(locale: Locale.current) != nil || SFSpeechRecognizer() != nil }

    func toggle(unit: DeleteUnit, send: @escaping (String) -> Void) {
        if listening { stop() } else { start(unit: unit, send: send) }
    }

    private func start(unit: DeleteUnit, send: @escaping (String) -> Void) {
        problem = nil
        self.unit = unit
        self.send = send
        Task {
            guard await Self.authorized() else {
                problem = "Allow the microphone and speech recognition for Session Deck in iOS Settings."
                return
            }
            do { try begin() }
            catch { problem = "Could not start listening: \(error.localizedDescription)" }
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

    private func begin() throws {
        guard let recognizer = SFSpeechRecognizer(locale: Locale.current) ?? SFSpeechRecognizer(), recognizer.isAvailable else {
            throw NSError(domain: "SpeechTyper", code: 1, userInfo: [NSLocalizedDescriptionKey: "speech recognition is not available right now"])
        }
        self.recognizer = recognizer
        let session = AVAudioSession.sharedInstance()
        try session.setCategory(.record, mode: .measurement, options: .duckOthers)
        try session.setActive(true, options: .notifyOthersOnDeactivation)

        let request = SFSpeechAudioBufferRecognitionRequest()
        request.shouldReportPartialResults = true
        if recognizer.supportsOnDeviceRecognition { request.requiresOnDeviceRecognition = false }
        self.request = request

        let input = engine.inputNode
        let format = input.outputFormat(forBus: 0)
        input.removeTap(onBus: 0)
        input.installTap(onBus: 0, bufferSize: 1024, format: format) { buffer, _ in request.append(buffer) }
        engine.prepare()
        try engine.start()

        sent = ""
        transcript = ""
        listening = true
        task = recognizer.recognitionTask(with: request) { [weak self] result, error in
            Task { @MainActor [weak self] in
                guard let self else { return }
                if let result {
                    self.apply(result.bestTranscription.formattedString)
                    if result.isFinal { self.stop() }
                }
                if let error, self.listening {
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

    func stop() {
        guard listening || task != nil else { return }
        listening = false
        task?.finish()
        task = nil
        request?.endAudio()
        request = nil
        if engine.isRunning { engine.stop() }
        engine.inputNode.removeTap(onBus: 0)
        try? AVAudioSession.sharedInstance().setActive(false, options: .notifyOthersOnDeactivation)
    }
}
