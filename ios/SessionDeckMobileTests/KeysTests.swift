import XCTest
@testable import SessionDeckMobile

final class KeysTests: XCTestCase {
    func testEveryKeyMapsToTheBytesATerminalExpects() {
        XCTAssertEqual(Keys.bytes(for: .esc), "\u{1b}")
        XCTAssertEqual(Keys.bytes(for: .tab), "\t")
        XCTAssertEqual(Keys.bytes(for: .shiftTab), "\u{1b}[Z")
        XCTAssertEqual(Keys.bytes(for: .up), "\u{1b}[A")
        XCTAssertEqual(Keys.bytes(for: .down), "\u{1b}[B")
        XCTAssertEqual(Keys.bytes(for: .right), "\u{1b}[C")
        XCTAssertEqual(Keys.bytes(for: .left), "\u{1b}[D")
        XCTAssertEqual(Keys.bytes(for: .ctrlC), "\u{03}")
        XCTAssertEqual(Keys.bytes(for: .enter), "\r")
    }

    func testJsLiteralEscapesWhatWouldBreakAScript() {
        XCTAssertEqual(Keys.jsLiteral("ls"), "\"ls\"")
        XCTAssertEqual(Keys.jsLiteral("a\"b"), "\"a\\\"b\"")
        XCTAssertEqual(Keys.jsLiteral("\u{1b}[A").lowercased(), "\"\\u001b[a\"")
        XCTAssertEqual(Keys.jsLiteral("line\r"), "\"line\\r\"")
        XCTAssertEqual(Keys.jsLiteral("</script>"), "\"<\\/script>\"")
    }
}
