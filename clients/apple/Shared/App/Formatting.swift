import FlexarrKit
import Foundation

/// How long a screen's data counts as current. Apps stay alive for days, and changes made elsewhere
/// (watching on the web, a new download) reach a screen only when it fetches again.
enum Freshness {
    static let maxAge: TimeInterval = 60

    static func isStale(_ loadedAt: Date?) -> Bool {
        loadedAt.map { Date().timeIntervalSince($0) > maxAge } ?? true
    }
}

extension VideoRange {
    var label: String {
        switch self {
        case .sdr: "SDR"
        case .hdr10: "HDR10"
        case .hlg: "HLG"
        case .dolbyVision: "Dolby Vision"
        case .unknown: ""
        }
    }
}

extension Bundle {
    var shortVersion: String {
        (infoDictionary?["CFBundleShortVersionString"] as? String) ?? ""
    }
}

enum ServerVersion {
    /// "0.1.0+d4c892c6190c…" reads better as "0.1.0 (d4c892c)".
    static func short(_ version: String) -> String {
        let parts = version.split(separator: "+", maxSplits: 1)
        guard parts.count == 2 else { return version }
        return "\(parts[0]) (\(parts[1].prefix(7)))"
    }
}

/// "Mono", "Stereo", "5.1".
enum AudioChannels {
    static func label(_ count: Int) -> String {
        switch count {
        case 1: "Mono"
        case 2: "Stereo"
        case 6: "5.1"
        case 8: "7.1"
        default: "\(count) ch"
        }
    }
}
