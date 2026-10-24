import Foundation
import Testing
@testable import FlexarrKit

/// JSON captured from a live Flexarr server (arrays trimmed to keep the fixtures small).
enum Fixture {
    static func data(_ name: String) throws -> Data {
        let url = try #require(Bundle.module.url(forResource: name, withExtension: "json", subdirectory: "Fixtures"))
        return try Data(contentsOf: url)
    }

    static func decode<T: Decodable>(_ type: T.Type, _ name: String) throws -> T {
        try FlexarrClient.decoder.decode(T.self, from: data(name))
    }
}

struct DecodingTests {
    @Test func systemStatus() throws {
        let status = try Fixture.decode(SystemStatus.self, "system-status")
        #expect(status.appName == "Flexarr")
        #expect(!status.version.isEmpty)
    }

    @Test func libraries() throws {
        let libraries = try Fixture.decode([Library].self, "libraries")
        #expect(libraries.count == 3)
        #expect(libraries.contains { $0.type == .movies && $0.name == "Movies" })
        #expect(libraries.contains { $0.type == .tvShows })
    }

    @Test func home() throws {
        let home = try Fixture.decode(Home.self, "home")

        #expect(!home.featured.isEmpty)
        let featured = home.featured[0]
        #expect(featured.title == "Curb Your Enthusiasm")
        #expect(featured.kind == .series)
        #expect(featured.accentColor == "#264c99")
        #expect(featured.artwork.logo?.hasPrefix("/api/v1/images/items/") == true)
        #expect(home.overview(for: home.featured[1].id) != nil || home.overview(for: featured.id) != nil)

        #expect(home.rows.map(\.style) == [.wide, .poster, .top10])
        let continueRow = home.rows[0]
        #expect(continueRow.id == "continue")
        #expect(continueRow.link == nil)
        let episode = continueRow.items[0]
        #expect(episode.kind == .episode)
        #expect(episode.seriesTitle == "Breaking Bad")
        #expect(episode.episodeCode == "S2 E3")
        #expect(episode.userData.lastPlayedAt != nil, "no-offset .NET timestamp with 6 fractional digits")
        let progress = try #require(episode.progress)
        #expect(progress > 0.5 && progress < 0.55)
        #expect(home.rows[1].link?.contains("sort=added") == true)
        #expect(home.rows[2].items.count == 3)
    }

    @Test func seriesDetail() throws {
        let detail = try Fixture.decode(ItemDetail.self, "item-series")

        #expect(detail.item.kind == .series)
        #expect(detail.genres == ["Comedy"])
        #expect(detail.studio == "HBO")
        #expect(detail.ancestors.isEmpty)
        #expect(detail.children.allSatisfy { $0.kind == .season })
        #expect(detail.children.first?.seasonNumber == 1)
        #expect(detail.credits.first?.name == "Larry David")
        #expect(detail.credits.first?.role == .actor)
        #expect(detail.files.isEmpty)
        #expect(detail.item.facts.contains("12 seasons"))
    }

    @Test func seasonDetailHasEpisodesAndOverviews() throws {
        let detail = try Fixture.decode(ItemDetail.self, "item-season")

        #expect(detail.item.kind == .season)
        #expect(detail.ancestors.map(\.title) == ["Curb Your Enthusiasm"])
        #expect(detail.children.allSatisfy { $0.kind == .episode })
        let first = try #require(detail.children.first)
        #expect(first.thumbOrBackdrop != nil)
        #expect((detail.childOverviews[first.id] ?? nil) != nil)
    }

    @Test func movieDetailWithFiles() throws {
        let detail = try Fixture.decode(ItemDetail.self, "item-movie")

        #expect(detail.item.kind == .movie)
        let file = try #require(detail.files.first)
        #expect(file.container == "mkv")
        #expect(file.videoRange == .sdr)
        #expect(file.size > 1_000_000_000)
        #expect(file.streams.contains { $0.type == .audio && $0.channels == 6 })
    }

    @Test func search() throws {
        let results = try Fixture.decode(SearchResults.self, "search")
        #expect(!results.titles.isEmpty)
        #expect(results.episodes.allSatisfy { $0.kind == .episode })
        #expect(results.people.first?.credits ?? 0 > 0)
        #expect(!results.isEmpty)
    }

    @Test func person() throws {
        let person = try Fixture.decode(Person.self, "person")
        #expect(person.name == "Larry David")
        #expect(person.credits.first?.item.title == "Curb Your Enthusiasm")
    }

    @Test func playbackInfo() throws {
        let info = try Fixture.decode(PlaybackInfo.self, "playback")

        #expect(info.method == .transcode)
        #expect(info.url.hasSuffix("/master.m3u8"))
        #expect(info.startPositionSeconds > 1500)
        #expect(info.markers.map(\.type) == [.intro, .credits])
        #expect(info.audioTracks.first?.codec == "eac3")
        #expect(info.subtitles.first?.isText == true)
        #expect(info.next?.kind == .episode)
        #expect(info.ancestors.map(\.kind) == [.series, .season])
        #expect(info.transcodeSummary?.contains("H.264") == true)
    }

    @Test func unknownEnumValuesDoNotFailDecoding() throws {
        let json = #"{"type":"Chapter","start":1,"end":2}"#
        let marker = try FlexarrClient.decoder.decode(Marker.self, from: Data(json.utf8))
        #expect(marker.type == .unknown)
    }
}

extension ItemSummary {
    var thumbOrBackdrop: String? { artwork.thumb ?? artwork.backdrop }
}
