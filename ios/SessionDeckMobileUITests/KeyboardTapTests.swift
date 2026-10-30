import XCTest

/// Drives a real finger-style tap on the terminal of an already paired device: the keyboard must
/// come up and stay up without a single dip, and the next tap must put it away. The session to open
/// comes from TestConfig.host, written on the build machine before the run.
final class KeyboardTapTests: XCTestCase {
    func testATapRaisesTheKeyboardCleanlyAndANextTapLowersIt() throws {
        let host = TestConfig.host
        try XCTSkipIf(host.isEmpty, "TestConfig.host not set")
        let app = XCUIApplication()
        app.launchArguments = ["--open", host]
        app.launch()

        let web = app.webViews.firstMatch
        XCTAssertTrue(web.waitForExistence(timeout: 20), "terminal web view did not appear")
        sleep(4)
        XCTAssertEqual(app.keyboards.count, 0, "keyboard up before any tap")

        web.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.4)).tap()
        XCTAssertTrue(app.keyboards.firstMatch.waitForExistence(timeout: 5), "keyboard did not come up on tap")
        // Watch it for two seconds: a close-and-reopen shows up as a sample with no keyboard.
        var dips = 0
        for _ in 0..<20 {
            usleep(100_000)
            if app.keyboards.count == 0 { dips += 1 }
        }
        XCTAssertEqual(dips, 0, "keyboard dipped \(dips) times after opening (open, close, reopen)")
        XCTAssertEqual(app.keyboards.count, 1, "keyboard not up after the first tap")

        web.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.3)).tap()
        let gone = XCTNSPredicateExpectation(predicate: NSPredicate(format: "count == 0"), object: app.keyboards)
        XCTAssertEqual(XCTWaiter.wait(for: [gone], timeout: 5), .completed, "keyboard did not go away on the next tap")
        usleep(1_500_000)
        XCTAssertEqual(app.keyboards.count, 0, "keyboard came back after the lowering tap")
    }
}
