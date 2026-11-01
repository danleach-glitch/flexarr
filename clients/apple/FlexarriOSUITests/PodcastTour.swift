import FlexarrKit
import XCTest

/// Walks the podcast screens against a live server with a Podcasts library, saving a screenshot at each stop: Podcasts,
/// Browse, a show from the charts (added to the library and followed, which a test server doesn't mind), an episode
/// playing, Now Playing with a podcast's controls, Downloads once the phone has fetched the show's newest episodes, and
/// the Podcasts settings.
/// Same environment as `ScreenshotTour`; plays with `-FlexarrNoHistory`.
@MainActor
final class PodcastTour: XCTestCase {
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

    func testPodcasts() async throws {
        guard try await client.podcastStatus()?.available == true else { throw XCTSkip("no Podcasts library") }
        app.tabBars.buttons["Libraries"].firstMatch.tap()
        pause(2)
        let podcasts = app.buttons["Podcasts"].firstMatch
        XCTAssertTrue(podcasts.waitForExistence(timeout: 15), "Libraries should offer Podcasts")
        podcasts.tap()
        pause(4)
        shot("40-podcasts")
        app.swipeUp()
        pause(2)
        shot("40b-podcasts-new-episodes")
        app.swipeDown()
        pause(1)

        app.buttons["Browse"].firstMatch.tap()
        pause(6)
        shot("41-browse")

        // The first show in the charts.
        let show = app.scrollViews.buttons.element(boundBy: 0)
        XCTAssertTrue(show.waitForExistence(timeout: 10), "the charts should list shows")
        show.tap()
        pause(8)
        shot("42-show")

        let follow = app.buttons["Follow"].firstMatch
        if follow.exists {
            follow.tap()
            pause(3)
        }
        shot("43-show-following")

        // Swiping an episode right marks it played: it leaves the Unplayed list.
        let row = app.cells.containing(NSPredicate(format: "label BEGINSWITH 'Play '")).firstMatch
        XCTAssertTrue(row.waitForExistence(timeout: 10), "episodes should be listed")
        let before = row.buttons.matching(NSPredicate(format: "label BEGINSWITH 'Play '")).firstMatch.label
        // A full swipe: from the left of the row's title to its far right.
        row.coordinate(withNormalizedOffset: CGVector(dx: 0.15, dy: 0.3))
            .press(forDuration: 0.05, thenDragTo: row.coordinate(withNormalizedOffset: CGVector(dx: 0.98, dy: 0.3)), withVelocity: .fast, thenHoldForDuration: 0)
        pause(4)
        shot("43b-swiped-played")
        XCTAssertFalse(app.buttons[before].exists, "a played episode leaves the Unplayed list")

        // The ⋯ menu: oldest first, then back.
        app.buttons["More"].firstMatch.tap()
        pause(1)
        shot("43c-show-menu")
        app.buttons["Oldest first"].firstMatch.tap()
        pause(4)
        shot("43d-oldest-first")
        app.buttons["More"].firstMatch.tap()
        pause(1)
        app.buttons["Newest first"].firstMatch.tap()
        pause(4)

        // Up Next from an episode's menu.
        let episodeRow = app.cells.containing(NSPredicate(format: "label BEGINSWITH 'Play '")).element(boundBy: 1)
        episodeRow.press(forDuration: 1.2)
        pause(1)
        shot("43e-episode-menu")
        app.buttons["Add to Up next"].firstMatch.tap()
        pause(2)

        let play = app.buttons.matching(NSPredicate(format: "label BEGINSWITH 'Play '")).firstMatch
        XCTAssertTrue(play.waitForExistence(timeout: 10), "episodes should have a Play button")
        play.tap()
        pause(8)
        shot("44-episode-playing")

        let bar = app.descendants(matching: .any).matching(NSPredicate(format: "label BEGINSWITH 'Now playing'")).firstMatch
        XCTAssertTrue(bar.waitForExistence(timeout: 5), "the now-playing bar should be up")
        bar.buttons.firstMatch.tap()
        pause(3)
        app.buttons["Forward 30 seconds"].firstMatch.tap()
        pause(3)
        shot("45-now-playing-podcast")
        app.buttons["Pause"].firstMatch.tap()
        pause(1)
        app.swipeDown(velocity: .fast)
        pause(2)

        // Following a show fetches its newest episodes to the phone by themselves (the simulator counts as Wi-Fi).
        app.tabBars.buttons["Downloads"].firstMatch.tap()
        pause(25)
        shot("46-downloads")

        // The Podcasts settings, on the server for the profile.
        app.tabBars.buttons["Settings"].firstMatch.tap()
        pause(2)
        app.collectionViews.buttons["Podcasts"].firstMatch.tap()
        pause(1)
        shot("47-settings-podcasts")
        app.navigationBars.buttons.element(boundBy: 0).tap()

        // Back on Podcasts, Up Next has the episode added from the menu.
        app.tabBars.buttons["Libraries"].firstMatch.tap()
        pause(2)
        // The show was opened from Browse: back twice.
        app.navigationBars.buttons.element(boundBy: 0).tap()
        pause(2)
        app.navigationBars.buttons.element(boundBy: 0).tap()
        pause(4)
        shot("48-up-next")
        XCTAssertTrue(app.staticTexts["Up next"].exists, "Up next shows on Podcasts")
    }

    /// Turns on notifications in Settings (new episodes come with them), allowing them when the system asks.
    func testNotificationsSwitch() async throws {
        guard try await client.podcastStatus()?.available == true else { throw XCTSkip("no Podcasts library") }
        app.tabBars.buttons["Settings"].firstMatch.tap()
        pause(2)
        app.collectionViews.buttons["Notifications"].firstMatch.tap()
        pause(1)
        let toggle = app.switches["Notifications"].firstMatch
        XCTAssertTrue(toggle.exists, "Settings offers notifications")
        if (toggle.value as? String) != "1" { toggle.switches.firstMatch.tap() }
        let springboard = XCUIApplication(bundleIdentifier: "com.apple.springboard")
        let allow = springboard.buttons["Allow"]
        if allow.waitForExistence(timeout: 5) { allow.tap() }
        pause(2)
        shot("49-notifications-on")
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
