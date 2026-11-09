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
        target.control = control
        PasteButton.debugState(target, "created")
        DispatchQueue.main.asyncAfter(deadline: .now() + 3.0) { PasteButton.debugState(target, "after 3 s") }
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

    /// Debug: what the control and its target think of the clipboard right now.
    static func debugState(_ target: PasteTarget, _ when: String) {
        let pb = UIPasteboard.general
        let providers = pb.itemProviders
        let types = providers.flatMap { $0.registeredTypeIdentifiers }
        let can = target.canPaste(providers)
        var hit = "n/a", scroll = "none"
        if let control = target.control, let window = target.window {
            let center = control.convert(CGPoint(x: control.bounds.midX, y: control.bounds.midY), to: window)
            let view = window.hitTest(center, with: nil)
            hit = view.map { "\(type(of: $0)) interactive=\($0.isUserInteractionEnabled) insideControl=\($0.isDescendant(of: control))" } ?? "nil"
            var v: UIView? = target.superview
            while let s = v { if let sv = s as? UIScrollView { scroll = "\(type(of: sv)) delays=\(sv.delaysContentTouches) cancels=\(sv.canCancelContentTouches)"; break }; v = s.superview }
        }
        LaunchOptions.log("paste \(when): enabled=\(target.control?.isEnabled ?? false) bounds=\(target.control?.bounds.size ?? .zero) window=\(target.window != nil) hasStrings=\(pb.hasStrings) types=\(types) canPaste=\(can) hit=\(hit) scroll=\(scroll) recognizers=\(target.control?.gestureRecognizers?.count ?? -1)")
    }

    /// Where the pasted text lands: a view that accepts plain text, as the paste control requires.
    final class PasteTarget: UIView {
        var onPaste: ((String) -> Void)?
        weak var control: UIPasteControl?

        override init(frame: CGRect) {
            super.init(frame: frame)
            pasteConfiguration = UIPasteConfiguration(acceptableTypeIdentifiers: [UTType.plainText.identifier, UTType.utf8PlainText.identifier, UTType.text.identifier])
        }

        required init?(coder: NSCoder) { fatalError("init(coder:) is not used") }

        /// The key bar is a horizontal scroll view that delays touches to tell a scroll from a tap; the
        /// system paste control needs the touch at once or it never fires. Scrolling still works: the
        /// scroll view cancels a touch that turns into a drag.
        override func didMoveToWindow() {
            super.didMoveToWindow()
            var view: UIView? = superview
            while let v = view {
                if let scroll = v as? UIScrollView { scroll.delaysContentTouches = false; break }
                view = v.superview
            }
        }

        override func paste(itemProviders: [NSItemProvider]) {
            LaunchOptions.log("paste: handed \(itemProviders.count) item(s): \(itemProviders.flatMap { $0.registeredTypeIdentifiers })")
            guard let provider = itemProviders.first(where: { $0.canLoadObject(ofClass: NSString.self) }) else { LaunchOptions.log("paste: no string item"); return }
            provider.loadObject(ofClass: NSString.self) { [weak self] object, _ in
                guard let text = object as? String, !text.isEmpty else { return }
                DispatchQueue.main.async { self?.onPaste?(text) }
            }
        }
    }
}
