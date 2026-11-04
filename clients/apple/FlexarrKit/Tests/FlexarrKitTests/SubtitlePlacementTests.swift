import Foundation
import Testing
@testable import FlexarrKit

struct SubtitlePlacementTests {
    @Test func olderServersLeaveSubtitlesAtTheBottomOverThePicture() throws {
        let json = #"{"autoPlayNext":true,"skipIntros":false,"skipRecaps":false,"skipCredits":false,"subtitleSize":"Large"}"#
        let preferences = try FlexarrClient.decoder.decode(PlaybackPreferences.self, from: Data(json.utf8))
        #expect(preferences.subtitlePlacement == SubtitlePlacement(position: .bottom, inBlackBars: false))
    }

    @Test func aPositionAndBlackBarsAreRead_AndAnUnknownPositionIsTheBottom() throws {
        let json = #"{"autoPlayNext":true,"skipIntros":false,"skipRecaps":false,"skipCredits":false,"subtitlePosition":"Higher","subtitlesInBlackBars":true}"#
        let preferences = try FlexarrClient.decoder.decode(PlaybackPreferences.self, from: Data(json.utf8))
        #expect(preferences.subtitlePlacement == SubtitlePlacement(position: .higher, inBlackBars: true))

        let newer = try FlexarrClient.decoder.decode(SubtitlePosition.self, from: Data(#""Middle""#.utf8))
        #expect(newer == .unknown)
        #expect(newer.lift == SubtitlePosition.bottom.lift)
    }

    @Test func sizesAndPositionsFromANewerServerAreLeftOutWhenSaving() throws {
        let json = #"{"autoPlayNext":false,"skipIntros":true,"skipRecaps":false,"skipCredits":true,"subtitleSize":"Huge","subtitlePosition":"Middle","subtitlesInBlackBars":true}"#
        let preferences = try FlexarrClient.decoder.decode(PlaybackPreferences.self, from: Data(json.utf8))
        #expect(preferences.subtitleSize == .unknown)
        #expect(preferences.subtitlePosition == .unknown)
        let sent = try JSONSerialization.jsonObject(with: JSONEncoder().encode(preferences)) as? [String: Any] ?? [:]
        #expect(sent["subtitleSize"] == nil)
        #expect(sent["subtitlePosition"] == nil)
        #expect(sent["subtitlesInBlackBars"] as? Bool == true)
        #expect(sent["autoPlayNext"] as? Bool == false)
        #expect(sent["skipCredits"] as? Bool == true)

        var known = preferences
        known.subtitleSize = .large
        known.subtitlePosition = .raised
        let sentKnown = try JSONSerialization.jsonObject(with: JSONEncoder().encode(known)) as? [String: Any] ?? [:]
        #expect(sentKnown["subtitleSize"] as? String == "Large")
        #expect(sentKnown["subtitlePosition"] as? String == "Raised")
    }

    @Test func positionsRiseInOrder() {
        #expect(SubtitlePosition.bottom.lift! < SubtitlePosition.raised.lift!)
        #expect(SubtitlePosition.raised.lift! < SubtitlePosition.higher.lift!)
        #expect(SubtitlePosition.top.lift == nil)
    }
}
