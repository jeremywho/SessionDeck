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
                    .overlay(alignment: .bottom) {
                        // Laid over the terminal, not stacked under it: a strip that changed the web view's height would refit its zoom.
                        if speech.listening {
                            HStack(spacing: 6) {
                                Image(systemName: "waveform").foregroundStyle(.red)
                                Text(speech.transcript.isEmpty ? "Listening…" : speech.transcript).lineLimit(2).font(.callout)
                                Spacer()
                            }
                            .padding(.horizontal, 10).padding(.vertical, 6).frame(maxWidth: .infinity).background(Color(white: 0.12).opacity(0.92))
                        }
                    }
            }
            if let note = bridge.note ?? speech.problem {
                Text(note).font(.caption).foregroundStyle(.orange).frame(maxWidth: .infinity).padding(4).background(Color(white: 0.12))
            }
            TypingProxy(bridge: bridge).frame(height: 1)
            InputBar(selecting: bridge.selecting, hasSelection: bridge.hasSelection, keyboardShown: bridge.keyboardShown, listening: speech.listening,
                     onKey: { key in speech.stop(); bridge.resetTyping(); Task { await bridge.input(key.bytes) } },
                     onNewline: { speech.stop(); bridge.resetTyping(); Task { await bridge.input("\\\r") } },
                     onPaste: paste, onCopy: copy, onSelect: { bridge.select(!bridge.selecting) },
                     onKeyboard: { bridge.toggleKeyboard() },
                     onMic: {
                         // Speech appends at the cursor; whatever the keyboard had typed is left as it is.
                         bridge.resetTyping()
                         speech.toggle(unit: DeleteUnit.forProvider(entry.provider)) { [weak bridge] bytes in Task { await bridge?.input(bytes) } }
                     })
            .onAppear {
                // Keyboard typing, a paste or a key while speech runs would corrupt its baseline: speech yields first.
                bridge.manualInput = { [weak speech] in speech?.stop() }
                speech.onStopped = { [weak bridge] in bridge?.resetTyping() }
            }
        }
        .background(Color(red: 0.047, green: 0.047, blue: 0.047))
        .navigationTitle(entry.name.isEmpty ? entry.provider : entry.name)
        .navigationBarTitleDisplayMode(.inline)
        .onAppear { gateway.markViewed(entry.sessionId); bridge.sessionId = entry.sessionId.isEmpty ? entry.hostId : entry.sessionId; bridge.provider = entry.provider; bridge.proxy?.unit = DeleteUnit.forProvider(entry.provider) }
        .onDisappear { gateway.markViewed(entry.sessionId); speech.stop(); bridge.manualInput = nil; speech.onStopped = nil }
        .onChange(of: bridge.connected) { _, connected in
            if connected, let z = LaunchOptions.zoom, !autoZoomed {
                autoZoomed = true
                DispatchQueue.main.asyncAfter(deadline: .now() + 3.0) { bridge.testPinch(to: z) }
                if LaunchOptions.focus { DispatchQueue.main.asyncAfter(deadline: .now() + 6.0) { LaunchOptions.log("raising keyboard"); bridge.focus(true) } }
                if LaunchOptions.scrollTest { DispatchQueue.main.asyncAfter(deadline: .now() + 6.0) { bridge.scrollTest() } }
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
        speech.stop()
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
    /// What the user asked for last, apart from what the editing callbacks report: WebKit can take first
    /// responder on the same tap, which would read as "hidden" and make a hide-tap reopen the keyboard.
    private var wantKeyboard = false
    weak var webView: WKWebView?

    func toggleKeyboard() { focus(!wantKeyboard) }
    weak var proxy: ProxyTextView?

    /// Runs before the keyboard sends anything, so another input source (speech) can yield first.
    var manualInput: (() -> Void)?

    /// Anything that moves the terminal's cursor on its own makes the typed buffer stale; typing then starts afresh.
    func resetTyping() { proxy?.resetBaseline() }

    private var raisedAt = Date.distantPast

    func focus(_ on: Bool) {
        wantKeyboard = on
        LaunchOptions.log("keyboard \(on ? "raise" : "lower") requested")
        if on { raisedAt = Date(); proxy?.becomeFirstResponder() } else { proxy?.resignFirstResponder() }
    }

    /// The keyboard field lost focus. If that is within a moment of our own raise and the user still
    /// wants the keyboard, something else (WebKit on the same tap) took it: take it back once.
    func editingEnded() {
        keyboardShown = false
        LaunchOptions.log("keyboard field ended editing (wanted \(wantKeyboard))")
        guard wantKeyboard, Date().timeIntervalSince(raisedAt) < 1.0 else { return }
        raisedAt = .distantPast
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.05) { [weak self] in
            guard let self, self.wantKeyboard, self.proxy?.isFirstResponder == false else { return }
            LaunchOptions.log("keyboard re-raised after a focus steal")
            self.proxy?.becomeFirstResponder()
        }
    }

    func keyboardCameUp() { land("keyboard up") }

    private var landUntil = Date.distantPast
    private var lastLandedY: CGFloat?
    private var readerAtBottom = true
    private var readerOffsetY: CGFloat = 0
    private var keepingUntil = Date.distantPast
    private var offsetObservation: NSKeyValueObservation?

    /// A session opening or the keyboard rising lands the page at the newest output, and for the next
    /// moments the zoom restores that follow land there too. After that the reader owns the position.
    func land(_ reason: String) {
        landUntil = Date().addingTimeInterval(3.0)
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.6) { [weak self] in self?.scrollToBottom(reason) }
    }

    /// The newest output is what matters: xterm's viewport goes to its end and the page scrolls so the
    /// last row with text on it sits at the bottom of the screen, not xterm's blank spare rows.
    /// With `resetViewport` false the scrollback stays where the reader put it and only the page moves.
    func scrollToBottom(_ reason: String = "unspecified", resetViewport: Bool = true) {
        guard let webView else { return }
        LaunchOptions.log("scroll to bottom (\(reason))")
        let js = resetViewport ? "window.sdScrollToBottom(); window.sdTextBottom()" : "window.sdTextBottom()"
        webView.evaluateJavaScript(js) { [weak self] result, _ in
            guard let self, let webView = self.webView else { return }
            let scroll = webView.scrollView
            let visible = scroll.bounds.height - scroll.adjustedContentInset.bottom
            let maxY = max(0, scroll.contentSize.height - visible)
            var y = maxY
            if let bottomCss = result as? Double {
                y = min(maxY, max(0, CGFloat(bottomCss) * scroll.zoomScale - visible + 8))
            }
            self.lastLandedY = y
            self.readerAtBottom = true
            scroll.setContentOffset(CGPoint(x: scroll.contentOffset.x, y: y), animated: false)
        }
    }

    /// Where the reader is, sampled only while the zoom is the stable one: a restore in progress or
    /// WebKit's refit moves the offset on its own, and that is not the reader scrolling.
    private func noteOffset(_ scroll: UIScrollView, force: Bool = false) {
        let saved = zooms.zoom(for: sessionId) ?? Double(scroll.zoomScale)
        guard abs(Double(scroll.zoomScale) - saved) < 0.01 else { return }
        guard force || (!userIsZooming && Date() >= keepingUntil) else { return }
        let visible = scroll.bounds.height - scroll.adjustedContentInset.bottom
        let maxY = max(0, scroll.contentSize.height - visible)
        readerOffsetY = scroll.contentOffset.y
        readerAtBottom = ScrollFollow.atBottom(offsetY: Double(scroll.contentOffset.y), maxY: Double(maxY), lastLandedY: lastLandedY.map(Double.init))
    }

    private struct ReaderPlace {
        let y: CGFloat
        let atBottom: Bool
    }

    /// Putting the zoom back moves the offset synchronously and would be sampled as the reader's own
    /// scrolling, so the reader's place is taken before the zoom is touched and sampling pauses.
    private func restoreZoom(_ saved: Double, on scroll: UIScrollView, _ reason: String) {
        let place = ReaderPlace(y: readerOffsetY, atBottom: readerAtBottom)
        keepingUntil = Date().addingTimeInterval(0.4)
        setZoom(saved, on: scroll)
        switch ScrollFollow.decide(landing: Date() < landUntil, readerAtBottom: place.atBottom) {
        case .land: scrollToBottom(reason + ", landing")
        case .follow: scrollToBottom(reason + ", reader was at the bottom", resetViewport: false)
        case .keep: keepReadersPlace(reason, y: place.y)
        }
    }

    /// WebKit sets an offset computed for its own refit scale after the zoom observer returns, which
    /// would leave the reader on other lines; their offset is put back once WebKit has had its turn.
    private func keepReadersPlace(_ reason: String, y: CGFloat) {
        for delay in [0.0, 0.15, 0.3] {
            DispatchQueue.main.asyncAfter(deadline: .now() + delay) { [weak self] in
                guard let self, let scroll = self.webView?.scrollView, !self.userIsZooming else { return }
                // A finger on the page wins: the reader is choosing a new place, not WebKit.
                if scroll.isTracking || scroll.isDragging || scroll.isDecelerating { self.keepingUntil = .distantPast; return }
                let visible = scroll.bounds.height - scroll.adjustedContentInset.bottom
                let target = min(y, max(0, scroll.contentSize.height - visible))
                if abs(scroll.contentOffset.y - target) > 0.5 {
                    LaunchOptions.log(String(format: "%@: the reader scrolled up, putting y=%.0f back (WebKit left it at %.0f)", reason, target, scroll.contentOffset.y))
                    scroll.setContentOffset(CGPoint(x: scroll.contentOffset.x, y: target), animated: false)
                }
            }
        }
        // Whatever the page shows once the pause is over is the reader's place, even if nothing moved since.
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.45) { [weak self] in
            guard let self, let scroll = self.webView?.scrollView else { return }
            self.noteOffset(scroll, force: true)
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
        offsetObservation = webView.scrollView.observe(\.contentOffset, options: [.new]) { [weak self] scrollView, _ in
            self?.noteOffset(scrollView)
        }
        zoomObservation = webView.scrollView.observe(\.zoomScale, options: [.new]) { [weak self] scrollView, _ in
            guard let self else { return }
            let scale = Double(scrollView.zoomScale)
            if self.userIsZooming {
                self.zooms.set(scale, for: self.sessionId)
                LaunchOptions.log(String(format: "zoom saved %.3f (pinch)", scale))
            } else if let saved = self.zooms.zoom(for: self.sessionId), abs(saved - scale) > 0.01 {
                if Date() < self.restoreUntil {
                    // WebKit refitting after the page's viewport settled or the view changed size: put the user's zoom back.
                    LaunchOptions.log(String(format: "zoom refit %.3f -> restoring %.3f", scale, saved))
                    self.restoreZoom(saved, on: scrollView, "refit undone")
                } else {
                    LaunchOptions.log(String(format: "zoom changed to %.3f outside a restore window (saved %.3f)", scale, saved))
                }
            }
        }
    }

    /// The web view changed size (the keyboard rose, a strip appeared): WebKit refits its scale to the
    /// new box, which is not what the user asked for, so the saved zoom is put back.
    func viewResized() {
        LaunchOptions.log("web view resized")
        applySavedZoom()
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
            // The place sampled before the pinch belongs to the old scale; take it again once the pinch has settled.
            DispatchQueue.main.asyncAfter(deadline: .now() + 1.05) { [weak self] in
                guard let self, let scroll = self.webView?.scrollView else { return }
                self.noteOffset(scroll, force: true)
            }
        }
    }

    private func setZoom(_ zoom: Double, on scroll: UIScrollView) {
        let clamped = min(max(CGFloat(zoom), scroll.minimumZoomScale), scroll.maximumZoomScale)
        scroll.setZoomScale(clamped, animated: false)
    }

    /// After the page laid out at a (new) size, bring back the zoom this session had, and keep
    /// bringing it back for a couple of seconds while WebKit settles its own fit. A pass that finds
    /// the zoom already right leaves the page alone: the reader may have scrolled up to read.
    private func applySavedZoom() {
        restoreUntil = Date().addingTimeInterval(2.5)
        for delay in [0.3, 1.0, 2.0] {
            DispatchQueue.main.asyncAfter(deadline: .now() + delay) { [weak self] in
                guard let self, let scroll = self.webView?.scrollView, !self.userIsZooming else { return }
                guard let saved = self.zooms.zoom(for: self.sessionId) else { LaunchOptions.log("zoom none saved"); return }
                guard abs(Double(scroll.zoomScale) - saved) > 0.01 else { return }
                LaunchOptions.log(String(format: "zoom restore %.3f (was %.3f)", saved, Double(scroll.zoomScale)))
                self.restoreZoom(saved, on: scroll, "zoom restored")
            }
        }
    }

    /// Debug driving: raise the keyboard, scroll the page to its top as a reader would, lower the
    /// keyboard, and report where the page sits for the next few seconds. A reader's place must survive.
    func scrollTest() {
        func at(_ t: Double, _ work: @escaping () -> Void) { DispatchQueue.main.asyncAfter(deadline: .now() + t, execute: work) }
        at(0) { [weak self] in
            guard let scroll = self?.webView?.scrollView else { return }
            let maxY = max(0, scroll.contentSize.height - scroll.bounds.height)
            LaunchOptions.log(String(format: "scroll-test: after open page y=%.0f of max %.0f (landed at %@)", scroll.contentOffset.y, maxY, self?.lastLandedY.map { String(format: "%.0f", $0) } ?? "nothing"))
        }
        at(0.1) { [weak self] in LaunchOptions.log("scroll-test: raising keyboard"); self?.focus(true) }
        at(3.5) { [weak self] in
            guard let scroll = self?.webView?.scrollView else { return }
            scroll.setContentOffset(CGPoint(x: scroll.contentOffset.x, y: 1000), animated: false)
            LaunchOptions.log(String(format: "scroll-test: reader scrolled up to y=1000 (content %.0f, visible %.0f)", scroll.contentSize.height, scroll.bounds.height))
        }
        at(4.0) { [weak self] in LaunchOptions.log("scroll-test: lowering keyboard"); self?.focus(false) }
        for i in 1...10 {
            at(4.0 + Double(i) * 0.5) { [weak self] in
                guard let scroll = self?.webView?.scrollView else { return }
                LaunchOptions.log(String(format: "scroll-test: t+%.1fs page y=%.0f zoom=%.3f", Double(i) * 0.5, scroll.contentOffset.y, scroll.zoomScale))
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
        case "replayed": land("replay finished")
        case "tap": if !selecting { toggleKeyboard() }
        case "open": if let s = body["uri"] as? String, let url = URL(string: s), ["http", "https"].contains(url.scheme ?? "") { UIApplication.shared.open(url) }
        case "connected": note = nil; connected = true; LaunchOptions.log("terminal connected")
        case "disconnected": note = "Reconnecting…"; connected = false
        case "revoked": note = "This phone was revoked from the deck."
        case "exit": note = "The session ended."
        default: break
        }
    }
}

/// A web view that reports when its size changes, so the saved zoom can be restored after WebKit refits.
final class ResizeAwareWebView: WKWebView {
    var onResize: (() -> Void)?
    private var lastSize = CGSize.zero

    override func layoutSubviews() {
        super.layoutSubviews()
        if bounds.size != lastSize {
            let first = lastSize == .zero
            lastSize = bounds.size
            if !first { onResize?() }
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
        let view = ResizeAwareWebView(frame: .zero, configuration: config)
        view.onResize = { [weak bridge] in bridge?.viewResized() }
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
