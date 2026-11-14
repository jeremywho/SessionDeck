import XCTest

/// Swipes a session that is still at work (TestConfig.host names its row): the end is asked about first, Cancel
/// leaves it, and confirming ends it. Against a test gateway only.
final class SessionEndConfirmTests: XCTestCase {
    func testSwipingASessionAtWorkAsksFirst() throws {
        let name = TestConfig.host
        let pair = TestConfig.pair.split(separator: " ").map(String.init)
        guard !name.isEmpty, pair.count == 2 else {
            XCTFail("TestConfig.host (the row's name) and TestConfig.pair (\"address:port CODE\") must name a test gateway's session")
            return
        }
        let app = XCUIApplication()
        app.launchArguments = ["--pair", pair[0], pair[1]]
        app.launch()

        let row = app.cells.containing(.staticText, identifier: name).firstMatch
        XCTAssertTrue(row.waitForExistence(timeout: 30), "the session's row did not appear")
        row.swipeLeft()
        XCTAssertTrue(app.buttons["Close and end session"].waitForExistence(timeout: 5), "no end action under the swipe")
        app.buttons["Close and end session"].tap()

        let confirm = app.buttons.matching(NSPredicate(format: "label == 'Close and end session'")).firstMatch
        XCTAssertTrue(confirm.waitForExistence(timeout: 5), "a session at work was ended without asking")
        sleep(2)
        // iOS shows the question as a popover with no Cancel button: a tap outside it says no.
        app.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.9)).tap()
        sleep(2)
        XCTAssertFalse(confirm.exists, "the question stayed up")
        XCTAssertTrue(row.exists, "saying no ended it anyway")
        print("PROBE asked, and saying no kept it")

        row.swipeLeft()
        XCTAssertTrue(app.buttons["Close and end session"].waitForExistence(timeout: 5))
        app.buttons["Close and end session"].tap()
        XCTAssertTrue(confirm.waitForExistence(timeout: 5))
        confirm.tap()
        let gone = NSPredicate(format: "exists == false")
        expectation(for: gone, evaluatedWith: row)
        waitForExpectations(timeout: 15)
        print("PROBE confirmed, and it ended")
        sleep(2)
    }
}
