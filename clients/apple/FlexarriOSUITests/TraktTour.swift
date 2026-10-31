import FlexarrKit
import XCTest

/// Settings › Trakt on iPhone, against a server that offers Trakt (a stand-in Trakt is enough), saving a screenshot at
/// each stop. It connects, imports and disconnects nothing; the preview it opens changes nothing either.
/// Environment: TEST_RUNNER_FLEXARR_SERVER, TEST_RUNNER_FLEXARR_KEY, and TEST_RUNNER_FLEXARR_SCREENSHOTS for a folder.
@MainActor
final class TraktTour: XCTestCase {
    private let env = ProcessInfo.processInfo.environment
    private var app: XCUIApplication!

    func testTraktPage() throws {
        guard let server = env["FLEXARR_SERVER"], let key = env["FLEXARR_KEY"], let address = FlexarrClient.normalizeAddress(server) else {
            throw XCTSkip("Set TEST_RUNNER_FLEXARR_SERVER and TEST_RUNNER_FLEXARR_KEY to run the tour.")
        }
        XCUIDevice.shared.orientation = .portrait
        app = XCUIApplication()
        app.launchArguments = ["-FlexarrServer", address.absoluteString, "-FlexarrApiKey", key, "-FlexarrNoHistory", "YES", "-FlexarrWelcomed", "YES"]
        app.launch()
        Thread.sleep(forTimeInterval: 6)

        let byLabel = NSPredicate(format: "label == %@", "Settings")
        let inTabBar = app.tabBars.buttons.matching(byLabel).firstMatch
        (inTabBar.exists ? inTabBar : app.buttons.matching(byLabel).firstMatch).tap()
        Thread.sleep(forTimeInterval: 3)
        let trakt = app.buttons.containing(NSPredicate(format: "label BEGINSWITH %@", "Trakt")).firstMatch
        guard trakt.waitForExistence(timeout: 10) else { throw XCTSkip("This server doesn't offer Trakt.") }
        shot("50-settings")
        trakt.tap()
        Thread.sleep(forTimeInterval: 4)
        shot("51-trakt")
        let entry = app.buttons["Bring your Trakt history across"]
        if entry.exists {
            entry.tap()
            Thread.sleep(forTimeInterval: 4)
            shot("52-trakt-preview")
        }
        app.swipeUp()
        Thread.sleep(forTimeInterval: 1.5)
        shot("53-trakt-lists")
    }

    private func shot(_ name: String) {
        let screenshot = XCUIScreen.main.screenshot()
        let attachment = XCTAttachment(screenshot: screenshot)
        attachment.name = name
        attachment.lifetime = .keepAlways
        add(attachment)
        if let path = env["FLEXARR_SCREENSHOTS"] {
            try? FileManager.default.createDirectory(atPath: path, withIntermediateDirectories: true)
            try? screenshot.pngRepresentation.write(to: URL(fileURLWithPath: path).appendingPathComponent("\(name).png"))
        }
    }
}
