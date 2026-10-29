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

    var body: some View {
        if gateway.pairing == nil {
            PairView()
        } else {
            SessionsView()
        }
    }
}
