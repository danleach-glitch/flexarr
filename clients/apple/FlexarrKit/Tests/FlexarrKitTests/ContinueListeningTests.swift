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

    @Test func homePlacesTheNewPodcastsRowFromNewerServers() throws {
        let older = #"{"featured":[],"rows":[],"overviews":{}}"#
        #expect(try FlexarrClient.decoder.decode(Home.self, from: Data(older.utf8)).placesPodcasts == nil)

        // An empty row of a style this app doesn't know: where the app's own New podcasts row goes, never a shelf itself.
        let film = Self.summary("Movie", #""seriesTitle":null"#)
        let newer = #"{"featured":[],"rows":[{"id":"continue","title":"Continue watching","style":"wide","items":[\#(film)],"link":null},{"id":"podcasts-new","title":"New podcasts","style":"podcasts","items":[],"link":"/podcasts"}],"overviews":{},"placesPodcasts":true}"#
        let home = try FlexarrClient.decoder.decode(Home.self, from: Data(newer.utf8))
        #expect(home.placesPodcasts == true)
        #expect(home.rows.map(\.id) == ["continue", HomeRow.podcastsRowId])
        #expect(home.rows[1].style == .unknown)
        #expect(!home.rows[1].hasContent)
    }

    @Test func homeCarriesComingSoon() throws {
        let json = #"{"featured":[],"rows":[{"id":"upcoming","title":"Coming soon","style":"upcoming","items":[],"link":null}],"overviews":{},"downloading":2,"upcoming":[{"id":"sonarr-Sonarr-7","kind":"Episode","title":"Pilot","seriesTitle":"Brand New","seasonNumber":1,"episodeNumber":1,"date":"2026-11-03T20:00:00Z","when":"Airs","overview":null,"poster":null,"still":"/api/v1/images/remote?u=x","libraryItemId":null,"available":false,"download":{"progress":0.5,"timeLeft":null,"status":"downloading"},"source":"Sonarr","manageUrl":null,"tmdbId":null},{"id":"radarr-Radarr-3","kind":"Movie","title":"A Film","seriesTitle":null,"seasonNumber":null,"episodeNumber":null,"date":"2026-11-04T00:00:00Z","when":"Digital release","poster":null,"still":null,"libraryItemId":"01a1","available":false,"download":null}]}"#
        let home = try FlexarrClient.decoder.decode(Home.self, from: Data(json.utf8))
        #expect(home.rows.first?.style == .upcoming)
        #expect(home.downloading == 2)
        #expect(home.upcoming?.map(\.title) == ["Pilot", "A Film"])
        #expect(home.upcoming?.first?.download?.progress == 0.5)
        #expect(home.upcoming?.last?.kind == .movie)
        #expect(home.upcoming?.last?.libraryItemId == "01a1")
    }
}

struct UpcomingTests {
    @Test func decodesTheComingSoonPage() throws {
        let json = #"[{"id":"sonarr-Sonarr-7","kind":"Episode","title":"Pilot","seriesTitle":"Brand New","seasonNumber":1,"episodeNumber":1,"date":"2026-11-03T20:00:00Z","when":"Airs","overview":"It starts.","poster":null,"still":null,"libraryItemId":null,"available":false,"download":null,"source":"Sonarr","manageUrl":"http://sonarr.local/series/brand-new"},{"id":"radarr-1","kind":"Movie","title":"A Film","date":"2026-11-04T00:00:00Z","when":"In cinemas"}]"#
        let items = try FlexarrClient.decoder.decode([UpcomingItem].self, from: Data(json.utf8))
        #expect(items.first?.overview == "It starts.")
        #expect(items.first?.available == false)
        #expect(items.first?.manageUrl == "http://sonarr.local/series/brand-new")
        #expect(items.last?.overview == nil)
        #expect(items.last?.available == nil)
    }
}
