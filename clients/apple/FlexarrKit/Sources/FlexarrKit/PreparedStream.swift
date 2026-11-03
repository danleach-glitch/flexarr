import Foundation

/// A title got ready ahead of Play (`prepare`; docs/fast-start.md): the next episode during the credits, or a film,
/// episode or book whose page has been looked at for a moment. Play takes it over only when it asks for exactly the
/// same stream and it's still fresh; otherwise it's let go and Play starts the usual way.
public struct PreparedStream: Sendable {
    /// A warm-up older than this isn't used: its resume point may have moved on meanwhile.
    public static let maxAge: TimeInterval = 300

    public let request: PlaybackRequest
    public let info: PlaybackInfo
    public let at: Date

    public init(request: PlaybackRequest, info: PlaybackInfo, at: Date = Date()) {
        self.request = request
        self.info = info
        self.at = at
    }

    /// Whether this is the stream `wanted` asks for, and still fresh at `now`.
    public func serves(_ wanted: PlaybackRequest, now: Date = Date()) -> Bool {
        now.timeIntervalSince(at) < Self.maxAge && Self.same(request, wanted)
    }

    /// The same stream: everything asked for matches but how it was asked (getting ready, what it replaces, the
    /// measured speed).
    public static func same(_ a: PlaybackRequest, _ b: PlaybackRequest) -> Bool {
        key(a) == key(b)
    }

    private static func key(_ request: PlaybackRequest) -> PlaybackRequest {
        var request = request
        request.prepare = nil
        request.replacesSessionId = nil
        request.measuredBitrate = nil
        return request
    }
}
