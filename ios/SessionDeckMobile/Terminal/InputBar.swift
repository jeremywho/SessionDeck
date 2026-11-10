import SwiftUI

/// The keys a phone keyboard lacks, in a row above the keyboard. Typing itself goes straight into
/// the terminal: tapping the terminal (or the keyboard key here) brings the keyboard up.
struct InputBar: View {
    let selecting: Bool
    let hasSelection: Bool
    let keyboardShown: Bool
    let listening: Bool
    let onKey: (Key) -> Void
    let onNewline: () -> Void
    let onPaste: () -> Void
    let onCopy: () -> Void
    let onSelect: () -> Void
    let onKeyboard: () -> Void
    let onMic: () -> Void

    var body: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: 6) {
                Button { onKeyboard() } label: { Image(systemName: keyboardShown ? "keyboard.chevron.compact.down" : "keyboard") }
                    .buttonStyle(.bordered)
                    .accessibilityLabel(keyboardShown ? "Hide keyboard" : "Show keyboard")
                Button { onMic() } label: { Image(systemName: listening ? "mic.fill" : "mic") }
                    .buttonStyle(.bordered)
                    .tint(listening ? .red : nil)
                    .accessibilityLabel(listening ? "Stop listening" : "Speak into the terminal")
                Divider().frame(height: 20)
                ForEach(Key.allCases) { key in
                    Button(key.label) { onKey(key) }
                        .buttonStyle(.bordered)
                        .font(.system(size: 13, weight: .medium, design: .monospaced))
                        .accessibilityLabel(key.spoken)
                }
                Button("newline") { onNewline() }
                    .buttonStyle(.bordered).font(.system(size: 13, design: .monospaced))
                    .accessibilityLabel("Newline inside a Claude prompt")
                Divider().frame(height: 20)
                Button("paste") { onPaste() }.buttonStyle(.bordered).font(.system(size: 13, design: .monospaced))
                Button(selecting ? (hasSelection ? "copy" : "cancel") : "select") { selecting && hasSelection ? onCopy() : onSelect() }
                    .buttonStyle(.bordered).font(.system(size: 13, design: .monospaced))
                    .tint(selecting ? .orange : nil)
            }
            .padding(.horizontal, 8)
        }
        .padding(.vertical, 6)
        .background(Color(white: 0.08))
    }
}
