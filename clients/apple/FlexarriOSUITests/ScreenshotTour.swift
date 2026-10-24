import FlexarrKit
import XCTest

/// Walks the app against a live server and saves a screenshot at each stop. Playback runs with
/// `-FlexarrNoHistory` so the server's watch history is untouched.
///
///     TEST_RUNNER_FLEXARR_SERVER=http://192.168.1.20:8383 TEST_RUNNER_FLEXARR_KEY=... \
///     TEST_RUNNER_FLEXARR_SCREENSHOTS=/tmp/flexarr-shots \
///     xcodebuild test -project Flexarr.xcodeproj -scheme FlexarriOS -destination 'platform=iOS Simulator,name=iPhone 17 Pro'
///
/// (The simulator can't resolve `.local` names over mDNS; use the server's IP address.)
@MainActor
final class ScreenshotTour: XCTestCase {
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

    func test1Home() async throws {
        shot("01-home")
        app.swipeUp()
        pause(2)
        shot("02-home-rows")
    }

    func test2Detail() async throws {
        let home = try await client.home()
        let titles = home.rows.flatMap(\.items)
        if let series = titles.first(where: { $0.kind == .series }) {
            app.open(URL(string: "flexarr://item/\(series.id)")!)
            pause(5)
            shot("03-series")
            app.swipeUp()
            pause(2)
            shot("04-series-episodes")
        }
        if let movie = titles.first(where: { $0.kind == .movie }) {
            app.open(URL(string: "flexarr://item/\(movie.id)")!)
            pause(5)
            shot("05-movie")
        }
    }

    func test3Library() async throws {
        tab("Libraries")
        pause(2)
        shot("06-libraries")
        app.cells.firstMatch.tap()
        pause(4)
        shot("07-library-grid")
        app.buttons.matching(NSPredicate(format: "label == %@", "Sort and filter")).firstMatch.tap()
        pause(1.5)
        shot("08-library-sort")
    }

    func test4SettingsAndSearch() async throws {
        tab("Settings")
        pause(3)
        shot("09-settings")
        // Search last: on iOS 26 it turns the tab bar into its search field.
        tab("Search")
        pause(2)
        let field = app.searchFields.firstMatch
        if field.waitForExistence(timeout: 3) {
            field.tap()
            field.typeText("the")
            pause(3)
            shot("10-search")
        }
    }

    func test5Playback() async throws {
        // Find an in-progress episode with an intro (sessions are stopped straight away).
        let home = try await client.home()
        let episodes = home.rows.first { $0.style == .wide }?.items.filter { $0.kind == .episode } ?? []
        for episode in episodes.prefix(6) {
            let info = try await client.startPlayback(PlaybackRequest(itemId: episode.id, profile: .iOS(hdr: false)))
            try? await client.stopPlayback(sessionId: info.sessionId)
            guard let intro = info.markers.first(where: { $0.type == .intro || $0.type == .recap }) else { continue }
            XCUIDevice.shared.orientation = .landscapeLeft
            app.open(URL(string: "flexarr://play/\(episode.id)?t=\(Int(intro.start))")!)
            pause(10)
            shot("11-player-skip-intro")
            let skip = app.buttons.matching(NSPredicate(format: "label BEGINSWITH %@", "Skip ")).firstMatch
            if skip.exists { skip.tap() }
            pause(4)
            shot("12-player-after-skip")
            // Close the player, which stops the stream on the server.
            app.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.5)).tap()
            let close = app.buttons["Close"].firstMatch
            if close.waitForExistence(timeout: 3) { close.tap() }
            pause(3)
            XCUIDevice.shared.orientation = .portrait
            return
        }
    }

    /// Subtitles turned off in the player stay off after dragging the scrubber.
    func test6SubtitlesStayOff() async throws {
        let home = try await client.home()
        let titles = home.rows.flatMap(\.items).filter { $0.kind == .episode || $0.kind == .movie }
        for title in titles.prefix(12) {
            let info = try await client.startPlayback(PlaybackRequest(itemId: title.id, profile: .iOS(hdr: false)))
            try? await client.stopPlayback(sessionId: info.sessionId)
            guard let key = info.selectedSubtitleKey, info.subtitles.contains(where: { $0.key == key && $0.isText }) else { continue }
            XCUIDevice.shared.orientation = .landscapeLeft
            app.open(URL(string: "flexarr://play/\(title.id)?t=600")!)
            pause(10)
            shot("13-subtitles-on")
            let legible = app.buttons["Legible Options"].firstMatch
            func showControls() {
                if !legible.isHittable { app.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.5)).tap() }
                _ = legible.waitForExistence(timeout: 3)
            }
            showControls()
            guard legible.waitForExistence(timeout: 3) else { return XCTFail("No subtitles button") }
            legible.tap()
            pause(1)
            shot("14-subtitles-menu")
            let off = app.buttons.matching(NSPredicate(format: "label ==[c] 'Off'")).firstMatch
            guard off.waitForExistence(timeout: 3) else { return XCTFail("No Off in the subtitles menu") }
            off.tap()
            pause(6)
            shot("15-subtitles-off")
            showControls()
            // Both sliders are labelled "Current position"; the volume one is told apart by its identifier.
            let scrubber = app.sliders.matching(identifier: "Current position").firstMatch
            if scrubber.waitForExistence(timeout: 3) { scrubber.adjust(toNormalizedSliderPosition: 0.6) }
            pause(10)
            shot("16-after-seek")
            app.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.5)).tap()
            let close = app.buttons["Close"].firstMatch
            if close.waitForExistence(timeout: 3) { close.tap() }
            pause(3)
            XCUIDevice.shared.orientation = .portrait
            return
        }
        throw XCTSkip("Nothing in Home plays with subtitles on")
    }

    // MARK: Helpers

    /// Tabs are found by their label: in the tab bar on iPhone, the bar along the top on iPad.
    private func tab(_ label: String) {
        let byLabel = NSPredicate(format: "label == %@", label)
        let inTabBar = app.tabBars.buttons.matching(byLabel).firstMatch
        (inTabBar.exists ? inTabBar : app.buttons.matching(byLabel).firstMatch).tap()
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
