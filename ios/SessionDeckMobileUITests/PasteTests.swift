import XCTest

/// A real tap on the key bar's paste key, with text on the pasteboard, must hand that text over.
/// The bar is shown alone (`--bar-only`), so nothing is typed into any session.
final class PasteTests: XCTestCase {
    func testTappingThePasteKeyHandsOverTheClipboard() throws {
        let app = XCUIApplication()
        app.launchArguments = ["--bar-only", "--clipboard", "paste-probe-7"]
        app.launch()

        let paste = app.buttons["paste"].firstMatch
        XCTAssertTrue(paste.waitForExistence(timeout: 20), "no paste key in the key bar")
        // On a phone the bar is wider than the screen: bring the key into view before tapping.
        var swipes = 0
        while !paste.isHittable && swipes < 4 { app.scrollViews.firstMatch.swipeLeft(); swipes += 1; usleep(400_000) }
        XCTAssertTrue(paste.isHittable, "paste key not on screen after \(swipes) swipes")

        let pasted = app.staticTexts["pasted: paste-probe-7"]
        XCTAssertFalse(pasted.exists, "the probe text is on screen before any paste")
        paste.tap()
        XCTAssertTrue(pasted.waitForExistence(timeout: 5), "tapping the paste key handed nothing over")
    }
}
