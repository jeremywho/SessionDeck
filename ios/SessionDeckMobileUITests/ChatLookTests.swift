import XCTest

/// Holds a chat open with the keyboard up and a message typed, so the screen can be photographed
/// (`sd-sim-uitest-shots.sh`), and measures what sits between the message box and the keyboard.
/// Sends nothing. The gateway and the session come from TestConfig, as in ChatKeyboardTests.
final class ChatLookTests: XCTestCase {
    func testTheMessageBoxSitsOnTheKeyboardAndATapOnTheConversationPutsTheKeyboardAway() throws {
        let host = TestConfig.host
        let pair = TestConfig.pair.split(separator: " ").map(String.init)
        guard !host.isEmpty, pair.count == 2 else {
            XCTFail("TestConfig.host and TestConfig.pair (\"address:port CODE\") must name a test gateway's chat session")
            return
        }
        let app = XCUIApplication()
        app.launchArguments = ["--pair", pair[0], pair[1], "--open", host]
        app.launch()

        let web = app.webViews.firstMatch
        XCTAssertTrue(web.waitForExistence(timeout: 30), "the chat page did not appear")
        let box = web.textViews.firstMatch
        XCTAssertTrue(box.waitForExistence(timeout: 30), "the message box did not appear")
        let send = web.buttons["Send"]
        XCTAssertTrue(send.waitForExistence(timeout: 30), "the send button did not appear")
        XCTAssertTrue(web.otherElements["Model"].waitForExistence(timeout: 30) || web.popUpButtons["Model"].waitForExistence(timeout: 5) || web.buttons["Model"].waitForExistence(timeout: 5),
                      "the model picker did not appear: the page never attached")
        sleep(3)

        box.tap()
        XCTAssertTrue(app.keyboards.firstMatch.waitForExistence(timeout: 8), "the keyboard did not come up on a tap in the message box")
        box.typeText("This is a test message")
        sleep(1)

        // The top of the first row of letters is where the keyboard proper starts: anything the system
        // draws above it (the form bar a web view gets) is the gap measured here.
        let firstRow = app.keyboards.keys.matching(NSPredicate(format: "label ==[c] 'q'")).firstMatch.frame
        let window = app.windows.firstMatch.frame
        let gap = firstRow.minY - send.frame.maxY
        print("PROBE window=\(window) qKey=\(firstRow) box=\(box.frame) send=\(send.frame) gap=\(gap) toolbars=\(app.toolbars.count)")
        XCTAssertGreaterThan(firstRow.height, 20, "no real keyboard to measure against")
        XCTAssertLessThanOrEqual(send.frame.maxY, firstRow.minY, "the send button is under the keyboard")
        // The keyboard's own row of suggestions is some 50 points; the web view's form bar was another 50.
        XCTAssertLessThan(gap, 110, "something sits between the message box and the keyboard")
        sleep(5)

        // With no bar over the keyboard there is no button to put it away: a tap on the conversation does.
        web.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.12)).tap()
        let gone = NSPredicate(format: "count == 0")
        expectation(for: gone, evaluatedWith: app.keyboards)
        waitForExpectations(timeout: 6)
        print("PROBE keyboard put away by a tap on the conversation")
        sleep(3)
    }
}
