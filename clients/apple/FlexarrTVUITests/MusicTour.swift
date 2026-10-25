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
        press(.up, wait: 1.5)
        // Tabs are in the server's order after Home; move right to the music one.
        let position = (libraries.firstIndex(of: music) ?? 0) + 1
        press(.right, times: position, wait: 1.2)
        pause(4)
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
        // Opened from the tab bar, focus is still up there: down lands on Play.
        press(.down, wait: 1.5)
        logFocus("album")
        press(.select, wait: 7)
        shot("34-album-playing")
        press(.down, times: 3, wait: 0.8)
        shot("35-album-tracks-focused")
        // The Now Playing tab is the last one; up to the tab bar and right past the rest.
        press(.up, times: 6, wait: 0.6)
        press(.right, times: 8, wait: 0.6)
        pause(2)
        shot("36-now-playing-tab")
        logFocus("tab")
        press(.down, wait: 1.5)
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
        // Down from the tab bar: past the artist's pill, when the album has one, to Play.
        for _ in 0..<3 where !app.buttons["Play"].firstMatch.hasFocus {
            press(.down, wait: 1.5)
        }
        logFocus("album")
        press(.select, wait: 7)
        press(.up, times: 6, wait: 0.6)
        press(.right, times: 8, wait: 0.6)
        press(.down, wait: 1.5) // Play/Pause, then past Next and Repeat to Lyrics.
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
