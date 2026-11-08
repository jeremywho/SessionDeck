import SwiftUI
import UIKit
import UniformTypeIdentifiers

/// The system paste button: iOS draws it and hands over the clipboard when it is tapped, so no
/// "allow paste?" banner appears. Reading UIPasteboard from a button of our own shows the banner
/// on every paste.
struct PasteButton: UIViewRepresentable {
    let onPaste: (String) -> Void

    func makeUIView(context: Context) -> PasteTarget {
        let target = PasteTarget()
        target.onPaste = onPaste
        var config = UIPasteControl.Configuration()
        config.displayMode = .labelOnly
        config.cornerStyle = .medium
        config.baseBackgroundColor = UIColor.secondarySystemFill
        config.baseForegroundColor = UIColor.label
        let control = UIPasteControl(configuration: config)
        control.target = target
        control.translatesAutoresizingMaskIntoConstraints = false
        target.addSubview(control)
        NSLayoutConstraint.activate([
            control.leadingAnchor.constraint(equalTo: target.leadingAnchor),
            control.trailingAnchor.constraint(equalTo: target.trailingAnchor),
            control.topAnchor.constraint(equalTo: target.topAnchor),
            control.bottomAnchor.constraint(equalTo: target.bottomAnchor),
            control.heightAnchor.constraint(equalToConstant: 30),
        ])
        return target
    }

    func updateUIView(_ uiView: PasteTarget, context: Context) { uiView.onPaste = onPaste }

    func sizeThatFits(_ proposal: ProposedViewSize, uiView: PasteTarget, context: Context) -> CGSize? {
        CGSize(width: 68, height: 30)
    }

    /// Where the pasted text lands: a view that accepts plain text, as the paste control requires.
    final class PasteTarget: UIView {
        var onPaste: ((String) -> Void)?

        override init(frame: CGRect) {
            super.init(frame: frame)
            pasteConfiguration = UIPasteConfiguration(acceptableTypeIdentifiers: [UTType.plainText.identifier, UTType.utf8PlainText.identifier, UTType.text.identifier])
        }

        required init?(coder: NSCoder) { fatalError("init(coder:) is not used") }

        override func paste(itemProviders: [NSItemProvider]) {
            guard let provider = itemProviders.first(where: { $0.canLoadObject(ofClass: NSString.self) }) else { return }
            provider.loadObject(ofClass: NSString.self) { [weak self] object, _ in
                guard let text = object as? String, !text.isEmpty else { return }
                DispatchQueue.main.async { self?.onPaste?(text) }
            }
        }
    }
}
