import FlexarrKit
import XCTest

/// Drives "Who's watching?" and the profile button in the tab bar with the Siri Remote against a live server
/// (several profiles needed), saving a screenshot at each stop. Same environment as `ScreenshotTour`.
@MainActor
final class ProfilePickerTour: XCTestCase {
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
        app.launchArguments = ["-FlexarrServer", address.absoluteString, "-FlexarrApiKey", key,
                               "-FlexarrAskProfile", "YES", "-FlexarrNoHistory", "YES"]
        app.launch()
        pause(6)
    }

    /// First launch: every profile is a choice, and left/right moves between them.
    func testFirstLaunchPickerMovesSideways() async throws {
        shot("10-picker-first-launch")
        let tiles = app.buttons.allElementsBoundByIndex
        XCTAssertGreaterThan(tiles.count, 1, "the server needs several profiles")
        logFocus("start")
        let startFocused = focusedLabel()

        press(.right, wait: 1.5)
        shot("11-picker-after-right")
        logFocus("after right")
        let afterRight = focusedLabel()

        press(.left, wait: 1.5)
        shot("12-picker-after-left")
        logFocus("after left")
        let afterLeft = focusedLabel()

        XCTAssertNotEqual(startFocused, afterRight, "right should move focus to the next profile")
        XCTAssertEqual(startFocused, afterLeft, "left should move focus back")
    }

    /// Choose the first profile, reach the profile button at the end of the tab bar, open the picker from it.
    func testCornerButtonAndSwitchPicker() async throws {
        press(.select, wait: 6)
        shot("20-home")
        press(.up, wait: 1.5)
        shot("21-tab-bar")
        // Right past the last tab lands on the profile button.
        press(.right, times: 8, wait: 0.8)
        shot("22-corner-button-focused")
        logFocus("corner")

        press(.select, wait: 3)
        shot("23-switch-picker")
        logFocus("switch picker start")
        let startFocused = focusedLabel()
        XCTAssertNotNil(startFocused)
        // Focus starts on the other profile, which with two profiles is the second tile: left reaches whoever's
        // watching now, right comes back.
        press(.left, wait: 1.5)
        shot("24-switch-picker-after-left")
        logFocus("switch picker after left")
        let afterLeft = focusedLabel()
        press(.right, wait: 1.5)
        shot("25-switch-picker-after-right")
        logFocus("switch picker after right")
        let afterRight = focusedLabel()
        XCTAssertNotEqual(startFocused, afterLeft, "left should reach the profile watching now")
        XCTAssertEqual(startFocused, afterRight, "right should move focus back")
    }

    private func focusedLabel() -> String? {
        app.buttons.allElementsBoundByIndex.first(where: \.hasFocus)?.label
    }

    private func logFocus(_ stop: String) {
        let states = app.buttons.allElementsBoundByIndex.map { "\($0.label)=\($0.hasFocus)" }
        print("FOCUS[\(stop)]: \(states.joined(separator: " | "))")
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
