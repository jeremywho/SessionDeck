import XCTest

/// Opens the "+" menu on the session list and reads it top to bottom: every chat comes before every
/// terminal session. Nothing in it is chosen, so no session is started. Against a test gateway only
/// (TestConfig.pair), since the menu is offered only while one is connected.
final class NewSessionMenuTests: XCTestCase {
    func testChatsComeFirstInTheNewSessionMenu() throws {
        let pair = TestConfig.pair.split(separator: " ").map(String.init)
        guard pair.count == 2 else {
            XCTFail("TestConfig.pair (\"address:port CODE\") must name a test gateway")
            return
        }
        let app = XCUIApplication()
        app.launchArguments = ["--pair", pair[0], pair[1]]
        app.launch()

        let plus = app.buttons["New session"]
        XCTAssertTrue(plus.waitForExistence(timeout: 30), "no + button")
        let enabled = NSPredicate(format: "isEnabled == true")
        expectation(for: enabled, evaluatedWith: plus)
        waitForExpectations(timeout: 30)
        plus.tap()

        let first = app.buttons["New Claude chat"]
        XCTAssertTrue(first.waitForExistence(timeout: 8), "the menu did not open")
        sleep(1)
        let items = app.buttons.matching(NSPredicate(format: "label BEGINSWITH 'New ' AND label != 'New session'")).allElementsBoundByIndex
            .filter { $0.exists && $0.frame.height > 0 }
            .sorted { $0.frame.minY < $1.frame.minY }
            .map { $0.label }
        print("PROBE menu=\(items)")
        XCTAssertEqual(items.count, 10, "the menu does not offer what it did")
        let chats = items.prefix { $0.hasSuffix(" chat") }
        XCTAssertEqual(Array(chats), ["New Claude chat", "New Claude Swift chat", "New Codex chat", "New AGY chat", "New OpenCode chat"])
        XCTAssertEqual(Array(items.dropFirst(chats.count)), ["New Claude session", "New Claude Swift session", "New Codex session", "New AGY session", "New terminal"])
        sleep(4)

        // Put away with a tap outside it: nothing was chosen.
        app.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.95)).tap()
        sleep(1)
        XCTAssertFalse(first.exists, "the menu stayed up")
    }
}
