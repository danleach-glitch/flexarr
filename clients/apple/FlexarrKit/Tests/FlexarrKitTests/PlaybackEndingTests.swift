@testable import FlexarrKit
import Testing

struct PlaybackEndingTests {
    @Test func movesOnPastAFinishedEpisode() {
        let ending = PlaybackEnding(from: "page", startedWith: "e2", stoppedOn: "e2", watched: true, next: "e3")
        #expect(ending.moveOn(from: "e2") == "e3")
    }

    @Test func goesToTheEpisodeItStoppedPartWayThrough() {
        let ending = PlaybackEnding(from: "page", startedWith: "e2", stoppedOn: "e4", watched: false, next: "e5")
        #expect(ending.moveOn(from: "e2") == "e4")
    }

    @Test func staysWhenStoppedPartWayOrAtTheLastEpisode() {
        #expect(PlaybackEnding(from: nil, startedWith: "e2", stoppedOn: "e2", watched: false, next: "e3").moveOn(from: "e2") == nil)
        #expect(PlaybackEnding(from: nil, startedWith: "e9", stoppedOn: "e9", watched: true, next: nil).moveOn(from: "e9") == nil)
    }

    @Test func onlyMovesThePageShowingWhatWasStarted() {
        let ending = PlaybackEnding(from: "page", startedWith: "e2", stoppedOn: "e2", watched: true, next: "e3")
        #expect(ending.moveOn(from: "series") == nil)
    }

    @Test func watchedAtTheCreditsOr92PercentWhicheverComesFirst() {
        #expect(PlaybackEnding.isWatched(position: 94, duration: 100, creditsStart: 97))
        #expect(PlaybackEnding.isWatched(position: 85, duration: 100, creditsStart: 84))
        #expect(!PlaybackEnding.isWatched(position: 90, duration: 100, creditsStart: nil))
        #expect(!PlaybackEnding.isWatched(position: 0, duration: 0, creditsStart: nil))
    }
}
