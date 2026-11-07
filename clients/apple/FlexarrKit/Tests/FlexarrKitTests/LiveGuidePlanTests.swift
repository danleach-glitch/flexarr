import Foundation
import Testing
@testable import FlexarrKit

/// The guide screens ask for the window on screen plus a margin, favourites only on Favourites, and the same question
/// again with its ETag (audit perf-20).
struct LiveGuidePlanTests {
    /// 10:10 on a day, UTC.
    private let now = Date(timeIntervalSinceReferenceDate: 800_000_000 - 800_000_000.truncatingRemainder(dividingBy: 86400) + 10 * 3600 + 600)
    private let hour: TimeInterval = 3600

    private func guide(_ ids: [String], from: Date, hours: Int, recording: LiveProgramme? = nil) -> LiveGuide {
        LiveGuide(from: from, until: from.addingTimeInterval(Double(hours) * hour), channels: ids.map {
            GuideChannel(id: $0, number: 1, name: $0, group: nil, logoUrl: nil, isFavourite: false, programmes: recording.map { [$0] } ?? [])
        })
    }

    private func kept(_ request: LiveGuideRequest, channels: [String], etag: String? = nil, at: Date? = nil) -> KeptLiveGuide {
        KeptLiveGuide(guide: guide(channels, from: request.from, hours: request.hours), request: request, etag: etag, fetchedAt: at ?? now)
    }

    /// The TV's: three hours on screen, asked for 4½ hours past it, again once less than 1½ hours are left past it.
    private func tv(_ kept: KeptLiveGuide?, _ scope: LiveGuideScope = .all, shown: [String] = ["a"], until: Date? = nil,
                    force: Bool = false, at: Date? = nil) -> LiveGuideRequest? {
        LiveGuidePlan.next(kept: kept, scope: scope, shown: shown, showingUntil: until ?? halfHour(now).addingTimeInterval(3 * hour),
                           keepAhead: 1.5 * hour, askAhead: 4.5 * hour, force: force, now: at ?? now)
    }

    private func halfHour(_ date: Date) -> Date {
        Date(timeIntervalSinceReferenceDate: (date.timeIntervalSinceReferenceDate / 1800).rounded(.down) * 1800)
    }

    @Test func theFirstAskIsTheWindowAndAMarginFromTheHalfHourAnHourBack() throws {
        let first = try #require(tv(nil, .favourites))
        #expect(first.from == halfHour(now.addingTimeInterval(-hour)))
        #expect(first.from == now.addingTimeInterval(-hour - 600))
        // 09:00 to 13:00 on screen plus 4½ hours: 9 hours, not the whole day.
        #expect(first.hours == 9)
        #expect(first.scope == .favourites)
        // The same for the whole half hour, so the same question gets a 304.
        #expect(LiveGuidePlan.start(at: now.addingTimeInterval(19 * 60)) == first.from)
        #expect(LiveGuidePlan.start(at: now.addingTimeInterval(21 * 60)) == first.from.addingTimeInterval(1800))

        // The iPhone's six hours, asked for an hour past: eight hours, as before.
        let phone = LiveGuidePlan.next(kept: nil, scope: .all, shown: [], showingUntil: halfHour(now).addingTimeInterval(6 * hour),
                                       keepAhead: 0, askAhead: hour, now: now)
        #expect(phone?.hours == 8)
    }

    @Test func aKeptGuideDoesUntilItsDueOrTheWindowNearsItsEnd() throws {
        let first = try #require(tv(nil))
        let held = kept(first, channels: ["a"])
        #expect(tv(held) == nil)
        // Paged on to 16:30: still 1½ hours past it.
        #expect(tv(held, until: first.until.addingTimeInterval(-1.5 * hour)) == nil)
        // A little further: more, from the same start (what's on screen stays put).
        let more = try #require(tv(held, until: first.until.addingTimeInterval(-hour)))
        #expect(more.from == first.from)
        #expect(more.hours == 13)
        // Never more than a day, and a day is never asked for again for being too short.
        let day = try #require(tv(held, until: first.from.addingTimeInterval(30 * hour)))
        #expect(day.hours == LiveGuidePlan.maxHours)
        #expect(tv(kept(day, channels: ["a"]), until: first.from.addingTimeInterval(30 * hour)) == nil)
        // Half an hour on, it's asked for again from a new start, as large as the window wants.
        let later = try #require(tv(held, at: now.addingTimeInterval(LiveGuide.keptFor)))
        #expect(later.from == first.from.addingTimeInterval(1800))
        #expect(later.hours == 8)
        // A recording set or taken off: the same again, never smaller.
        #expect(tv(kept(more, channels: ["a"]), force: true) == more)
    }

    @Test func favouritesAreAskedForOnlyWhileFavouritesShow() throws {
        let first = try #require(tv(nil, .favourites, shown: ["a"]))
        let favourites = kept(first, channels: ["a"])
        #expect(tv(favourites, .favourites, shown: ["a"]) == nil)
        // All channels: every channel, from the same start.
        let all = try #require(tv(favourites, .all, shown: ["a", "b"]))
        #expect(all.scope == .all)
        #expect(all.from == first.from)
        // Back on Favourites, the guide of every channel does.
        let every = kept(all, channels: ["a", "b"])
        #expect(tv(every, .favourites, shown: ["a"]) == nil)
        // A favourite starred elsewhere that the favourites guide hasn't got: every channel, rather than the same again.
        #expect(tv(favourites, .favourites, shown: ["a", "c"])?.scope == .all)
        // A channel that even every channel's guide hasn't got (just added): nothing to ask for until it's due.
        #expect(tv(every, .all, shown: ["a", "b", "new"]) == nil)
        // A recording changed on Favourites with every channel kept: still every channel.
        #expect(tv(every, .favourites, shown: ["a"], force: true)?.scope == .all)
    }

    @Test func anOlderServerThatSendsEveryChannelForFavouritesNeedsNoMore() throws {
        let first = try #require(tv(nil, .favourites, shown: ["a"]))
        // The server ignored favourites=true and sent both.
        #expect(tv(kept(first, channels: ["a", "b"]), .all, shown: ["a", "b"]) == nil)
    }

    @Test func aGuideThatsDueForItsMarksIsAskedForAgain() throws {
        let first = try #require(tv(nil))
        let recording = LiveProgramme(id: 1, start: now.addingTimeInterval(-600), end: now.addingTimeInterval(600), title: "News", subtitle: nil,
                                      description: nil, category: nil, season: nil, episode: nil, isNew: false, recording: "scheduled", series: nil)
        let held = KeptLiveGuide(guide: guide(["a"], from: first.from, hours: first.hours, recording: recording), request: first, etag: "W/\"1\"",
                                 fetchedAt: now)
        #expect(held.isDue(now: now))
        #expect(tv(held) == first)
    }

    @Test func theSameQuestionCarriesTheETagAndA304KeepsTheGuide() async throws {
        let request = LiveGuideRequest(from: LiveGuidePlan.start(at: now), hours: 9, scope: .favourites)
        let body = try encoded(guide(["a"], from: request.from, hours: 9))
        let (client, host) = stubbedClient(.init(status: 200, body: body, headers: ["ETag": "W/\"abc\""]))

        let first = try await client.liveGuide(request, kept: nil, now: now)
        #expect(first.etag == "W/\"abc\"")
        #expect(first.guide.channels.map(\.id) == ["a"])
        let asked = try #require(StubProtocol.recorded(host: host).last)
        #expect(asked.value(forHTTPHeaderField: "If-None-Match") == nil)
        let items = URLComponents(url: try #require(asked.url), resolvingAgainstBaseURL: false)?.queryItems ?? []
        #expect(items.contains(.init(name: "favourites", value: "true")))
        #expect(items.contains(.init(name: "descriptions", value: "false")))
        #expect(items.contains(.init(name: "hours", value: "9")))

        // Asked again: the ETag goes with it, and the 304 keeps the guide, as fetched now.
        StubProtocol.register(host: host, .init(status: 304, body: Data()))
        let again = try await client.liveGuide(request, kept: first, now: now.addingTimeInterval(300))
        #expect(StubProtocol.recorded(host: host).last?.value(forHTTPHeaderField: "If-None-Match") == "W/\"abc\"")
        #expect(again.guide == first.guide)
        #expect(again.etag == "W/\"abc\"")
        #expect(again.fetchedAt == now.addingTimeInterval(300))

        // A different question (every channel) carries no ETag, nor asks for favourites.
        StubProtocol.register(host: host, .init(status: 200, body: body))
        let all = LiveGuideRequest(from: request.from, hours: 9, scope: .all)
        let wider = try await client.liveGuide(all, kept: first, now: now)
        let widerAsked = try #require(StubProtocol.recorded(host: host).last)
        #expect(widerAsked.value(forHTTPHeaderField: "If-None-Match") == nil)
        #expect(widerAsked.url?.query()?.contains("favourites") == false)
        // An older server sends no ETag: none is kept, so none is sent.
        #expect(wider.etag == nil)
    }

    @Test func a304IsOnlyAnAnswerToAQuestionWithAnETag() async throws {
        let (client, _) = stubbedClient(.init(status: 304, body: Data()))
        let request = LiveGuideRequest(from: now, hours: 6, scope: .all)
        await #expect(throws: FlexarrClientError.self) { _ = try await client.liveGuide(request, kept: nil) }
    }

    private func encoded(_ guide: LiveGuide) throws -> Data {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        return try encoder.encode(guide)
    }
}
