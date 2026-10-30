import SwiftUI
import UIKit

/// What a prompt needs from a phone: a draft of one or more lines with Send, and the keys the
/// keyboard lacks. Return sends; the newline key (or Shift+Return on a hardware keyboard) adds a line.
struct InputBar: View {
    @Binding var text: String
    var focused: FocusState<Bool>.Binding
    let selecting: Bool
    let hasSelection: Bool
    let onSend: () -> Void
    let onKey: (Key) -> Void
    let onPaste: () -> Void
    let onCopy: () -> Void
    let onSelect: () -> Void
    @StateObject private var draft = DraftHandle()

    var body: some View {
        VStack(spacing: 6) {
            ScrollView(.horizontal, showsIndicators: false) {
                HStack(spacing: 6) {
                    ForEach(Key.allCases) { key in
                        Button(key.label) { onKey(key) }
                            .buttonStyle(.bordered)
                            .font(.system(size: 13, weight: .medium, design: .monospaced))
                    }
                    Button("newline") { draft.insertNewline() }.buttonStyle(.bordered).font(.system(size: 13, design: .monospaced))
                    Divider().frame(height: 20)
                    Button("paste") { onPaste() }.buttonStyle(.bordered).font(.system(size: 13, design: .monospaced))
                    Button(selecting ? (hasSelection ? "copy" : "cancel") : "select") { selecting && hasSelection ? onCopy() : onSelect() }
                        .buttonStyle(.bordered).font(.system(size: 13, design: .monospaced))
                        .tint(selecting ? .orange : nil)
                }
                .padding(.horizontal, 8)
            }
            HStack(alignment: .bottom, spacing: 8) {
                DraftField(text: $text, handle: draft, placeholder: "Type to the session, return sends", onSubmit: onSend)
                    .frame(minHeight: 36, maxHeight: 132)
                    .padding(.horizontal, 6)
                    .background(Color(white: 0.14), in: RoundedRectangle(cornerRadius: 10))
                    .focused(focused)
                Button(action: onSend) { Image(systemName: "arrow.up.circle.fill").font(.system(size: 30)) }
                    .disabled(text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                    .padding(.bottom, 3)
            }
            .padding(.horizontal, 8)
        }
        .padding(.vertical, 6)
        .background(Color(white: 0.08))
    }
}

/// The SwiftUI side's handle on the text view, so the newline key inserts where the cursor is.
@MainActor
final class DraftHandle: ObservableObject {
    weak var textView: DraftTextView?
    func insertNewline() { textView?.insertNewlineAtCursor() }
}

/// A growing text view that sends exactly what was typed: no autocorrect, smart quotes or dashes,
/// no capitalisation. Return sends, Shift+Return (hardware keyboard) or the newline key adds a line,
/// and the keyboard's dictation key works as it does anywhere else.
struct DraftField: UIViewRepresentable {
    @Binding var text: String
    let handle: DraftHandle
    let placeholder: String
    let onSubmit: () -> Void

    func makeUIView(context: Context) -> DraftTextView {
        let v = DraftTextView()
        v.delegate = context.coordinator
        v.placeholderLabel.text = placeholder
        v.autocorrectionType = .no
        v.autocapitalizationType = .none
        v.spellCheckingType = .no
        v.smartQuotesType = .no
        v.smartDashesType = .no
        v.smartInsertDeleteType = .no
        v.returnKeyType = .send
        v.enablesReturnKeyAutomatically = true
        v.font = UIFont.monospacedSystemFont(ofSize: 15, weight: .regular)
        v.textColor = .white
        v.backgroundColor = .clear
        v.isScrollEnabled = true
        v.textContainerInset = UIEdgeInsets(top: 8, left: 2, bottom: 8, right: 2)
        v.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        handle.textView = v
        return v
    }

    func updateUIView(_ uiView: DraftTextView, context: Context) {
        if uiView.text != text { uiView.text = text }
        uiView.placeholderLabel.isHidden = !text.isEmpty
        uiView.invalidateIntrinsicContentSize()
    }

    func makeCoordinator() -> Coordinator { Coordinator(self) }

    final class Coordinator: NSObject, UITextViewDelegate {
        var parent: DraftField
        init(_ parent: DraftField) { self.parent = parent }

        func textViewDidChange(_ textView: UITextView) {
            parent.text = textView.text ?? ""
            (textView as? DraftTextView)?.placeholderLabel.isHidden = !textView.text.isEmpty
            textView.invalidateIntrinsicContentSize()
        }

        func textView(_ textView: UITextView, shouldChangeTextIn range: NSRange, replacementText text: String) -> Bool {
            // The on-screen return key: send. Newlines come only through insertNewlineAtCursor.
            if text == "\n", let draft = textView as? DraftTextView, !draft.insertingNewline {
                parent.onSubmit()
                return false
            }
            return true
        }
    }
}

final class DraftTextView: UITextView {
    private(set) var insertingNewline = false
    let placeholderLabel = UILabel()

    override init(frame: CGRect, textContainer: NSTextContainer?) {
        super.init(frame: frame, textContainer: textContainer)
        placeholderLabel.font = UIFont.monospacedSystemFont(ofSize: 15, weight: .regular)
        placeholderLabel.textColor = UIColor(white: 0.55, alpha: 1)
        placeholderLabel.translatesAutoresizingMaskIntoConstraints = false
        addSubview(placeholderLabel)
        NSLayoutConstraint.activate([
            placeholderLabel.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 7),
            placeholderLabel.topAnchor.constraint(equalTo: topAnchor, constant: 8),
        ])
    }

    required init?(coder: NSCoder) { fatalError("not used") }

    override var intrinsicContentSize: CGSize {
        let fitted = sizeThatFits(CGSize(width: bounds.width > 0 ? bounds.width : 300, height: .greatestFiniteMagnitude))
        return CGSize(width: UIView.noIntrinsicMetric, height: min(max(fitted.height, 36), 132))
    }

    override var keyCommands: [UIKeyCommand]? {
        let shiftReturn = UIKeyCommand(input: "\r", modifierFlags: .shift, action: #selector(insertNewlineAtCursor))
        // Ahead of the system handler, or UIKit delivers a plain newline first and the draft is sent instead.
        shiftReturn.wantsPriorityOverSystemBehavior = true
        return [shiftReturn]
    }

    /// A newline where the cursor is (replacing any selection), bypassing the return-sends rule.
    @objc func insertNewlineAtCursor() {
        insertingNewline = true
        insertText("\n")
        insertingNewline = false
        delegate?.textViewDidChange?(self)
    }
}
