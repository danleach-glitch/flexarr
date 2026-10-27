import FlexarrKit
import XCTest

/// Drives Live TV with the Siri Remote against a server with Live TV set up, saving a screenshot at each stop: the
/// tab, a channel playing, the next channel (the remote's bottom edge) and back. Same environment as `ScreenshotTour`.
@MainActor
final class LiveTvTour: XCTestCase {
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

    func testLiveTv() async throws {
        guard try await client.liveTvStatus()?.configured == true else { throw XCTSkip("no Live TV source") }
        shot("49-home")
        // Opened from the sidebar, focus is on the first category; then down past Recordings to All channels, across
        // to the channels, and down a few.
        app.openSidebarRow("Live TV")
        shot("50-live-tv")
        shot("51-live-tv-categories")
        for _ in 0..<3 where !focused(prefix: "All channels") { press(.down, wait: 2) }
        shot("51b-live-tv-all")
        press(.right, wait: 2)
        press(.down, times: 3, wait: 0.8)
        pause(1)
        shot("51c-live-tv-list")

        press(.select, wait: 9)
        shot("52-live-playing")
        // Controls hidden: the bottom edge changes channel.
        pause(6)
        press(.down, wait: 10)
        shot("53-live-next-channel")
        press(.select, wait: 1)
        press(.select, wait: 1.5)
        shot("54-live-controls")
        press(.menu, wait: 1.5)
        press(.menu, wait: 3)
        shot("55-live-back")
    }

    /// The guide: across from the categories, then around the grid, and a programme that's on now plays.
    func testGuide() async throws {
        guard try await client.liveTvStatus()?.configured == true else { throw XCTSkip("no Live TV source") }
        // Opened from the sidebar, focus is on the first category; across into the guide, what's on now on the first
        // channel plays.
        app.openSidebarRow("Live TV")
        logFocus("categories")
        shot("60-guide")
        press(.right, wait: 2)
        shot("61-guide-focused")
        press(.select, wait: 2)
        // A channel can take a while to start (one connection); the player's Guide button means it's up.
        _ = app.buttons["Guide"].waitForExistence(timeout: 40)
        pause(2)
        shot("64-guide-play")
        logFocus("player start")
        // The guide over the picture: the controls are up as it starts, on the timeline; up to the buttons (Channels), across to Guide.
        press(.up, wait: 1)
        logFocus("player down")
        for _ in 0..<4 where !focused("Guide") { press(.right, wait: 0.6) }
        logFocus("player menus")
        press(.select, wait: 5)
        shot("65-player-guide")
        press(.down, times: 2, wait: 1)
        shot("66-player-guide-moved")
        press(.menu, wait: 2)
        shot("67-player-after-guide")
        press(.menu, wait: 1.5)
        press(.menu, wait: 3)
        // Around the grid: later, then back again (focus stays in the grid).
        press(.right, times: 3, wait: 1)
        press(.down, times: 2, wait: 1)
        shot("62-guide-moved")
        press(.right, times: 6, wait: 1)
        pause(1.5)
        shot("63-guide-later")
        press(.left, times: 8, wait: 0.8)
        logFocus("after going back")
        shot("68-guide-back")
    }

    /// Recordings in the categories, then holding a later programme in the guide for its record options (not chosen).
    func testRecordings() async throws {
        guard try await client.liveTvStatus()?.configured == true else { throw XCTSkip("no Live TV source") }
        app.openSidebarRow("Live TV")
        for _ in 0..<6 where !focused(prefix: "Recordings") {
            press(.down, wait: 0.6)
        }
        logFocus("recordings")
        pause(3)
        shot("70-recordings")
        // Down to All channels, across into the guide, along to a later programme, and hold it.
        press(.down, wait: 1)
        pause(2)
        press(.right, wait: 2)
        press(.right, times: 3, wait: 1)
        logFocus("later programme")
        XCUIRemote.shared.press(.select, forDuration: 1.6)
        pause(2)
        shot("71-record-menu")
        press(.menu, wait: 1.5)
    }

    private func focused(_ label: String) -> Bool {
        app.buttons.matching(NSPredicate(format: "label == %@ AND hasFocus == true", label)).count > 0
    }

    /// A category's label also carries its count ("All channels, 122").
    private func focused(prefix: String) -> Bool {
        app.buttons.matching(NSPredicate(format: "label BEGINSWITH %@ AND hasFocus == true", prefix)).firstMatch.exists
    }

    private func logFocus(_ stop: String) {
        let element = app.descendants(matching: .any).matching(NSPredicate(format: "hasFocus == true")).firstMatch
        let what = element.exists ? "\(element.elementType.rawValue) '\(element.label)' '\(element.identifier)'" : "nothing"
        print("FOCUS[\(stop)]: \(what)")
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
