import SwiftUI
import WebKit
import UIKit

/// One chat session: the deck's own conversation page, served by the gateway, in a web view.
/// The page carries its own message box, so there is no native input bar here.
struct ChatScreen: View {
    @EnvironmentObject var gateway: GatewayStore
    let entry: CatalogEntry
    @StateObject private var bridge = ChatBridge()

    var body: some View {
        VStack(spacing: 0) {
            if let note = bridge.note {
                Text(note).font(.caption).foregroundStyle(.orange).frame(maxWidth: .infinity).padding(4).background(Color(white: 0.12))
            }
            if let pairing = gateway.pairing {
                ChatWebView(bridge: bridge, pairing: pairing, hostId: entry.hostId)
                    .ignoresSafeArea(.container, edges: .bottom)
            }
        }
        .background(Color(red: 0.047, green: 0.047, blue: 0.047))
        .navigationTitle(bridge.title ?? (entry.name.isEmpty ? entry.provider : entry.name))
        .navigationBarTitleDisplayMode(.inline)
        .onAppear { gateway.markViewed(entry.sessionId) }
        .onDisappear { gateway.markViewed(entry.sessionId) }
    }
}

/// Native side of the chat page: what it reports, and the links it asks to have opened.
@MainActor
final class ChatBridge: NSObject, ObservableObject, WKScriptMessageHandler {
    @Published var note: String?
    @Published var title: String?
    var pageBase: URL?

    func userContentController(_ controller: WKUserContentController, didReceive message: WKScriptMessage) {
        guard let body = message.body as? [String: Any], let type = body["type"] as? String else { return }
        handle(type, body)
    }

    func handle(_ type: String, _ body: [String: Any]) {
        switch type {
        case "copy":
            if let text = body["text"] as? String, !text.isEmpty { UIPasteboard.general.string = text }
        case "open":
            if let url = Self.externalURL(body["uri"] as? String) { UIApplication.shared.open(url) }
        case "connected": note = nil; LaunchOptions.log("chat connected")
        case "disconnected": note = "Reconnecting…"
        case "exit": note = "The session ended."
        case "title": if let t = body["title"] as? String, !t.isEmpty { title = t }
        default: break
        }
    }

    /// Only web links leave the app; anything else the page names is ignored.
    static func externalURL(_ text: String?) -> URL? {
        guard let text, let url = URL(string: text), ["http", "https"].contains(url.scheme?.lowercased() ?? "") else { return nil }
        return url
    }
}

struct ChatWebView: UIViewRepresentable {
    @ObservedObject var bridge: ChatBridge
    let pairing: Pairing
    let hostId: String

    static func pageURL(_ pairing: Pairing) -> URL { pairing.baseURL.appendingPathComponent("mobile/mobile-chat.html") }

    func makeUIView(context: Context) -> WKWebView {
        let config = WKWebViewConfiguration()
        let boot = "window.SD = { token: \(Keys.jsLiteral(pairing.token)), hostId: \(Keys.jsLiteral(hostId)) };"
        config.userContentController.addUserScript(WKUserScript(source: boot, injectionTime: .atDocumentStart, forMainFrameOnly: true))
        config.userContentController.add(bridge, name: "sd")
        let view = WKWebView(frame: .zero, configuration: config)
        view.isOpaque = false
        view.backgroundColor = UIColor(red: 0.047, green: 0.047, blue: 0.047, alpha: 1)
        view.scrollView.backgroundColor = view.backgroundColor
        view.scrollView.bounces = false
        view.scrollView.contentInsetAdjustmentBehavior = .never
        view.accessibilityIdentifier = "chat-page"
        bridge.pageBase = pairing.baseURL
        view.load(URLRequest(url: Self.pageURL(pairing)))
        return view
    }

    /// The path changed under an open chat (the phone left or reached home): the page is reloaded from
    /// the new base, since its socket only ever goes back to the origin it was served from.
    func updateUIView(_ uiView: WKWebView, context: Context) {
        guard bridge.pageBase != pairing.baseURL else { return }
        bridge.pageBase = pairing.baseURL
        LaunchOptions.log("chat page reloaded via \(pairing.host)")
        uiView.load(URLRequest(url: Self.pageURL(pairing)))
    }

    static func dismantleUIView(_ uiView: WKWebView, coordinator: ()) {
        uiView.configuration.userContentController.removeScriptMessageHandler(forName: "sd")
    }
}
