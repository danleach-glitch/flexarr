import FlexarrKit
import XCTest

/// Drives the music screens with the Siri Remote against a live server (a music library with at least one artist
/// needed), saving a screenshot at each stop. Same environment as `ScreenshotTour`; plays with `-FlexarrNoHistory`.
@MainActor
final class MusicTour: XCTestCase {
    private var app: XCUIApplication!
    private var client: FlexarrClient!
    private var outputDirectory: URL?
    private let remote = XCUIRemote.shared

    override func setUp() async throws {
        let env = ProcessInfo.processInfo.environment
        guard let server = env["FLEXARR_SERVER"], let key = env["FLEXARR_KEY"],
              let address = FlexarrClient.normalizeAddress(server) else {
            throw XCTSkip("Set TEST_RUNNER_FLEXARR_SERVER and TEST_RUNNER_FLEXARR_KEY to run the tour.")
        }
        if let path = env["FLEXARR_SCREENSHOTS"] {
            let directory = URL(fileURLWithPath: path)
            try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            outputDirectory = directory
        }
        client = FlexarrClient(baseURL: address, apiKey: key)
        app = XCUIApplication()
        app.launchArguments = ["-FlexarrServer", address.absoluteString, "-FlexarrApiKey", key, "-FlexarrNoHistory", "YES"]
        app.launch()
        pause(6)
    }

    /// The Music tab: recently added albums and the artists.
    func test1Library() async throws {
        let libraries = try await client.libraries()
        guard let music = libraries.first(where: { $0.type == .music }) else { throw XCTSkip("no music library") }
        app.openSidebarRow(music.name)
        shot("30-music-library")
        press(.down, wait: 1.5)
        shot("31-music-library-row")
    }

    /// An artist by deep link, then an album: Play, the bar, and Now Playing.
    func test2ArtistAlbumAndNowPlaying() async throws {
        let libraries = try await client.libraries()
        guard let music = libraries.first(where: { $0.type == .music }),
              let artist = try await client.libraryItems(music.id, take: 1).items.first else { throw XCTSkip("no artists") }
        app.open(URL(string: "flexarr://item/\(artist.id)")!)
        pause(5)
        shot("32-artist")

        let albums = try await client.item(artist.id).children.filter { $0.kind == .album }
        guard let album = albums.last else { throw XCTSkip("no albums") }
        app.open(URL(string: "flexarr://item/\(album.id)")!)
        pause(5)
        shot("33-album")
        // Opened by a link, focus is on Play; otherwise down to it, past the artist's pill when the album has one.
        for _ in 0..<3 where !app.buttons["Play"].firstMatch.hasFocus {
            press(.down, wait: 1.5)
        }
        logFocus("album")
        guard app.buttons["Play"].firstMatch.hasFocus else { return XCTFail("the album's Play should have focus") }
        press(.select, wait: 7)
        shot("34-album-playing")
        press(.down, times: 3, wait: 0.8)
        shot("35-album-tracks-focused")
        // Now Playing, in the sidebar while the music plays; selecting it puts focus on the controls.
        app.openSidebarRow("Now Playing", wait: 2)
        shot("36-now-playing-tab")
        logFocus("tab")
        shot("37-now-playing")
        logFocus("now playing")
        press(.right, wait: 1.5)
        shot("38-now-playing-next-focused")
        press(.select, wait: 6)
        shot("39-now-playing-next-track")
        press(.select, wait: 6)
        press(.left, times: 2, wait: 1)
        press(.select, wait: 2)
        shot("40-now-playing-paused")
    }

    /// An album whose opener has lyrics: Play, then Now Playing's Lyrics control opens the words, and Menu comes back.
    func test3Lyrics() async throws {
        guard let album = try await albumOpeningWithLyrics() else { throw XCTSkip("no album opens with lyrics") }
        app.open(URL(string: "flexarr://item/\(album)")!)
        pause(5)
        shot("41-album-with-lyrics")
        // Opened by a link, focus is on Play; otherwise down to it, past the artist's pill when the album has one.
        for _ in 0..<3 where !app.buttons["Play"].firstMatch.hasFocus {
            press(.down, wait: 1.5)
        }
        logFocus("album")
        guard app.buttons["Play"].firstMatch.hasFocus else { return XCTFail("the album's Play should have focus") }
        press(.select, wait: 7)
        app.openSidebarRow("Now Playing", wait: 2)
        // From Play/Pause, past Next and Repeat to Lyrics.
        press(.right, times: 3, wait: 1)
        logFocus("lyrics control")
        shot("42-now-playing-lyrics-focused")
        press(.select, wait: 3)
        shot("43-lyrics")
        pause(8)
        shot("44-lyrics-later")
        press(.menu, wait: 2)
        shot("45-back-from-lyrics")
        logFocus("after lyrics")
    }

    /// An artist's Shuffle, with nothing playing, opens Now Playing over the artist (not Home, as the sidebar is built
    /// afresh underneath for the new tab); Menu closes it, back on the artist's Play, which opens it again with music on.
    func test4ShuffleGoesToNowPlaying() async throws {
        let libraries = try await client.libraries()
        guard let music = libraries.first(where: { $0.type == .music }),
              let artist = try await client.libraryItems(music.id, take: 1).items.first else { throw XCTSkip("no artists") }
        app.open(URL(string: "flexarr://item/\(artist.id)")!)
        pause(5)
        for _ in 0..<3 where !app.buttons["Play"].firstMatch.hasFocus {
            press(.down, wait: 1.5)
        }
        press(.right, wait: 1.5)
        logFocus("artist")
        guard app.buttons["Shuffle"].firstMatch.hasFocus else { return XCTFail("the artist's Shuffle should have focus") }
        shot("46-artist-shuffle-focused")
        press(.select, wait: 7)
        shot("47-after-shuffle")
        logFocus("after shuffle")
        XCTAssertTrue(app.buttons["Repeat"].exists, "Shuffle should go to Now Playing")

        press(.menu, wait: 3)
        shot("48-back-from-now-playing")
        logFocus("after menu")
        XCTAssertFalse(app.buttons["Repeat"].exists, "Menu should leave Now Playing")
        XCTAssertTrue(app.staticTexts[artist.title].exists, "Menu should come back to \(artist.title)")
        // The artist is built afresh under Now Playing (see `MainTabView.showNowPlayingTab`), so focus starts on Play.
        XCTAssertTrue(app.buttons["Play"].firstMatch.hasFocus, "focus should be on the artist's Play")

        press(.select, wait: 7)
        shot("49-after-play-while-playing")
        XCTAssertTrue(app.buttons["Repeat"].exists, "Play with music already on should go to Now Playing")
        press(.playPause, wait: 1)
    }

    /// The first album in the library whose first track has lyrics.
    private func albumOpeningWithLyrics() async throws -> String? {
        let libraries = try await client.libraries()
        guard let music = libraries.first(where: { $0.type == .music }) else { return nil }
        for artist in try await client.libraryItems(music.id, take: 10).items {
            var seen = Set<String>()
            for track in try await client.tracks(of: artist.id) {
                guard let album = track.parentId, seen.insert(album).inserted else { continue }
                if try await client.lyrics(of: track.id) != nil { return album }
            }
        }
        return nil
    }

    private func logFocus(_ stop: String) {
        let states = app.buttons.allElementsBoundByIndex.prefix(40).map { "\($0.label)=\($0.hasFocus)" }
        print("FOCUS[\(stop)]: \(states.joined(separator: " | "))")
    }

    private func press(_ button: XCUIRemote.Button, times: Int = 1, wait: TimeInterval) {
        for _ in 0..<times {
            remote.press(button)
            pause(wait)
        }
    }

    private func pause(_ seconds: TimeInterval) {
        Thread.sleep(forTimeInterval: seconds)
    }

    private func shot(_ name: String) {
        let screenshot = XCUIScreen.main.screenshot()
        let attachment = XCTAttachment(screenshot: screenshot)
        attachment.name = name
        attachment.lifetime = .keepAlways
        add(attachment)
        if let outputDirectory {
            try? screenshot.pngRepresentation.write(to: outputDirectory.appending(path: "\(name).png"))
        }
    }
}
