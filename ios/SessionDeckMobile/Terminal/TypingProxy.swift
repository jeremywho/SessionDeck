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
        v.send = { [weak bridge] bytes in Task { await bridge?.input(bytes) } }
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
        bridge.proxy = v
        return v
    }

    func updateUIView(_ uiView: ProxyTextView, context: Context) {}

    func makeCoordinator() -> Coordinator { Coordinator(bridge) }

    final class Coordinator: NSObject, UITextViewDelegate {
        let bridge: TerminalBridge
        init(_ bridge: TerminalBridge) { self.bridge = bridge }

        func textViewDidBeginEditing(_ textView: UITextView) { bridge.keyboardShown = true; bridge.keyboardCameUp() }
        func textViewDidEndEditing(_ textView: UITextView) { bridge.keyboardShown = false }

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

final class ProxyTextView: UITextView {
    var send: ((String) -> Void)?
    private(set) var sent = ""

    /// What differs between the text the terminal has and the text here: backspaces for the part
    /// that changed, then the new tail. Comparison is by grapheme so a rewrite of "wanted" to "want"
    /// lands on the right spot; erasures are counted in UTF-16 units, which is what PSReadLine and a
    /// Node prompt delete per backspace (a combining mark or an emoji's halves each need their own).
    static func delta(from old: String, to new: String) -> String {
        let o = Array(old), n = Array(new)
        var p = 0
        while p < o.count && p < n.count && o[p] == n[p] { p += 1 }
        let erase = String(o[p...]).utf16.count
        return String(repeating: "\u{7f}", count: erase) + String(n[p...])
    }

    func flush() {
        let now = text ?? ""
        guard now != sent else { return }
        let bytes = Self.delta(from: sent, to: now)
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
