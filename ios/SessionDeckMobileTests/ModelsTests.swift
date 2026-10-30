import XCTest
@testable import SessionDeckMobile

final class ModelsTests: XCTestCase {
    func testPairingWithoutASchemeIsHttp() throws {
        let p = try JSONDecoder().decode(Pairing.self, from: Data(#"{"host":"10.0.0.5","port":7420,"token":"t","deviceId":"d"}"#.utf8))
        XCTAssertEqual(p.scheme, "http")
        XCTAssertEqual(p.baseURL.absoluteString, "http://10.0.0.5:7420")
        XCTAssertEqual(p.wsBaseURL.absoluteString, "ws://10.0.0.5:7420")
        XCTAssertFalse(p.isRelay)
        let again = try JSONDecoder().decode(Pairing.self, from: JSONEncoder().encode(p))
        XCTAssertEqual(again, p)
    }

    func testHttpsPairingUsesWss() {
        let p = Pairing(host: "sessiondeck.0a.gg", port: 443, token: "k", deviceId: "relay", scheme: "https")
        XCTAssertEqual(p.baseURL.absoluteString, "https://sessiondeck.0a.gg:443")
        XCTAssertEqual(p.wsBaseURL.absoluteString, "wss://sessiondeck.0a.gg:443")
        XCTAssertTrue(p.isRelay)
    }

    @MainActor func testRelayPairingComesFromTheSecrets() {
        let p = GatewayStore.relay(url: "https://sessiondeck.0a.gg", key: "k")
        XCTAssertEqual(p?.host, "sessiondeck.0a.gg")
        XCTAssertEqual(p?.port, 443)
        XCTAssertEqual(p?.scheme, "https")
        XCTAssertEqual(p?.token, "k")
        XCTAssertEqual(p?.deviceId, "relay")
        XCTAssertEqual(GatewayStore.relay(url: "http://talon:7420", key: "k")?.port, 7420)
        XCTAssertNil(GatewayStore.relay(url: nil, key: nil))
        XCTAssertNil(GatewayStore.relay(url: "https://x", key: ""))
    }

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
