import Foundation
import Testing
@testable import FlexarrKit

struct NowPlayingClockTests {
    private let start = Date(timeIntervalSince1970: 1_000_000)

    @Test func nothingToldYetIsOff() {
        #expect(NowPlayingClock().isOff(position: 0, duration: 200, rate: 1, at: start))
    }

    @Test func playingAsToldNeedsNothing() {
        var clock = NowPlayingClock()
        clock.told(position: 10, duration: 200, rate: 1, at: start)
        // Ten minutes of ticks along the system's own count: never sent.
        for second in 1...600 {
            #expect(!clock.isOff(position: 10 + Double(second), duration: 200, rate: 1, at: start + Double(second)))
        }
        // At 1.5×, the count runs faster.
        clock.told(position: 10, duration: 200, rate: 1.5, at: start)
        #expect(!clock.isOff(position: 25, duration: 200, rate: 1.5, at: start + 10))
        // Paused: the count stands still.
        clock.told(position: 40, duration: 200, rate: 0, at: start)
        #expect(!clock.isOff(position: 40, duration: 200, rate: 0, at: start + 3600))
    }

    @Test func aStallOrAJumpIsOff() {
        var clock = NowPlayingClock()
        clock.told(position: 10, duration: 200, rate: 1, at: start)
        // Stalled for five seconds while still playing.
        #expect(clock.isOff(position: 12, duration: 200, rate: 1, at: start + 7))
        // Jumped on.
        #expect(clock.isOff(position: 60, duration: 200, rate: 1, at: start + 1))
    }

    @Test func aNewRateOrLengthIsOff() {
        var clock = NowPlayingClock()
        clock.told(position: 10, duration: 200, rate: 1, at: start)
        #expect(clock.isOff(position: 11, duration: 200, rate: 0, at: start + 1))
        #expect(clock.isOff(position: 11, duration: 260, rate: 1, at: start + 1))
        #expect(clock.isOff(position: 11, duration: .nan, rate: 1, at: start + 1))
        #expect(clock.isOff(position: 11, duration: 200, rate: 1, at: start - 5))
    }
}
