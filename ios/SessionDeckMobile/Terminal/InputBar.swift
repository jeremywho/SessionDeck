import SwiftUI
import UIKit

/// What a prompt needs from a phone: a draft of one or more lines with one Send button, and the keys
/// the keyboard lacks. Return adds a line, as in any message box; Send (or Shift+Return on a hardware
/// keyboard) submits; the key bar's enter key presses Enter in the terminal without a draft.
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

    private var canSend: Bool { !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }

    var body: some View {
        VStack(spacing: 6) {
            ScrollView(.horizontal, showsIndicators: false) {
                HStack(spacing: 6) {
                    ForEach(Key.allCases) { key in
                        Button(key.label) { onKey(key) }
                            .buttonStyle(.bordered)
                            .font(.system(size: 13, weight: .medium, design: .monospaced))
                            .accessibilityLabel(key.spoken)
                    }
                    Divider().frame(height: 20)
                    Button("paste") { onPaste() }.buttonStyle(.bordered).font(.system(size: 13, design: .monospaced))
                    Button(selecting ? (hasSelection ? "copy" : "cancel") : "select") { selecting && hasSelection ? onCopy() : onSelect() }
                        .buttonStyle(.bordered).font(.system(size: 13, design: .monospaced))
                        .tint(selecting ? .orange : nil)
                }
                .padding(.horizontal, 8)
            }
            HStack(alignment: .bottom, spacing: 8) {
                DraftField(text: $text, placeholder: "Type to the session", onSubmit: onSend)
                    .frame(minHeight: 36, maxHeight: 132)
                    .padding(.horizontal, 6)
                    .background(Color(white: 0.14), in: RoundedRectangle(cornerRadius: 10))
                    .focused(focused)
                Button(action: onSend) {
                    Label("Send", systemImage: "paperplane.fill")
                        .font(.system(size: 14, weight: .semibold))
                        .padding(.horizontal, 12)
                        .frame(height: 36)
                }
                .buttonStyle(.borderedProminent)
                .disabled(!canSend)
            }
            .padding(.horizontal, 8)
        }
        .padding(.vertical, 6)
        .background(Color(white: 0.08))
    }
}

/// A growing text view that sends exactly what was typed: no autocorrect, smart quotes or dashes,
/// no capitalisation. Return adds a line; Shift+Return on a hardware keyboard submits; the
/// keyboard's dictation key works as it does anywhere else.
struct DraftField: UIViewRepresentable {
    @Binding var text: String
    let placeholder: String
    let onSubmit: () -> Void

    func makeUIView(context: Context) -> DraftTextView {
        let v = DraftTextView()
        v.delegate = context.coordinator
        v.onSubmit = onSubmit
        v.placeholderLabel.text = placeholder
        v.autocorrectionType = .no
        v.autocapitalizationType = .none
        v.spellCheckingType = .no
        v.smartQuotesType = .no
        v.smartDashesType = .no
        v.smartInsertDeleteType = .no
        v.returnKeyType = .default
        v.font = UIFont.monospacedSystemFont(ofSize: 15, weight: .regular)
        v.textColor = .white
        v.backgroundColor = .clear
        v.isScrollEnabled = true
        v.textContainerInset = UIEdgeInsets(top: 8, left: 2, bottom: 8, right: 2)
        v.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
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
    }
}

final class DraftTextView: UITextView {
    var onSubmit: (() -> Void)?
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
        let send = UIKeyCommand(input: "\r", modifierFlags: .shift, action: #selector(submit))
        send.wantsPriorityOverSystemBehavior = true
        let sendCmd = UIKeyCommand(input: "\r", modifierFlags: .command, action: #selector(submit))
        sendCmd.wantsPriorityOverSystemBehavior = true
        return [send, sendCmd]
    }

    @objc private func submit() { onSubmit?() }
}
