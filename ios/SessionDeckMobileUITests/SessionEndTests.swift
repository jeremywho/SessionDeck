import XCTest

/// Swipes a session in the list to close and end it, against a test gateway only. TestConfig.host names the
/// session's row (its name as the list shows it); the session must be idle, so it ends without being asked about.
final class SessionEndTests: XCTestCase {
    func testSwipingAnIdleSessionEndsItWithoutAsking() throws {
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
        let others = app.cells.count
        print("PROBE rows=\(others)")
        row.swipeLeft()
        let end = app.buttons["Close and end session"]
        XCTAssertTrue(end.waitForExistence(timeout: 5), "no end action under the swipe")
        sleep(1)
        end.tap()

        XCTAssertFalse(app.sheets.firstMatch.waitForExistence(timeout: 2), "an idle session was asked about")
        let gone = NSPredicate(format: "exists == false")
        expectation(for: gone, evaluatedWith: row)
        waitForExpectations(timeout: 15)
        XCTAssertEqual(app.cells.count, others - 1, "another row went with it")
        print("PROBE ended \(name)")
        sleep(2)
    }
}
