import FlexarrKit
import XCTest

/// Walks Live TV against a server with Live TV set up, saving a screenshot at each stop: the channel list from
/// Libraries, a channel playing, and the next channel. Same environment as `ScreenshotTour`.
@MainActor
final class LiveTvTour: XCTestCase {
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

    /// Choosing Record on a later programme in the guide: the cell should show it straight away.
    func testRecordFromGuide() async throws {
        guard try await client.liveTvStatus()?.configured == true else { throw XCTSkip("no Live TV source") }
        app.tabBars.buttons["Libraries"].firstMatch.tap()
        pause(2)
        app.buttons["Live TV"].firstMatch.tap()
        pause(3)
        if app.buttons["Guide"].firstMatch.waitForExistence(timeout: 3) { app.buttons["Guide"].firstMatch.tap() }
        pause(4)
        // The next programme on the first row (what's on now would play).
        let next = try await client.liveChannels().first { $0.name == "BBC One" }?.next?.title ?? ""
        let later = app.buttons.matching(NSPredicate(format: "label BEGINSWITH %@", "BBC One, \(next)")).firstMatch
        print("CELL[before]: \(later.label)")
        later.tap()
        pause(1.5)
        shot("80-details")
        app.alerts.buttons["Record"].firstMatch.tap()
        pause(3)
        shot("81-after-record")
        print("CELL[after]: \(app.buttons.matching(NSPredicate(format: "label BEGINSWITH 'BBC One, ' AND label CONTAINS 'set to record'")).count) marked")
    }

    func testLiveTv() async throws {
        let channels = try await client.liveChannels()
        guard try await client.liveTvStatus()?.configured == true, let first = channels.first(where: \.isFavourite) ?? channels.first else {
            throw XCTSkip("no Live TV channels")
        }
        shot("49-home")
        app.tabBars.buttons["Libraries"].firstMatch.tap()
        pause(2)
        app.buttons["Live TV"].firstMatch.tap()
        pause(4)
        shot("50-live-tv")

        // Recordings, then back.
        let recordingsButton = app.buttons["Recordings"].firstMatch
        if recordingsButton.waitForExistence(timeout: 3) {
            recordingsButton.tap()
            pause(3)
            shot("50a-recordings")
            app.navigationBars.buttons.element(boundBy: 0).tap()
            pause(2)
        }

        // The guide (holding a programme for its record options, not chosen), then back to the list.
        let guideButton = app.buttons["Guide"].firstMatch
        if guideButton.waitForExistence(timeout: 3) {
            guideButton.tap()
            pause(4)
            shot("50b-live-guide")
            let later = app.buttons.matching(NSPredicate(format: "label CONTAINS ', ' AND NOT label CONTAINS 'Favourites'")).element(boundBy: 3)
            if later.exists {
                later.press(forDuration: 1.2)
                pause(1.5)
                shot("50d-record-menu")
                app.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.08)).tap()
                pause(1)
            }
            app.swipeLeft()
            pause(1.5)
            shot("50c-live-guide-later")
            app.buttons["List"].firstMatch.tap()
            pause(2)
        }

        // A row reads as one element: the channel's number, name and what's on.
        app.descendants(matching: .any).matching(NSPredicate(format: "label CONTAINS %@", first.name)).firstMatch.tap()
        pause(9)
        shot("51-live-playing")
        let next = app.buttons["Next channel"].firstMatch
        if !next.isHittable { app.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.5)).tap() }
        pause(1)
        shot("52-live-controls")
        let list = app.buttons["Channels"].firstMatch
        if list.waitForExistence(timeout: 3) {
            list.tap()
            pause(3)
            shot("52b-live-channel-list")
            app.buttons["Done"].firstMatch.tap()
            pause(1.5)
            if !next.isHittable { app.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.5)).tap() }
        }
        if next.waitForExistence(timeout: 3) {
            next.tap()
            pause(9)
            shot("53-live-next-channel")
        }
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
