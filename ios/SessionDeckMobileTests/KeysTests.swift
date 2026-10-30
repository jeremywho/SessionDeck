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

    func testAPasteWrapsTextInBracketedPasteMarkersWithTerminalNewlines() {
        XCTAssertEqual(Keys.pasteSequence("one\ntwo"), "\u{1b}[200~one\rtwo\u{1b}[201~")
        XCTAssertEqual(Keys.pasteSequence("a\r\nb"), "\u{1b}[200~a\rb\u{1b}[201~")
        XCTAssertEqual(Keys.pasteSequence("plain"), "\u{1b}[200~plain\u{1b}[201~")
    }

    func testLaunchArgumentEscapesSpellKeyBytes() {
        XCTAssertEqual(LaunchOptions.unescape("abc\\e"), "abc\u{1b}")
        XCTAssertEqual(LaunchOptions.unescape("\\x03"), "\u{03}")
        XCTAssertEqual(LaunchOptions.unescape("a\\tb\\r"), "a\tb\r")
        XCTAssertEqual(LaunchOptions.unescape("C:\\\\x"), "C:\\x")
        XCTAssertEqual(LaunchOptions.unescape("plain"), "plain")
    }

    func testJsLiteralEscapesWhatWouldBreakAScript() {
        XCTAssertEqual(Keys.jsLiteral("ls"), "\"ls\"")
        XCTAssertEqual(Keys.jsLiteral("a\"b"), "\"a\\\"b\"")
        XCTAssertEqual(Keys.jsLiteral("\u{1b}[A").lowercased(), "\"\\u001b[a\"")
        XCTAssertEqual(Keys.jsLiteral("line\r"), "\"line\\r\"")
        XCTAssertEqual(Keys.jsLiteral("</script>"), "\"<\\/script>\"")
    }
}
