import FlexarrKit
import Foundation

/// One start, timed from Play to the first picture (or sound), and sent to the server's log (Settings › Logs) as one
/// Information entry; docs/fast-start.md lists its fields.
struct StartTiming {
    enum Kind: String { case video, book }

    let kind: Kind
    let pressedAt = ContinuousClock.now
    private(set) var answeredAt: ContinuousClock.Instant?
    private(set) var warmed = false
    private(set) var method: String?
    private(set) var playbackId: String?

    init(_ kind: Kind) { self.kind = kind }

    var isAnswered: Bool { answeredAt != nil }

    /// The server's start answer came (at once, for a stream got ready ahead of Play).
    mutating func answered(_ info: PlaybackInfo, warmed: Bool) {
        guard answeredAt == nil else { return }
        answeredAt = .now
        self.warmed = warmed
        method = info.method.rawValue
        playbackId = info.playbackId
    }

    /// The first picture or sound is here: the entry goes.
    func report(to log: FlexarrLog, server: URL?) {
        let now = ContinuousClock.now
        func ms(_ instant: ContinuousClock.Instant?) -> String {
            let elapsed = (instant ?? now) - pressedAt
            return String(max(0, Int((Double(elapsed.components.seconds) + Double(elapsed.components.attoseconds) / 1e18) * 1000)))
        }
        // The id keeps each start its own entry, should anything group them by message.
        log.measured("Playback \(playbackId ?? "unknown") started", [
            "kind": kind.rawValue, "method": method ?? "unknown", "warmed": String(warmed),
            "remote": String(Self.looksRemote(server)), "requestMs": ms(answeredAt), "firstFrameMs": ms(now), "playbackId": playbackId,
        ])
    }

    /// Best guess whether the server is reached from away: a family guest (through the tunnel), or an address that
    /// isn't this device, a private network or a .local name.
    static func looksRemote(_ server: URL?) -> Bool {
        if GuestMode.isActive { return true }
        guard let host = server?.host()?.lowercased() else { return false }
        if host == "localhost" || host.hasSuffix(".local") || host == "::1" { return false }
        let parts = host.split(separator: ".").compactMap { Int($0) }
        if parts.count == 4 {
            let (a, b) = (parts[0], parts[1])
            return !(a == 127 || a == 10 || (a == 172 && (16...31).contains(b)) || (a == 192 && b == 168) || (a == 169 && b == 254))
        }
        if host.contains(":"), host.hasPrefix("fc") || host.hasPrefix("fd") || host.hasPrefix("fe80") { return false }
        return true
    }
}
