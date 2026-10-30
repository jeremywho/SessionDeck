import Foundation
import Combine

/// The phone's view of the gateway: the pairing, the live session list, and whether the feed is up.
@MainActor
final class GatewayStore: ObservableObject {
    @Published private(set) var pairing: Pairing?
    @Published private(set) var sessions: [CatalogEntry] = []
    @Published private(set) var connected = false
    @Published private(set) var lastError: String?
    @Published private(set) var viewed: [String: Date]

    private var eventsTask: Task<Void, Never>?
    private let ledger = ViewedLedger()

    init() {
        pairing = Keychain.loadPairing()
        viewed = ledger.all()
        if pairing != nil { startEvents() }
    }

    // MARK: pairing

    /// A host is an IPv4/IPv6 literal or a hostname; anything else would make a URL that cannot be built.
    static func validHost(_ host: String) -> Bool {
        let trimmed = host.trimmingCharacters(in: .whitespaces)
        guard !trimmed.isEmpty, trimmed.count <= 253 else { return false }
        let allowed = CharacterSet.alphanumerics.union(CharacterSet(charactersIn: ".-:"))
        return trimmed.unicodeScalars.allSatisfy { allowed.contains($0) } && URL(string: "http://\(trimmed):1/") != nil
    }

    func pair(host: String, port: Int, code: String, deviceName: String) async throws {
        guard Self.validHost(host), (1...65535).contains(port), let url = URL(string: "http://\(host):\(port)/pair") else { throw GatewayError.badAddress }
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try JSONEncoder().encode(["code": code, "deviceName": deviceName])
        request.timeoutInterval = 8
        let (data, response) = try await URLSession.shared.data(for: request)
        guard let http = response as? HTTPURLResponse else { throw GatewayError.badResponse }
        guard http.statusCode == 200 else { throw http.statusCode == 403 ? GatewayError.badCode : GatewayError.status(http.statusCode) }
        let paired = try JSONDecoder().decode(PairResponse.self, from: data)
        let pairing = Pairing(host: host, port: port, token: paired.token, deviceId: paired.deviceId)
        Keychain.savePairing(pairing)
        self.pairing = pairing
        startEvents()
    }

    func forgetPairing() {
        eventsTask?.cancel()
        eventsTask = nil
        Keychain.deletePairing()
        pairing = nil
        sessions = []
        connected = false
    }

    // MARK: list

    func refresh() async {
        guard let pairing else { return }
        var request = URLRequest(url: pairing.baseURL.appendingPathComponent("sessions"))
        request.setValue("Bearer \(pairing.token)", forHTTPHeaderField: "Authorization")
        request.timeoutInterval = 8
        do {
            let (data, response) = try await URLSession.shared.data(for: request)
            if let http = response as? HTTPURLResponse, http.statusCode == 401 { forgetPairing(); lastError = "This phone is no longer paired."; return }
            sessions = try JSONDecoder().decode([CatalogEntry].self, from: data)
            lastError = nil
        } catch {
            lastError = error.localizedDescription
        }
    }

    func markViewed(_ sessionId: String) {
        guard !sessionId.isEmpty else { return }
        let now = Date()
        viewed[sessionId] = now
        ledger.set(sessionId, now)
    }

    func isUnread(_ entry: CatalogEntry) -> Bool {
        guard entry.state == "Completed", !entry.hostExited else { return false }
        guard let seen = viewed[entry.sessionId] else { return true }
        return entry.stateSince > seen
    }

    // MARK: events feed

    /// Drop the backoff and try the gateway again now.
    func reconnectNow() async {
        startEvents()
        await refresh()
    }

    private func startEvents() {
        eventsTask?.cancel()
        eventsTask = Task { [weak self] in
            var attempt = 0
            while !Task.isCancelled {
                guard let self, let pairing = self.pairing else { return }
                let started = Date()
                await self.runEvents(pairing)
                if Task.isCancelled { return }
                if Date().timeIntervalSince(started) > 15 { attempt = 0 }
                let delay = min(30.0, 0.5 * pow(2.0, Double(min(attempt, 6))))
                attempt += 1
                try? await Task.sleep(nanoseconds: UInt64(delay * 1_000_000_000))
            }
        }
    }

    private func runEvents(_ pairing: Pairing) async {
        var url = URLComponents(url: pairing.wsBaseURL.appendingPathComponent("events"), resolvingAgainstBaseURL: false)!
        url.queryItems = [URLQueryItem(name: "token", value: pairing.token)]
        let task = URLSession.shared.webSocketTask(with: url.url!)
        task.resume()
        defer { task.cancel(with: .goingAway, reason: nil) }
        do {
            while !Task.isCancelled {
                let message = try await task.receive()
                connected = true
                lastError = nil
                let data: Data
                switch message {
                case .string(let text): data = Data(text.utf8)
                case .data(let bytes): data = bytes
                @unknown default: continue
                }
                if let frame = try? JSONDecoder().decode(EventsFrame.self, from: data), frame.type == "sessions" {
                    sessions = frame.sessions
                }
            }
        } catch {
            connected = false
            // A revoked phone is told at the handshake (401) or, if it was already connected, by close code 4401.
            let handshake = (task.response as? HTTPURLResponse)?.statusCode
            if task.closeCode.rawValue == 4401 || handshake == 401 {
                forgetPairing()
                lastError = "This phone was revoked from the deck."
            } else if !Task.isCancelled {
                lastError = "Gateway unreachable at \(pairing.host):\(pairing.port)"
            }
        }
    }
}

enum GatewayError: LocalizedError {
    case badResponse
    case badCode
    case badAddress
    case status(Int)

    var errorDescription: String? {
        switch self {
        case .badAddress: return "Address is host:port, for example 192.168.1.10:7420."
        case .badResponse: return "The gateway did not answer."
        case .badCode: return "That code was not accepted. Codes last five minutes and work once."
        case .status(let code): return "The gateway answered \(code)."
        }
    }
}
