import Foundation
import Testing
@testable import FlexarrKit

struct ShowGapsTests {
    private let json = #"""
    {"source":"sonarr","canRequest":true,"canSearch":true,
     "seasons":[{"seasonNumber":3,"name":null,"episodes":8,"year":2022,"poster":null,"availability":"None"},
                {"seasonNumber":4,"name":null,"episodes":6,"year":2024,"poster":null,"availability":"Requested"}],
     "episodes":[{"seasonNumber":2,"episodeNumber":4,"title":"Four","airDate":"2021-03-01T02:00:00Z","still":null,"monitored":false},
                 {"seasonNumber":1,"episodeNumber":2,"title":null,"airDate":null,"still":null,"monitored":true}],
     "upcoming":[{"seasonNumber":5,"episodeNumber":1,"title":"Back","airDate":"2026-11-12T02:00:00Z","still":null,"monitored":true}],
     "counts":[{"seasonNumber":1,"have":9,"aired":10},{"seasonNumber":2,"have":8,"aired":8}]}
    """#

    @Test func decodes_andSaysWhatEachSeasonOffers() throws {
        let gaps = try FlexarrClient.decoder.decode(ShowGaps.self, from: Data(json.utf8))
        #expect(gaps.hasMissing)
        #expect(gaps.seasons.map(\.title) == ["Season 3", "Season 4"])
        #expect(gaps.action(for: gaps.seasons[0]) == .request)
        #expect(gaps.action(for: gaps.seasons[1]) == .requested)
        #expect(gaps.countLabel(season: 1) == "9 of 10")
        #expect(gaps.countLabel(season: 2) == nil)
        #expect(gaps.episodesBySeason.map(\.season) == [1, 2])
        #expect(gaps.upcoming.first?.code == "S5E1")
        // A season it has some of is Requested when Sonarr's already looking for every missing episode (season 1), else not.
        let row = gaps.row()
        #expect(row.map(\.season.seasonNumber) == [1, 2, 3, 4])
        #expect(row.map(\.season.availability) == [.requested, .none, .none, .requested])

        let searchOnly = ShowGaps(source: "sonarr", canRequest: false, canSearch: true, seasons: gaps.seasons, episodes: [], upcoming: [], counts: [])
        #expect(searchOnly.action(for: gaps.seasons[0]) == .search)
        let seerrOnly = ShowGaps(source: "seerr", canRequest: false, canSearch: true, seasons: gaps.seasons, episodes: [], upcoming: [], counts: [])
        #expect(seerrOnly.action(for: gaps.seasons[0]) == .none)
    }

    @Test func airDates_readShort() {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(identifier: "UTC")!
        let now = Date(timeIntervalSince1970: 1_793_620_800) // 2 Nov 2026
        let locale = Locale(identifier: "en_GB")
        func episode(_ seconds: TimeInterval) -> ShowEpisode {
            ShowEpisode(seasonNumber: 1, episodeNumber: 1, title: nil, airDate: now.addingTimeInterval(seconds), still: nil, monitored: true)
        }
        #expect(episode(10 * 86_400).airs(now: now, calendar: calendar, locale: locale) == "Airs 12 Nov")
        #expect(episode(60).airs(now: now, calendar: calendar, locale: locale) == "Airs today")
        #expect(episode(400 * 86_400).airs(now: now, calendar: calendar, locale: locale)?.hasSuffix("2027") == true)
    }

    @Test func olderServers_haveNoGaps() async throws {
        let (client, _) = stubbedClient(.init(status: 404, body: Data()))
        #expect(try await client.showGaps("s1") == .none)
        #expect(try await client.bookSeriesGaps("b1") == .none)
    }

    @Test func aSeasonRequest_namesItsSeasons() async throws {
        let (client, host) = stubbedClient(.init(status: 202, body: Data()))
        try await client.requestSeasons(tmdbId: 95396, seasons: [3])
        try await client.searchMissing(showId: "s1", season: 2)
        let requests = StubProtocol.recorded(host: host)
        #expect(requests.map { $0.url?.path } == ["/api/v1/requests", "/api/v1/items/s1/missing/search"])
        let sent = try JSONSerialization.jsonObject(with: requests[0].httpBody ?? Data()) as? NSDictionary
        #expect(sent == ["mediaType": "tv", "tmdbId": 95396, "seasons": [3]] as NSDictionary)
    }

    @Test func missingBooks_sayTheirNumberAndYear() throws {
        let gaps = try FlexarrClient.decoder.decode(BookSeriesGaps.self, from: Data(#"""
            {"books":[{"asin":"A4","title":"The Last Devil to Die","sequence":"4","authors":["Richard Osman"],"year":2023,"cover":null},
                      {"asin":"N","title":"Extra","sequence":null,"authors":[],"year":null,"cover":null}],"answered":true}
            """#.utf8))
        #expect(gaps.books.map(\.caption) == ["Book 4, 2023", nil])
    }
}
