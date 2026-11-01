import Foundation
import Testing
@testable import FlexarrKit

/// Books and podcast episodes in Home's Continue watching row (servers from 0.58).
struct ContinueListeningTests {
    private static func summary(_ kind: String, _ extra: String) -> String {
        #"""
        {"id":"\#(kind)-1","libraryId":"lib","parentId":"p1","kind":"\#(kind)","title":"The \#(kind)","year":null,
         "seasonNumber":null,"episodeNumber":null,"episodeNumberEnd":null,"airDate":null,"childCount":0,"durationSeconds":3600,
         "resolution":null,"addedAt":"2026-10-01T10:00:00","artwork":{"poster":"/api/v1/images/items/x/poster","backdrop":null,
         "logo":null,"thumb":null},"accentColor":null,"communityRating":null,"criticRating":null,"contentRating":null,
         "runtimeMinutes":null,"releaseDate":null,"userData":{"positionSeconds":900,"watched":false,"playCount":0,
         "lastPlayedAt":"2026-10-31T20:00:00","unwatchedCount":null},\#(extra)}
        """#
    }

    @Test func aBookAndAPodcastEpisodeInTheContinueRowDecode() throws {
        let book = Self.summary("Audiobook", #""seriesTitle":null,"book":{"authors":"Lee Child, Andrew Child","authorCount":3,"narrator":"Scott Brick","series":null,"sequence":null,"durationSeconds":3600,"finishedAt":null}"#)
        let episode = Self.summary("PodcastEpisode", #""seriesTitle":"The Rest Is History","seriesId":"p1""#)
        let json = #"{"featured":[],"rows":[{"id":"continue","title":"Continue watching or listening","style":"wide","items":[\#(book),\#(episode)],"link":null}],"overviews":{}}"#
        let home = try FlexarrClient.decoder.decode(Home.self, from: Data(json.utf8))

        let row = try #require(home.rows.first)
        #expect(row.title == "Continue watching or listening")
        #expect(row.items.map(\.kind) == [.audiobook, .podcastEpisode])
        #expect(row.items.allSatisfy { $0.isListening })
        #expect(row.items[0].listeningSubtitle == "Lee Child, Andrew Child and 1 more")
        #expect(row.items[1].listeningSubtitle == "The Rest Is History")
        #expect(row.items[0].progress == 0.25)
        #expect(row.items[1].isResumable)
    }

    @Test func olderServersSayNothingOfTheSwitch_NewerOnesDo() throws {
        let older = #"{"autoPlayNext":true,"skipIntros":false,"skipRecaps":false,"skipCredits":false}"#
        #expect(try FlexarrClient.decoder.decode(PlaybackPreferences.self, from: Data(older.utf8)).listeningInContinue == nil)

        let newer = #"{"autoPlayNext":true,"skipIntros":false,"skipRecaps":false,"skipCredits":false,"listeningInContinue":false}"#
        #expect(try FlexarrClient.decoder.decode(PlaybackPreferences.self, from: Data(newer.utf8)).listeningInContinue == false)
    }

    @Test func savingLeavesTheSwitchOutUnlessItWasSet() throws {
        let unset = String(data: try JSONEncoder().encode(PlaybackPreferences()), encoding: .utf8) ?? ""
        #expect(!unset.contains("listeningInContinue"))

        let set = String(data: try JSONEncoder().encode(PlaybackPreferences(listeningInContinue: true)), encoding: .utf8) ?? ""
        #expect(set.contains(#""listeningInContinue":true"#))
    }

    @Test func homeAsksForWhatTheAppCanPlay_AndNothingByDefault() async throws {
        let body = Data(#"{"featured":[],"rows":[],"overviews":{}}"#.utf8)
        let (client, host) = stubbedClient(.init(status: 200, body: body))

        _ = try await client.home()
        _ = try await client.home(listening: [.books, .podcasts])
        _ = try await client.home(listening: [.books])

        let urls = StubProtocol.recorded(host: host).compactMap(\.url)
        #expect(urls.map(\.path) == ["/api/v1/home", "/api/v1/home", "/api/v1/home"])
        #expect(urls.map { URLComponents(url: $0, resolvingAgainstBaseURL: false)?.queryItems?.first(where: { $0.name == "listening" })?.value }
            == [nil, "books,podcasts", "books"])
    }
}
