import Foundation

/// How far the terminal page was zoomed, per session with the last zoom anywhere as the fallback,
/// so a session opens readable instead of fitted to the screen every time.
struct ZoomStore {
    private let perSessionKey = "zoomBySession"
    private let globalKey = "zoomGlobal"
    private let defaults: UserDefaults

    init(defaults: UserDefaults = .standard) { self.defaults = defaults }

    func zoom(for sessionId: String) -> Double? {
        if !sessionId.isEmpty, let map = defaults.dictionary(forKey: perSessionKey) as? [String: Double], let z = map[sessionId] { return z }
        let g = defaults.double(forKey: globalKey)
        return g > 0 ? g : nil
    }

    func set(_ zoom: Double, for sessionId: String) {
        guard zoom > 0 else { return }
        if !sessionId.isEmpty {
            var map = defaults.dictionary(forKey: perSessionKey) as? [String: Double] ?? [:]
            map[sessionId] = zoom
            if map.count > 200 { map = Dictionary(uniqueKeysWithValues: Array(map.suffix(200))) }
            defaults.set(map, forKey: perSessionKey)
        }
        defaults.set(zoom, forKey: globalKey)
    }
}
