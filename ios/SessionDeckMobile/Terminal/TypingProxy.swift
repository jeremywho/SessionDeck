import SwiftUI
import UIKit

/// Where the phone keyboard types. iOS dictation (and autocorrect, had it been on) rewrites the
/// whole phrase on every interim result; a terminal must receive only the difference. This hidden
/// field keeps what the terminal has been sent and emits backspaces plus the new tail on each change,
/// so a dictated or corrected phrase arrives in the terminal exactly once.
struct TypingProxy: UIViewRepresentable {
    @ObservedObject var bridge: TerminalBridge

    func makeUIView(context: Context) -> ProxyTextView {
        let v = ProxyTextView()
        v.delegate = context.coordinator
        v.send = { [weak bridge] bytes in bridge?.manualInput?(); Task { await bridge?.input(bytes) } }
        v.autocorrectionType = .no
        v.autocapitalizationType = .none
        v.spellCheckingType = .no
        v.smartQuotesType = .no
        v.smartDashesType = .no
        v.smartInsertDeleteType = .no
        v.returnKeyType = .default
        v.alpha = 0.02
        v.isScrollEnabled = false
        v.backgroundColor = .clear
        v.textColor = .clear
        v.tintColor = .clear
        v.unit = DeleteUnit.forProvider(bridge.provider)
        bridge.proxy = v
        return v
    }

    func updateUIView(_ uiView: ProxyTextView, context: Context) {}

    func makeCoordinator() -> Coordinator { Coordinator(bridge) }

    final class Coordinator: NSObject, UITextViewDelegate {
        let bridge: TerminalBridge
        init(_ bridge: TerminalBridge) { self.bridge = bridge }

        func textViewDidBeginEditing(_ textView: UITextView) { bridge.keyboardShown = true; LaunchOptions.log("keyboard field began editing"); bridge.keyboardCameUp() }
        func textViewDidEndEditing(_ textView: UITextView) { bridge.editingEnded() }

        func textView(_ textView: UITextView, shouldChangeTextIn range: NSRange, replacementText text: String) -> Bool {
            guard text == "\n", let proxy = textView as? ProxyTextView else { return true }
            proxy.flush()
            proxy.send?("\r")
            proxy.resetBaseline()
            return false
        }

        func textViewDidChange(_ textView: UITextView) { (textView as? ProxyTextView)?.flush() }
    }
}

/// What one backspace removes in the program at the other end. PSReadLine deletes a UTF-16 unit, a
/// Node prompt (Claude Code) a code point, the Codex TUI a whole grapheme.
enum DeleteUnit {
    case utf16, scalar, grapheme

    static func forProvider(_ provider: String) -> DeleteUnit {
        switch provider {
        case "Codex": return .grapheme
        case "Shell": return .utf16
        default: return .scalar
        }
    }

    func count(_ s: Substring) -> Int {
        switch self {
        case .utf16: return s.utf16.count
        case .scalar: return s.unicodeScalars.count
        case .grapheme: return s.count
        }
    }
}

final class ProxyTextView: UITextView {
    var send: ((String) -> Void)?
    var unit: DeleteUnit = .scalar
    private(set) var sent = ""

    /// What differs between the text the terminal has and the text here: backspaces for the part
    /// that changed, then the new tail. Comparison is by grapheme so a rewrite of "wanted" to "want"
    /// lands on the right spot; erasures are counted in the unit the program deletes per backspace.
    static func delta(from old: String, to new: String, unit: DeleteUnit = .scalar) -> String {
        let o = Array(old), n = Array(new)
        var p = 0
        while p < o.count && p < n.count && o[p] == n[p] { p += 1 }
        let erase = unit.count(Substring(String(o[p...])))
        return String(repeating: "\u{7f}", count: erase) + String(n[p...])
    }

    func flush() {
        let now = text ?? ""
        guard now != sent else { return }
        let bytes = Self.delta(from: sent, to: now, unit: unit)
        sent = now
        if !bytes.isEmpty { send?(bytes) }
    }

    /// The terminal's cursor moved (a key-bar key, Enter, a paste): what is here no longer lines up
    /// with the terminal's line, so typing starts afresh from the cursor.
    func resetBaseline() {
        text = ""
        sent = ""
    }

    /// Backspace with nothing of ours left: the terminal still has text, so the key goes through as DEL.
    override func deleteBackward() {
        if (text ?? "").isEmpty { send?("\u{7f}"); return }
        super.deleteBackward()
    }

    override var intrinsicContentSize: CGSize { CGSize(width: UIView.noIntrinsicMetric, height: 1) }
    override func caretRect(for position: UITextPosition) -> CGRect { .zero }
    override func selectionRects(for range: UITextRange) -> [UITextSelectionRect] { [] }
}
