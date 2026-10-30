import Foundation

/// The few keys a prompt needs that the iOS keyboard does not have, as the bytes a terminal expects.
enum Key: CaseIterable, Identifiable {
    case esc, tab, shiftTab, up, down, left, right, ctrlC, enter

    var id: Self { self }

    var bytes: String {
        switch self {
        case .esc: return "\u{1b}"
        case .tab: return "\t"
        case .shiftTab: return "\u{1b}[Z"
        case .up: return "\u{1b}[A"
        case .down: return "\u{1b}[B"
        case .right: return "\u{1b}[C"
        case .left: return "\u{1b}[D"
        case .ctrlC: return "\u{03}"
        case .enter: return "\r"
        }
    }

    var label: String {
        switch self {
        case .esc: return "esc"
        case .tab: return "tab"
        case .shiftTab: return "⇧tab"
        case .up: return "↑"
        case .down: return "↓"
        case .left: return "←"
        case .right: return "→"
        case .ctrlC: return "^C"
        case .enter: return "enter"
        }
    }

    var spoken: String {
        switch self {
        case .esc: return "Escape"
        case .tab: return "Tab"
        case .shiftTab: return "Shift Tab"
        case .up: return "Arrow up"
        case .down: return "Arrow down"
        case .left: return "Arrow left"
        case .right: return "Arrow right"
        case .ctrlC: return "Control C"
        case .enter: return "Enter in the terminal"
        }
    }
}

enum Keys {
    static func bytes(for key: Key) -> String { key.bytes }

    /// Text as a terminal paste: the bracketed-paste markers let Claude Code and PSReadLine take a
    /// multi-line draft as one block instead of running each line.
    static func pasteSequence(_ text: String) -> String {
        "\u{1b}[200~" + text.replacingOccurrences(of: "\r\n", with: "\r").replacingOccurrences(of: "\n", with: "\r") + "\u{1b}[201~"
    }

    /// A JavaScript string literal for `text`, safe for any character.
    static func jsLiteral(_ text: String) -> String {
        let data = try! JSONSerialization.data(withJSONObject: [text])
        let array = String(decoding: data, as: UTF8.self)
        return String(array.dropFirst().dropLast())
    }
}
