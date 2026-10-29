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
    @State private var autoPaired = false

    var body: some View {
        Group {
            if gateway.pairing == nil {
                PairView()
            } else {
                SessionsView()
            }
        }
        .task {
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
