import FlexarrKit
import XCTest

/// The iPad's own layout against a live server (#419, #421): Search keeps a way back to the tab bar, and Now Playing
/// has a close button with the mini player out of the way. Runs on an iPad simulator only, and is skipped unless
/// TEST_RUNNER_FLEXARR_SERVER and TEST_RUNNER_FLEXARR_KEY are set (a music library with an album lets the Now Playing
/// test run too). Plays with `-FlexarrNoHistory`, so nothing's watch state changes.
@MainActor
final class IPadLayoutTest: XCTestCase {
    private var server = ""
    private var key = ""
    private var client: FlexarrClient!

    override func setUp() async throws {
        let env = ProcessInfo.processInfo.environment
        guard let server = env["FLEXARR_SERVER"], let key = env["FLEXARR_KEY"],
              let address = FlexarrClient.normalizeAddress(server) else {
            throw XCTSkip("Set TEST_RUNNER_FLEXARR_SERVER and TEST_RUNNER_FLEXARR_KEY to run it.")
        }
        guard UIDevice.current.userInterfaceIdiom == .pad else { throw XCTSkip("iPad only") }
        self.server = server
        self.key = key
        client = FlexarrClient(baseURL: address, apiKey: key)
        continueAfterFailure = false
    }

    private func launch(_ extra: [String] = []) -> XCUIApplication {
        XCUIDevice.shared.orientation = .portrait
        let app = XCUIApplication()
        app.launchArguments = ["-FlexarrServer", server, "-FlexarrApiKey", key, "-FlexarrNoHistory", "YES",
                               "-FlexarrWelcomed", "YES"] + extra
        app.launch()
        return app
    }

    private func keep(_ app: XCUIApplication, _ name: String) {
        let shot = XCTAttachment(screenshot: app.screenshot())
        shot.name = name
        shot.lifetime = .keepAlways
        add(shot)
    }

    /// #419: with a search typed and the keyboard away, Cancel shows; it ends the search and the tab bar is back.
    func testSearchHasCancelAndGivesTheTabBarBack() {
        let app = launch()
        app.buttons["Search"].firstMatch.tap()
        let field = app.searchFields.firstMatch
        XCTAssertTrue(field.waitForExistence(timeout: 10))
        field.tap()
        field.typeText("Test\n")
        keep(app, "ipad-search-active")
        let cancel = app.navigationBars.buttons["Cancel"].firstMatch
        XCTAssertTrue(cancel.waitForExistence(timeout: 5), "an active search on iPad should offer Cancel")
        cancel.tap()
        XCTAssertTrue(app.buttons["Home"].firstMatch.waitForExistence(timeout: 5), "the tab bar should be back")
        XCTAssertTrue(app.buttons["Home"].firstMatch.isHittable)
        XCTAssertEqual(field.value as? String ?? "", field.placeholderValue ?? "", "Cancel should clear the search")
        keep(app, "ipad-search-cancelled")
    }

    /// #421: Now Playing opens page-sized with a close button, and the mini player is hidden under it.
    func testNowPlayingHasAClose() async throws {
        let libraries = try await client.libraries()
        guard let music = libraries.first(where: { $0.type == .music }) else { throw XCTSkip("no music library") }
        guard let album = try await firstAlbum(music.id) else {
            throw XCTSkip("no albums")
        }
        guard let track = try await client.item(album.id).children.first else { throw XCTSkip("an empty album") }
        // The album, so it plays in the music player (a track's id goes to the video player).
        let app = launch(["-FlexarrPlay", album.id, "-FlexarrShowNowPlaying", "YES"])
        let close = app.buttons["Close Now Playing"].firstMatch
        XCTAssertTrue(close.waitForExistence(timeout: 20), "Now Playing on iPad should have a close button")
        keep(app, "ipad-now-playing")
        // The mini player's own controls are hidden while Now Playing is up: one Stop and close, not two sets.
        XCTAssertFalse(app.buttons["Stop and close"].firstMatch.isHittable)
        close.tap()
        XCTAssertTrue(app.buttons["Stop and close"].firstMatch.waitForExistence(timeout: 5), "the mini player is back")
        XCUIDevice.shared.orientation = .landscapeLeft
        app.staticTexts[track.title].firstMatch.tap()
        if close.waitForExistence(timeout: 5) {
            sleep(2) // the rotation settled
            keep(app, "ipad-now-playing-landscape")
        }
        XCUIDevice.shared.orientation = .portrait
    }

    private func firstAlbum(_ libraryId: String) async throws -> ItemSummary? {
        guard let artist = try await client.libraryItems(libraryId, take: 1).items.first else { return nil }
        return try await client.item(artist.id).children.first
    }
}
