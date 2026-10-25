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

    /// Libraries, the music library, an artist, an album, Play, the bar, Now Playing.
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
        app.buttons["Play"].firstMatch.tap()
        pause(7)
        shot("33-album-playing")

        let bar = app.descendants(matching: .any).matching(NSPredicate(format: "label BEGINSWITH 'Now playing'")).firstMatch
        XCTAssertTrue(bar.waitForExistence(timeout: 5), "the now-playing bar should be up")
        bar.buttons.firstMatch.tap()
        pause(3)
        shot("34-now-playing")
        app.buttons["Next"].firstMatch.tap()
        pause(6)
        shot("35-now-playing-next-track")
        app.buttons["Pause"].firstMatch.tap()
        pause(1.5)
        shot("36-now-playing-paused")
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
