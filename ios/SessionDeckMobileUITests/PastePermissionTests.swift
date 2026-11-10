import UIKit
import XCTest

/// Text another app put on the clipboard: iOS asks before our paste key may read it, every time,
/// unless this app's "Paste from Other Apps" setting says Allow. The test runner is that other app.
/// Each test prints PROBE lines saying what happened; run them one at a time.
final class PastePermissionTests: XCTestCase {
    private let springboard = XCUIApplication(bundleIdentifier: "com.apple.springboard")

    func testPasteFromAnotherApp() throws {
        let probe = "from-another-app-\(Int.random(in: 1000...9999))"
        UIPasteboard.general.string = probe
        let app = XCUIApplication()
        app.launchArguments = ["--bar-only"]
        app.launch()

        let paste = app.buttons["paste"].firstMatch
        XCTAssertTrue(paste.waitForExistence(timeout: 20), "no paste key in the key bar")
        var swipes = 0
        while !paste.isHittable && swipes < 4 { app.scrollViews.firstMatch.swipeLeft(); swipes += 1; usleep(400_000) }
        paste.tap()

        let allowButtons = [springboard.buttons["Allow Paste"], app.buttons["Allow Paste"]]
        var asked = false
        let deadline = Date().addingTimeInterval(4)
        while Date() < deadline && !asked {
            for button in allowButtons where button.exists {
                asked = true
                let alert = springboard.alerts.firstMatch.exists ? springboard.alerts.firstMatch.label : app.alerts.firstMatch.label
                print("PROBE asked=true alert=[\(alert)]")
                button.tap()
                break
            }
            usleep(200_000)
        }
        if !asked { print("PROBE asked=false") }
        let pasted = app.staticTexts["pasted: \(probe)"].waitForExistence(timeout: 5)
        print("PROBE pasted=\(pasted)")
        XCTAssertTrue(pasted, "the clipboard text never reached the app")
    }

    func testAllowPasteInSettings() throws {
        let app = XCUIApplication()
        app.launchArguments = ["--bar-only", "--open-settings"]
        app.launch()
        let settings = XCUIApplication(bundleIdentifier: "com.apple.Preferences")
        XCTAssertTrue(settings.wait(for: .runningForeground, timeout: 15), "Settings did not come to the front")

        let row = settings.staticTexts["Paste from Other Apps"].firstMatch
        guard row.waitForExistence(timeout: 8) else {
            print("PROBE settings rows: \(settings.staticTexts.allElementsBoundByIndex.prefix(40).map(\.label))")
            return XCTFail("no Paste from Other Apps row on the app's Settings page")
        }
        row.tap()
        let allow = settings.staticTexts["Allow"].firstMatch
        guard allow.waitForExistence(timeout: 5) else {
            print("PROBE settings options: \(settings.staticTexts.allElementsBoundByIndex.prefix(40).map(\.label))")
            return XCTFail("no Allow option under Paste from Other Apps")
        }
        allow.tap()
        usleep(600_000)
        print("PROBE settings: Paste from Other Apps set to Allow")
    }
}
