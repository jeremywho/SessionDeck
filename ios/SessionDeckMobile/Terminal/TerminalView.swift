import SwiftUI
import WebKit
import UIKit

/// One session's terminal: the gateway's page in a web view above a native input bar.
struct TerminalScreen: View {
    @EnvironmentObject var gateway: GatewayStore
    let entry: CatalogEntry
    @StateObject private var bridge = TerminalBridge()
    @State private var text = ""
    @FocusState private var focused: Bool

    var body: some View {
        VStack(spacing: 0) {
            if let pairing = gateway.pairing {
                TerminalWebView(bridge: bridge, pairing: pairing, hostId: entry.hostId)
                    .ignoresSafeArea(.container, edges: .bottom)
            }
            if let note = bridge.note {
                Text(note).font(.caption).foregroundStyle(.orange).frame(maxWidth: .infinity).padding(4).background(Color(white: 0.12))
            }
            InputBar(text: $text, focused: $focused, selecting: bridge.selecting, hasSelection: bridge.hasSelection,
                     onSend: send, onKey: { key in Task { await bridge.input(key.bytes) } }, onPaste: paste, onCopy: copy, onSelect: { bridge.select(!bridge.selecting) })
        }
        .background(Color(red: 0.047, green: 0.047, blue: 0.047))
        .navigationTitle(entry.name.isEmpty ? entry.provider : entry.name)
        .navigationBarTitleDisplayMode(.inline)
        .onAppear { gateway.markViewed(entry.sessionId) }
        .onDisappear { gateway.markViewed(entry.sessionId) }
        .onChange(of: bridge.connected) { _, connected in
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

    /// One line is typed; a draft with newlines goes as a terminal paste so the CLI takes it whole.
    private func send() {
        let draft = text
        guard !draft.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return }
        text = ""
        let bytes = draft.contains("\n") ? Keys.pasteSequence(draft) + "\r" : draft + "\r"
        Task {
            if await bridge.input(bytes) == false {
                text = draft
                bridge.note = "Not connected. The text was kept."
            }
        }
    }

    private func paste() {
        guard let s = UIPasteboard.general.string, !s.isEmpty else { return }
        Task { if await bridge.input(Keys.pasteSequence(s)) == false { bridge.note = "Not connected. Nothing was pasted." } }
    }

    private func copy() {
        bridge.copySelection()
    }
}

/// Native side of the page bridge: calls into the page, receives its messages.
@MainActor
final class TerminalBridge: NSObject, ObservableObject, WKScriptMessageHandler {
    @Published var note: String?
    @Published var selecting = false
    @Published var hasSelection = false
    @Published var connected = false
    weak var webView: WKWebView?

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
        view.load(URLRequest(url: pairing.baseURL.appendingPathComponent("mobile/mobile.html")))
        return view
    }

    func updateUIView(_ uiView: WKWebView, context: Context) {}

    static func dismantleUIView(_ uiView: WKWebView, coordinator: ()) {
        uiView.configuration.userContentController.removeScriptMessageHandler(forName: "sd")
    }
}
