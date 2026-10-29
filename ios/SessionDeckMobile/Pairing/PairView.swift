import SwiftUI
import UIKit

struct PairView: View {
    @EnvironmentObject var gateway: GatewayStore
    @State private var address = ""
    @State private var code = ""
    @State private var scanning = false
    @State private var busy = false
    @State private var error: String?

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    Button { scanning = true } label: { Label("Scan pairing code", systemImage: "qrcode.viewfinder") }
                } footer: {
                    Text("In the deck: Settings, Phone, Show pairing code.")
                }
                Section("Or type it") {
                    TextField("192.168.1.10:7420", text: $address)
                        .keyboardType(.numbersAndPunctuation)
                        .textInputAutocapitalization(.never)
                        .autocorrectionDisabled()
                    TextField("Code", text: $code)
                        .textInputAutocapitalization(.characters)
                        .autocorrectionDisabled()
                    Button("Pair") { Task { await pairTyped() } }
                        .disabled(busy || address.isEmpty || code.isEmpty)
                }
                if let error {
                    Section { Text(error).foregroundStyle(.red) }
                }
            }
            .navigationTitle("Pair with the deck")
            .sheet(isPresented: $scanning) {
                QRScannerView { text in
                    scanning = false
                    Task { await pairScanned(text) }
                }
            }
        }
    }

    private func pairTyped() async {
        let parts = address.split(separator: ":", maxSplits: 1).map(String.init)
        guard parts.count == 2, let port = Int(parts[1]) else { error = "Address is host:port, for example 192.168.1.10:7420."; return }
        await pair(host: parts[0], port: port, code: code)
    }

    private func pairScanned(_ text: String) async {
        guard let parsed = Pairing.parseQR(text) else { error = "That is not a Session Deck pairing code."; return }
        address = "\(parsed.host):\(parsed.port)"
        code = parsed.code
        await pair(host: parsed.host, port: parsed.port, code: parsed.code)
    }

    private func pair(host: String, port: Int, code: String) async {
        busy = true
        defer { busy = false }
        do {
            try await gateway.pair(host: host, port: port, code: code.trimmingCharacters(in: .whitespaces), deviceName: UIDevice.current.name)
            error = nil
        } catch {
            self.error = error.localizedDescription
        }
    }
}
