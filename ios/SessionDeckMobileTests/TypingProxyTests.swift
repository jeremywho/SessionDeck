import XCTest
@testable import SessionDeckMobile

final class TypingProxyTests: XCTestCase {
    typealias D = TypingProxy.Coordinator

    func testTypingSendsOnlyTheNewTail() {
        XCTAssertEqual(D.delta(from: "", to: "h"), "h")
        XCTAssertEqual(D.delta(from: "h", to: "he"), "e")
        XCTAssertEqual(D.delta(from: "hello", to: "hello world"), " world")
    }

    func testADictationRewriteBecomesBackspacesAndTheCorrectedTail() {
        // iOS replaces "I wanted to" with "I want to": back over "ed to" (five), then " to".
        XCTAssertEqual(D.delta(from: "I wanted to", to: "I want to"), String(repeating: "\u{7f}", count: 5) + " to")
        XCTAssertEqual(D.delta(from: "The problem", to: "The problem is"), " is")
        XCTAssertEqual(D.delta(from: "abc", to: "ab"), "\u{7f}")
        XCTAssertEqual(D.delta(from: "abc", to: ""), "\u{7f}\u{7f}\u{7f}")
        XCTAssertEqual(D.delta(from: "same", to: "same"), "")
    }

    func testTheGrowingInterimResultsFromTheBugReportCollapseToOnePhrase() {
        let interims = ["Th", "The", "The problem", "The problem is with", "The problem is with the", "The problem is with the scroll", "The problem is with the scrolling"]
        var terminal = ""
        var sent = ""
        for now in interims {
            let bytes = D.delta(from: sent, to: now)
            sent = now
            for ch in bytes {
                if ch == "\u{7f}" { if !terminal.isEmpty { terminal.removeLast() } } else { terminal.append(ch) }
            }
        }
        XCTAssertEqual(terminal, "The problem is with the scrolling")
    }
}
