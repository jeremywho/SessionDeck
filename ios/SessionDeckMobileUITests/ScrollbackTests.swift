import XCTest

/// Real finger swipes over the terminal of an already paired device, with the app logging where
/// xterm's viewport sits each second (see `--watch-viewport`); the log says whether swiping reaches the
/// scrollback or only moves the page.
final class ScrollbackTests: XCTestCase {
    func testSwipingDownScrollsIntoTheScrollback() throws {
        let host = TestConfig.host
        try XCTSkipIf(host.isEmpty, "TestConfig.host not set")
        let app = XCUIApplication()
        app.launchArguments = ["--open", host, "--watch-viewport"]
        app.launch()
        let web = app.webViews.firstMatch
        XCTAssertTrue(web.waitForExistence(timeout: 20), "terminal web view did not appear")
        sleep(5)
        for _ in 0..<4 {
            let from = web.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.3))
            let to = web.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.8))
            from.press(forDuration: 0.05, thenDragTo: to)
            sleep(2)
        }
        sleep(3)
    }
}
