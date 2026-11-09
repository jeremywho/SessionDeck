import SwiftUI

@main
struct SessionDeckMobileApp: App {
    @StateObject private var gateway = GatewayStore()

    var body: some Scene {
        WindowGroup {
            RootView()
                .environmentObject(gateway)
                .preferredColorScheme(.dark)
        }
    }
}

struct RootView: View {
    @EnvironmentObject var gateway: GatewayStore
    @Environment(\.scenePhase) private var scenePhase
    @State private var autoPaired = false

    var body: some View {
        Group {
            if LaunchOptions.barOnly {
                KeyBarProbe()
            } else if gateway.pairing == nil {
                PairView()
            } else {
                SessionsView()
            }
        }
        .onChange(of: scenePhase) { _, phase in
            if phase == .active { Task { await gateway.choosePath() } }
        }
        .task {
            AudioSessionLog.watch()
            if let text = LaunchOptions.clipboard { UIPasteboard.general.string = text; LaunchOptions.log("clipboard set to \(text.debugDescription)") }
            guard !autoPaired, gateway.pairing == nil, let p = LaunchOptions.pair else { return }
            autoPaired = true
            do {
                try await gateway.pair(host: p.host, port: p.port, code: p.code, deviceName: "launch-argument test")
                LaunchOptions.log("paired \(p.host):\(p.port)")
            } catch {
                LaunchOptions.log("pair failed: \(error.localizedDescription)")
            }
        }
    }
}
