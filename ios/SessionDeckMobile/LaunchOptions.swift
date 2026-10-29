import Foundation

/// Debug builds can be driven from the command line (devicectl passes arguments through), so a
/// pairing, an open session and a typed line can be exercised on a device nobody is touching:
///   --pair 192.0.2.10:7420 ABCD2345   --open <hostId>   --send "Get-Date"
enum LaunchOptions {
    static let arguments = ProcessInfo.processInfo.arguments

    static var pair: (host: String, port: Int, code: String)? {
        #if DEBUG
        guard let i = arguments.firstIndex(of: "--pair"), arguments.count > i + 2 else { return nil }
        let parts = arguments[i + 1].split(separator: ":", maxSplits: 1).map(String.init)
        guard parts.count == 2, let port = Int(parts[1]) else { return nil }
        return (parts[0], port, arguments[i + 2])
        #else
        return nil
        #endif
    }

    static var open: String? { value("--open") }
    static var send: String? { value("--send") }

    private static func value(_ flag: String) -> String? {
        #if DEBUG
        guard let i = arguments.firstIndex(of: flag), arguments.count > i + 1 else { return nil }
        return arguments[i + 1]
        #else
        return nil
        #endif
    }

    static func log(_ step: String) { NSLog("sd-test %@", step) }
}
