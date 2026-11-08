import Foundation
import Testing
@testable import FlexarrKit

struct ProgressPacingTests {
    private let start = Date(timeIntervalSince1970: 1_000_000)

    @Test func aTransferNotShowingProgressYetIsShownAtOnce() {
        #expect(ProgressPacing.shows(0.001, after: nil, shownAt: nil, now: start))
        #expect(ProgressPacing.shows(0.5, after: nil, shownAt: start, now: start))
    }

    @Test func aStepWithinTheSamePercentIsLetGo() {
        #expect(!ProgressPacing.shows(0.104, after: 0.101, shownAt: start.addingTimeInterval(-10), now: start))
    }

    /// A fast transfer moves several percent a second: only a few steps a second are shown.
    @Test func aNewPercentWaitsForTheInterval() {
        #expect(!ProgressPacing.shows(0.12, after: 0.10, shownAt: start, now: start.addingTimeInterval(0.1)))
        #expect(ProgressPacing.shows(0.12, after: 0.10, shownAt: start, now: start.addingTimeInterval(ProgressPacing.interval)))
        #expect(ProgressPacing.shows(0.12, after: 0.10, shownAt: nil, now: start))
    }

    @Test func aClockPutBackStillShows() {
        #expect(ProgressPacing.shows(0.2, after: 0.1, shownAt: start, now: start.addingTimeInterval(-60)))
    }

    @Test func oddNumbersAreShown() {
        #expect(ProgressPacing.shows(.nan, after: 0.1, shownAt: start, now: start))
    }
}
