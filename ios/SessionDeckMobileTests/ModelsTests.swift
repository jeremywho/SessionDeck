import XCTest
@testable import SessionDeckMobile

final class ModelsTests: XCTestCase {
    let sample = """
    {"type":"sessions","sessions":[{"hostId":"h1","sessionId":"s1","provider":"Claude","name":"alpha","modelChip":"Opus 5","effort":"high","state":"Completed","stateSinceMs":1793300000000,"subagents":0,"group":"tr","groupOrder":1,"sortPriority":3,"cwd":"C:\\\\x","hostExited":false,"mobileReady":true},{"hostId":"h2","sessionId":"","provider":"Shell","name":"Terminal","modelChip":"","effort":"","state":"Idle","stateSinceMs":0,"subagents":0,"group":"","groupOrder":0,"sortPriority":8,"cwd":"","hostExited":false,"mobileReady":false}]}
    """

    func testTheGatewaysEventsFrameDecodes() throws {
        let frame = try JSONDecoder().decode(EventsFrame.self, from: Data(sample.utf8))
        XCTAssertEqual(frame.type, "sessions")
        XCTAssertEqual(frame.sessions.count, 2)
        let first = frame.sessions[0]
        XCTAssertEqual(first.id, "h1")
        XCTAssertEqual(first.state, "Completed")
        XCTAssertEqual(first.stateSince.timeIntervalSince1970, 1_793_300_000, accuracy: 0.001)
        XCTAssertEqual(first.providerGlyph, "✳")
        XCTAssertTrue(first.mobileReady)
        XCTAssertEqual(frame.sessions[1].providerGlyph, ">")
        XCTAssertFalse(frame.sessions[1].mobileReady)
    }

    func testThePairingQRParses() {
        let parsed = Pairing.parseQR("sessiondeck://pair?host=192.0.2.10&port=7420&code=ABCD2345")
        XCTAssertEqual(parsed?.host, "192.0.2.10")
        XCTAssertEqual(parsed?.port, 7420)
        XCTAssertEqual(parsed?.code, "ABCD2345")
        XCTAssertNil(Pairing.parseQR("https://example.com/?host=a&port=1&code=b"))
        XCTAssertNil(Pairing.parseQR("sessiondeck://pair?host=&port=7420&code=X"))
        XCTAssertNil(Pairing.parseQR("sessiondeck://pair?host=a&port=notaport&code=X"))
    }
}
