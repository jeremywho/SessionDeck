import Foundation
import Combine
import CryptoKit

/// The phone's view of the gateway: the pairing, the live session list, and whether the feed is up.
@MainActor
final class GatewayStore: ObservableObject {
    @Published private(set) var pairing: Pairing?
    @Published private(set) var sessions: [CatalogEntry] = []
    @Published private(set) var machine: MachineLoad?
    @Published private(set) var connected = false
    @Published private(set) var lastError: String?
    @Published private(set) var viewed: [String: Date]

    private var eventsTask: Task<Void, Never>?
    private let ledger = ViewedLedger()

    /// The relay pairing the build carries, and the gateway's own network address once learned from it.
    /// At home the LAN path wins (shorter, and it works with the server down); the relay is the fallback.
    private let relayPairing: Pairing?
    private var lanPairing: Pairing?
    /// The remembered LAN address belongs to one relay: a build pointed at another deck starts afresh.
    private var lanBaseKey: String { Self.lanBaseKey(for: relayPairing) }
    private static func lanBaseKey(for relay: Pairing?) -> String { "lanBase:\(relay?.host ?? "")" }

    init() {
        let relay = Self.relay()
        relayPairing = relay
        lanPairing = Self.lan(base: UserDefaults.standard.string(forKey: Self.lanBaseKey(for: relay)), key: RelaySecrets.key)
        // The remembered LAN address is only a candidate until it proves itself: the key never goes there first.
        pairing = relayPairing ?? Keychain.loadPairing()
        viewed = ledger.all()
        if pairing != nil { startEvents() }
        if relayPairing != nil { Task { await choosePath(); await learnLan() } }
    }

    /// The build's own way in: when `RelaySecrets` carry a URL and key, the deck is reached through the
    /// relay with that key and the pairing screen never shows.
    static func relay() -> Pairing? { relay(url: RelaySecrets.url, key: RelaySecrets.key) }

    static func relay(url: String?, key: String?) -> Pairing? {
        guard let url, let key, !key.isEmpty, let parsed = URL(string: url), let host = parsed.host, let scheme = parsed.scheme else { return nil }
        return Pairing(host: host, port: parsed.port ?? (scheme == "https" ? 443 : 80), token: key, deviceId: "relay", scheme: scheme)
    }

    /// The gateway's LAN address as `/meta` reports it, with the same key; nil unless it is a plain http
    /// address on a private network, which is the only kind of place a home gateway lives.
    static func lan(base: String?, key: String?) -> Pairing? {
        guard let base, let key, !key.isEmpty, let parsed = URL(string: base), parsed.scheme == "http", let host = parsed.host, let port = parsed.port, isPrivateIPv4(host) else { return nil }
        return Pairing(host: host, port: port, token: key, deviceId: "relay", scheme: "http")
    }

    /// 10/8, 172.16/12, 192.168/16.
    static func isPrivateIPv4(_ host: String) -> Bool {
        let parts = host.split(separator: ".").compactMap { UInt8($0) }
        guard parts.count == 4 else { return false }
        if parts[0] == 10 { return true }
        if parts[0] == 172, (16...31).contains(parts[1]) { return true }
        return parts[0] == 192 && parts[1] == 168
    }

    /// What the gateway must answer to `/identity?nonce=`: HMAC-SHA256 of the nonce under the key, lower-case hex.
    static func proof(nonce: String, key: String) -> String {
        let mac = HMAC<SHA256>.authenticationCode(for: Data(nonce.utf8), using: SymmetricKey(data: Data(key.trimmingCharacters(in: .whitespaces).utf8)))
        return mac.map { String(format: "%02x", $0) }.joined()
    }

    /// The build carries the relay key, so there is no pairing screen and no forgetting; 401 means the key, not the phone.
    var usesRelay: Bool { relayPairing != nil }

    /// Which way the deck is being reached right now, for the menu.
    var pathLabel: String? {
        guard let pairing, relayPairing != nil else { return nil }
        return pairing == lanPairing ? "via \(pairing.host) (home network)" : "via \(pairing.host) (relay)"
    }

    /// Take the short way when it answers, the relay otherwise. Run at launch and whenever the app comes back to the front.
    func choosePath() async {
        guard let relayPairing else { return }
        var want = relayPairing
        if let lanPairing, await answers(lanPairing) { want = lanPairing }
        LaunchOptions.log("path: \(want == lanPairing ? "lan" : "relay") \(want.host)")
        guard want != pairing else { return }
        pairing = want
        startEvents()
    }

    /// Ask the gateway, over the relay's TLS and nowhere else, where it lives on its own network, and remember it.
    func learnLan() async {
        guard let relayPairing, let meta = await meta(via: relayPairing) else { return }
        let learned = Self.lan(base: meta.lan, key: RelaySecrets.key)
        guard learned != lanPairing else { return }
        lanPairing = learned
        UserDefaults.standard.set(learned == nil ? nil : meta.lan, forKey: lanBaseKey)
        LaunchOptions.log("lan learned: \(learned?.host ?? "none")")
        await choosePath()
    }

    /// A short, bounded session for talking to something that has not proved itself yet: two seconds
    /// in total, whatever it streams.
    private static let probeSession: URLSession = {
        let config = URLSessionConfiguration.ephemeral
        config.timeoutIntervalForRequest = 1.5
        config.timeoutIntervalForResource = 2.0
        return URLSession(configuration: config)
    }()

    /// Whatever answers at the LAN address must prove it holds our key, by HMAC over a fresh nonce,
    /// before the key is sent there. The answer is read a byte at a time and cut off at a kilobyte.
    private func answers(_ candidate: Pairing) async -> Bool {
        let nonce = (0..<16).map { _ in String(format: "%02x", UInt8.random(in: 0...255)) }.joined()
        var parts = URLComponents(url: candidate.baseURL.appendingPathComponent("identity"), resolvingAgainstBaseURL: false)!
        parts.queryItems = [URLQueryItem(name: "nonce", value: nonce)]
        guard let url = parts.url, let (bytes, response) = try? await Self.probeSession.bytes(from: url),
              (response as? HTTPURLResponse)?.statusCode == 200 else { return false }
        var data = Data()
        do {
            for try await byte in bytes {
                data.append(byte)
                if data.count > 1024 { return false }
            }
        } catch { return false }
        guard let answer = try? JSONDecoder().decode([String: String].self, from: data), let proof = answer["proof"] else { return false }
        return proof == Self.proof(nonce: nonce, key: candidate.token)
    }

    /// The deck said 401. A LAN pairing is gone for good; the relay key is a build-time fact, so the
    /// app says so and keeps trying rather than dropping to a pairing screen that cannot help.
    private func rejected(_ lanMessage: String) {
        if usesRelay { connected = false; lastError = "Relay key rejected" } else { forgetPairing(); lastError = lanMessage }
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
            if let http = response as? HTTPURLResponse, http.statusCode == 401 { rejected("This phone is no longer paired."); return }
            sessions = try JSONDecoder().decode([CatalogEntry].self, from: data)
            lastError = nil
        } catch {
            lastError = error.localizedDescription
        }
    }

    private func authorized(_ path: String, method: String = "GET", body: Data? = nil) -> URLRequest? {
        guard let pairing else { return nil }
        var request = URLRequest(url: pairing.baseURL.appendingPathComponent(path))
        request.httpMethod = method
        request.setValue("Bearer \(pairing.token)", forHTTPHeaderField: "Authorization")
        if let body { request.httpBody = body; request.setValue("application/json", forHTTPHeaderField: "Content-Type") }
        request.timeoutInterval = 10
        return request
    }

    func meta() async -> Meta? {
        guard let pairing else { return nil }
        return await meta(via: pairing)
    }

    private func meta(via pairing: Pairing) async -> Meta? {
        var request = URLRequest(url: pairing.baseURL.appendingPathComponent("meta"))
        request.setValue("Bearer \(pairing.token)", forHTTPHeaderField: "Authorization")
        request.timeoutInterval = 8
        guard let (data, _) = try? await URLSession.shared.data(for: request) else { return nil }
        return try? JSONDecoder().decode(Meta.self, from: data)
    }

    /// Start a session on the deck; returns the new host id, which the list will show once the gateway sees it.
    func startSession(provider: String, cwd: String, name: String, prompt: String) async throws -> String {
        let body = try JSONEncoder().encode(["provider": provider, "cwd": cwd, "name": name, "prompt": prompt])
        guard let request = authorized("sessions", method: "POST", body: body) else { throw GatewayError.badResponse }
        let (data, response) = try await URLSession.shared.data(for: request)
        guard let http = response as? HTTPURLResponse else { throw GatewayError.badResponse }
        if http.statusCode == 401 { rejected("This phone is no longer paired."); throw GatewayError.status(401) }
        guard http.statusCode == 201 else {
            if let err = try? JSONDecoder().decode([String: String].self, from: data), let text = err["error"] { throw GatewayError.message(text) }
            throw GatewayError.status(http.statusCode)
        }
        return try JSONDecoder().decode(StartResponse.self, from: data).hostId
    }

    func markViewed(_ sessionId: String) {
        guard !sessionId.isEmpty else { return }
        let now = Date()
        viewed[sessionId] = now
        ledger.set(sessionId, now)
        // Tell the deck, so the session is read on the desktop and on the other devices too.
        if let body = try? JSONEncoder().encode(["sessionId": sessionId]), let request = authorized("read", method: "POST", body: body) {
            Task { _ = try? await URLSession.shared.data(for: request) }
        }
    }

    func isUnread(_ entry: CatalogEntry) -> Bool { Self.unread(entry, seenHere: viewed[entry.sessionId]) }

    /// Unread = finished a turn after anyone last looked: here (this device's own record) or anywhere the deck knows of.
    nonisolated static func unread(_ entry: CatalogEntry, seenHere: Date?) -> Bool {
        guard entry.state == "Completed", !entry.hostExited else { return false }
        let here = seenHere.map { Int64(($0.timeIntervalSince1970 * 1000).rounded()) } ?? 0
        let seen = max(here, entry.readAtMs ?? 0)
        return seen == 0 || entry.stateSinceMs > seen
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
                // The home network went away (the phone left the house): go round by the relay without waiting out the backoff.
                if let relay = self.relayPairing, pairing != relay {
                    LaunchOptions.log("path: lan feed failed, falling back to the relay")
                    self.pairing = relay
                    attempt = 0
                    continue
                }
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
                    if let m = frame.machine { machine = m }
                }
            }
        } catch {
            connected = false
            // A revoked phone is told at the handshake (401) or, if it was already connected, by close code 4401.
            let handshake = (task.response as? HTTPURLResponse)?.statusCode
            if task.closeCode.rawValue == 4401 || handshake == 401 {
                rejected("This phone was revoked from the deck.")
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
    case message(String)

    var errorDescription: String? {
        switch self {
        case .message(let text): return text
        case .badAddress: return "Address is host:port, for example 192.168.1.10:7420."
        case .badResponse: return "The gateway did not answer."
        case .badCode: return "That code was not accepted. Codes last five minutes and work once."
        case .status(let code): return "The gateway answered \(code)."
        }
    }
}
