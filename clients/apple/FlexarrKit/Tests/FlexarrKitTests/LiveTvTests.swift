import Foundation
import Testing
@testable import FlexarrKit

struct LiveTvTests {
    private func status(refreshing: Bool = false, at: TimeInterval = 0, error: String? = nil) -> LiveTvStatus {
        LiveTvStatus(configured: true, channels: 1, refreshing: refreshing, refreshedAt: Date(timeIntervalSince1970: at), error: error, rewindMinutes: 60)
    }

    @Test func aRefreshIsFinishedOnceItsCameAndGoneOrTheAnswerChanged() {
        let before = status()
        #expect(!status(refreshing: true, at: 5).finishedRefresh(since: before, sawRefreshing: true))
        #expect(!status().finishedRefresh(since: before, sawRefreshing: false))
        #expect(status().finishedRefresh(since: before, sawRefreshing: true))
        #expect(status(at: 5).finishedRefresh(since: before, sawRefreshing: false))
        #expect(status(error: "No").finishedRefresh(since: before, sawRefreshing: false))
    }

    @Test func statusFromAnOlderServerHasNoCountOfMissingListings() throws {
        let old = try JSONDecoder().decode(LiveTvStatus.self, from: Data(#"{"configured":true,"channels":1,"refreshing":false,"rewindMinutes":60}"#.utf8))
        #expect(old.channelsWithoutListings == nil)
        #expect(old.downloadError == nil)
    }

    @Test func channels() throws {
        let channels = try Fixture.decode([LiveChannel].self, "live-channels")
        let bbc = try #require(channels.first)
        #expect(bbc.numberText == "101")
        #expect(bbc.logoUrl?.hasPrefix("/api/v1/livetv/channels/") == true)
        let now = try #require(bbc.now)
        #expect(now.end > now.start)
        #expect(bbc.next?.start == now.end)
        #expect(bbc.with(favourite: !bbc.isFavourite).isFavourite != bbc.isFavourite)
        #expect(bbc.groupName == "UK GENERAL")
    }

    @Test func theTitleFetchesOnlyOnceWhatsShownHasEnded() {
        let start = Date(timeIntervalSince1970: 0)
        func programme(_ id: Int64, from: Double, to: Double) -> LiveProgramme {
            LiveProgramme(id: id, start: start.addingTimeInterval(from), end: start.addingTimeInterval(to), title: "P\(id)", subtitle: nil,
                          description: nil, category: nil, season: nil, episode: nil, isNew: false, recording: nil, series: nil)
        }
        let news = programme(1, from: -600, to: 1800)
        let film = programme(2, from: 1800, to: 9000)
        func step(_ at: Double, showing: LiveProgramme?, next: LiveProgramme?, checked: Double = 0) -> LiveTitleRefresh.Step {
            LiveTitleRefresh.step(showing: showing, next: next, checkedAt: start.addingTimeInterval(checked), now: start.addingTimeInterval(at))
        }
        // Still on: nothing fetched, however long it's been.
        #expect(step(1500, showing: news, next: film) == .keep)
        // Ended, and the next one (already known) covers now: shown without a fetch.
        #expect(step(1815, showing: news, next: film) == .show(film))
        // Ended with nothing known after it: fetched, but no more than once a minute.
        #expect(step(9015, showing: film, next: nil) == .fetch)
        #expect(step(9030, showing: film, next: nil, checked: 9015) == .keep)
        #expect(step(9080, showing: film, next: nil, checked: 9015) == .fetch)
        // No guide for the channel: every half hour.
        #expect(step(600, showing: nil, next: nil) == .keep)
        #expect(step(1800, showing: nil, next: nil) == .fetch)
        // A gap in the guide before a programme already known: kept until it starts, then shown, with no fetch.
        #expect(step(1500, showing: nil, next: film) == .keep)
        #expect(step(1815, showing: nil, next: film) == .show(film))
        // And once that one has ended too, asked again (at most once a minute).
        #expect(step(9015, showing: nil, next: film) == .fetch)
        #expect(step(9030, showing: nil, next: film, checked: 9015) == .keep)
    }

    @Test func programmeProgressAndTitle() {
        let start = Date(timeIntervalSince1970: 0)
        let programme = LiveProgramme(id: 1, start: start, end: start.addingTimeInterval(3600), title: "Doctor Who", subtitle: "The Star Beast",
                                      description: nil, category: nil, season: 14, episode: 1, isNew: true, recording: nil, series: nil)
        #expect(programme.progress(at: start.addingTimeInterval(900)) == 0.25)
        #expect(programme.progress(at: start.addingTimeInterval(-60)) == 0)
        #expect(programme.displayTitle == "Doctor Who: The Star Beast")
    }

    @Test func aFetchedGuideIsKeptForHalfAnHour_AndOneNeverFetchedIsDueAtOnce() {
        let fetched = Date(timeIntervalSince1970: 1_800_000_000)
        #expect(LiveGuide.isDue(fetchedAt: nil, now: fetched))
        // The screens look every couple of minutes: none of those looks fetches it again until it's old.
        #expect(!LiveGuide.isDue(fetchedAt: fetched, now: fetched.addingTimeInterval(120)))
        #expect(!LiveGuide.isDue(fetchedAt: fetched, now: fetched.addingTimeInterval(29 * 60)))
        #expect(LiveGuide.isDue(fetchedAt: fetched, now: fetched.addingTimeInterval(30 * 60)))
        #expect(LiveGuide.isDue(fetchedAt: fetched, now: fetched.addingTimeInterval(6 * 3600)))
    }

    @Test func aKeptGuideIsDueOnceARecordingMarkIsOutOfDate_NotBefore() {
        let fetched = Date(timeIntervalSince1970: 1_800_000_000)
        func guide(_ recording: String?, from start: TimeInterval, to end: TimeInterval) -> LiveGuide {
            let programme = LiveProgramme(id: 1, start: fetched.addingTimeInterval(start), end: fetched.addingTimeInterval(end), title: "F1",
                                          subtitle: nil, description: nil, category: nil, season: nil, episode: nil, isNew: false,
                                          recording: recording, series: nil)
            let plain = LiveProgramme(id: 2, start: fetched.addingTimeInterval(-3600), end: fetched.addingTimeInterval(3600), title: "News",
                                      subtitle: nil, description: nil, category: nil, season: nil, episode: nil, isNew: false,
                                      recording: nil, series: nil)
            let channels = [
                GuideChannel(id: "a", number: 1, name: "One", group: nil, logoUrl: nil, isFavourite: false, programmes: [plain]),
                GuideChannel(id: "b", number: 2, name: "Two", group: nil, logoUrl: nil, isFavourite: false, programmes: [plain, programme]),
            ]
            return LiveGuide(from: fetched.addingTimeInterval(-3600), until: fetched.addingTimeInterval(7 * 3600), channels: channels)
        }
        func after(_ seconds: TimeInterval) -> Date { fetched.addingTimeInterval(seconds) }

        // Nothing set to record: kept the half hour, whatever's on.
        #expect(!guide(nil, from: 600, to: 1200).isDue(fetchedAt: fetched, now: after(900)))
        #expect(guide(nil, from: 600, to: 1200).isDue(fetchedAt: fetched, now: after(30 * 60)))
        #expect(guide(nil, from: 600, to: 1200).isDue(fetchedAt: nil, now: after(0)))

        // Set to record, ten minutes off: kept until it starts, then due (it should say it's recording), and no longer
        // once it's over (nothing more to learn about it).
        let scheduled = guide("scheduled", from: 600, to: 1200)
        #expect(!scheduled.isDue(fetchedAt: fetched, now: after(480)))
        #expect(scheduled.isDue(fetchedAt: fetched, now: after(600)))
        #expect(scheduled.isDue(fetchedAt: fetched, now: after(900)))
        #expect(!scheduled.isDue(fetchedAt: fetched, now: after(1200)))

        // Set while it was on, the answer saying "scheduled" as the recording hadn't begun: due at the next look.
        #expect(guide("scheduled", from: -600, to: 1200).isDue(fetchedAt: fetched, now: after(120)))

        // Recording now: kept while it's on, due once it has ended (the mark comes off).
        let recording = guide("recording", from: -600, to: 600)
        #expect(!recording.isDue(fetchedAt: fetched, now: after(480)))
        #expect(recording.isDue(fetchedAt: fetched, now: after(600)))
        #expect(recording.isDue(fetchedAt: fetched, now: after(720)))
    }

    @Test func guide() throws {
        let guide = try Fixture.decode(LiveGuide.self, "live-guide")
        #expect(guide.until > guide.from)
        let first = try #require(guide.channels.first)
        #expect(!first.programmes.isEmpty)
        let now = try #require(first.programmes.first).start.addingTimeInterval(60)
        #expect(first.channel(at: now).now == first.programmes.first)
    }

    @Test func aGuideWithoutDescriptionsSaysSo_AndADetailsSheetFetchesTheOneItShows() async throws {
        let start = Date(timeIntervalSince1970: 1_800_000_000)
        func programme(_ description: String?) -> LiveProgramme {
            LiveProgramme(id: 42, start: start, end: start.addingTimeInterval(3600), title: "News", subtitle: nil, description: description,
                          category: nil, season: nil, episode: nil, isNew: false, recording: nil, series: nil)
        }
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        let full = try encoder.encode(programme("The day's stories."))
        let (client, host) = stubbedClient(.init(status: 200, body: full))

        _ = try? await client.liveGuide(from: start, hours: 8, descriptions: false)
        let asked = try #require(StubProtocol.recorded(host: host).last?.url)
        #expect(asked.path == "/api/v1/livetv/guide")
        #expect(URLComponents(url: asked, resolvingAgainstBaseURL: false)?.queryItems?.contains(.init(name: "descriptions", value: "false")) == true)
        _ = try? await client.liveGuide(from: start, hours: 8)
        #expect(StubProtocol.recorded(host: host).last?.url?.query()?.contains("descriptions") == false)

        // Without one, its details fetch it.
        let fetched = await client.programmeWithDescription(programme(nil))
        #expect(fetched.description == "The day's stories.")
        #expect(StubProtocol.recorded(host: host).last?.url?.path == "/api/v1/livetv/programmes/42")

        // With one (an older server sent it in the guide), nothing is fetched.
        let before = StubProtocol.recorded(host: host).count
        #expect(await client.programmeWithDescription(programme("Kept.")).description == "Kept.")
        #expect(StubProtocol.recorded(host: host).count == before)

        // An older server has no such endpoint: the programme stays as it was.
        let (older, _) = stubbedClient(.init(status: 404, body: Data()))
        #expect(await older.programmeWithDescription(programme(nil)) == programme(nil))
    }

    @Test func recordings() throws {
        let list = try Fixture.decode(Recordings.self, "recordings")
        #expect(list.streams == 1)
        let recorded = try #require(list.recent.first)
        #expect(recorded.state == "completed")
        #expect(recorded.itemId != nil)
        #expect(!recorded.isRecordingNow)
    }

    @Test func aRecordingInTheWayIsReadFromTheProblem() throws {
        let problem = Data("""
            {"type":"https://tools.ietf.org/html/rfc9110#section-15.5.10","title":"Recording","status":409,
             "detail":"F1 on Channel 4 is recording, using every stream there is. Watch Channel 4, or stop the recording.",
             "recordingId":"01a177e6-a32a-7d1f-96e9-31ebd464d4d5","channelId":"01a17633-26dc-7bc2-b4f9-1f8dd37605ed",
             "channelName":"Channel 4","recordingTitle":"F1","until":"2026-10-26T11:15:00Z"}
            """.utf8)
        let recording = try #require(RecordingInTheWay(problem: problem))
        #expect(recording.channelName == "Channel 4")
        #expect(FlexarrClientError.recordingInTheWay(recording).errorDescription?.hasPrefix("F1 on Channel 4") == true)
        #expect(RecordingInTheWay(problem: Data(#"{"detail":"Busy"}"#.utf8)) == nil)
    }
}
