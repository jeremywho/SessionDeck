import SwiftUI
import WebKit
import UIKit

/// One session's terminal: the gateway's page in a web view above a native input bar.
struct TerminalScreen: View {
    @EnvironmentObject var gateway: GatewayStore
    let entry: CatalogEntry
    @StateObject private var bridge = TerminalBridge()
    @StateObject private var speech = SpeechTyper()

    var body: some View {
        VStack(spacing: 0) {
            if let pairing = gateway.pairing {
                TerminalWebView(bridge: bridge, pairing: pairing, hostId: entry.hostId)
                    .ignoresSafeArea(.container, edges: .bottom)
            }
            if let note = bridge.note ?? speech.problem {
                Text(note).font(.caption).foregroundStyle(.orange).frame(maxWidth: .infinity).padding(4).background(Color(white: 0.12))
            }
            if speech.listening {
                HStack(spacing: 6) {
                    Image(systemName: "waveform").foregroundStyle(.red)
                    Text(speech.transcript.isEmpty ? "Listening…" : speech.transcript).lineLimit(2).font(.callout)
                    Spacer()
                }
                .padding(.horizontal, 10).padding(.vertical, 6).frame(maxWidth: .infinity).background(Color(white: 0.12))
            }
            TypingProxy(bridge: bridge).frame(height: 1)
            InputBar(selecting: bridge.selecting, hasSelection: bridge.hasSelection, keyboardShown: bridge.keyboardShown, listening: speech.listening,
                     onKey: { key in bridge.resetTyping(); Task { await bridge.input(key.bytes) } },
                     onNewline: { bridge.resetTyping(); Task { await bridge.input("\\\r") } },
                     onPaste: paste, onCopy: copy, onSelect: { bridge.select(!bridge.selecting) },
                     onKeyboard: { bridge.focus(!bridge.keyboardShown) },
                     onMic: {
                         // Speech appends at the cursor; whatever the keyboard had typed is left as it is.
                         bridge.resetTyping()
                         speech.toggle(unit: DeleteUnit.forProvider(entry.provider)) { bytes in Task { await bridge.input(bytes) } }
                     })
        }
        .background(Color(red: 0.047, green: 0.047, blue: 0.047))
        .navigationTitle(entry.name.isEmpty ? entry.provider : entry.name)
        .navigationBarTitleDisplayMode(.inline)
        .onAppear { gateway.markViewed(entry.sessionId); bridge.sessionId = entry.sessionId.isEmpty ? entry.hostId : entry.sessionId; bridge.provider = entry.provider; bridge.proxy?.unit = DeleteUnit.forProvider(entry.provider) }
        .onDisappear { gateway.markViewed(entry.sessionId); speech.stop() }
        .onChange(of: bridge.connected) { _, connected in
            if connected, let z = LaunchOptions.zoom, !autoZoomed {
                autoZoomed = true
                DispatchQueue.main.asyncAfter(deadline: .now() + 3.0) { bridge.testPinch(to: z) }
            }
            guard connected, !autoSent, !LaunchOptions.sends.isEmpty else { return }
            autoSent = true
            Task {
                for line in LaunchOptions.sends {
                    let ok = await bridge.input(line)
                    LaunchOptions.log("sent \(ok ? "ok" : "failed"): \(line.debugDescription)")
                    try? await Task.sleep(nanoseconds: 700_000_000)
                }
            }
        }
    }

    @State private var autoSent = false
    @State private var autoZoomed = false

    private func paste() {
        guard let s = UIPasteboard.general.string, !s.isEmpty else { return }
        bridge.resetTyping()
        Task { if await bridge.input(Keys.pasteSequence(s)) == false { bridge.note = "Not connected. Nothing was pasted." } }
    }

    private func copy() {
        bridge.copySelection()
    }
}

/// Native side of the page bridge: calls into the page, receives its messages.
@MainActor
final class TerminalBridge: NSObject, ObservableObject, WKScriptMessageHandler, UIGestureRecognizerDelegate {
    nonisolated func gestureRecognizer(_ g: UIGestureRecognizer, shouldRecognizeSimultaneouslyWith other: UIGestureRecognizer) -> Bool { true }

    @Published var note: String?
    @Published var selecting = false
    @Published var hasSelection = false
    @Published var connected = false
    @Published var keyboardShown = false
    weak var webView: WKWebView?
    weak var proxy: ProxyTextView?

    /// Anything that moves the terminal's cursor on its own makes the typed buffer stale; typing then starts afresh.
    func resetTyping() { proxy?.resetBaseline() }

    func focus(_ on: Bool) {
        if on { proxy?.becomeFirstResponder() } else { proxy?.resignFirstResponder() }
    }

    func keyboardCameUp() {
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.6) { [weak self] in self?.scrollToBottom() }
    }

    /// The newest output is what matters: xterm's viewport goes to its end and the page scrolls so the
    /// last row with text on it sits at the bottom of the screen, not xterm's blank spare rows.
    func scrollToBottom() {
        guard let webView else { return }
        webView.evaluateJavaScript("window.sdScrollToBottom(); window.sdTextBottom()") { [weak self] result, _ in
            guard let self, let webView = self.webView else { return }
            let scroll = webView.scrollView
            let visible = scroll.bounds.height - scroll.adjustedContentInset.bottom
            let maxY = max(0, scroll.contentSize.height - visible)
            var y = maxY
            if let bottomCss = result as? Double {
                y = min(maxY, max(0, CGFloat(bottomCss) * scroll.zoomScale - visible + 8))
            }
            scroll.setContentOffset(CGPoint(x: scroll.contentOffset.x, y: y), animated: false)
        }
    }
    var sessionId = ""
    var provider = ""
    private let zooms = ZoomStore()
    private var zoomObservation: NSKeyValueObservation?
    private var pinch: UIPinchGestureRecognizer?
    private var pinchEndedAt = Date.distantPast
    private var restoreUntil = Date.distantPast

    /// Remember how far this session is zoomed, and bring it back to that zoom when it opens.
    /// A pinch of the user's is told apart from WebKit's own refits by a recognizer of our own on
    /// the web view (WebKit does not report its scroll view as zooming while it applies a pinch).
    func watchZoom(of webView: WKWebView) {
        let recognizer = UIPinchGestureRecognizer(target: self, action: #selector(pinched(_:)))
        recognizer.cancelsTouchesInView = false
        recognizer.delegate = self
        webView.scrollView.addGestureRecognizer(recognizer)
        pinch = recognizer
        zoomObservation = webView.scrollView.observe(\.zoomScale, options: [.new]) { [weak self] scrollView, _ in
            guard let self else { return }
            let scale = Double(scrollView.zoomScale)
            if self.userIsZooming {
                self.zooms.set(scale, for: self.sessionId)
                LaunchOptions.log(String(format: "zoom saved %.3f (pinch)", scale))
            } else if Date() < self.restoreUntil, let saved = self.zooms.zoom(for: self.sessionId), abs(saved - scale) > 0.01 {
                // WebKit refitting after the page's viewport settled: put the user's zoom back.
                LaunchOptions.log(String(format: "zoom refit %.3f -> restoring %.3f", scale, saved))
                self.setZoom(saved, on: scrollView)
                self.scrollToBottom()
            }
        }
    }

    private var userIsZooming: Bool {
        if let pinch, pinch.state == .began || pinch.state == .changed { return true }
        return Date().timeIntervalSince(pinchEndedAt) < 1.0
    }

    @objc private func pinched(_ g: UIPinchGestureRecognizer) {
        if g.state == .ended || g.state == .cancelled {
            pinchEndedAt = Date()
            restoreUntil = .distantPast
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) { [weak self] in
                guard let self, let scroll = self.webView?.scrollView else { return }
                self.zooms.set(Double(scroll.zoomScale), for: self.sessionId)
                LaunchOptions.log(String(format: "zoom saved %.3f (pinch ended)", Double(scroll.zoomScale)))
            }
        }
    }

    private func setZoom(_ zoom: Double, on scroll: UIScrollView) {
        let clamped = min(max(CGFloat(zoom), scroll.minimumZoomScale), scroll.maximumZoomScale)
        scroll.setZoomScale(clamped, animated: false)
    }

    /// After the page laid out at a (new) size, bring back the zoom this session had, and keep
    /// bringing it back for a couple of seconds while WebKit settles its own fit.
    private func applySavedZoom() {
        restoreUntil = Date().addingTimeInterval(2.5)
        for delay in [0.3, 1.0, 2.0] {
            DispatchQueue.main.asyncAfter(deadline: .now() + delay) { [weak self] in
                guard let self, let scroll = self.webView?.scrollView, !self.userIsZooming else { return }
                guard let saved = self.zooms.zoom(for: self.sessionId) else { LaunchOptions.log("zoom none saved"); return }
                if abs(Double(scroll.zoomScale) - saved) > 0.01 {
                    LaunchOptions.log(String(format: "zoom restore %.3f (was %.3f)", saved, Double(scroll.zoomScale)))
                    self.setZoom(saved, on: scroll)
                }
                self.scrollToBottom()
            }
        }
    }

    /// Debug driving: pretend the user pinched to `zoom`, so persistence can be proven from the command line.
    func testPinch(to zoom: Double) {
        guard let scroll = webView?.scrollView else { return }
        pinchEndedAt = Date()
        restoreUntil = .distantPast
        setZoom(zoom, on: scroll)
        zooms.set(Double(scroll.zoomScale), for: sessionId)
        LaunchOptions.log(String(format: "zoom saved %.3f (test pinch; asked %.3f, range %.3f-%.3f)", Double(scroll.zoomScale), zoom, Double(scroll.minimumZoomScale), Double(scroll.maximumZoomScale)))
    }

    /// Sends to the page; false when the page had no open socket and dropped it.
    @discardableResult
    func input(_ text: String) async -> Bool {
        UIImpactFeedbackGenerator(style: .light).impactOccurred()
        guard let webView else { return false }
        do {
            let result = try await webView.evaluateJavaScript("window.sdInput(\(Keys.jsLiteral(text)))")
            return (result as? Bool) ?? false
        } catch {
            return false
        }
    }

    func select(_ on: Bool) {
        selecting = on
        webView?.evaluateJavaScript("window.sdSelect(\(on ? "true" : "false"))")
    }

    func copySelection() {
        webView?.evaluateJavaScript("window.sdCopySelection()")
    }

    func userContentController(_ controller: WKUserContentController, didReceive message: WKScriptMessage) {
        guard let body = message.body as? [String: Any], let type = body["type"] as? String else { return }
        switch type {
        case "copy":
            if let text = body["text"] as? String, !text.isEmpty {
                UIPasteboard.general.string = text
                note = "Copied"
                Task { try? await Task.sleep(nanoseconds: 1_200_000_000); if note == "Copied" { note = nil } }
            }
            selecting = false
            hasSelection = false
        case "selection": hasSelection = body["has"] as? Bool ?? false
        case "selecting": selecting = body["on"] as? Bool ?? false
        case "size": applySavedZoom()
        case "replayed": DispatchQueue.main.asyncAfter(deadline: .now() + 0.6) { [weak self] in self?.scrollToBottom() }
        case "tap": if !selecting { focus(true) }
        case "connected": note = nil; connected = true; LaunchOptions.log("terminal connected")
        case "disconnected": note = "Reconnecting…"; connected = false
        case "revoked": note = "This phone was revoked from the deck."
        case "exit": note = "The session ended."
        default: break
        }
    }
}

struct TerminalWebView: UIViewRepresentable {
    @ObservedObject var bridge: TerminalBridge
    let pairing: Pairing
    let hostId: String

    func makeUIView(context: Context) -> WKWebView {
        let config = WKWebViewConfiguration()
        let boot = "window.SD = { token: \(Keys.jsLiteral(pairing.token)), hostId: \(Keys.jsLiteral(hostId)), after: 0 };"
        config.userContentController.addUserScript(WKUserScript(source: boot, injectionTime: .atDocumentStart, forMainFrameOnly: true))
        config.userContentController.add(bridge, name: "sd")
        config.allowsInlineMediaPlayback = true
        let view = WKWebView(frame: .zero, configuration: config)
        view.isOpaque = false
        view.backgroundColor = UIColor(red: 0.047, green: 0.047, blue: 0.047, alpha: 1)
        view.scrollView.backgroundColor = view.backgroundColor
        view.scrollView.bounces = false
        view.scrollView.contentInsetAdjustmentBehavior = .never
        bridge.webView = view
        bridge.watchZoom(of: view)
        view.load(URLRequest(url: pairing.baseURL.appendingPathComponent("mobile/mobile.html")))
        return view
    }

    func updateUIView(_ uiView: WKWebView, context: Context) {}

    static func dismantleUIView(_ uiView: WKWebView, coordinator: ()) {
        uiView.configuration.userContentController.removeScriptMessageHandler(forName: "sd")
    }
}
