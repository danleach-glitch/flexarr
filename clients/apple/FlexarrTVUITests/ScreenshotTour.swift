import FlexarrKit
import XCTest

/// Walks the app with the Siri Remote against a live server and saves a screenshot at each
/// stop. Playback runs with `-FlexarrNoHistory` so the server's watch history is untouched.
///
///     TEST_RUNNER_FLEXARR_SERVER=http://192.168.1.20:8383 TEST_RUNNER_FLEXARR_KEY=... \
///     TEST_RUNNER_FLEXARR_SCREENSHOTS=/tmp/flexarr-shots \
///     xcodebuild test -project Flexarr.xcodeproj -scheme FlexarrTV \
///       -destination 'platform=tvOS Simulator,name=Apple TV 4K (3rd generation) (at 1080p)'
///
/// (The simulator can't resolve `.local` names over mDNS; use the server's IP address.)
@MainActor
final class ScreenshotTour: XCTestCase {
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
        serverAddress = address.absoluteString
        apiKey = key
        app = XCUIApplication()
        if name.contains("test0Connect") {
            // Start signed out, remembering only the address, to exercise the connect screen.
            app.launchArguments = ["-FlexarrSignedOut", "YES", "-FlexarrNoHistory", "YES"]
        } else {
            app.launchArguments = ["-FlexarrServer", address.absoluteString, "-FlexarrApiKey", key, "-FlexarrNoHistory", "YES"]
        }
        app.launch()
        pause(6)
    }

    private var serverAddress = ""
    private var apiKey = ""

    func test0Connect() async throws {
        shot("00a-connect")
        let address = app.textFields.firstMatch
        if (address.value as? String)?.contains(":") != true {
            // No remembered address: fill it in first (focus starts on the address field).
            press(.select, wait: 2)
            app.typeText(serverAddress)
            press(.menu, wait: 1.5)
            press(.down, wait: 1)
        }
        // A wrong key shows the error in place. Menu closes the keyboard, then Connect.
        press(.select, wait: 2)
        app.typeText("not-the-key")
        press(.menu, wait: 1.5)
        press(.down, wait: 1)
        press(.select, wait: 4)
        shot("00b-connect-wrong-key")

        press(.up, wait: 1)
        press(.select, wait: 2)
        app.typeText(apiKey)
        press(.menu, wait: 1.5)
        press(.down, wait: 1)
        press(.select, wait: 8)
        shot("00c-connected-home")
    }

    func test1HomeAndLibrary() async throws {
        shot("01-home")
        // The app opens with focus on Home in the sidebar; right goes into it.
        press(.right, wait: 1.5)
        shot("02-home-hero-focused")
        press(.down, wait: 2)
        shot("03-home-continue-watching")
        press(.down, times: 4, wait: 1.2)
        pause(1.5)
        shot("04-home-rows")
        press(.down, wait: 1.5)
        shot("05-home-top10")

        // The first library, below Home in the sidebar.
        app.focusSidebarRow("Home")
        press(.down, wait: 1)
        XCTAssertNotNil(app.focusedSidebarRow, "the sidebar should have a library below Home")
        if app.focusedSidebarRow != nil {
            press(.select, wait: 4)
            press(.down, wait: 2)
        }
        shot("06-library")
    }

    func test2Detail() async throws {
        let episode = try await inProgressEpisode()
        let season = try await client.item(try XCTUnwrap(episode.parentId))
        let series = try XCTUnwrap(season.ancestors.first { $0.kind == .series })

        app.open(URL(string: "flexarr://item/\(series.id)")!)
        pause(5)
        shot("07-detail-series")
        press(.down, wait: 1.5)
        shot("08-detail-play-focused")
        press(.down, wait: 2)
        shot("09-detail-seasons")
        press(.down, wait: 2.5)
        shot("10-detail-episodes")
        press(.right, wait: 1.5)
        shot("11-detail-episodes-next")
        press(.down, times: 2, wait: 2)
        shot("12-detail-cast")

        app.open(URL(string: "flexarr://item/\(episode.id)")!)
        pause(5)
        shot("13-detail-episode")
    }

    func test3Playback() async throws {
        // Read markers for the in-progress episodes (sessions are stopped straight away).
        var candidates: [(ItemSummary, PlaybackInfo)] = []
        for episode in try await continueEpisodes().prefix(6) {
            let info = try await client.startPlayback(PlaybackRequest(itemId: episode.id, profile: .tvOS(hdr: false)))
            try? await client.stopPlayback(sessionId: info.sessionId)
            candidates.append((episode, info))
        }

        if let (episode, info) = candidates.first(where: { $0.1.markers.contains { $0.type == .intro || $0.type == .recap } }),
           let intro = info.markers.first(where: { $0.type == .intro || $0.type == .recap }) {
            app.open(URL(string: "flexarr://play/\(episode.id)?t=\(Int(intro.start))")!)
            pause(9)
            shot("14-player-skip-intro")
            press(.playPause, wait: 1.5)
            shot("15-player-paused")
            press(.menu, wait: 1)
            press(.menu, wait: 3)
        }

        if let (episode, info) = candidates.first(where: { $0.1.next != nil && $0.1.markers.contains { $0.type == .credits } }),
           let credits = info.markers.first(where: { $0.type == .credits }) {
            app.open(URL(string: "flexarr://play/\(episode.id)?t=\(Int(credits.start))")!)
            pause(9)
            shot("16-player-next-episode")
            // The contextual action takes focus; selecting it starts the next episode in place.
            press(.select, wait: 10)
            press(.playPause, wait: 1.5)
            shot("16b-player-advanced")
            press(.menu, wait: 1)
            press(.menu, wait: 3)
        }
    }

    func test4SettingsAndSearch() async throws {
        let libraries = try await client.libraries().filter { $0.type != .music }
        // Tab order: Home, each library, Search, Settings.
        press(.right, times: libraries.count + 2, wait: 0.8)
        pause(2)
        shot("17-settings")

        press(.left, wait: 2)
        shot("18-search-empty")
        // Move into the on-screen keyboard so the field has keyboard focus, then type.
        press(.down, wait: 1.5)
        app.searchFields.firstMatch.typeText("the")
        pause(3)
        shot("19-search-results")
        press(.down, times: 2, wait: 1.5)
        shot("20-search-focused")

        // The app icon on the Apple TV home screen.
        XCUIDevice.shared.press(.home)
        pause(3)
        shot("21-tvos-home-screen")
    }

    // MARK: Helpers

    private func continueEpisodes() async throws -> [ItemSummary] {
        let home = try await client.home()
        return home.rows.first { $0.style == .wide }?.items.filter { $0.kind == .episode } ?? []
    }

    private func inProgressEpisode() async throws -> ItemSummary {
        let episodes = try await continueEpisodes()
        return try XCTUnwrap(episodes.first { $0.isResumable } ?? episodes.first, "No episode in Continue Watching")
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
