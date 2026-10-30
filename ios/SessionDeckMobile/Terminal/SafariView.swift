import SafariServices
import SwiftUI

/// A link from the terminal, opened in the in-app Safari sheet so Done brings the session straight back.
struct SafariView: UIViewControllerRepresentable {
    let url: URL

    func makeUIViewController(context: Context) -> SFSafariViewController {
        let config = SFSafariViewController.Configuration()
        config.entersReaderIfAvailable = false
        let c = SFSafariViewController(url: url, configuration: config)
        c.preferredControlTintColor = UIColor(red: 0.36, green: 0.61, blue: 0.96, alpha: 1)
        return c
    }

    func updateUIViewController(_ uiViewController: SFSafariViewController, context: Context) {}
}

extension URL: @retroactive Identifiable {
    public var id: String { absoluteString }
}
