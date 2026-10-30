import Foundation
import Testing
@testable import FlexarrKit

struct DiscoverTests {
    @Test func rows() throws {
        let rows = try Fixture.decode([ExternalRow].self, "requests-discover")
        #expect(rows.map(\.id) == ["trending", "movies", "tv", "upcoming"])
        let first = try #require(rows.first?.items.first)
        #expect(first.mediaType == "movie")
        #expect(!first.isShow)
        #expect(first.poster?.hasPrefix("/api/v1/images/remote?") == true)
        #expect(first.id == "movie-\(first.tmdbId)")
        // One the library has opens as itself.
        let owned = try #require(rows.flatMap(\.items).first { $0.libraryItemId != nil })
        #expect(owned.availability == .available)
        #expect(rows.flatMap(\.items).contains { $0.availability == .requested })
    }

    @Test func title() throws {
        let title = try Fixture.decode(ExternalDetail.self, "requests-title")
        #expect(title.title == "The Dog Stars")
        #expect(title.runtimeMinutes == 119)
        #expect(title.seasons == nil)
        #expect(title.certification == "R")
        #expect(title.criticScore == 41)
        #expect(title.imdbRating == 6.4)
        #expect(title.madeBy == ["Ridley Scott"])
        #expect(title.cast.first?.character == "Hig")
        #expect(title.availability == .none)
        #expect(title.libraryItemId == nil)
        #expect(!title.recommendations.isEmpty)
        #expect(title.summary.id == "movie-1384216")
    }

    @Test func genres() throws {
        let genres = try Fixture.decode([Genre].self, "genres")
        #expect(genres.first?.name == "Drama")
        #expect(genres.first?.movies ?? 0 > 0)
        #expect(genres.first?.backdrop?.hasPrefix("/api/v1/images/items/") == true)
    }

    @Test func aGenresNameStaysOneSegment() async throws {
        let page = Data(#"{"total":0,"items":[]}"#.utf8)
        for (name, path) in [
            ("Action & Adventure", "/api/v1/genres/Action%20&%20Adventure/items"),
            ("Action/Adventure", "/api/v1/genres/Action%2FAdventure/items"),
            ("100% Drama", "/api/v1/genres/100%25%20Drama/items"),
        ] {
            let (client, host) = stubbedClient(.init(status: 200, body: page))
            _ = try await client.genreItems(name)
            let request = try #require(StubProtocol.recorded(host: host).first)
            #expect(request.url.map { URLComponents(url: $0, resolvingAgainstBaseURL: false)?.percentEncodedPath } == path)
        }
    }

    @Test func status() throws {
        let status = try JSONDecoder().decode(RequestStatus.self, from: Data(#"{"configured":true,"reachable":false}"#.utf8))
        #expect(status.configured)
        #expect(!status.reachable)
    }
}

struct WatchlistTests {
    @Test func entries() throws {
        let json = #"""
        [{"id":"a","mediaType":"movie","tmdbId":438631,"title":"Dune","year":2021,"poster":null,"backdrop":null,"addedAt":"2026-10-30T10:00:00Z",
          "status":"Ready","releaseDate":null,"libraryItemId":"i1","item":null,"source":"flexarr"},
         {"id":"b","mediaType":"tv","tmdbId":1,"title":"New","year":2027,"poster":null,"backdrop":null,"addedAt":"2026-10-30T10:00:00.1234567Z",
          "status":"SomethingNewer","releaseDate":"2027-03-05","libraryItemId":null,"item":null,"source":"trakt"}]
        """#
        let entries = try FlexarrClient.decoder.decode([WatchlistEntry].self, from: Data(json.utf8))
        #expect(entries.map(\.status) == [.ready, .unknown])
        #expect(entries[1].isShow)
        #expect(entries[1].external?.id == "tv-1")
        #expect(WatchlistKey(entries[1].external!) == .tmdb(mediaType: "tv", id: 1))
        #expect(entries[1].external?.onWatchlist == true)
        #expect(entries[0].streaming == nil)
        let streaming = try FlexarrClient.decoder.decode(WatchlistEntry.self, from: Data(#"""
        {"id":"c","mediaType":"movie","tmdbId":2,"title":"S","year":null,"poster":null,"backdrop":null,"addedAt":"2026-10-30T10:00:00Z",
         "status":"NotAvailable","releaseDate":null,"libraryItemId":null,"item":null,"source":"flexarr","streaming":["Netflix"]}
        """#.utf8))
        #expect(streaming.streaming == ["Netflix"])
        let keys = try FlexarrClient.decoder.decode(WatchlistKeys.self, from: Data(#"{"libraryItemIds":["i1"],"titles":["movie:1"]}"#.utf8))
        #expect(keys.libraryItemIds == ["i1"])
    }

    @Test func oldServersHaveNoWatchlist() throws {
        let title = try Fixture.decode(ExternalDetail.self, "requests-title")
        #expect(title.onWatchlist == nil)
        let added = try JSONDecoder().decode(ExternalTitle.self, from: Data(#"""
        {"tmdbId":1,"mediaType":"movie","title":"T","year":null,"overview":null,"poster":null,"backdrop":null,"rating":0,"availability":"None","libraryItemId":null,"onWatchlist":true}
        """#.utf8))
        #expect(added.onWatchlist == true)
    }
}
