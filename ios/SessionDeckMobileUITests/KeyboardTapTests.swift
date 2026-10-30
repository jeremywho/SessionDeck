import XCTest

/// Drives a real finger-style tap on the terminal of an already paired device and checks the
/// keyboard comes up and stays up, then goes away on the next tap. Needs SD_UITEST_HOST in the
/// environment: the host id of a session to open.
final class KeyboardTapTests: XCTestCase {
    func testATapRaisesTheKeyboardAndKeepsItAndANextTapLowersIt() throws {
        let host = ProcessInfo.processInfo.environment["SD_UITEST_HOST"] ?? ""
        try XCTSkipIf(host.isEmpty, "SD_UITEST_HOST not set")
        let app = XCUIApplication()
        app.launchArguments = ["--open", host]
        app.launch()

        let web = app.webViews.firstMatch
        XCTAssertTrue(web.waitForExistence(timeout: 20), "terminal web view did not appear")
        sleep(4)

        XCTAssertEqual(app.keyboards.count, 0, "keyboard up before any tap")
        web.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.4)).tap()
        XCTAssertTrue(app.keyboards.firstMatch.waitForExistence(timeout: 5), "keyboard did not come up on tap")
        sleep(2)
        XCTAssertEqual(app.keyboards.count, 1, "keyboard did not stay up (flashed and closed)")

        web.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.3)).tap()
        let gone = NSPredicate(format: "count == 0")
        let expectation = XCTNSPredicateExpectation(predicate: gone, object: app.keyboards)
        XCTAssertEqual(XCTWaiter.wait(for: [expectation], timeout: 5), .completed, "keyboard did not go away on the next tap")
    }
}
