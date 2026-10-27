import FlexarrKit
import XCTest

/// Opens Settings from the sidebar and walks each of its pages with the Siri Remote, saving a screenshot at each
/// stop. Same environment as `ScreenshotTour`; the server needs several profiles for the Profiles page. It
/// changes nothing on the server: the profile settings it opens are left as they were.
@MainActor
final class SettingsTour: XCTestCase {
    private var app: XCUIApplication!
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
        app = XCUIApplication()
        app.launchArguments = ["-FlexarrServer", address.absoluteString, "-FlexarrApiKey", key, "-FlexarrNoHistory", "YES"]
        app.launch()
        pause(6)
    }

    func testSettingsPages() throws {
        openSettings()
        shot("30-settings")
        XCTAssertTrue(row("Playback").exists, "Settings should list Playback")
        XCTAssertTrue(row("Sign out").exists, "Settings should end with Sign out")

        // Down from the profile row to Playback, in.
        focus("Playback")
        shot("31-settings-playback-focused")
        press(.select, wait: 2.5)
        shot("32-playback")
        focus("Subtitle size")
        shot("33-playback-subtitle-size-focused")
        press(.select, wait: 2)
        shot("34-subtitle-size-choices")
        press(.menu, wait: 2)
        focus("Lossless surround")
        shot("35-playback-this-apple-tv")
        press(.menu, wait: 2)

        focus("Home screen")
        press(.select, wait: 2)
        shot("36-home-screen")
        press(.menu, wait: 2)

        if row("Profiles").exists {
            focus("Profiles")
            press(.select, wait: 2)
            focus("Ask who's watching")
            shot("37-profiles")
            press(.menu, wait: 2)
        }

        focus("Server")
        press(.select, wait: 3)
        shot("38-server")
        press(.menu, wait: 2)

        focus("Sign out")
        shot("39-sign-out-focused")
    }

    /// Selects Settings in the sidebar, which puts focus on the page's first row.
    private func openSettings() {
        app.openSidebarRow("Settings", wait: 2)
    }

    /// A row by its title: a row's label also carries its value ("Playback, Files play directly").
    private func row(_ title: String) -> XCUIElement {
        app.buttons.matching(NSPredicate(format: "label BEGINSWITH %@", title)).firstMatch
    }

    /// Moves focus down (or up, if it's above) until the row with this title has it.
    private func focus(_ title: String) {
        let row = self.row(title)
        XCTAssertTrue(row.waitForExistence(timeout: 3), "no row \(title)")
        for _ in 0..<12 where !row.hasFocus {
            press(focusedFrame().minY < row.frame.minY ? .down : .up, wait: 0.8)
        }
        XCTAssertTrue(row.hasFocus, "\(title) should have focus")
        pause(0.6)
    }

    private func focusedFrame() -> CGRect {
        app.buttons.allElementsBoundByIndex.first(where: \.hasFocus)?.frame ?? .zero
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
