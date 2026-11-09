import Foundation

/// Which channels a guide is asked for.
public enum LiveGuideScope: String, Sendable, Hashable {
    /// Only the profile's favourites: the guide of the Favourites list.
    case favourites
    /// Every channel the profile may watch.
    case all
}

/// One ask of the guide (always without descriptions): from when, for how many hours, for which channels.
public struct LiveGuideRequest: Sendable, Hashable {
    public let from: Date
    public let hours: Int
    public let scope: LiveGuideScope

    public init(from: Date, hours: Int, scope: LiveGuideScope) {
        self.from = from
        self.hours = hours
        self.scope = scope
    }

    public var until: Date { from.addingTimeInterval(Double(hours) * 3600) }
}

/// A guide as fetched: what was asked for, the server's ETag for the answer (nil from servers before the release after
/// 0.62.1), and when.
public struct KeptLiveGuide: Sendable {
    public let guide: LiveGuide
    public let request: LiveGuideRequest
    public let etag: String?
    public let fetchedAt: Date

    public init(guide: LiveGuide, request: LiveGuideRequest, etag: String?, fetchedAt: Date) {
        self.guide = guide
        self.request = request
        self.etag = etag
        self.fetchedAt = fetchedAt
    }

    /// Whether it's due to be fetched again (see `LiveGuide.isDue`).
    public func isDue(now: Date = Date()) -> Bool { guide.isDue(fetchedAt: fetchedAt, now: now) }
}

/// What a guide screen asks the server for (audit perf-20): the window it shows plus a margin rather than a whole day,
/// only the favourites while it shows Favourites, and the same question for half an hour, so an unchanged guide is
/// answered 304 to its ETag. Servers before the release after 0.62.1 ignore the favourites (and send every channel,
/// which then does for every list) and the ETag; `hours` they have always honoured.
public enum LiveGuidePlan {
    /// The longest guide the server gives.
    public static let maxHours = 24

    /// Where a guide asked for at `now` starts: the half hour at or before an hour ago. The screens' windows start no
    /// earlier, and it stays the same for half an hour.
    public static func start(at now: Date) -> Date {
        let seconds = now.addingTimeInterval(-3600).timeIntervalSinceReferenceDate
        return Date(timeIntervalSinceReferenceDate: (seconds / 1800).rounded(.down) * 1800)
    }

    /// What to ask for, or nil when `kept` will do.
    ///
    /// - `scope`, `shown`: the list on screen and its channels' ids.
    /// - `showingUntil`: the end of the window on screen; the guide is asked for to `askAhead` past it, and asked for
    ///   again (from the same start, so what's on screen stays put) once it reaches less than `keepAhead` past it.
    /// - `force`: fetch whatever is kept (a recording was set or taken off, so its marks change).
    ///
    /// The kept guide is fetched again once it's due (with a new start); for a channel on screen that it hasn't got
    /// (asked for favourites, now showing more, or a channel added since); or to reach further. It never shrinks while
    /// it's kept.
    public static func next(kept: KeptLiveGuide?, scope: LiveGuideScope, shown: [String], showingUntil: Date,
                            keepAhead: TimeInterval, askAhead: TimeInterval, force: Bool = false, now: Date = Date()) -> LiveGuideRequest? {
        func hours(from: Date) -> Int {
            let needed = showingUntil.addingTimeInterval(askAhead).timeIntervalSince(from) / 3600
            return min(maxHours, max(1, Int(needed.rounded(.up))))
        }

        guard let kept, !kept.isDue(now: now) else {
            let from = start(at: now)
            return LiveGuideRequest(from: from, hours: hours(from: from), scope: scope)
        }

        let had = Set(kept.guide.channels.map(\.id))
        let lacksChannels = shown.contains { !had.contains($0) }
        let lacksTime = kept.request.hours < maxHours && kept.request.until < showingUntil.addingTimeInterval(keepAhead)
        guard force || lacksChannels || lacksTime else { return nil }

        // Broader, never narrower, than what's kept; a favourites guide without a favourite on screen asks for every
        // channel rather than the same again.
        let wider: LiveGuideScope = scope == .all || kept.request.scope == .all || lacksChannels ? .all : .favourites
        let from = kept.request.from
        return LiveGuideRequest(from: from, hours: max(kept.request.hours, hours(from: from)), scope: wider)
    }
}

extension FlexarrClient {
    /// The guide `request` asks for, without descriptions. When `kept` answered the same question, its ETag is sent,
    /// and a 304 (nothing changed) keeps its guide, as fetched now.
    public func liveGuide(_ request: LiveGuideRequest, kept: KeptLiveGuide?, now: Date = Date()) async throws -> KeptLiveGuide {
        var query: [URLQueryItem] = [
            .init(name: "from", value: ISO8601DateFormatter().string(from: request.from)),
            .init(name: "hours", value: String(request.hours)),
            .init(name: "descriptions", value: "false"),
        ]
        if request.scope == .favourites { query.append(.init(name: "favourites", value: "true")) }
        let same = kept.flatMap { $0.request == request ? $0 : nil }
        let headers = same?.etag.map { ["If-None-Match": $0] } ?? [:]
        let (data, response) = try await answer("GET", "livetv/guide", query: query, headers: headers)
        if response.statusCode == 304 {
            guard let same else { throw FlexarrClientError.unexpectedResponse }
            return KeptLiveGuide(guide: same.guide, request: request, etag: same.etag, fetchedAt: now)
        }
        return KeptLiveGuide(guide: try decode(LiveGuide.self, from: data), request: request,
                             etag: response.value(forHTTPHeaderField: "ETag"), fetchedAt: now)
    }
}
