import Foundation
import Testing
@testable import FlexarrKit

struct PodcastTests {
    @Test func status() throws {
        let status = try Fixture.decode(PodcastStatus.self, "podcast-status")
        #expect(status.available)
        #expect(status.country == "gb")
    }

    @Test func podcast() throws {
        let podcast = try Fixture.decode(Podcast.self, "podcast")
        #expect(podcast.title == "The Rest Is History")
        #expect(podcast.author == "Goalhanger")
        #expect(podcast.episodeCount > 700)
        #expect(podcast.iTunesId == 1537788786)
        #expect(podcast.artwork.poster?.hasPrefix("/api/v1/images/items/") == true)
        #expect(podcast.latestEpisodeAt != nil)
        #expect(!podcast.isSubscribed)
    }

    @Test func episodes() throws {
        let page = try Fixture.decode(Paged<PodcastEpisode>.self, "podcast-episodes")
        #expect(page.total > 700)
        let episode = try #require(page.items.first)
        #expect(episode.podcastTitle == "The Rest Is History")
        #expect(episode.episodeType == .full)
        #expect(episode.durationSeconds ?? 0 > 3000)
        #expect(episode.publishedAt != nil)
        #expect(!episode.isPlayed)

        let summary = episode.summary
        #expect(summary.kind == .podcastEpisode)
        #expect(summary.id == episode.id)
        #expect(summary.artistName == "The Rest Is History")
        #expect(summary.seriesId == episode.podcastId)
    }

    @Test func directory() throws {
        let results = try Fixture.decode([DirectoryPodcast].self, "podcast-search")
        let first = try #require(results.first)
        #expect(first.iTunesId == 1537788786)
        #expect(first.hasFeed)
        #expect(first.podcastId != nil)
        #expect(first.artwork?.hasPrefix("/api/v1/images/remote") == true)
        #expect(try Fixture.decode([PodcastGenre].self, "podcast-genres").first?.name == "Arts")
    }

    @Test func newKindsAndUnknownOnes() throws {
        let kinds = try FlexarrClient.decoder.decode([MediaKind].self, from: Data(#"["Podcast","PodcastEpisode","Audiobook"]"#.utf8))
        #expect(kinds == [.podcast, .podcastEpisode, .unknown])
        #expect(kinds[1].isPodcast && !kinds[1].isMusic)
        #expect(try FlexarrClient.decoder.decode(LibraryType.self, from: Data(#""Podcasts""#.utf8)) == .podcasts)
        #expect(try FlexarrClient.decoder.decode(PodcastEpisodeType.self, from: Data(#""Bonus""#.utf8)) == .bonus)
    }

    @Test func progress() throws {
        let json = """
            {"id":"e","podcastId":"p","podcastTitle":"Show","title":"One","overview":null,"publishedAt":"2026-10-21T23:05:00",
             "durationSeconds":1000,"season":null,"episode":null,"episodeType":"Full","explicit":false,
             "artwork":{"poster":null,"backdrop":null,"logo":null,"thumb":null},"accentColor":null,
             "userData":{"positionSeconds":250,"watched":false,"playCount":0,"lastPlayedAt":null,"unwatchedCount":null},
             "sizeBytes":null,"hasTranscript":false}
            """
        let episode = try FlexarrClient.decoder.decode(PodcastEpisode.self, from: Data(json.utf8))
        #expect(episode.progress == 0.25)
    }
}

struct PodcastClientTests {
    @Test func asksForAPageOfEpisodes() async throws {
        let (client, host) = stubbedClient(.init(status: 200, body: try Fixture.data("podcast-episodes")))

        let page = try await client.podcastEpisodes("p1", sort: .oldest, unplayed: true, skip: 50, take: 25)

        #expect(page.items.count == 2)
        let url = try #require(StubProtocol.recorded(host: host).first?.url)
        #expect(url.path == "/api/v1/podcasts/p1/episodes")
        let query = Set(URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems?.map { "\($0.name)=\($0.value ?? "")" } ?? [])
        #expect(query == ["limit=25", "offset=50", "sort=oldest", "unplayed=true"])
    }

    @Test func subscribes() async throws {
        let (client, host) = stubbedClient(.init(status: 200, body: try Fixture.data("podcast")))

        _ = try await client.subscribe(to: "p1")

        let request = try #require(StubProtocol.recorded(host: host).first)
        #expect(request.httpMethod == "PUT")
        #expect(request.url?.path == "/api/v1/podcasts/p1/subscription")
        #expect(String(data: request.httpBody ?? Data(), encoding: .utf8) == "{}")
    }

    @Test func changesTheOrder() async throws {
        let (client, host) = stubbedClient(.init(status: 200, body: try Fixture.data("podcast")))

        _ = try await client.subscribe(to: "p1", oldestFirst: true)

        #expect(String(data: StubProtocol.recorded(host: host).first?.httpBody ?? Data(), encoding: .utf8) == #"{"oldestFirst":true}"#)
    }

    @Test func markingPlayedLocally() throws {
        let page = try Fixture.decode(Paged<PodcastEpisode>.self, "podcast-episodes")
        let episode = try #require(page.items.first)
        #expect(episode.with(played: true).isPlayed)
        #expect(!episode.with(played: true).with(played: false).isPlayed)
        #expect(episode.with(played: true).progress == nil)
        let subscription = try FlexarrClient.decoder.decode(PodcastSubscription.self, from: Data(#"{"subscribedAt":"2026-10-26T00:00:00Z","oldestFirst":true}"#.utf8))
        #expect(subscription.oldestFirst == true)
    }

    @Test func upNext() async throws {
        let page = try Fixture.data("podcast-episodes")
        let items = try JSONSerialization.jsonObject(with: page) as? [String: Any]
        let list = try JSONSerialization.data(withJSONObject: items?["items"] ?? [])
        let (client, host) = stubbedClient(.init(status: 200, body: list))

        let queue = try await client.addToUpNext("e1", next: true)

        #expect(queue.count == 2)
        let request = try #require(StubProtocol.recorded(host: host).first)
        #expect(request.httpMethod == "POST")
        #expect(request.url?.path == "/api/v1/podcasts/queue")
        let body = try #require(try JSONSerialization.jsonObject(with: request.httpBody ?? Data()) as? [String: Any])
        #expect(body["episodeId"] as? String == "e1")
        #expect(body["next"] as? Bool == true)
    }

    @Test func reordersUpNext() async throws {
        let (client, host) = stubbedClient(.init(status: 200, body: Data("[]".utf8)))

        _ = try await client.setUpNext(["b", "a"])

        let request = try #require(StubProtocol.recorded(host: host).first)
        #expect(request.httpMethod == "PUT")
        #expect(String(data: request.httpBody ?? Data(), encoding: .utf8) == #"{"episodeIds":["b","a"]}"#)
    }

    @Test func nothingLeftToPlayIsNil() async throws {
        let (client, host) = stubbedClient(.init(status: 204, body: Data()))

        #expect(try await client.nextEpisode(after: "e1") == nil)
        #expect(StubProtocol.recorded(host: host).first?.url?.path == "/api/v1/podcasts/episodes/e1/next")
    }

    @Test func readsChapters_andAnOlderServerHasNone() async throws {
        let (client, host) = stubbedClient(.init(status: 200, body: Data(#"[{"startSeconds":0,"title":"Intro"},{"startSeconds":230,"title":"A gift"}]"#.utf8)))

        let chapters = try await client.podcastChapters("e1")
        #expect(chapters == [PodcastChapter(startSeconds: 0, title: "Intro"), PodcastChapter(startSeconds: 230, title: "A gift")])
        #expect(StubProtocol.recorded(host: host).first?.url?.path == "/api/v1/podcasts/episodes/e1/chapters")
        #expect(chapters.index(at: 100) == 0)
        #expect(chapters.index(at: 229.8) == 1)

        let (older, _) = stubbedClient(.init(status: 404, body: Data()))
        #expect(try await older.podcastChapters("e1").isEmpty)
    }

    @Test func savesPreferences() async throws {
        let (client, host) = stubbedClient(.init(status: 200, body: Data(#"{"autoDownload":false,"keepEpisodes":5,"speed":1.5}"#.utf8)))

        let saved = try await client.setPodcastPreferences(.init(speed: 1.5))

        #expect(saved == PodcastPreferences(autoDownload: false, keepEpisodes: 5, speed: 1.5))
        let body = try #require(StubProtocol.recorded(host: host).first?.httpBody)
        #expect(String(data: body, encoding: .utf8) == #"{"speed":1.5}"#)
    }

    @Test func aServerWithoutPodcastsHasNone() async throws {
        let (client, _) = stubbedClient(.init(status: 404, body: Data()))

        #expect(try await client.podcastStatus() == nil)
    }
}
