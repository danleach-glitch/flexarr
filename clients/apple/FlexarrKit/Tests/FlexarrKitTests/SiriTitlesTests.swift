import Foundation
import Testing
@testable import FlexarrKit

struct SiriTitlesTests {
    private static func item(_ id: String, _ kind: String, added: String, series: (id: String, title: String)? = nil) throws -> ItemSummary {
        let show = series.map { #","seriesId":"\#($0.id)","seriesTitle":"\#($0.title)""# } ?? #","seriesTitle":null"#
        let json = #"""
        {"id":"\#(id)","libraryId":"lib","parentId":null,"kind":"\#(kind)","title":"Title \#(id)","year":2020,
         "seasonNumber":null,"episodeNumber":null,"episodeNumberEnd":null,"airDate":null,"childCount":0,"durationSeconds":3600,
         "resolution":null,"addedAt":"\#(added)","artwork":{"poster":null,"backdrop":null,"logo":null,"thumb":null},
         "accentColor":null,"communityRating":null,"criticRating":null,"contentRating":null,"runtimeMinutes":null,
         "releaseDate":null,"userData":{"positionSeconds":0,"watched":false,"playCount":0,"lastPlayedAt":null,
         "unwatchedCount":null}\#(show)}
        """#
        return try FlexarrClient.decoder.decode(ItemSummary.self, from: Data(json.utf8))
    }

    private static func podcast(_ n: Int) -> SiriTitles.Title {
        SiriTitles.Title(id: "podcast:\(n)", title: "Podcast \(n)", year: nil, kind: .podcast)
    }

    @Test func aBigLibraryTeachesAboutAHundredAndFiftyNamesPodcastsIncluded() throws {
        var added: [ItemSummary] = []
        for n in 0..<600 {
            added.append(try Self.item("m\(n)", n.isMultiple(of: 2) ? "Movie" : "Series", added: String(format: "2026-01-01T%02d:%02d:00", n / 60, n % 60)))
        }
        let picked = SiriTitles.pick(podcasts: (0..<20).map(Self.podcast), inProgress: [], added: added)
        #expect(picked.count == SiriTitles.cap)
        #expect(picked.prefix(20).allSatisfy { $0.kind == .podcast })
        // The newest added, films and shows together.
        #expect(picked[20].id == "m599")
        #expect(picked[21].id == "m598")
        #expect(Set(picked.dropFirst(20).map(\.kind)) == [.movie, .series])
    }

    @Test func whatIsInProgressComesBeforeTheNewestAndAnEpisodeIsItsShow() throws {
        let inProgress = [
            try Self.item("e1", "Episode", added: "2020-01-01T00:00:00", series: ("s1", "Curb Your Enthusiasm")),
            try Self.item("old-film", "Movie", added: "2020-01-01T00:00:00"),
            try Self.item("e2", "Episode", added: "2020-01-01T00:00:00", series: ("s1", "Curb Your Enthusiasm")),
            try Self.item("book", "Audiobook", added: "2020-01-01T00:00:00"),
        ]
        let added = [
            try Self.item("new-show", "Series", added: "2026-10-01T00:00:00"),
            try Self.item("s1", "Series", added: "2026-09-01T00:00:00"),
        ]
        let picked = SiriTitles.pick(podcasts: [Self.podcast(1)], inProgress: inProgress, added: added)
        #expect(picked.map(\.id) == ["podcast:1", "s1", "old-film", "new-show"])
        #expect(picked[1] == SiriTitles.Title(id: "s1", title: "Curb Your Enthusiasm", year: nil, kind: .series))
    }

    @Test func podcastsAloneStillKeepToTheCap() {
        let picked = SiriTitles.pick(podcasts: (0..<200).map(Self.podcast), inProgress: [], added: [])
        #expect(picked.count == SiriTitles.cap)
    }
}
