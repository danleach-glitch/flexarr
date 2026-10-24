import Foundation
import Testing
@testable import FlexarrKit

struct DateTests {
    @Test(arguments: [
        ("2026-10-24T06:40:12.1234567+00:00", 1_792_824_012.1234567),
        ("2026-10-24T06:40:12Z", 1_792_824_012),
        ("2026-10-24T06:40:12", 1_792_824_012),
        ("2026-10-24T06:40:12.5", 1_792_824_012.5),
        ("2026-10-24T08:40:12+02:00", 1_792_824_012),
        ("2026-10-24T01:40:12-05:00", 1_792_824_012),
    ])
    func parsesDotNetTimestamps(text: String, expected: Double) throws {
        let date = try #require(FlexarrDate.parse(text))
        #expect(abs(date.timeIntervalSince1970 - expected) < 0.0001)
    }

    @Test(arguments: ["", "yesterday", "2026-13-01T00:00:00", "2026-10-24T06:40:12+", "2026-10-24T06:40:12Zjunk"])
    func rejectsGarbage(text: String) {
        #expect(FlexarrDate.parse(text) == nil)
    }

    @Test func decodesDatesWithoutFractionalSeconds() throws {
        let json = #"{"appName":"Flexarr","version":"0.1.0","osDescription":"","runtimeVersion":"","startTime":"2026-10-24T06:40:12Z"}"#
        let status = try FlexarrClient.decoder.decode(SystemStatus.self, from: Data(json.utf8))
        #expect(status.startTime == Date(timeIntervalSince1970: 1_792_824_012))
    }
}

struct FormatTests {
    @Test func runtimes() {
        #expect(Format.runtime(minutes: 116) == "1h 56m")
        #expect(Format.runtime(minutes: 47) == "47m")
        #expect(Format.runtime(minutes: 120) == "2h")
        #expect(Format.runtime(minutes: 0) == nil)
    }

    @Test func timestamps() {
        #expect(Format.timestamp(1930) == "32:10")
        #expect(Format.timestamp(3725) == "1:02:05")
        #expect(Format.timestamp(-4) == "0:00")
    }

    @Test func remaining() {
        #expect(Format.remaining(42 * 60) == "42m left")
        #expect(Format.remaining(30) == nil)
    }

    @Test func movieFacts() throws {
        let movie = try Fixture.decode(ItemDetail.self, "item-movie").item
        let facts = movie.facts
        #expect(facts.first == movie.releaseYear.map(String.init))
        #expect(facts.contains { $0.hasSuffix("m") || $0.hasSuffix("h") })
    }

    @Test func episodeTitles() throws {
        let episode = try Fixture.decode(Home.self, "home").rows[0].items[0]
        #expect(episode.displayTitle == "Breaking Bad")
        #expect(episode.episodeSubtitle == "S2 E3  Bit by a Dead Bee")
    }
}

struct AmbientColourTests {
    @Test func parsesHex() throws {
        let colour = try #require(RGB(hex: "#264c99"))
        #expect(abs(colour.red - 0x26 / 255.0) < 0.0001)
        #expect(abs(colour.blue - 0x99 / 255.0) < 0.0001)
        #expect(RGB(hex: "fff") == RGB(red: 1, green: 1, blue: 1))
        #expect(RGB(hex: "#12345") == nil)
        #expect(RGB(hex: nil) == nil)
    }

    @Test func glowLiftsVeryDarkColours() throws {
        let navy = try #require(RGB(hex: "#0a0f2a"))
        #expect(navy.luminance < 0.12)
        #expect(navy.glow.luminance >= 0.12)
        let gold = try #require(RGB(hex: "#b39c31"))
        #expect(gold.glow == gold, "already bright enough")
    }
}

struct PlaybackPromptTests {
    let markers = [
        Marker(type: .recap, start: 0, end: 45),
        Marker(type: .intro, start: 215.26, end: 234.34),
        Marker(type: .credits, start: 2811.72, end: 2831.26),
    ]

    @Test func offersSkipInsideIntroAndRecap() {
        #expect(PlaybackInfo.prompt(at: 10, markers: markers, duration: 2831, hasNext: true) == .skip(.recap, to: 45))
        #expect(PlaybackInfo.prompt(at: 220, markers: markers, duration: 2831, hasNext: true) == .skip(.intro, to: 234.34))
        #expect(PlaybackInfo.prompt(at: 234, markers: markers, duration: 2831, hasNext: true) == .none, "not in the final second")
        #expect(PlaybackInfo.prompt(at: 1000, markers: markers, duration: 2831, hasNext: true) == .none)
    }

    @Test func offersNextEpisodeOnceCreditsStart() {
        #expect(PlaybackInfo.prompt(at: 2811, markers: markers, duration: 2831, hasNext: true) == .none)
        #expect(PlaybackInfo.prompt(at: 2812, markers: markers, duration: 2831, hasNext: true) == .nextEpisode)
        #expect(PlaybackInfo.prompt(at: 2812, markers: markers, duration: 2831, hasNext: false) == .none)
    }

    @Test func fallsBackToFinalSecondsWithoutCreditsMarker() {
        #expect(PlaybackInfo.prompt(at: 2790, markers: [], duration: 2831, hasNext: true) == .none)
        #expect(PlaybackInfo.prompt(at: 2805, markers: [], duration: 2831, hasNext: true) == .nextEpisode)
    }

    @Test func realPlaybackFixture() throws {
        let info = try Fixture.decode(PlaybackInfo.self, "playback")
        #expect(info.prompt(at: 216) == .skip(.intro, to: 234.34))
        #expect(info.prompt(at: 2820) == .nextEpisode)
    }
}
