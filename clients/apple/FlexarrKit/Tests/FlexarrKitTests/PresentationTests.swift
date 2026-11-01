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

struct LyricsTests {
    @Test func theActiveLineIsTheLastWhoseTimeHasCome() throws {
        let lyrics = try Fixture.decode(Lyrics.self, "lyrics")

        #expect(lyrics.activeLine(at: 0) == nil) // Before the first line, nothing is sung yet.
        #expect(lyrics.activeLine(at: 12) == 0)
        #expect(lyrics.activeLine(at: 17) == 1)
        #expect(lyrics.activeLine(at: 20) == 2) // The empty line between verses counts: the one before is over.
        #expect(lyrics.activeLine(at: 300) == 3)
    }

    @Test func unsyncedLyricsHaveNoActiveLine() {
        let plain = Lyrics(synced: false, lines: [LyricLine(time: nil, text: "Where do you think you're going?")])
        #expect(plain.activeLine(at: 10) == nil)
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
        #expect(episode.episodeSubtitle == "S2E3  Bit by a Dead Bee")
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

    @Test func fallsBackToTheEndWithoutCreditsMarker() {
        // 3% of a 47-minute episode: the last 85 seconds.
        #expect(PlaybackInfo.prompt(at: 2740, markers: [], duration: 2831, hasNext: true) == .none)
        #expect(PlaybackInfo.prompt(at: 2750, markers: [], duration: 2831, hasNext: true) == .nextEpisode)
        // Never less than 30 seconds, nor more than 2 minutes.
        #expect(PlaybackInfo.unmarkedCreditsSeconds(duration: 600) == 30)
        #expect(PlaybackInfo.unmarkedCreditsSeconds(duration: 7200) == 120)
    }

    @Test func realPlaybackFixture() throws {
        let info = try Fixture.decode(PlaybackInfo.self, "playback")
        #expect(info.prompt(at: 216) == .skip(.intro, to: 234.34))
        #expect(info.prompt(at: 2820) == .nextEpisode)
    }
}

struct DeviceProfileTests {
    @Test func iPhoneAndIPadPlayWhatAppleTVPlays() {
        let tv = DeviceProfile.tvOS(hdr: true), phone = DeviceProfile.iOS(hdr: true)
        #expect(phone.client == "ios")
        #expect(phone.videoCodecs == tv.videoCodecs && phone.audioCodecs == tv.audioCodecs && phone.containers == tv.containers)
        #expect(DeviceProfile.iOS(hdr: false).hdr == false)
    }
}

struct SubtitleMatchTests {
    private let options = [
        SubtitleOption(name: "English", language: "en", isForced: false),
        SubtitleOption(name: "English (SDH)", language: "en", isForced: false),
        SubtitleOption(name: "French (Forced)", language: "fr", isForced: true),
    ]

    @Test func matchesTheServersChoiceByName() throws {
        let info = try Fixture.decode(PlaybackInfo.self, "playback")
        #expect(SubtitleMatch.option(for: "e3", in: info, among: options) == 1)
        #expect(SubtitleMatch.option(for: "e2", in: info, among: options) == 0)
    }

    @Test func noneWhenTheServerChoseNoneOrAnUnknownTrack() throws {
        let info = try Fixture.decode(PlaybackInfo.self, "playback")
        #expect(SubtitleMatch.option(for: nil, in: info, among: options) == nil)
        #expect(SubtitleMatch.option(for: "nope", in: info, among: options) == nil)
    }

    @Test func fallsBackToLanguageWithThreeLetterCodes() throws {
        let info = try Fixture.decode(PlaybackInfo.self, "playback")
        // "English (United Kingdom (SDH))" isn't listed by that name: the English one stands in.
        #expect(SubtitleMatch.option(for: "e4", in: info, among: options) == 0)
        #expect(SubtitleMatch.normalized("eng") == "en")
        #expect(SubtitleMatch.normalized("en-GB") == "en")
        #expect(SubtitleMatch.normalized("und") == nil)
    }

    @Test func mapsThePlayersChoiceBackToAKey() throws {
        let info = try Fixture.decode(PlaybackInfo.self, "playback")
        #expect(SubtitleMatch.key(for: options[1], in: info) == "e3")
        #expect(SubtitleMatch.key(for: SubtitleOption(name: "Anglais", language: "en", isForced: false), in: info) == "e2")
        #expect(SubtitleMatch.key(for: options[2], in: info) == nil)
    }
}

struct TrickplayManifestTests {
    @Test func thumbnailsAreFoundBySheetAndCell() {
        let manifest = TrickplayManifest(interval: 10, width: 320, height: 180, columns: 10, rows: 10, count: 250, sheets: 3)
        #expect(manifest.thumbnail(at: 0)?.sheet == 0)
        #expect(manifest.thumbnail(at: 0)?.x == 0)
        #expect(manifest.thumbnail(at: 119)?.x == 320) // thumbnail 11: row 1, column 1
        #expect(manifest.thumbnail(at: 119)?.y == 180)
        #expect(manifest.thumbnail(at: 1000)?.sheet == 1)
        #expect(manifest.thumbnail(at: 1000)?.x == 0)
        #expect(manifest.thumbnail(at: 99_999)?.sheet == 2) // past the end: the last one
        #expect(TrickplayManifest(interval: 0, width: 1, height: 1, columns: 1, rows: 1, count: 0, sheets: 0).thumbnail(at: 5) == nil)
    }
}

struct EmptyHomeMessageTests {
    private func library(_ name: String, _ type: LibraryType, items: Int = 0, folders: [String] = ["/media"], scan: ScanStatus? = nil) -> Library {
        Library(id: name, name: name, type: type, folders: folders, itemCount: items, lastScannedAt: nil, scan: scan)
    }

    @Test func saysNothingWhileALibraryHasTitles() {
        #expect(EmptyHomeMessage(libraries: [library("Movies", .movies, items: 3), library("TV", .tvShows)]) == nil)
    }

    @Test func asksForALibraryWhenThereIsOnlyPodcasts() throws {
        let message = try #require(EmptyHomeMessage(libraries: [library("Podcasts", .podcasts)]))
        #expect(message.title == "Nothing to watch yet")
        #expect(message.folders.isEmpty)
    }

    @Test func saysALibraryIsEmptyAndWhereItLooked() throws {
        let message = try #require(EmptyHomeMessage(libraries: [library("Films", .movies, folders: ["/media/films"]), library("TV", .tvShows, folders: ["/media/tv"])]))
        #expect(message.title == "Your library is empty")
        #expect(message.body == "Add files to your library folders, or connect Sonarr or Radarr in Flexarr on the web.")
        #expect(message.folders == ["/media/films", "/media/tv"])
    }

    @Test func saysOneLineToALimitedProfile() throws {
        let message = try #require(EmptyHomeMessage(libraries: [library("Films", .movies, folders: ["/media/films"])], limitedProfile: "Sam"))
        #expect(message.title == "Nothing here for Sam yet")
        #expect(message.body.isEmpty)
        #expect(message.folders.isEmpty)
        #expect(EmptyHomeMessage(libraries: [library("Films", .movies, items: 2)], limitedProfile: "Sam") == nil)
    }

        @Test func saysAFirstScanIsUnderWay() throws {
        let message = try #require(EmptyHomeMessage(libraries: [library("Films", .movies, scan: ScanStatus(path: "/media/films", total: 10, processed: 4))]))
        #expect(message.title == "Looking through your library")
        #expect(message.body == "Flexarr is reading the files in Films (4 of 10). Your movies appear here as they’re recognised.")
    }
}
