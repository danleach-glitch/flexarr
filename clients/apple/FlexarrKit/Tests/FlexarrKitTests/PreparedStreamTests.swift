import Foundation
import Testing
@testable import FlexarrKit

@Suite struct PreparedStreamTests {
    private let profile = DeviceProfile.tvOS(hdr: false)

    private func info() throws -> PlaybackInfo {
        try Fixture.decode(PlaybackInfo.self, "playback")
    }

    @Test func playTakesOverExactlyTheSameStream() throws {
        var asked = PlaybackRequest(itemId: "ep", profile: profile)
        asked.prepare = true
        asked.measuredBitrate = 8_000_000
        let ready = PreparedStream(request: asked, info: try info())

        #expect(ready.serves(PlaybackRequest(itemId: "ep", profile: profile)))
        // Start over, another audio track, another title, shuffle: not the stream got ready.
        #expect(!ready.serves(PlaybackRequest(itemId: "ep", profile: profile, startPositionSeconds: 0)))
        #expect(!ready.serves(PlaybackRequest(itemId: "ep", profile: profile, audioStreamIndex: 2)))
        #expect(!ready.serves(PlaybackRequest(itemId: "film", profile: profile)))
        #expect(!ready.serves(PlaybackRequest(itemId: "ep", profile: profile, shuffle: true)))
    }

    @Test func anOldOneIsntUsed() throws {
        let ready = PreparedStream(request: PlaybackRequest(itemId: "ep", profile: profile), info: try info(), at: Date(timeIntervalSinceNow: -PreparedStream.maxAge - 1))
        #expect(!ready.serves(PlaybackRequest(itemId: "ep", profile: profile)))
    }
}
