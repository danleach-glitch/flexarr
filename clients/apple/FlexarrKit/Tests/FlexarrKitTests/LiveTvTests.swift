import Foundation
import Testing
@testable import FlexarrKit

struct LiveTvTests {
    private func status(refreshing: Bool = false, at: TimeInterval = 0, error: String? = nil) -> LiveTvStatus {
        LiveTvStatus(configured: true, channels: 1, refreshing: refreshing, refreshedAt: Date(timeIntervalSince1970: at), error: error, rewindMinutes: 60)
    }

    @Test func aRefreshIsFinishedWhenTheServersCountMoves() throws {
        let decoder = JSONDecoder()
        func status(_ finished: Int?, error: String? = "No") throws -> LiveTvStatus {
            let count = finished.map { ",\"refreshesFinished\":\($0)" } ?? ""
            return try decoder.decode(LiveTvStatus.self, from: Data("{\"configured\":true,\"channels\":1,\"refreshing\":false,\"error\":\"\(error ?? "")\",\"rewindMinutes\":0\(count)}".utf8))
        }
        // Failing as the last did: only the count differs.
        #expect(try status(5).finishedRefresh(since: status(4), sawRefreshing: false))
        // The count stays put: still waiting, even having seen another finish.
        #expect(try !status(4).finishedRefresh(since: status(4), sawRefreshing: true, looks: 100))
        // A server that doesn't count: only waited for so long.
        #expect(try !status(nil).finishedRefresh(since: status(nil), sawRefreshing: false, looks: 7))
        #expect(try status(nil).finishedRefresh(since: status(nil), sawRefreshing: false, looks: 8))
    }

    @Test func aRefreshIsFinishedOnceItsCameAndGoneOrTheAnswerChanged() {
        let before = status()
        #expect(!status(refreshing: true, at: 5).finishedRefresh(since: before, sawRefreshing: true))
        #expect(!status().finishedRefresh(since: before, sawRefreshing: false))
        #expect(status().finishedRefresh(since: before, sawRefreshing: true))
        #expect(status(at: 5).finishedRefresh(since: before, sawRefreshing: false))
        #expect(status(error: "No").finishedRefresh(since: before, sawRefreshing: false))
    }

    private func refreshingClient(status body: String) -> FlexarrClient {
        let host = "livetv-\(UUID().uuidString.lowercased()).local"
        StubProtocol.register(host: host, path: "/api/v1/livetv/status", .init(status: 200, body: Data(body.utf8)))
        StubProtocol.register(host: host, path: "/api/v1/livetv/refresh", .init(status: 204, body: Data()))
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [StubProtocol.self]
        return FlexarrClient(baseURL: URL(string: "http://\(host):8383")!, apiKey: "secret", session: URLSession(configuration: configuration))
    }

    @Test func waitingGivesUpSayingARefreshIsStillGoing() async throws {
        let client = refreshingClient(status: #"{"configured":true,"channels":1,"refreshing":true,"error":"An old failure","rewindMinutes":60}"#)
        #expect(try await client.refreshLiveTv(looks: 3, every: .milliseconds(1)) == .stillGoing)
    }

    @Test func waitingEndsWithTheStatusOnceARefreshHasFinished() async throws {
        let client = refreshingClient(status: #"{"configured":true,"channels":1,"refreshing":false,"rewindMinutes":60}"#)
        // A server that doesn't count: the eighth look calls it done.
        guard case .finished(let status) = try await client.refreshLiveTv(looks: 9, every: .milliseconds(1)) else {
            Issue.record("should have finished")
            return
        }
        #expect(status?.channels == 1)
    }

    /// A clock that jumps as if the app had been suspended between the first look and the second.
    private final class SuspendedClock: @unchecked Sendable {
        private let lock = NSLock()
        private var reads = 0
        func read() -> Date {
            lock.withLock { reads += 1 }
            return Date(timeIntervalSince1970: lock.withLock { reads } > 1 ? 86_400 : 0)
        }
    }

    @Test func comingBackLongAfterARefreshFinishedIsntNews() async throws {
        let client = refreshingClient(status: #"{"configured":true,"channels":1,"refreshing":false,"error":"An old failure","rewindMinutes":60}"#)
        let clock = SuspendedClock()
        guard case .unwatched(let status) = try await client.refreshLiveTv(looks: 9, every: .milliseconds(1), now: { clock.read() }) else {
            Issue.record("should be unwatched")
            return
        }
        #expect(status?.error == "An old failure")
    }

    @Test func comingBackToARefreshStillGoingKeepsItsStatus() async throws {
        let client = refreshingClient(status: #"{"configured":true,"channels":1,"refreshing":true,"rewindMinutes":60}"#)
        let clock = SuspendedClock()
        guard case .unwatched(let status) = try await client.refreshLiveTv(looks: 2, every: .milliseconds(1), now: { clock.read() }) else {
            Issue.record("should be unwatched")
            return
        }
        #expect(status?.refreshing == true)
    }

    /// The status fails from the first look on: what a phone sees waking up with no network yet.
    private func clientWhoseStatusFailsOnceStarted() -> (FlexarrClient, @Sendable () -> Void) {
        let host = "livetv-\(UUID().uuidString.lowercased()).local"
        let path = "/api/v1/livetv/status"
        StubProtocol.register(host: host, path: path, .init(status: 200, body: Data(#"{"configured":true,"channels":1,"refreshing":true,"rewindMinutes":60}"#.utf8)))
        StubProtocol.register(host: host, path: "/api/v1/livetv/refresh", .init(status: 204, body: Data()))
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [StubProtocol.self]
        let client = FlexarrClient(baseURL: URL(string: "http://\(host):8383")!, apiKey: "secret", session: URLSession(configuration: configuration))
        return (client, { StubProtocol.register(host: host, path: path, .init(status: 503, body: Data())) })
    }

    @Test func comingBackWithTheServerNotAnsweringIsntAFailureEither() async throws {
        let (client, startFailing) = clientWhoseStatusFailsOnceStarted()
        let clock = SuspendedClock()
        let end = try await client.refreshLiveTv(looks: 2, every: .milliseconds(1), now: {
            startFailing()
            return clock.read()
        })
        guard case .unwatched(nil) = end else {
            Issue.record("should be unwatched, with no status")
            return
        }
    }

    @Test func aServerNotAnsweringWhileWatchedStillFails() async throws {
        let (client, startFailing) = clientWhoseStatusFailsOnceStarted()
        await #expect(throws: FlexarrClientError.self) {
            _ = try await client.refreshLiveTv(looks: 9, every: .milliseconds(1), now: {
                startFailing()
                return Date()
            })
        }
    }

    private let goodStatus = StubProtocol.Stub(status: 200, body: Data(#"{"configured":true,"channels":1,"refreshing":true,"rewindMinutes":60}"#.utf8))
    private let accepted = StubProtocol.Stub(status: 204, body: Data())

    private func client(status: StubProtocol.Stub, press: StubProtocol.Stub) -> (FlexarrClient, String) {
        let host = "livetv-\(UUID().uuidString.lowercased()).local"
        StubProtocol.register(host: host, path: "/api/v1/livetv/status", status)
        StubProtocol.register(host: host, path: "/api/v1/livetv/refresh", press)
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [StubProtocol.self]
        return (FlexarrClient(baseURL: URL(string: "http://\(host):8383")!, apiKey: "secret", session: URLSession(configuration: configuration)), host)
    }

    private func refused(status: StubProtocol.Stub? = nil, press: StubProtocol.Stub? = nil) async -> LiveTvRefreshRefused? {
        let (client, _) = client(status: status ?? goodStatus, press: press ?? accepted)
        do {
            _ = try await client.refreshLiveTv(looks: 1, every: .milliseconds(1))
            return nil
        } catch {
            return error as? LiveTvRefreshRefused
        }
    }

    @Test func aRefusedPressIsARefusalWhateverItsBody() async throws {
        let empty = StubProtocol.Stub(status: 403, body: Data())
        #expect(await refused(press: empty)?.message == "Not allowed")
        let worded = StubProtocol.Stub(status: 403, body: Data(#"{"title":"Unlock first","status":403}"#.utf8))
        #expect(await refused(press: worded)?.message == "Unlock first")
        let pin = StubProtocol.Stub(status: 403, body: Data(#"{"title":"Enter it","status":403,"parentPin":true}"#.utf8))
        #expect(await refused(press: pin)?.message == "Enter it")
        let signIn = await refused(press: .init(status: 401, body: Data()))
        #expect(signIn?.message == FlexarrClientError.unauthorized.errorDescription)
    }

    @Test func aRefusedCheckBeforeThePressIsARefusalAndThePressIsntSent() async throws {
        for status in [401, 403] {
            let (client, host) = client(status: .init(status: status, body: Data()), press: accepted)
            await #expect(throws: LiveTvRefreshRefused.self) {
                _ = try await client.refreshLiveTv(looks: 1, every: .milliseconds(1))
            }
            #expect(!StubProtocol.recorded(host: host).contains { $0.url?.path.hasSuffix("/refresh") == true })
        }
    }

    @Test func anyOtherAnswerToThePressIsntARefusal() async throws {
        #expect(await refused(press: .init(status: 429, body: Data(#"{"title":"Slow down","status":429}"#.utf8))) == nil)
        #expect(await refused(press: .init(status: 400, body: Data())) == nil)
        #expect(await refused(press: .init(status: 500, body: Data())) == nil)
    }

    @Test(arguments: [401, 403, 400, 503])
    func looksFailingAfterAnAcceptedPressArentARefusal(status: Int) async throws {
        let host = "livetv-\(UUID().uuidString.lowercased()).local"
        let path = "/api/v1/livetv/status"
        StubProtocol.register(host: host, path: path, goodStatus)
        StubProtocol.register(host: host, path: "/api/v1/livetv/refresh", accepted)
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [StubProtocol.self]
        let client = FlexarrClient(baseURL: URL(string: "http://\(host):8383")!, apiKey: "secret", session: URLSession(configuration: configuration))
        do {
            _ = try await client.refreshLiveTv(looks: 9, every: .milliseconds(1), now: {
                StubProtocol.register(host: host, path: path, .init(status: status, body: Data()))
                return Date()
            })
            Issue.record("should have failed")
        } catch {
            #expect(error is FlexarrClientError, "was \(error)")
        }
    }

    @Test func aRefreshWatchedThroughIsStillNews() async throws {
        let client = refreshingClient(status: #"{"configured":true,"channels":1,"refreshing":false,"rewindMinutes":60}"#)
        let end = try await client.refreshLiveTv(looks: 9, every: .milliseconds(1))
        guard case .finished = end else {
            Issue.record("should have finished")
            return
        }
    }

    @Test func statusFromAnOlderServerHasNoCountOfMissingListings() throws {
        let old = try JSONDecoder().decode(LiveTvStatus.self, from: Data(#"{"configured":true,"channels":1,"refreshing":false,"rewindMinutes":60}"#.utf8))
        #expect(old.channelsWithoutListings == nil)
        #expect(old.downloadError == nil)
        #expect(old.downloadSkipped == nil)
        #expect(!old.keptFailureWasntAskedAgain)
    }

    @Test func aKeptSourceFailureSaysItWasntAskedAgainOnlyWhenItWasnt() throws {
        func status(_ fields: String) throws -> LiveTvStatus {
            try JSONDecoder().decode(LiveTvStatus.self, from: Data(#"{"configured":true,"channels":1,"refreshing":false,"rewindMinutes":60,\#(fields)}"#.utf8))
        }
        #expect(try status(#""downloadError":"No","downloadSkipped":true"#).keptFailureWasntAskedAgain)
        #expect(try !status(#""downloadError":"No","downloadSkipped":false"#).keptFailureWasntAskedAgain)
        #expect(try !status(#""downloadSkipped":true"#).keptFailureWasntAskedAgain)
        // Absent, from an older server.
        #expect(try !status(#""downloadError":"No""#).keptFailureWasntAskedAgain)
    }

    @Test func aChosenGroupIsOnOfferWhileItHasChannelsAndIsOneOfMany() {
        func channel(_ group: String?) -> LiveChannel {
            LiveChannel(id: UUID().uuidString, number: 1, name: "C", group: group, logoUrl: nil, isFavourite: false, now: nil, next: nil)
        }
        #expect([channel("News"), channel("Sport")].offersGroup("News"))
        // Gone with a refresh.
        #expect(![channel("Sport"), channel("Films")].offersGroup("News"))
        // The only group left isn't offered, and no longer shows every channel.
        #expect(![channel("News"), channel(nil)].offersGroup("News"))
        #expect(![LiveChannel]().offersGroup("News"))
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
