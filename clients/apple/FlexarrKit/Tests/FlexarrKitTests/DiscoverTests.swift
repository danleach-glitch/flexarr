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

    @Test func status() throws {
        let status = try JSONDecoder().decode(RequestStatus.self, from: Data(#"{"configured":true,"reachable":false}"#.utf8))
        #expect(status.configured)
        #expect(!status.reachable)
    }
}
