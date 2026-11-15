import Testing
@testable import FlexarrKit

/// Download the next episode keeps one episode ahead, no further, and leaves alone one deleted by hand.
struct NextEpisodeRuleTests {
    @Test func aDownloadedEpisodeBringsItsNext() {
        #expect(NextEpisodeRule.nextToFetch(kind: .episode, isDownloaded: true, automatic: false, watched: false, nextId: "e2") == "e2")
        #expect(NextEpisodeRule.nextToFetch(kind: .episode, isDownloaded: false, automatic: false, watched: false, nextId: "e2") == nil)
        #expect(NextEpisodeRule.nextToFetch(kind: .movie, isDownloaded: true, automatic: false, watched: false, nextId: "e2") == nil)
        #expect(NextEpisodeRule.nextToFetch(kind: .episode, isDownloaded: true, automatic: false, watched: false, nextId: nil) == nil)
    }

    @Test func theEpisodeAheadBringsTheOneAfterOnlyOnceWatched() {
        #expect(NextEpisodeRule.nextToFetch(kind: .episode, isDownloaded: true, automatic: true, watched: false, nextId: "e3") == nil)
        #expect(NextEpisodeRule.nextToFetch(kind: .episode, isDownloaded: true, automatic: true, watched: true, nextId: "e3") == "e3")
    }

    @Test func aNextEpisodeDeletedByHandIsntBroughtBack() {
        #expect(NextEpisodeRule.nextToFetch(kind: .episode, isDownloaded: true, automatic: false, watched: true, nextId: "e2", skipped: ["e2"]) == nil)
    }
}
