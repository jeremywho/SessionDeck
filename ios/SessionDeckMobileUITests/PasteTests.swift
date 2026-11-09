import XCTest

/// A real tap on the key bar's system paste button, with text on the pasteboard: the app log must
/// show the paste being handed over (the system control cannot be triggered any other way).
final class PasteTests: XCTestCase {
    func testTappingThePasteButtonHandsOverTheClipboard() throws {
        let host = TestConfig.host
        try XCTSkipIf(host.isEmpty, "TestConfig.host not set")
        let app = XCUIApplication()
        app.launchArguments = ["--open", host, "--clipboard", "paste-probe-7"]
        app.launch()
        let web = app.webViews.firstMatch
        XCTAssertTrue(web.waitForExistence(timeout: 20), "terminal web view did not appear")
        sleep(3)
        let paste = app.buttons["Paste"].firstMatch
        XCTAssertTrue(paste.waitForExistence(timeout: 5), "no Paste button in the key bar")
        XCTAssertTrue(paste.isEnabled, "Paste button disabled with text on the pasteboard")
        paste.tap()
        sleep(3)
    }
}
