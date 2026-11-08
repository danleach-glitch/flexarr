import Foundation
import Testing
@testable import FlexarrKit

/// What the Mac app does each time its window becomes active happens at most once a minute.
struct RefreshPacingTests {
    private let start = Date(timeIntervalSince1970: 1_800_000_000)

    @Test func dueAtFirst_ThenNotAgainUntilTheIntervalHasPassed() {
        let pacing = RefreshPacing(every: 60)
        #expect(pacing.isDue(now: start))
        // Cmd-Tab away and back, again and again.
        #expect(!pacing.isDue(now: start.addingTimeInterval(1)))
        #expect(!pacing.isDue(now: start.addingTimeInterval(59)))
        #expect(pacing.isDue(now: start.addingTimeInterval(60)))
        // The wait starts over from when it last ran, not from when it was last asked.
        #expect(!pacing.isDue(now: start.addingTimeInterval(119)))
        #expect(pacing.isDue(now: start.addingTimeInterval(120)))
    }

    @Test func anotherServerOrProfileIsDueAtOnce() {
        let pacing = RefreshPacing(every: 60)
        #expect(pacing.isDue(for: "home#dan", now: start))
        #expect(!pacing.isDue(for: "home#dan", now: start.addingTimeInterval(5)))
        #expect(pacing.isDue(for: "home#sam", now: start.addingTimeInterval(6)))
        #expect(!pacing.isDue(for: "home#sam", now: start.addingTimeInterval(7)))
        #expect(pacing.isDue(for: "home#dan", now: start.addingTimeInterval(8)))
    }

    @Test func aClockPutBackDoesNotStopItForGood() {
        let pacing = RefreshPacing(every: 60)
        #expect(pacing.isDue(now: start))
        #expect(pacing.isDue(now: start.addingTimeInterval(-3600)))
    }
}
