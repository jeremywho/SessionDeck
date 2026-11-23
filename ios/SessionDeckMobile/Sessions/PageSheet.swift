import SwiftUI
import WebKit

/// The one page of the deck's machine that the gateway lets a phone read (a dashboard that listens
/// only there), shown as it is and put away with Done or a swipe down.
struct PageSheet: View {
    @EnvironmentObject var gateway: GatewayStore
    @Environment(\.dismiss) private var dismiss
    let page: PhonePageInfo
    @State private var reloads = 0

    var body: some View {
        NavigationStack {
            Group {
                if let pairing = gateway.pairing {
                    PageWebView(pairing: pairing, reloads: reloads).ignoresSafeArea(.container, edges: .bottom)
                } else {
                    Text("Not connected to the deck.").foregroundStyle(.secondary)
                }
            }
            .navigationTitle(page.name)
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .topBarLeading) {
                    Button { reloads += 1 } label: { Image(systemName: "arrow.clockwise") }.accessibilityLabel("Reload")
                }
                ToolbarItem(placement: .topBarTrailing) { Button("Done") { dismiss() } }
            }
        }
    }
}

/// Answers everything the page asks for by asking the gateway: `sdpage://page/x` is `GET /page/x`
/// there, with this phone's credential. The page was written for a browser on the deck's machine and
/// fetches from the root of its own server (`/api/…`), which a scheme of our own lets it go on
/// doing: its origin is this handler, wherever the phone is. Only reads are passed on. Whatever the
/// page would change is answered 405 here, and the gateway refuses it too.
@MainActor
final class PageLoader: NSObject, WKURLSchemeHandler {
    static let scheme = "sdpage"
    static let home = URL(string: "sdpage://page/")!

    var pairing: Pairing
    private var running: [ObjectIdentifier: Task<Void, Never>] = [:]

    init(pairing: Pairing) { self.pairing = pairing }

    /// The gateway's address for something the page asks for.
    nonisolated static func request(for url: URL, via pairing: Pairing) -> URLRequest {
        let path = (url.path.isEmpty ? "/" : url.path).addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) ?? "/"
        var request = URLRequest(url: URL(string: pairing.baseURL.absoluteString + "/page" + path) ?? pairing.baseURL.appendingPathComponent("page"))
        request.setValue("Bearer \(pairing.token)", forHTTPHeaderField: "Authorization")
        request.timeoutInterval = 20
        return request
    }

    /// What is shown in place of the page when it cannot be had, in words, readable in light and dark.
    nonisolated static func message(_ text: String) -> Data {
        let safe = text.replacingOccurrences(of: "&", with: "&amp;").replacingOccurrences(of: "<", with: "&lt;")
        return Data("<!doctype html><meta name=viewport content='width=device-width, initial-scale=1'><meta name=color-scheme content='light dark'><body style='font: -apple-system-body; padding: 28px 22px; opacity: .75'>\(safe)</body>".utf8)
    }

    func webView(_ webView: WKWebView, start task: WKURLSchemeTask) {
        guard let url = task.request.url else { task.didFailWithError(URLError(.badURL)); return }
        guard (task.request.httpMethod ?? "GET").uppercased() == "GET" else {
            answer(task, url, 405, "text/plain; charset=utf-8", Data("This page is read-only here.".utf8))
            return
        }
        let request = Self.request(for: url, via: pairing)
        let id = ObjectIdentifier(task)
        let document = url.path.isEmpty || url.path == "/"
        running[id] = Task { [weak self] in
            let status: Int, type: String, body: Data
            do {
                let (data, response) = try await URLSession.shared.data(for: request)
                let http = response as? HTTPURLResponse
                (status, type, body) = (http?.statusCode ?? 502, http?.value(forHTTPHeaderField: "Content-Type") ?? "text/plain; charset=utf-8", data)
            } catch {
                (status, type, body) = (502, "text/plain; charset=utf-8", Data("The deck could not be reached: \(error.localizedDescription)".utf8))
            }
            // A task WebKit stopped must not be answered: it throws.
            guard let self, self.running.removeValue(forKey: id) != nil else { return }
            if document && status != 200 {
                self.answer(task, url, 200, "text/html; charset=utf-8", Self.message(String(decoding: body, as: UTF8.self)))
            } else {
                self.answer(task, url, status, type, body)
            }
        }
    }

    func webView(_ webView: WKWebView, stop task: WKURLSchemeTask) {
        running.removeValue(forKey: ObjectIdentifier(task))?.cancel()
    }

    private func answer(_ task: WKURLSchemeTask, _ url: URL, _ status: Int, _ type: String, _ body: Data) {
        guard let response = HTTPURLResponse(url: url, statusCode: status, httpVersion: "HTTP/1.1", headerFields: ["Content-Type": type, "Cache-Control": "no-store"]) else {
            task.didFailWithError(URLError(.badServerResponse)); return
        }
        task.didReceive(response)
        task.didReceive(body)
        task.didFinish()
    }
}

struct PageWebView: UIViewRepresentable {
    let pairing: Pairing
    let reloads: Int

    final class Coordinator {
        var loader: PageLoader?
        var reloads = 0
    }

    func makeCoordinator() -> Coordinator { Coordinator() }

    func makeUIView(context: Context) -> WKWebView {
        let loader = PageLoader(pairing: pairing)
        context.coordinator.loader = loader
        context.coordinator.reloads = reloads
        let config = WKWebViewConfiguration()
        config.setURLSchemeHandler(loader, forURLScheme: PageLoader.scheme)
        // The page's buttons change things on the deck's machine, and nothing here can: they are shown as out of reach.
        let still = "var s = document.createElement('style'); s.textContent = 'button, input[type=button], input[type=submit] { pointer-events: none !important; opacity: .4 !important; }'; document.documentElement.appendChild(s);"
        config.userContentController.addUserScript(WKUserScript(source: still, injectionTime: .atDocumentEnd, forMainFrameOnly: true))
        let view = WKWebView(frame: .zero, configuration: config)
        view.accessibilityIdentifier = "deck-page"
        view.load(URLRequest(url: PageLoader.home))
        return view
    }

    /// The way to the deck changed (the phone left or reached home), or a reload was asked for.
    func updateUIView(_ uiView: WKWebView, context: Context) {
        guard let loader = context.coordinator.loader else { return }
        let moved = loader.pairing != pairing
        loader.pairing = pairing
        guard moved || context.coordinator.reloads != reloads else { return }
        context.coordinator.reloads = reloads
        uiView.load(URLRequest(url: PageLoader.home))
    }
}
