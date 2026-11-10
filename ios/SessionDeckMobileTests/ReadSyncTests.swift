import XCTest
@testable import SessionDeckMobile

final class ReadSyncTests: XCTestCase {
    private func entry(state: String = "Completed", sinceMs: Int64, readAtMs: Int64?, exited: Bool = false) throws -> CatalogEntry {
        let read = readAtMs.map { ",\"readAtMs\":\($0)" } ?? ""
        let json = """
        {"hostId":"h1","sessionId":"s1","provider":"Claude","name":"alpha","modelChip":"","effort":"","state":"\(state)","stateSinceMs":\(sinceMs),"subagents":0,"group":"","groupOrder":0,"sortPriority":0,"cwd":"","hostExited":\(exited),"mobileReady":true\(read)}
        """
        return try JSONDecoder().decode(CatalogEntry.self, from: Data(json.utf8))
    }

    private func at(_ ms: Int64) -> Date { Date(timeIntervalSince1970: Double(ms) / 1000) }

    func testAnOlderGatewaysEntryDecodesWithoutAReadTime() throws {
        XCTAssertNil(try entry(sinceMs: 1000, readAtMs: nil).readAtMs)
        XCTAssertEqual(try entry(sinceMs: 1000, readAtMs: 900).readAtMs, 900)
    }

    func testReadOnTheDesktopIsReadHere() throws {
        // Finished at 1000; the deck says someone looked at 1500; this phone never opened it.
        XCTAssertFalse(GatewayStore.unread(try entry(sinceMs: 1000, readAtMs: 1500), seenHere: nil))
        // Finished again at 2000, after that look: unread once more.
        XCTAssertTrue(GatewayStore.unread(try entry(sinceMs: 2000, readAtMs: 1500), seenHere: nil))
    }

    func testTheLaterOfThisPhonesLookAndTheDecksCounts() throws {
        XCTAssertFalse(GatewayStore.unread(try entry(sinceMs: 2000, readAtMs: 1500), seenHere: at(2500)))
        XCTAssertFalse(GatewayStore.unread(try entry(sinceMs: 2000, readAtMs: 2500), seenHere: at(100)))
        XCTAssertTrue(GatewayStore.unread(try entry(sinceMs: 3000, readAtMs: 2500), seenHere: at(100)))
    }

    func testNeverSeenAnywhereIsUnreadAndOnlyFinishedLiveSessionsBadge() throws {
        XCTAssertTrue(GatewayStore.unread(try entry(sinceMs: 1000, readAtMs: nil), seenHere: nil))
        XCTAssertTrue(GatewayStore.unread(try entry(sinceMs: 1000, readAtMs: 0), seenHere: nil))
        XCTAssertFalse(GatewayStore.unread(try entry(state: "Working", sinceMs: 1000, readAtMs: nil), seenHere: nil))
        XCTAssertFalse(GatewayStore.unread(try entry(sinceMs: 1000, readAtMs: nil, exited: true), seenHere: nil))
    }
}
