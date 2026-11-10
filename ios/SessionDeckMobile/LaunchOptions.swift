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
    static var closeAfter: Double? { value("--close-after").flatMap(Double.init) }
    /// Debug: show the key bar alone (see KeyBarProbe), for UI tests that tap its keys.
    static var barOnly: Bool {
        #if DEBUG
        return arguments.contains("--bar-only")
        #else
        return false
        #endif
    }
    /// Debug: jump to this app's own page in Settings (where "Paste from Other Apps" lives).
    static var openSettings: Bool {
        #if DEBUG
        return arguments.contains("--open-settings")
        #else
        return false
        #endif
    }
    /// Debug: text put on the pasteboard at launch, so the paste button can be inspected with something to paste.
    static var clipboard: String? { value("--clipboard") }

    /// Every `--gesture swipe-down|swipe-up|long-press`, in order, played into the page as touch events.
    static var gestures: [String] {
        #if DEBUG
        var out: [String] = []
        var i = 0
        while i < arguments.count {
            if arguments[i] == "--gesture", i + 1 < arguments.count { out.append(arguments[i + 1]); i += 1 }
            i += 1
        }
        return out
        #else
        return []
        #endif
    }
    static var watchViewport: Bool {
        #if DEBUG
        return arguments.contains("--watch-viewport")
        #else
        return false
        #endif
    }
    static var zoom: Double? { value("--zoom").flatMap(Double.init) }
    static var scrollTest: Bool {
        #if DEBUG
        return arguments.contains("--scroll-test")
        #else
        return false
        #endif
    }

    static var focus: Bool {
        #if DEBUG
        return arguments.contains("--focus")
        #else
        return false
        #endif
    }

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

    static func log(_ step: String) {
        NSLog("sd-test %@", step)
        #if DEBUG
        appendToFile(step)
        #endif
    }

    /// Debug builds also keep the log in Documents/sd-test.log, which `devicectl device copy from` can
    /// fetch after a UI-test run (the console is not attached then).
    static let logFile = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask).first?.appendingPathComponent("sd-test.log")
    private static let logQueue = DispatchQueue(label: "sd-test-log")
    private static func appendToFile(_ step: String) {
        guard let logFile else { return }
        let line = "\(Date().formatted(.iso8601.time(includingFractionalSeconds: true))) \(step)\n"
        logQueue.async {
            if let handle = try? FileHandle(forWritingTo: logFile) {
                handle.seekToEndOfFile(); handle.write(Data(line.utf8)); try? handle.close()
            } else {
                try? Data(line.utf8).write(to: logFile)
            }
        }
    }
}
