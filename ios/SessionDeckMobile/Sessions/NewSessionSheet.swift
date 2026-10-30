import SwiftUI

/// Start a session on the deck from the phone: which CLI, which folder, and for Claude a name and a first prompt.
struct NewSessionSheet: View {
    @EnvironmentObject var gateway: GatewayStore
    @Environment(\.dismiss) private var dismiss
    let onStarted: (String) -> Void

    @State private var provider = "Claude"
    @State private var folders: [String] = []
    @State private var folder = ""
    @State private var customFolder = ""
    @State private var name = ""
    @State private var prompt = ""
    @State private var busy = false
    @State private var error: String?

    private let providers: [(key: String, label: String)] = [("Claude", "Claude"), ("Codex", "Codex"), ("Agy", "AGY"), ("Shell", "Terminal")]

    var body: some View {
        NavigationStack {
            Form {
                Section("Which") {
                    Picker("Provider", selection: $provider) {
                        ForEach(providers, id: \.key) { Text($0.label).tag($0.key) }
                    }
                    .pickerStyle(.segmented)
                }
                Section("Where") {
                    Picker("Folder", selection: $folder) {
                        ForEach(folders, id: \.self) { Text($0).lineLimit(1).truncationMode(.head).tag($0) }
                        Text("Other…").tag("__other__")
                    }
                    if folder == "__other__" {
                        TextField("C:\\Repos\\project", text: $customFolder)
                            .autocorrectionDisabled().textInputAutocapitalization(.never)
                    }
                }
                if provider == "Claude" {
                    Section("Name") {
                        TextField("Optional session name", text: $name).autocorrectionDisabled()
                    }
                }
                if provider != "Shell" {
                    Section("First prompt") {
                        TextField("Optional, typed once the CLI is up", text: $prompt, axis: .vertical).lineLimit(2...5)
                    }
                }
                if let error { Section { Text(error).foregroundStyle(.red) } }
            }
            .navigationTitle("New session")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) { Button("Cancel") { dismiss() } }
                ToolbarItem(placement: .confirmationAction) {
                    Button(busy ? "Starting…" : "Start") { Task { await start() } }.disabled(busy || chosenFolder.isEmpty)
                }
            }
            .task {
                let meta = await gateway.meta()
                folders = meta?.folders ?? []
                if folder.isEmpty { folder = folders.first ?? "__other__" }
            }
        }
    }

    private var chosenFolder: String { folder == "__other__" ? customFolder.trimmingCharacters(in: .whitespaces) : folder }

    private func start() async {
        busy = true
        defer { busy = false }
        do {
            let hostId = try await gateway.startSession(provider: provider, cwd: chosenFolder, name: name, prompt: prompt)
            onStarted(hostId)
            dismiss()
        } catch {
            self.error = error.localizedDescription
        }
    }
}

/// CPU, memory and GPU of the deck's machine, as the gateway last sampled them.
struct MachineLoadView: View {
    let load: MachineLoad

    var body: some View {
        HStack(spacing: 14) {
            gauge("CPU", load.cpuPercent, detail: nil)
            gauge("MEM", load.memoryPercent, detail: load.memoryTotalGb > 0 ? "\(format(load.memoryUsedGb)) / \(format(load.memoryTotalGb)) GB" : nil)
            gauge("GPU", load.gpuPercent, detail: nil)
        }
        .font(.caption.monospacedDigit())
    }

    private func format(_ gb: Double) -> String { gb >= 100 ? String(Int(gb.rounded())) : String(format: "%.1f", gb) }

    @ViewBuilder
    private func gauge(_ label: String, _ percent: Int, detail: String?) -> some View {
        VStack(alignment: .leading, spacing: 3) {
            HStack {
                Text(label).foregroundStyle(.secondary)
                Spacer()
                Text(percent < 0 ? "—" : "\(percent)%")
            }
            GeometryReader { geo in
                ZStack(alignment: .leading) {
                    Capsule().fill(Color(white: 0.2))
                    Capsule().fill(color(percent)).frame(width: geo.size.width * CGFloat(max(0, min(100, percent))) / 100)
                }
            }
            .frame(height: 5)
            if let detail { Text(detail).foregroundStyle(.secondary).font(.caption2) }
        }
        .frame(maxWidth: .infinity)
    }

    private func color(_ p: Int) -> Color { p >= 90 ? .red : p >= 70 ? .orange : Color(red: 0.36, green: 0.61, blue: 0.96) }
}
