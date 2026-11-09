import SwiftUI
import UIKit
import UniformTypeIdentifiers

/// The key bar's paste key: the system paste control, which hands over the clipboard without the
/// allow-paste banner that reading UIPasteboard shows on every paste. iOS draws it and lets only its
/// shape, its two colours and icon-or-label be chosen (never the font or the wording), so it is
/// icon-only in the fill and tint the bar's other keys have, like the keyboard and microphone keys.
struct BarPasteButton: UIViewRepresentable {
    let onPaste: (String) -> Void

    /// The size the bar's icon keys take (measured: the microphone key).
    static let size = CGSize(width: 41, height: 34)

    func makeCoordinator() -> Target { Target() }

    // The control must be this representable's own view, with a separate responder as its target.
    // Wrapped in a container view that is also the target, a tap hands nothing over (iPhone iOS 26.6
    // and the iOS 26.5 simulator), whatever its colours.
    func makeUIView(context: Context) -> UIPasteControl {
        var config = UIPasteControl.Configuration()
        config.displayMode = .iconOnly
        config.cornerStyle = .capsule
        config.baseBackgroundColor = Self.keyFill
        config.baseForegroundColor = .systemBlue
        let control = UIPasteControl(configuration: config)
        context.coordinator.onPaste = onPaste
        control.target = context.coordinator
        return control
    }

    func updateUIView(_ uiView: UIPasteControl, context: Context) { context.coordinator.onPaste = onPaste }

    /// What a bordered key's fill looks like on the bar: the system's secondary fill over the bar's own background.
    static let keyFill: UIColor = {
        let dark = UITraitCollection(userInterfaceStyle: .dark)
        var r: CGFloat = 0, g: CGFloat = 0, b: CGFloat = 0, a: CGFloat = 0
        UIColor.secondarySystemFill.resolvedColor(with: dark).getRed(&r, green: &g, blue: &b, alpha: &a)
        let bar = InputBar.backgroundWhite
        return UIColor(red: bar + (r - bar) * a, green: bar + (g - bar) * a, blue: bar + (b - bar) * a, alpha: 1)
    }()

    final class Target: UIResponder {
        var onPaste: ((String) -> Void)?

        override init() {
            super.init()
            pasteConfiguration = UIPasteConfiguration(acceptableTypeIdentifiers: [UTType.plainText.identifier, UTType.utf8PlainText.identifier, UTType.text.identifier])
        }

        override func paste(itemProviders: [NSItemProvider]) {
            guard let provider = itemProviders.first(where: { $0.canLoadObject(ofClass: NSString.self) }) else { return }
            provider.loadObject(ofClass: NSString.self) { [weak self] object, _ in
                guard let text = object as? String, !text.isEmpty else { return }
                DispatchQueue.main.async { self?.onPaste?(text) }
            }
        }
    }
}
