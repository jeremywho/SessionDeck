import Foundation

/// One session as the gateway lists it. Field names are the gateway's camelCase JSON.
struct CatalogEntry: Codable, Identifiable, Hashable {
    let hostId: String
    let sessionId: String
    let provider: String
    let name: String
    let modelChip: String
    let effort: String
    let state: String
    let stateSinceMs: Int64
    let subagents: Int
    let group: String
    let groupOrder: Int
    let sortPriority: Int
    let cwd: String
    let hostExited: Bool
    let mobileReady: Bool
    /// When anyone last looked at the session, on the desktop or a phone (unix ms, the deck's clock); absent on older gateways.
    let readAtMs: Int64?
    /// "chat" for a session that is a conversation rather than a terminal; empty or absent otherwise.
    let kind: String?
    /// The gateway can relay this chat session to a phone; absent on a gateway from before it could.
    let chatReady: Bool?

    var id: String { hostId }

    /// A chat session this gateway will open here: shown as the conversation page, not as a terminal.
    var opensAsChat: Bool { kind == "chat" && chatReady == true && !hostExited }

    /// Whether tapping the row opens the session.
    var opens: Bool { mobileReady || opensAsChat }

    /// Why the session cannot be opened on this device; nil when it can.
    var closedReason: String? {
        if opens { return nil }
        if hostExited { return "ended" }
        return kind == "chat" ? "update the deck to open chats here" : "restart from the desktop to open here"
    }
    var stateSince: Date { Date(timeIntervalSince1970: Double(stateSinceMs) / 1000) }

    var providerGlyph: String {
        switch provider {
        case "Codex": return "◆"
        case "Agy": return "◈"
        case "Shell": return ">"
        default: return "✳"
        }
    }
}

struct MachineLoad: Codable, Equatable {
    let cpuPercent: Int
    let memoryPercent: Int
    let memoryUsedGb: Double
    let memoryTotalGb: Double
    let gpuPercent: Int
}

struct EventsFrame: Codable {
    let type: String
    let sessions: [CatalogEntry]
    let machine: MachineLoad?
}

struct Meta: Codable {
    let folders: [String]
    let providers: [String]
    let machine: MachineLoad?
    /// Where the gateway can be reached on its own network, e.g. `http://192.168.1.5:7420`; absent on older gateways.
    let lan: String?
}

struct StartResponse: Codable {
    let hostId: String
}

/// What the app asks the deck to start. `chat` asks for a Claude session as a conversation instead of a terminal.
struct StartBody: Codable, Equatable {
    let provider: String
    let cwd: String
    let name: String
    let prompt: String
    let chat: Bool
}

struct PairResponse: Codable {
    let token: String
    let deviceId: String
}

/// Where the deck is and how this phone proves it was let in.
struct Pairing: Codable, Equatable {
    let host: String
    let port: Int
    let token: String
    let deviceId: String
    /// `http` on the LAN, `https` through the relay. Pairings saved before the relay existed have none and mean `http`.
    let scheme: String

    init(host: String, port: Int, token: String, deviceId: String, scheme: String = "http") {
        self.host = host
        self.port = port
        self.token = token
        self.deviceId = deviceId
        self.scheme = scheme
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        host = try c.decode(String.self, forKey: .host)
        port = try c.decode(Int.self, forKey: .port)
        token = try c.decode(String.self, forKey: .token)
        deviceId = try c.decode(String.self, forKey: .deviceId)
        scheme = try c.decodeIfPresent(String.self, forKey: .scheme) ?? "http"
    }

    var baseURL: URL { URL(string: "\(scheme)://\(host):\(port)")! }
    var wsBaseURL: URL { URL(string: "\(scheme == "https" ? "wss" : "ws")://\(host):\(port)")! }
    /// The relay pairing is baked into the build, not granted by the deck, so it is never forgotten on a 401.
    var isRelay: Bool { deviceId == "relay" }

    /// `sessiondeck://pair?host=…&port=…&code=…` as the deck's QR encodes it.
    static func parseQR(_ text: String) -> (host: String, port: Int, code: String)? {
        guard let url = URL(string: text), url.scheme == "sessiondeck",
              let items = URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems else { return nil }
        func value(_ name: String) -> String? { items.first { $0.name == name }?.value }
        guard let host = value("host"), !host.isEmpty, let port = Int(value("port") ?? ""), let code = value("code"), !code.isEmpty else { return nil }
        return (host, port, code)
    }
}
