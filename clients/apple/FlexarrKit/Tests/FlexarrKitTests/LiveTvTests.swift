import Foundation
import Testing
@testable import FlexarrKit

struct LiveTvTests {
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

    @Test func programmeProgressAndTitle() {
        let start = Date(timeIntervalSince1970: 0)
        let programme = LiveProgramme(id: 1, start: start, end: start.addingTimeInterval(3600), title: "Doctor Who", subtitle: "The Star Beast",
                                      description: nil, category: nil, season: 14, episode: 1, isNew: true)
        #expect(programme.progress(at: start.addingTimeInterval(900)) == 0.25)
        #expect(programme.progress(at: start.addingTimeInterval(-60)) == 0)
        #expect(programme.displayTitle == "Doctor Who: The Star Beast")
    }
}
