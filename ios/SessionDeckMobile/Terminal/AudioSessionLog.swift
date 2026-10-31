import AVFoundation
import Foundation

/// Debug builds only: what the app's audio session looks like at each step, and every change iOS
/// reports to it, so a pause in another app's playback can be pinned to the line that caused it.
enum AudioSessionLog {
    private static var observers: [NSObjectProtocol] = []

    static func note(_ step: String) {
        #if DEBUG
        let s = AVAudioSession.sharedInstance()
        LaunchOptions.log("audio \(step): category=\(s.category.rawValue) mode=\(s.mode.rawValue) options=\(s.categoryOptions.rawValue) othersPlaying=\(s.isOtherAudioPlaying) silenceHint=\(s.secondaryAudioShouldBeSilencedHint)")
        #endif
    }

    static func watch() {
        #if DEBUG
        guard observers.isEmpty else { return }
        let center = NotificationCenter.default
        observers.append(center.addObserver(forName: AVAudioSession.routeChangeNotification, object: nil, queue: .main) { n in
            let reason = (n.userInfo?[AVAudioSessionRouteChangeReasonKey] as? UInt).flatMap(AVAudioSession.RouteChangeReason.init(rawValue:))
            note("route change reason=\(reason.map { String(describing: $0) } ?? "?")")
        })
        observers.append(center.addObserver(forName: AVAudioSession.interruptionNotification, object: nil, queue: .main) { n in
            let type = (n.userInfo?[AVAudioSessionInterruptionTypeKey] as? UInt).flatMap(AVAudioSession.InterruptionType.init(rawValue:))
            note("interruption type=\(type.map { String(describing: $0) } ?? "?")")
        })
        observers.append(center.addObserver(forName: AVAudioSession.silenceSecondaryAudioHintNotification, object: nil, queue: .main) { _ in
            note("silence secondary audio hint changed")
        })
        note("watching")
        #endif
    }
}
