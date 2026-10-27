import FlexarrKit
import XCTest

/// The screens behind the App Store's Apple TV screenshots, on titles you choose. Same environment as
/// `ScreenshotTour` (`-FlexarrNoHistory`, so nothing lands in watch history), plus the titles to show:
///
///     TEST_RUNNER_FLEXARR_SERIES=<id> TEST_RUNNER_FLEXARR_MOVIE=<id> TEST_RUNNER_FLEXARR_EPISODE=<id>
///     TEST_RUNNER_FLEXARR_EPISODE_AT=<seconds> TEST_RUNNER_FLEXARR_ALBUM=<id> TEST_RUNNER_FLEXARR_LIVE=1
///
/// `TEST_RUNNER_FLEXARR_PROFILE=<name>` picks that profile at launch (one without a PIN). Screens are saved as
/// `tv-store-*.png`. A test whose title isn't set is skipped.
@MainActor
final class StoreTour: XCTestCase {
    private var app: XCUIApplication!
    private var outputDirectory: URL?
    private let env = ProcessInfo.processInfo.environment
    private let remote = XCUIRemote.shared
    private var client: FlexarrClient?

    override func setUp() async throws {
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
        if env["FLEXARR_PROFILE"] != nil { app.launchArguments += ["-FlexarrAskProfile", "YES"] }
        app.launch()
        pause(6)
        chooseProfile()
        pause(8)
    }

    func test1Home() {
        shot("store-home")
        // Into Home, then down past the hero to the rows.
        press(.right, wait: 1.5)
        press(.down, times: Int(env["FLEXARR_HOME_DOWN"] ?? "") ?? 3, wait: 1.5)
        pause(3)
        shot("store-home-rows")
    }

    func test2Titles() throws {
        guard env["FLEXARR_SERIES"] != nil || env["FLEXARR_MOVIE"] != nil else { throw XCTSkip("Set TEST_RUNNER_FLEXARR_SERIES or _MOVIE") }
        if let series = env["FLEXARR_SERIES"] {
            open("flexarr://item/\(series)")
            shot("store-series")
            press(.down, times: 2, wait: 2.5)
            shot("store-series-episodes")
        }
        if let movie = env["FLEXARR_MOVIE"] {
            open("flexarr://item/\(movie)")
            shot("store-movie")
        }
    }

    /// `TEST_RUNNER_FLEXARR_EPISODE` can list several episodes (commas): the first whose intro brings up Skip Intro
    /// is the one shot. With `TEST_RUNNER_FLEXARR_EPISODE_AT`, the first episode plays from that time instead.
    func test3Player() async throws {
        guard let episodes = env["FLEXARR_EPISODE"]?.split(separator: ",").map(String.init) else { throw XCTSkip("Set TEST_RUNNER_FLEXARR_EPISODE") }
        let skip = app.buttons.matching(NSPredicate(format: "label BEGINSWITH 'Skip'")).firstMatch
        for episode in episodes {
            var at = env["FLEXARR_EPISODE_AT"]
            if at == nil, let client {
                // A session started just to read the markers, and stopped straight away.
                let info = try await client.startPlayback(PlaybackRequest(itemId: episode, profile: .tvOS(hdr: false)))
                try? await client.stopPlayback(sessionId: info.sessionId)
                guard let intro = info.markers.first(where: { $0.type == .intro }) else { continue }
                at = String(Int(intro.start) + 1)
            }
            open("flexarr://play/\(episode)?t=\(at ?? "600")", settle: 0)
            let shown = skip.waitForExistence(timeout: 15)
            if shown || env["FLEXARR_EPISODE_AT"] != nil {
                pause(1.5)
                shot("store-player")
                // Paused, the transport bar shows the scrubbing preview.
                press(.playPause, wait: 2)
                shot("store-player-paused")
            }
            // Menu twice closes the player, which stops the stream on the server.
            press(.menu, wait: 1)
            press(.menu, wait: 3)
            if shown || env["FLEXARR_EPISODE_AT"] != nil { return }
        }
        XCTFail("none of the episodes showed Skip Intro")
    }

    func test4Music() throws {
        guard let album = env["FLEXARR_ALBUM"] else { throw XCTSkip("Set TEST_RUNNER_FLEXARR_ALBUM") }
        open("flexarr://item/\(album)")
        shot("store-album")
        for _ in 0..<3 where !app.buttons["Play"].firstMatch.hasFocus { press(.down, wait: 1.5) }
        guard app.buttons["Play"].firstMatch.hasFocus else { return XCTFail("the album's Play should have focus") }
        press(.select, wait: 7)
        app.openSidebarRow("Now Playing", wait: 4)
        shot("store-now-playing")
        // From Play/Pause, past Next and Repeat to Lyrics.
        press(.right, times: 3, wait: 1)
        press(.select, wait: 5)
        shot("store-lyrics")
        press(.menu, wait: 2)
        press(.playPause, wait: 1)
    }

    func test5LiveTv() throws {
        guard env["FLEXARR_LIVE"] != nil else { throw XCTSkip("Set TEST_RUNNER_FLEXARR_LIVE") }
        // Opened from the sidebar, focus is on the first category (Favourites, empty on a fresh profile): down to All
        // channels, then across into the guide.
        app.openSidebarRow("Live TV", wait: 6)
        let all = app.buttons.matching(NSPredicate(format: "label BEGINSWITH 'All channels'")).firstMatch
        for _ in 0..<4 where !all.hasFocus { press(.down, wait: 2) }
        pause(3)
        shot("store-guide")
        press(.right, wait: 3)
        shot("store-guide-focused")
    }

    // MARK: Helpers

    private func open(_ link: String, settle: TimeInterval = 6) {
        app.open(URL(string: link)!)
        chooseProfile()
        pause(settle)
    }

    /// Picks `FLEXARR_PROFILE` in "Who's watching?", if it's showing, by moving focus along the tiles.
    private func chooseProfile() {
        guard let profile = env["FLEXARR_PROFILE"] else { return }
        let choice = app.buttons.matching(NSPredicate(format: "label CONTAINS %@", profile)).firstMatch
        guard choice.waitForExistence(timeout: 4) else { return }
        for _ in 0..<8 where !choice.hasFocus { press(.right, wait: 1) }
        for _ in 0..<8 where !choice.hasFocus { press(.left, wait: 1) }
        XCTAssertTrue(choice.hasFocus, "focus should reach the \(profile) profile")
        press(.select, wait: 2)
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
        attachment.name = "tv-" + name
        attachment.lifetime = .keepAlways
        add(attachment)
        if let outputDirectory {
            try? screenshot.pngRepresentation.write(to: outputDirectory.appending(path: "tv-\(name).png"))
        }
    }
}
