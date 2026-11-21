#if canImport(UIKit)
import UIKit

/// The screen kept from sleeping while anything needs it: the picture playing, the visualiser being watched, or a
/// download away from home (the tunnel it comes through stops once the app is suspended). One switch for them all, so
/// one letting go doesn't let the screen sleep under another.
@MainActor
enum KeepAwake {
    enum Reason: Hashable {
        case player
        case visualiser
        case awayDownloads
    }

    private static var reasons: Set<Reason> = []

    static func set(_ reason: Reason, _ on: Bool) {
        if on { reasons.insert(reason) } else { reasons.remove(reason) }
        let awake = !reasons.isEmpty
        if UIApplication.shared.isIdleTimerDisabled != awake { UIApplication.shared.isIdleTimerDisabled = awake }
    }
}
#endif
