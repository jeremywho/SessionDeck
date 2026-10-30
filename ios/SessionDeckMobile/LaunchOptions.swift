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

    /// Every `--send` in order; `\e`, `\t`, `\r`, `\x03` and friends spell the bytes a key bar would send.
    static var sends: [String] {
        #if DEBUG
        var out: [String] = []
        var i = 0
        while i < arguments.count {
            if arguments[i] == "--send", i + 1 < arguments.count { out.append(unescape(arguments[i + 1])); i += 1 }
            i += 1
        }
        return out
        #else
        return []
        #endif
    }

    static func unescape(_ s: String) -> String {
        var result = ""
        var chars = Array(s)
        var i = 0
        while i < chars.count {
            if chars[i] == "\\", i + 1 < chars.count {
                switch chars[i + 1] {
                case "e": result.append("\u{1b}"); i += 2; continue
                case "t": result.append("\t"); i += 2; continue
                case "r": result.append("\r"); i += 2; continue
                case "n": result.append("\n"); i += 2; continue
                case "\\": result.append("\\"); i += 2; continue
                case "x":
                    if i + 3 < chars.count, let code = UInt32(String(chars[(i + 2)...(i + 3)]), radix: 16), let scalar = Unicode.Scalar(code) {
                        result.append(Character(scalar)); i += 4; continue
                    }
                default: break
                }
            }
            result.append(chars[i]); i += 1
        }
        chars = []
        return result
    }

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
