import SwiftUI

struct SessionsView: View {
    @EnvironmentObject var gateway: GatewayStore
    @State private var showForget = false
    @State private var path: [CatalogEntry] = []
    @State private var autoOpened = false
    @State private var awaitingHost: String?
    @State private var startError: String?

    /// "0.1.0 (57) 905e217": marketing version, commit count, commit, as build.sh stamps them.
    static var buildLabel: String {
        let info = Bundle.main.infoDictionary ?? [:]
        let version = info["CFBundleShortVersionString"] as? String ?? "?"
        let build = info["CFBundleVersion"] as? String ?? "?"
        let commit = info["SDCommit"] as? String ?? "dev"
        return "Build \(version) (\(build)) \(commit)"
    }

    private func start(_ provider: String) {
        Task {
            do { awaitingHost = try await gateway.startSession(provider: provider, cwd: "", name: "", prompt: "") }
            catch { startError = error.localizedDescription }
        }
    }

    private var grouped: [(name: String, order: Int, entries: [CatalogEntry])] {
        var byGroup: [String: (order: Int, entries: [CatalogEntry])] = [:]
        for e in gateway.sessions {
            var slot = byGroup[e.group] ?? (e.groupOrder, [])
            slot.entries.append(e)
            byGroup[e.group] = slot
        }
        return byGroup.map { (name: $0.key, order: $0.value.order, entries: $0.value.entries) }.sorted { $0.order < $1.order }
    }

    var body: some View {
        NavigationStack(path: $path) {
            List {
                if !gateway.connected {
                    Section {
                        HStack {
                            Label(gateway.lastError ?? "Connecting to the gateway…", systemImage: "wifi.exclamationmark").foregroundStyle(.orange)
                            Spacer()
                            Button("Retry") { Task { await gateway.reconnectNow() } }.buttonStyle(.bordered)
                        }
                    }
                }
                if let load = gateway.machine {
                    Section { MachineLoadView(load: load).padding(.vertical, 2) }
                }
                if let awaitingHost, !gateway.sessions.contains(where: { $0.hostId == awaitingHost }) {
                    Section { Label("Starting the session on the deck…", systemImage: "hourglass").foregroundStyle(.secondary) }
                }
                ForEach(grouped, id: \.name) { group in
                    Section(group.name.isEmpty ? "" : group.name) {
                        ForEach(group.entries) { entry in
                            if entry.mobileReady {
                                NavigationLink(value: entry) { SessionRowView(entry: entry, unread: gateway.isUnread(entry)) }
                            } else {
                                SessionRowView(entry: entry, unread: false)
                                    .opacity(0.45)
                                    .overlay(alignment: .bottomTrailing) {
                                        Text(entry.hostExited ? "ended" : "restart from the desktop to open here")
                                            .font(.caption2).foregroundStyle(.secondary)
                                    }
                            }
                        }
                    }
                }
            }
            .listStyle(.insetGrouped)
            .navigationTitle("Sessions")
            .navigationDestination(for: CatalogEntry.self) { entry in TerminalScreen(entry: entry) }
            .refreshable { await gateway.refresh() }
            .toolbar {
                ToolbarItem(placement: .topBarTrailing) {
                    // The deck's own "+" right-click: a new session in the home folder with the deck's flags.
                    Menu {
                        Button { start("Claude") } label: { Label("New Claude session", systemImage: "sparkle") }
                        Button { start("Codex") } label: { Label("New Codex session", systemImage: "diamond") }
                        Button { start("Agy") } label: { Label("New AGY session", systemImage: "diamond.inset.filled") }
                        Button { start("Shell") } label: { Label("New terminal", systemImage: "terminal") }
                    } label: { Image(systemName: "plus") }
                        .disabled(!gateway.connected)
                        .accessibilityLabel("New session")
                }
                ToolbarItem(placement: .topBarTrailing) {
                    Menu {
                        Text(Self.buildLabel)
                        Button(role: .destructive) { showForget = true } label: { Label("Forget this deck", systemImage: "xmark.circle") }
                    } label: { Image(systemName: "ellipsis.circle") }
                }
                ToolbarItem(placement: .topBarLeading) {
                    Circle().fill(gateway.connected ? Color.green : Color.gray).frame(width: 8, height: 8)
                        .accessibilityLabel(gateway.connected ? "Live" : "Disconnected")
                }
            }
            .confirmationDialog("Forget this deck?", isPresented: $showForget, titleVisibility: .visible) {
                Button("Forget", role: .destructive) { gateway.forgetPairing() }
            }
            .alert("Could not start the session", isPresented: Binding(get: { startError != nil }, set: { if !$0 { startError = nil } })) {
                Button("OK") { startError = nil }
            } message: { Text(startError ?? "") }
            .task { await gateway.refresh() }
            .onChange(of: gateway.sessions) { _, sessions in
                if let hostId = awaitingHost, let entry = sessions.first(where: { $0.hostId == hostId }) {
                    awaitingHost = nil
                    if entry.mobileReady { path = [entry] }
                }
                guard !autoOpened, let hostId = LaunchOptions.open, let entry = sessions.first(where: { $0.hostId == hostId }) else { return }
                autoOpened = true
                LaunchOptions.log("listed \(sessions.count) sessions, opening \(hostId)")
                path = [entry]
            }
        }
    }
}

struct SessionRowView: View {
    let entry: CatalogEntry
    let unread: Bool

    static func color(for state: String) -> Color {
        switch state {
        case "Working": return Color(red: 0.36, green: 0.61, blue: 0.96)
        case "Awaiting": return Color(red: 0.90, green: 0.72, blue: 0.31)
        case "Scheduled": return Color(red: 0.34, green: 0.71, blue: 0.76)
        case "Completed": return Color(red: 0.36, green: 0.77, blue: 0.42)
        case "Error": return Color(red: 0.95, green: 0.33, blue: 0.35)
        default: return Color.gray
        }
    }

    var body: some View {
        HStack(spacing: 10) {
            Circle().fill(Self.color(for: entry.state)).frame(width: 10, height: 10)
            Text(entry.providerGlyph).font(.system(size: 13)).foregroundStyle(.secondary).frame(width: 14)
            VStack(alignment: .leading, spacing: 2) {
                Text(entry.name.isEmpty ? entry.provider : entry.name).font(.body).lineLimit(1)
                HStack(spacing: 6) {
                    if !entry.modelChip.isEmpty { Text(entry.modelChip) }
                    if !entry.effort.isEmpty { Text(entry.effort) }
                    Text(entry.state == "Completed" && unread ? "Unread" : entry.state)
                    if entry.subagents > 0 { Text("\(entry.subagents) working") }
                }
                .font(.caption).foregroundStyle(.secondary)
            }
            Spacer()
        }
        .padding(.vertical, 2)
        .listRowBackground(unread ? Color(red: 0.45, green: 0.35, blue: 0.75).opacity(0.22) : nil)
    }
}
