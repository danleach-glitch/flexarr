import FlexarrKit
import XCTest

/// A paced walk through the app for the App Store preview video: record the simulator while it runs
/// (`xcrun simctl io <device> recordVideo`). Same environment as `StoreTour`, which names the titles.
@MainActor
final class PreviewTour: XCTestCase {
    private let env = ProcessInfo.processInfo.environment

    func testWalkthrough() throws {
        guard let server = env["FLEXARR_SERVER"], let key = env["FLEXARR_KEY"],
              let address = FlexarrClient.normalizeAddress(server),
              let series = env["FLEXARR_SERIES"], let movie = env["FLEXARR_MOVIE"], let album = env["FLEXARR_ALBUM"] else {
            throw XCTSkip("Set the StoreTour environment to run the walkthrough.")
        }
        XCUIDevice.shared.orientation = .portrait
        let app = XCUIApplication()
        app.launchArguments = ["-FlexarrServer", address.absoluteString, "-FlexarrApiKey", key, "-FlexarrNoHistory", "YES"]
        app.launch()
        pause(8)

        // Home, with a slow scroll through the rows and back.
        let middle = app.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.7))
        middle.press(forDuration: 0.05, thenDragTo: app.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.3)), withVelocity: 600, thenHoldForDuration: 0.1)
        pause(2)
        app.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.3))
            .press(forDuration: 0.05, thenDragTo: middle, withVelocity: 900, thenHoldForDuration: 0.1)
        pause(2)

        open(app, "flexarr://item/\(series)")
        app.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.75))
            .press(forDuration: 0.05, thenDragTo: app.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.35)), withVelocity: 500, thenHoldForDuration: 0.1)
        pause(2.5)

        open(app, "flexarr://item/\(movie)")
        pause(1)

        open(app, "flexarr://item/\(album)")
        app.buttons["Play"].firstMatch.tap()
        pause(3)
        let bar = app.descendants(matching: .any).matching(NSPredicate(format: "label BEGINSWITH 'Now playing'")).firstMatch
        if bar.waitForExistence(timeout: 5) {
            bar.buttons.firstMatch.tap()
            pause(3)
            let lyrics = app.buttons.matching(NSPredicate(format: "label ==[c] 'Lyrics'")).firstMatch
            if lyrics.exists { lyrics.tap(); pause(5) }
            app.buttons["Pause"].firstMatch.tap()
            pause(1)
            app.swipeDown(velocity: .fast)
            pause(1.5)
        }

        let libraries = app.tabBars.buttons["Libraries"].firstMatch
        if libraries.exists {
            libraries.tap()
            pause(1.5)
            app.buttons["Podcasts"].firstMatch.tap()
            pause(5)
        }
    }

    private func open(_ app: XCUIApplication, _ link: String) {
        app.open(URL(string: link)!)
        let allow = XCUIApplication(bundleIdentifier: "com.apple.springboard").buttons["Open"]
        if allow.waitForExistence(timeout: 2) { allow.tap() }
        pause(4)
    }

    private func pause(_ seconds: TimeInterval) {
        Thread.sleep(forTimeInterval: seconds)
    }
}
