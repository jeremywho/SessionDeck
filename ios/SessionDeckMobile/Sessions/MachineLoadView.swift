import SwiftUI

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
