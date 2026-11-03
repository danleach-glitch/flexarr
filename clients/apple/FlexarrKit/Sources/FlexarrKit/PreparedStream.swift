import Foundation

/// A title got ready ahead of Play (`prepare`; docs/fast-start.md): the next episode during the credits, or a film,
/// episode or book whose page has been looked at for a moment. Play takes it over only when it asks for exactly the
/// same stream and it's still fresh; otherwise it's let go and Play starts the usual way.
public struct PreparedStream: Sendable {
    /// A warm-up older than this isn't used: its resume point may have moved on meanwhile (another device, say).
    public static let maxAge: TimeInterval = 120

    /// A next episode got ready during the credits is used for this long (it starts from the beginning anyway).
    public static let nextEpisodeMaxAge: TimeInterval = 30 * 60

    public let request: PlaybackRequest
    public let info: PlaybackInfo
    public let at: Date
    /// Who got it ready (the profile's token): another profile never takes it over.
    public let owner: String?

    public init(request: PlaybackRequest, info: PlaybackInfo, at: Date = Date(), owner: String? = nil) {
        self.request = request
        self.info = info
        self.at = at
        self.owner = owner
    }

    /// Whether this is the stream `wanted` asks for, by the same profile, and still fresh at `now`.
    public func serves(_ wanted: PlaybackRequest, owner: String? = nil, now: Date = Date()) -> Bool {
        now.timeIntervalSince(at) < Self.maxAge && self.owner == owner && Self.same(request, wanted)
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
