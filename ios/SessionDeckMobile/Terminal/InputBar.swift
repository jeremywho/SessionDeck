import SwiftUI
import UIKit

/// What a prompt needs from a phone: a line of text with Send, and the keys the keyboard lacks.
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

    var body: some View {
        VStack(spacing: 6) {
            ScrollView(.horizontal, showsIndicators: false) {
                HStack(spacing: 6) {
                    ForEach(Key.allCases) { key in
                        Button(key.label) { onKey(key) }
                            .buttonStyle(.bordered)
                            .font(.system(size: 13, weight: .medium, design: .monospaced))
                    }
                    Divider().frame(height: 20)
                    Button("paste") { onPaste() }.buttonStyle(.bordered).font(.system(size: 13, design: .monospaced))
                    Button(selecting ? (hasSelection ? "copy" : "cancel") : "select") { selecting && hasSelection ? onCopy() : onSelect() }
                        .buttonStyle(.bordered).font(.system(size: 13, design: .monospaced))
                        .tint(selecting ? .orange : nil)
                }
                .padding(.horizontal, 8)
            }
            HStack(spacing: 8) {
                PlainTextField(text: $text, placeholder: "Type to the session", onSubmit: onSend)
                    .frame(height: 36)
                    .padding(.horizontal, 10)
                    .background(Color(white: 0.14), in: RoundedRectangle(cornerRadius: 10))
                    .focused(focused)
                Button(action: onSend) { Image(systemName: "arrow.up.circle.fill").font(.system(size: 30)) }
                    .disabled(text.isEmpty)
            }
            .padding(.horizontal, 8)
        }
        .padding(.vertical, 6)
        .background(Color(white: 0.08))
    }
}

/// A text field that sends exactly what was typed: no autocorrect, no smart quotes or dashes, no capitalisation.
struct PlainTextField: UIViewRepresentable {
    @Binding var text: String
    let placeholder: String
    let onSubmit: () -> Void

    func makeUIView(context: Context) -> UITextField {
        let f = UITextField()
        f.placeholder = placeholder
        f.autocorrectionType = .no
        f.autocapitalizationType = .none
        f.spellCheckingType = .no
        f.smartQuotesType = .no
        f.smartDashesType = .no
        f.smartInsertDeleteType = .no
        f.returnKeyType = .send
        f.enablesReturnKeyAutomatically = true
        f.font = UIFont.monospacedSystemFont(ofSize: 15, weight: .regular)
        f.textColor = .white
        f.delegate = context.coordinator
        f.addTarget(context.coordinator, action: #selector(Coordinator.changed(_:)), for: .editingChanged)
        f.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        return f
    }

    func updateUIView(_ uiView: UITextField, context: Context) {
        if uiView.text != text { uiView.text = text }
    }

    func makeCoordinator() -> Coordinator { Coordinator(self) }

    final class Coordinator: NSObject, UITextFieldDelegate {
        var parent: PlainTextField
        init(_ parent: PlainTextField) { self.parent = parent }

        @objc func changed(_ field: UITextField) { parent.text = field.text ?? "" }

        func textFieldShouldReturn(_ textField: UITextField) -> Bool {
            parent.onSubmit()
            return false
        }
    }
}
