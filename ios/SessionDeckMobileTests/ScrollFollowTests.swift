import XCTest
@testable import SessionDeckMobile

final class ScrollFollowTests: XCTestCase {
    func testLandingWinsOverTheReadersPlace() {
        XCTAssertEqual(ScrollFollow.decide(landing: true, readerAtBottom: false), .land)
        XCTAssertEqual(ScrollFollow.decide(landing: true, readerAtBottom: true), .land)
    }

    func testAReaderAtTheBottomFollowsAndAReaderWhoScrolledUpKeepsTheirPlace() {
        XCTAssertEqual(ScrollFollow.decide(landing: false, readerAtBottom: true), .follow)
        XCTAssertEqual(ScrollFollow.decide(landing: false, readerAtBottom: false), .keep)
    }

    func testAtBottomMeansNearThePageEndOrNearTheLastLanding() {
        XCTAssertTrue(ScrollFollow.atBottom(offsetY: 990, maxY: 1000, lastLandedY: nil))
        XCTAssertFalse(ScrollFollow.atBottom(offsetY: 0, maxY: 1000, lastLandedY: nil))
        XCTAssertTrue(ScrollFollow.atBottom(offsetY: 610, maxY: 1000, lastLandedY: 600))
        XCTAssertFalse(ScrollFollow.atBottom(offsetY: 300, maxY: 1000, lastLandedY: 600))
    }
}
