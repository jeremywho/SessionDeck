import XCTest
@testable import SessionDeckMobile

final class TypingProxyTests: XCTestCase {
    typealias P = ProxyTextView
    let del = "\u{7f}"

    func testTypingSendsOnlyTheNewTail() {
        XCTAssertEqual(P.delta(from: "", to: "h"), "h")
        XCTAssertEqual(P.delta(from: "h", to: "he"), "e")
        XCTAssertEqual(P.delta(from: "hello", to: "hello world"), " world")
        XCTAssertEqual(P.delta(from: "same", to: "same"), "")
    }

    func testADictationRewriteBecomesBackspacesAndTheCorrectedTail() {
        XCTAssertEqual(P.delta(from: "I wanted to", to: "I want to"), String(repeating: del, count: 5) + " to")
        XCTAssertEqual(P.delta(from: "The problem", to: "The problem is"), " is")
        XCTAssertEqual(P.delta(from: "abc", to: "ab"), del)
        XCTAssertEqual(P.delta(from: "abc", to: ""), String(repeating: del, count: 3))
    }

    func testErasuresAreCountedInUtf16UnitsAsTheTerminalDeletesThem() {
        XCTAssertEqual(P.delta(from: "a\u{0301}", to: "b"), del + del + "b")
        XCTAssertEqual(P.delta(from: "x😀", to: "x"), del + del)
        XCTAssertEqual(P.delta(from: "é", to: "e"), del + "e")
    }

    func testTheGrowingInterimResultsFromTheBugReportCollapseToOnePhrase() {
        let interims = ["Th", "The", "The problem", "The problem is with", "The problem is with the", "The problem is with the scroll", "The problem is with the scrolling"]
        var terminal: [Unicode.UTF16.CodeUnit] = []
        var sent = ""
        for now in interims {
            let bytes = P.delta(from: sent, to: now)
            sent = now
            var i = bytes.utf16.makeIterator()
            while let u = i.next() {
                if u == 0x7f { if !terminal.isEmpty { terminal.removeLast() } } else { terminal.append(u) }
            }
        }
        XCTAssertEqual(String(decoding: terminal, as: UTF16.self), "The problem is with the scrolling")
    }
}
