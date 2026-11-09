import SwiftUI

/// Debug screen (`--bar-only`): the key bar alone, attached to nothing, so a UI test can tap its keys
/// without a session to type into. What the last key did shows as text.
struct KeyBarProbe: View {
    @State private var last = "nothing yet"

    var body: some View {
        VStack(spacing: 0) {
            Spacer()
            Text(last).font(.body.monospaced()).foregroundStyle(.secondary)
            Spacer()
            InputBar(selecting: false, hasSelection: false, keyboardShown: false, listening: false,
                     onKey: { last = "key: \($0.label)" },
                     onNewline: { last = "newline" },
                     onPaste: { last = "pasted: \($0)" },
                     onCopy: { last = "copy" },
                     onSelect: { last = "select" },
                     onKeyboard: { last = "keyboard" },
                     onMic: { last = "mic" })
        }
        .background(Color(red: 0.047, green: 0.047, blue: 0.047))
    }
}
