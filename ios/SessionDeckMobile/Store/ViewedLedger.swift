import Foundation

/// When this phone last looked at each session. The deck keeps its own ledger; the two do not sync yet.
struct ViewedLedger {
    private let key = "viewedAt"
    private let defaults: UserDefaults

    init(defaults: UserDefaults = .standard) { self.defaults = defaults }

    func all() -> [String: Date] {
        guard let raw = defaults.dictionary(forKey: key) as? [String: Double] else { return [:] }
        return raw.mapValues { Date(timeIntervalSince1970: $0) }
    }

    func set(_ sessionId: String, _ when: Date) {
        var raw = defaults.dictionary(forKey: key) as? [String: Double] ?? [:]
        raw[sessionId] = when.timeIntervalSince1970
        let cutoff = Date().addingTimeInterval(-30 * 24 * 3600).timeIntervalSince1970
        raw = raw.filter { $0.value > cutoff }
        defaults.set(raw, forKey: key)
    }
}
