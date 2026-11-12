import XCTest

/// Types into a chat session with the on-screen keyboard and sends with the button, against a test
/// gateway only: the launch carries `--pair`, which sets the build's own relay aside. The gateway and
/// the chat session to open come from TestConfig, written on the build machine before the run.
final class ChatKeyboardTests: XCTestCase {
    func testTheMessageBoxStaysAboveTheKeyboardAndATypedMessageIsSentAndAnswered() throws {
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
        // The pickers show once the session has said what is in force: the page is attached by then.
        XCTAssertTrue(web.otherElements["Model"].waitForExistence(timeout: 30) || web.popUpButtons["Model"].waitForExistence(timeout: 5) || web.buttons["Model"].waitForExistence(timeout: 5),
                      "the model picker did not appear: the page never attached")
        XCTAssertEqual(app.keyboards.count, 0, "keyboard up before any tap")

        box.tap()
        XCTAssertTrue(app.keyboards.firstMatch.waitForExistence(timeout: 8), "the keyboard did not come up on a tap in the message box")
        box.typeText("Reply with exactly the single word: pong")
        usleep(800_000)

        let keyboard = app.keyboards.firstMatch.frame
        let window = app.windows.firstMatch.frame
        print("PROBE window=\(window) keyboard=\(keyboard) box=\(box.frame) send=\(send.frame)")
        XCTAssertGreaterThan(keyboard.height, 100, "no real keyboard to measure against")
        XCTAssertLessThanOrEqual(send.frame.maxY, keyboard.minY + 1, "the send button is under the keyboard")
        XCTAssertLessThanOrEqual(box.frame.maxY, keyboard.minY + 1, "the message box is under the keyboard")
        XCTAssertGreaterThanOrEqual(box.frame.minY, window.minY + 60, "the message box was pushed off the top")
        XCTAssertLessThan(keyboard.minY - send.frame.maxY, 140, "the message box floats far above the keyboard")
        XCTAssertTrue(send.isHittable, "the send button cannot be tapped")

        send.tap()
        XCTAssertTrue(web.staticTexts["pong"].waitForExistence(timeout: 120), "no reply arrived")
        print("PROBE replied")
    }
}
