import Foundation
import Testing
@testable import FlexarrKit

@Suite struct LinkSpeedTests {
    @Test func isTheMedianOfRecentBigDownloads() {
        let speed = LinkSpeed()
        let now = Date()
        #expect(speed.current(at: now) == nil)
        speed.sample(bytes: 1_000_000, seconds: 1, at: now)   // 8 Mbps
        speed.sample(bytes: 1_000_000, seconds: 0.5, at: now) // 16 Mbps
        speed.sample(bytes: 1_000_000, seconds: 2, at: now)   // 4 Mbps
        #expect(speed.current(at: now) == 8_000_000)
    }

    @Test func ignoresSmallDownloadsAndForgetsOldOnes() {
        let speed = LinkSpeed()
        let now = Date()
        speed.sample(bytes: Int64(LinkSpeed.minSampleBytes - 1), seconds: 0.01, at: now)
        #expect(speed.current(at: now) == nil)
        speed.sample(bytes: 1_000_000, seconds: 1, at: now)
        #expect(speed.current(at: now.addingTimeInterval(LinkSpeed.freshFor + 1)) == nil)
    }
}
