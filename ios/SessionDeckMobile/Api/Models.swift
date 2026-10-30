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

    var id: String { hostId }
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
}

struct StartResponse: Codable {
    let hostId: String
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

    var baseURL: URL { URL(string: "http://\(host):\(port)")! }
    var wsBaseURL: URL { URL(string: "ws://\(host):\(port)")! }

    /// `sessiondeck://pair?host=…&port=…&code=…` as the deck's QR encodes it.
    static func parseQR(_ text: String) -> (host: String, port: Int, code: String)? {
        guard let url = URL(string: text), url.scheme == "sessiondeck",
              let items = URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems else { return nil }
        func value(_ name: String) -> String? { items.first { $0.name == name }?.value }
        guard let host = value("host"), !host.isEmpty, let port = Int(value("port") ?? ""), let code = value("code"), !code.isEmpty else { return nil }
        return (host, port, code)
    }
}
