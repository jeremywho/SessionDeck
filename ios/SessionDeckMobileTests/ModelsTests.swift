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

    @MainActor func testLanPairingComesFromTheGatewaysMeta() {
        let p = GatewayStore.lan(base: "http://192.168.1.5:7420", key: "k")
        XCTAssertEqual(p?.host, "192.168.1.5")
        XCTAssertEqual(p?.port, 7420)
        XCTAssertEqual(p?.scheme, "http")
        XCTAssertEqual(p?.token, "k")
        XCTAssertEqual(p?.deviceId, "relay")
        XCTAssertNil(GatewayStore.lan(base: nil, key: "k"))
        XCTAssertNil(GatewayStore.lan(base: "https://evil.example:443", key: "k"))
        XCTAssertNil(GatewayStore.lan(base: "http://192.168.1.5", key: "k"))
        XCTAssertNil(GatewayStore.lan(base: "http://192.168.1.5:7420", key: ""))
        XCTAssertNil(GatewayStore.lan(base: "http://8.8.8.8:7420", key: "k"), "a public address is never a home gateway")
        XCTAssertNotNil(GatewayStore.lan(base: "http://10.1.2.3:7420", key: "k"))
        XCTAssertNotNil(GatewayStore.lan(base: "http://172.31.0.9:7420", key: "k"))
        XCTAssertNil(GatewayStore.lan(base: "http://172.32.0.9:7420", key: "k"))
    }

    @MainActor func testTheIdentityProofMatchesTheGateways() {
        // HMAC-SHA256("00112233445566778899aabbccddeeff") under "0123…cdef" (64 hex chars), as the C# test computes it.
        let key = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
        let proof = GatewayStore.proof(nonce: "00112233445566778899aabbccddeeff", key: key)
        XCTAssertEqual(proof, "030ffc11ec49be148af4e692ad06ac01633f5fcae141bab99873e78ab06db975")
        XCTAssertNotEqual(proof, GatewayStore.proof(nonce: "00112233445566778899aabbccddeeff", key: "other"))
        XCTAssertEqual(proof, GatewayStore.proof(nonce: "00112233445566778899aabbccddeeff", key: " \(key) "), "the key is trimmed like the gateway trims it")
    }

    func testMetaDecodesWithAndWithoutTheLanAddress() throws {
        let with = try JSONDecoder().decode(Meta.self, from: Data(#"{"folders":[],"providers":[],"machine":null,"lan":"http://10.0.0.2:7420"}"#.utf8))
        XCTAssertEqual(with.lan, "http://10.0.0.2:7420")
        let without = try JSONDecoder().decode(Meta.self, from: Data(#"{"folders":[],"providers":[],"machine":null}"#.utf8))
        XCTAssertNil(without.lan)
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

    func testARowThatCannotBeOpenedSaysWhy() throws {
        func entry(_ rest: String) throws -> CatalogEntry {
            let json = """
            {"hostId":"h","sessionId":"s","provider":"Claude","name":"n","modelChip":"","effort":"","state":"Idle","stateSinceMs":0,"subagents":0,"group":"","groupOrder":0,"sortPriority":0,"cwd":""\(rest)}
            """
            return try JSONDecoder().decode(CatalogEntry.self, from: Data(json.utf8))
        }
        XCTAssertNil(try entry(#","hostExited":false,"mobileReady":true"#).closedReason)
        XCTAssertEqual(try entry(#","hostExited":true,"mobileReady":false,"kind":"chat","chatReady":false"#).closedReason, "ended")
        XCTAssertEqual(try entry(#","hostExited":false,"mobileReady":false,"kind":"chat""#).closedReason, "update the deck to open chats here")
        XCTAssertEqual(try entry(#","hostExited":false,"mobileReady":false,"kind":"""#).closedReason, "restart from the desktop to open here")
        XCTAssertEqual(try entry(#","hostExited":false,"mobileReady":false"#).closedReason, "restart from the desktop to open here")
    }

    func testAChatSessionOpensAsAConversationWhenTheGatewayCanRelayIt() throws {
        func entry(_ rest: String) throws -> CatalogEntry {
            let json = """
            {"hostId":"h","sessionId":"s","provider":"Claude","name":"n","modelChip":"","effort":"","state":"Idle","stateSinceMs":0,"subagents":0,"group":"","groupOrder":0,"sortPriority":0,"cwd":""\(rest)}
            """
            return try JSONDecoder().decode(CatalogEntry.self, from: Data(json.utf8))
        }
        let chat = try entry(#","hostExited":false,"mobileReady":false,"kind":"chat","chatReady":true"#)
        XCTAssertTrue(chat.opensAsChat)
        XCTAssertTrue(chat.opens)
        XCTAssertNil(chat.closedReason)
        // A gateway from before it could relay a chat says nothing of chatReady: the row stays closed.
        XCTAssertFalse(try entry(#","hostExited":false,"mobileReady":false,"kind":"chat""#).opens)
        XCTAssertFalse(try entry(#","hostExited":true,"mobileReady":false,"kind":"chat","chatReady":true"#).opens)
        // A terminal session is never a chat, and still opens as it did.
        let terminal = try entry(#","hostExited":false,"mobileReady":true,"kind":"","chatReady":false"#)
        XCTAssertFalse(terminal.opensAsChat)
        XCTAssertTrue(terminal.opens)
    }

    @MainActor func testStartingAChatAsksForOneAndOnlyWebLinksLeaveTheApp() throws {
        let body = try JSONEncoder().encode(StartBody(provider: "Claude", cwd: "", name: "", prompt: "", chat: true))
        let sent = try JSONSerialization.jsonObject(with: body) as? [String: Any]
        XCTAssertEqual(sent?["chat"] as? Bool, true)
        XCTAssertEqual(sent?["provider"] as? String, "Claude")

        XCTAssertEqual(ChatBridge.externalURL("https://example.com/a?b=1")?.host, "example.com")
        XCTAssertNil(ChatBridge.externalURL("javascript:alert(1)"))
        XCTAssertNil(ChatBridge.externalURL("file:///etc/passwd"))
        XCTAssertNil(ChatBridge.externalURL(nil))
        let pairing = Pairing(host: "192.0.2.10", port: 7420, token: "t", deviceId: "d")
        XCTAssertEqual(ChatWebView.pageURL(pairing).absoluteString, "http://192.0.2.10:7420/mobile/mobile-chat.html")
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
