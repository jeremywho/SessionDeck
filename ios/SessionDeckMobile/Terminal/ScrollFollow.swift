import Foundation

/// What a zoom restore does to the reader's place. The page lands at the newest output when a session
/// opens and when the keyboard rises; after that the reader owns the scroll position, and a restore
/// only follows the text bottom when they were already there.
enum ScrollFollow: Equatable {
    case land
    case follow
    case keep

    static func decide(landing: Bool, readerAtBottom: Bool) -> ScrollFollow {
        if landing { return .land }
        return readerAtBottom ? .follow : .keep
    }

    /// At the bottom means within a few points of the page's end, or of where the last programmatic
    /// scroll left it (the text bottom sits above xterm's blank spare rows, so it is not the page's end).
    static func atBottom(offsetY: Double, maxY: Double, lastLandedY: Double?) -> Bool {
        if offsetY >= maxY - 24 { return true }
        if let lastLandedY { return abs(offsetY - lastLandedY) < 24 }
        return false
    }
}
