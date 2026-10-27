import FlexarrKit
import XCTest

/// The sidebar with the Siri Remote: the profile at the top, a long list of libraries, opening a title in one and
/// coming back. It presses Select on whatever has focus (tvOS doesn't say which sidebar row that is), so run it
/// against a test server with films in a library, not a real one: FLEXARR_LIBRARY_ROW is how many rows below Home
/// that library is.
///
///     TEST_RUNNER_FLEXARR_SERVER=http://localhost:8395 TEST_RUNNER_FLEXARR_KEY=... TEST_RUNNER_FLEXARR_LIBRARY_ROW=8 \
///     TEST_RUNNER_FLEXARR_SCREENSHOTS=/tmp/flexarr-shots \
///     xcodebuild test -project Flexarr.xcodeproj -scheme FlexarrTV -only-testing:FlexarrTVUITests/SidebarTour \
///       -destination 'platform=tvOS Simulator,name=Apple TV 4K (3rd generation) (at 1080p)'
@MainActor
final class SidebarTour: XCTestCase {
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
        app.launchArguments = ["-FlexarrServer", address.absoluteString, "-FlexarrApiKey", key, "-FlexarrNoHistory", "YES",
                               "-FlexarrLibraryOpensOn", "all"]
        app.launch()
        pause(6)
    }

    func testSidebar() throws {
        shot("60-sidebar-launch")

        // Focus passing over the profile mustn't open "Who's watching?"; selecting it does.
        press(.up, wait: 1.5)
        shot("61-profile-focused")
        XCTAssertFalse(app.staticTexts.matching(NSPredicate(format: "label BEGINSWITH 'Who'")).firstMatch.exists, "focusing the profile shouldn't open the picker")
        press(.select, wait: 2)
        shot("62-profile-selected")
        let picker = app.staticTexts.matching(NSPredicate(format: "label BEGINSWITH 'Who'")).firstMatch.exists
        press(.menu, wait: 2)
        XCTAssertTrue(picker, "selecting the profile should open the picker")
        shot("63-picker-closed")
        // Left from a screen opens the sidebar again.
        press(.left, wait: 1.5)
        shot("63b-left-opens-sidebar")

        // Down to the library, which opens on All, then right (onto the Browse · All switch) and down onto its first
        // poster.
        press(.down, times: Int(env("FLEXARR_LIBRARY_ROW") ?? "1") ?? 1, wait: 0.6)
        shot("64-library-focused")
        press(.select, wait: 3)
        press(.right, wait: 1.5)
        press(.down, wait: 1.5)
        shot("65-library-grid")
        let play = app.buttons["Play"]
        XCTAssertFalse(play.exists && play.hasFocus, "focus should be on a poster")

        // Opening a title pushes its details inside the sidebar's screen.
        press(.select, wait: 4)
        shot("66-title-details")
        XCTAssertEqual(app.state, .runningForeground, "opening a title shouldn't crash")
        XCTAssertTrue(app.buttons["Play"].exists, "the title's details should be up")
        press(.menu, wait: 2)
        shot("67-back-to-library")
        press(.menu, wait: 2)
        shot("68-menu-to-sidebar")
        XCTAssertEqual(app.state, .runningForeground)

        // Search and Settings sit below the libraries.
        press(.down, times: 12, wait: 0.5)
        shot("69-sidebar-bottom")
    }

    private func env(_ name: String) -> String? {
        ProcessInfo.processInfo.environment[name]
    }

    private func press(_ button: XCUIRemote.Button, times: Int = 1, wait: TimeInterval) {
        for _ in 0..<times {
            remote.press(button)
            pause(wait)
        }
    }

    private func pause(_ seconds: TimeInterval) {
        RunLoop.current.run(until: Date().addingTimeInterval(seconds))
    }

    private func shot(_ name: String) {
        let screenshot = XCUIScreen.main.screenshot()
        let attachment = XCTAttachment(screenshot: screenshot)
        attachment.name = name
        attachment.lifetime = .keepAlways
        add(attachment)
        guard let outputDirectory else { return }
        try? screenshot.pngRepresentation.write(to: outputDirectory.appendingPathComponent("\(name).png"))
    }
}
