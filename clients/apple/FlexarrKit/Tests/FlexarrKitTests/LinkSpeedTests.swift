import Foundation
import Testing
@testable import FlexarrKit

@Suite struct LinkSpeedTests {
    @Test func isTheUpperQuartileOfRecentRates() {
        let speed = LinkSpeed()
        let now = Date()
        #expect(speed.current(at: now) == nil)
        speed.sample(bytes: 1_000_000, seconds: 2, at: now)      // 4 Mbps
        speed.sample(bitsPerSecond: 8_000_000, at: now)
        speed.sample(bitsPerSecond: 16_000_000, at: now)
        speed.sample(bitsPerSecond: 32_000_000, at: now)
        #expect(speed.current(at: now) == 32_000_000)
    }

    @Test func ignoresSmallDownloadsAndForgetsOldOnes() {
        let speed = LinkSpeed()
        let now = Date()
        speed.sample(bytes: Int64(LinkSpeed.minSampleBytes - 1), seconds: 0.01, at: now)
        #expect(speed.current(at: now) == nil)
        speed.sample(bitsPerSecond: 8_000_000, at: now)
        #expect(speed.current(at: now.addingTimeInterval(LinkSpeed.freshFor + 1)) == nil)
    }
}
