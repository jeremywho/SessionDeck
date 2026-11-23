import XCTest

/// Opens the deck's page from the session list against a test gateway that names one (TestConfig.host is
/// the page's name, TestConfig.pair the gateway): the page comes up, what it fetches from the root of
/// its own server arrives, its button cannot be pressed, and Done puts it away.
final class PageSheetTests: XCTestCase {
    func testTheDecksPageOpensReadsItsDataAndIsPutAway() throws {
        let name = TestConfig.host
        let pair = TestConfig.pair.split(separator: " ").map(String.init)
        guard !name.isEmpty, pair.count == 2 else {
            XCTFail("TestConfig.host (the page's name) and TestConfig.pair (\"address:port CODE\") must name a test gateway with a page")
            return
        }
        let app = XCUIApplication()
        app.launchArguments = ["--pair", pair[0], pair[1]]
        app.launch()

        let open = app.buttons[name]
        XCTAssertTrue(open.waitForExistence(timeout: 30), "no button for the deck's page")
        open.tap()

        let web = app.webViews["deck-page"]
        XCTAssertTrue(web.waitForExistence(timeout: 15), "the page did not open")
        // The page writes this only once its fetch of /api/usage, from the root of its own server, has answered.
        let fetched = web.staticTexts["USAGE-OK 42"]
        XCTAssertTrue(fetched.waitForExistence(timeout: 20), "the page's own fetch did not arrive through the gateway")
        sleep(2)

        // Its button would change something on the deck's machine: a tap on it does nothing here.
        let spend = web.buttons["Spend it"]
        XCTAssertTrue(spend.exists, "the page's button is not there")
        spend.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.5)).tap()
        sleep(2)
        XCTAssertFalse(web.staticTexts["SPENT"].exists, "the page's button changed something")
        XCTAssertFalse(web.staticTexts["ASKED"].exists, "the page's button was pressed")

        app.buttons["Done"].tap()
        let gone = NSPredicate(format: "exists == false")
        expectation(for: gone, evaluatedWith: web)
        waitForExpectations(timeout: 6)
        XCTAssertTrue(open.exists, "the session list is not back")
    }
}
