import FlexarrKit
import XCTest

/// Walks the music screens against a live server (a music library with at least one artist needed), saving a
/// screenshot at each stop. Same environment as `ScreenshotTour`; plays with `-FlexarrNoHistory`.
@MainActor
final class MusicTour: XCTestCase {
    private var app: XCUIApplication!
    private var client: FlexarrClient!
    private var outputDirectory: URL?
    private var prefix = ""

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
        prefix = UIDevice.current.userInterfaceIdiom == .pad ? "ipad-" : "iphone-"
        client = FlexarrClient(baseURL: address, apiKey: key)
        XCUIDevice.shared.orientation = .portrait
        app = XCUIApplication()
        app.launchArguments = ["-FlexarrServer", address.absoluteString, "-FlexarrApiKey", key, "-FlexarrNoHistory", "YES"]
        app.launch()
        pause(6)
    }

    /// Libraries, the music library, an artist, an album, and Play, which opens Now Playing.
    func testMusic() async throws {
        let libraries = try await client.libraries()
        guard let music = libraries.first(where: { $0.type == .music }) else { throw XCTSkip("no music library") }
        app.tabBars.buttons["Libraries"].firstMatch.tap()
        pause(2)
        app.buttons[music.name].firstMatch.tap()
        pause(5)
        shot("30-music-library")

        guard let artist = try await client.libraryItems(music.id, take: 1).items.first else { throw XCTSkip("no artists") }
        app.open(URL(string: "flexarr://item/\(artist.id)")!)
        pause(5)
        shot("31-artist")

        let albums = try await client.item(artist.id).children.filter { $0.kind == .album }
        guard let album = albums.last else { throw XCTSkip("no albums") }
        app.open(URL(string: "flexarr://item/\(album.id)")!)
        pause(5)
        shot("32-album")
        // Play goes straight to Now Playing.
        app.buttons["Play"].firstMatch.tap()
        XCTAssertTrue(app.buttons["Next"].firstMatch.waitForExistence(timeout: 10), "Play should open Now Playing")
        pause(3)
        shot("34-now-playing")
        app.buttons["Next"].firstMatch.tap()
        pause(6)
        shot("35-now-playing-next-track")
        app.buttons["Pause"].firstMatch.tap()
        pause(1.5)
        shot("36-now-playing-paused")
    }

    /// A track with lyrics: its album (track artists under the titles, where they differ), the track from its row,
    /// Now Playing, then the Lyrics panel following the song.
    func testLyrics() async throws {
        guard let (track, album) = try await trackWithLyrics() else { throw XCTSkip("no track has lyrics") }
        app.open(URL(string: "flexarr://item/\(album)")!)
        pause(5)
        shot("37-album-with-lyrics")
        app.buttons.containing(NSPredicate(format: "label CONTAINS %@", track.title)).firstMatch.tap()
        pause(6)

        let bar = app.descendants(matching: .any).matching(NSPredicate(format: "label BEGINSWITH 'Now playing'")).firstMatch
        XCTAssertTrue(bar.waitForExistence(timeout: 5), "the now-playing bar should be up")
        bar.buttons.firstMatch.tap()
        pause(3)
        let lyrics = app.buttons["Lyrics"].firstMatch
        XCTAssertTrue(lyrics.waitForExistence(timeout: 5), "a track with lyrics offers them")
        lyrics.tap()
        pause(2)
        shot("38-lyrics")
        pause(8)
        shot("39-lyrics-later")
    }

    /// A track in the library that has lyrics (synced ones preferred, as they're the ones that move), with its album's id.
    private func trackWithLyrics() async throws -> (ItemSummary, String)? {
        let libraries = try await client.libraries()
        guard let music = libraries.first(where: { $0.type == .music }) else { return nil }
        var plain: (ItemSummary, String)?
        for artist in try await client.libraryItems(music.id, take: 10).items {
            for track in try await client.tracks(of: artist.id) {
                guard let album = track.parentId, let lyrics = try await client.lyrics(of: track.id) else { continue }
                if lyrics.synced { return (track, album) }
                plain = plain ?? (track, album)
            }
        }
        return plain
    }

    private func pause(_ seconds: TimeInterval) {
        Thread.sleep(forTimeInterval: seconds)
    }

    private func shot(_ name: String) {
        let screenshot = XCUIScreen.main.screenshot()
        let attachment = XCTAttachment(screenshot: screenshot)
        attachment.name = prefix + name
        attachment.lifetime = .keepAlways
        add(attachment)
        if let outputDirectory {
            try? screenshot.pngRepresentation.write(to: outputDirectory.appending(path: "\(prefix)\(name).png"))
        }
    }
}
