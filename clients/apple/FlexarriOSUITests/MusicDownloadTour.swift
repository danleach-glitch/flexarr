import FlexarrKit
import XCTest

/// Downloads an album against a live server and plays it from the Downloads tab, saving a screenshot at each stop:
/// the album page while it downloads and once it's here, the Downloads tab, the offline album page, and Now Playing
/// with the saved cover. Same environment as `ScreenshotTour`; plays with `-FlexarrNoHistory`. The first artist's
/// shortest album is used (`FLEXARR_ARTIST` names another), so the download stays small.
@MainActor
final class MusicDownloadTour: XCTestCase {
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

    func testDownloadAnAlbumAndPlayItOffline() async throws {
        let libraries = try await client.libraries()
        guard let music = libraries.first(where: { $0.type == .music }) else { throw XCTSkip("no music library") }
        let artists = try await client.libraryItems(music.id, take: 60).items
        let wanted = ProcessInfo.processInfo.environment["FLEXARR_ARTIST"]
        guard let artist = artists.first(where: { wanted == nil || $0.title == wanted }) else { throw XCTSkip("no artists") }
        let albums = try await client.item(artist.id).children.filter { $0.kind == .album && $0.childCount > 0 }
        guard let album = albums.min(by: { $0.childCount < $1.childCount }) else { throw XCTSkip("no albums") }

        app.open(URL(string: "flexarr://item/\(album.id)")!)
        pause(5)
        let download = app.buttons["Download"].firstMatch
        if download.waitForExistence(timeout: 5) {
            download.tap()
            pause(4)
            shot("40-album-downloading")
            // A few tracks at most: a couple of minutes on a local network.
            let done = app.buttons["Downloaded"].firstMatch
            XCTAssertTrue(done.waitForExistence(timeout: 300), "the album should finish downloading")
        } else {
            XCTAssertTrue(app.buttons["Downloaded"].firstMatch.waitForExistence(timeout: 5), "the album should be downloaded, or offer to be")
        }
        pause(1)
        shot("41-album-downloaded")

        app.tabBars.buttons["Downloads"].firstMatch.tap()
        pause(2)
        shot("42-downloads-tab")
        // The row's label is the album, artist and size together.
        app.buttons.matching(NSPredicate(format: "label BEGINSWITH %@", album.title)).firstMatch.tap()
        pause(3)
        shot("43-offline-album")
        app.buttons["Play"].firstMatch.tap()
        pause(5)
        shot("44-offline-album-playing")

        let bar = app.descendants(matching: .any).matching(NSPredicate(format: "label BEGINSWITH 'Now playing'")).firstMatch
        XCTAssertTrue(bar.waitForExistence(timeout: 5), "the now-playing bar should be up")
        bar.buttons.firstMatch.tap()
        pause(3)
        shot("45-now-playing-offline")
        XCTAssertTrue(app.staticTexts["Downloaded"].firstMatch.exists || app.images["Downloaded"].firstMatch.exists,
                      "Now Playing should mark the track as playing from its download")
        app.buttons["Pause"].firstMatch.tap()
        pause(1)
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
