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
        var sent = ""
        init(_ bridge: TerminalBridge) { self.bridge = bridge }

        func textViewDidBeginEditing(_ textView: UITextView) { bridge.keyboardShown = true; bridge.keyboardCameUp() }
        func textViewDidEndEditing(_ textView: UITextView) { bridge.keyboardShown = false }

        func textView(_ textView: UITextView, shouldChangeTextIn range: NSRange, replacementText text: String) -> Bool {
            guard text == "\n" else { return true }
            flush(textView)
            Task { await bridge.input("\r") }
            textView.text = ""
            sent = ""
            return false
        }

        func textViewDidChange(_ textView: UITextView) { flush(textView) }

        /// Send what changed since the last flush: backspaces for the part that differs, then the new tail.
        private func flush(_ textView: UITextView) {
            let now = textView.text ?? ""
            guard now != sent else { return }
            let bytes = Self.delta(from: sent, to: now)
            sent = now
            if !bytes.isEmpty { Task { await bridge.input(bytes) } }
            if now.count > 4000 { textView.text = ""; sent = "" }
        }

        static func delta(from old: String, to new: String) -> String {
            let o = Array(old), n = Array(new)
            var p = 0
            while p < o.count && p < n.count && o[p] == n[p] { p += 1 }
            return String(repeating: "\u{7f}", count: o.count - p) + String(n[p...])
        }
    }
}

final class ProxyTextView: UITextView {
    override var intrinsicContentSize: CGSize { CGSize(width: UIView.noIntrinsicMetric, height: 1) }
    override func caretRect(for position: UITextPosition) -> CGRect { .zero }
    override func selectionRects(for range: UITextRange) -> [UITextSelectionRect] { [] }
}
