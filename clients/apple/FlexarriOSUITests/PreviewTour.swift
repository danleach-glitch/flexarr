import FlexarrKit
import XCTest

/// A paced walk through the app for the App Store preview video: record the simulator while it runs
/// (`xcrun simctl io <device> recordVideo`). Same environment as `StoreTour`, which names the titles and,
/// with `FLEXARR_PROFILE`, the profile to show.
@MainActor
final class PreviewTour: XCTestCase {
    private let env = ProcessInfo.processInfo.environment
    private var app: XCUIApplication!

    func testWalkthrough() throws {
        guard let server = env["FLEXARR_SERVER"], let key = env["FLEXARR_KEY"],
              let address = FlexarrClient.normalizeAddress(server),
              let series = env["FLEXARR_SERIES"], let movie = env["FLEXARR_MOVIE"], let album = env["FLEXARR_ALBUM"] else {
            throw XCTSkip("Set the StoreTour environment to run the walkthrough.")
        }
        XCUIDevice.shared.orientation = .portrait
        app = XCUIApplication()
        app.launchArguments = ["-FlexarrServer", address.absoluteString, "-FlexarrApiKey", key, "-FlexarrNoHistory", "YES"]
        if env["FLEXARR_PROFILE"] != nil { app.launchArguments += ["-FlexarrAskProfile", "YES"] }
        app.launch()
        chooseProfile()
        pause(6)

        // Home: the hero, then a slow scroll down to the genre rows.
        pause(3)
        drag(from: 0.8, to: 0.25, velocity: 500)
        pause(2.5)
        drag(from: 0.8, to: 0.45, velocity: 400)
        pause(2.5)

        open("flexarr://item/\(series)")
        drag(from: 0.75, to: 0.4, velocity: 450)
        pause(2.5)

        open("flexarr://item/\(movie)")
        pause(1)

        // Live TV's guide.
        tab("Libraries")
        pause(1.5)
        app.buttons["Live TV"].firstMatch.tap()
        pause(2)
        if app.buttons["Guide"].firstMatch.waitForExistence(timeout: 3) { app.buttons["Guide"].firstMatch.tap() }
        pause(4)

        // An album playing, in Now Playing.
        open("flexarr://item/\(album)")
        app.buttons["Play"].firstMatch.tap()
        pause(2.5)
        let bar = app.descendants(matching: .any).matching(NSPredicate(format: "label BEGINSWITH 'Now playing'")).firstMatch
        if bar.waitForExistence(timeout: 5) {
            bar.buttons.firstMatch.tap()
            pause(4)
            app.buttons["Pause"].firstMatch.tap()
            pause(0.5)
            app.swipeDown(velocity: .fast)
            pause(1.5)
        }

        tab("Libraries")
        pause(1.5)
        app.buttons["Podcasts"].firstMatch.tap()
        pause(5)
    }

    private func drag(from start: CGFloat, to end: CGFloat, velocity: XCUIGestureVelocity) {
        app.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: start))
            .press(forDuration: 0.05, thenDragTo: app.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: end)),
                   withVelocity: velocity, thenHoldForDuration: 0.3)
    }

    private func open(_ link: String) {
        app.open(URL(string: link)!)
        let allow = XCUIApplication(bundleIdentifier: "com.apple.springboard").buttons["Open"]
        if allow.waitForExistence(timeout: 2) { allow.tap() }
        // Opening a link brings the app forward, which asks who's watching again.
        chooseProfile()
        pause(4)
    }

    /// Picks `FLEXARR_PROFILE` in "Who's watching?", if it's showing.
    private func chooseProfile() {
        guard let profile = env["FLEXARR_PROFILE"] else { return }
        let choice = app.buttons.matching(NSPredicate(format: "label BEGINSWITH %@", profile)).firstMatch
        guard choice.waitForExistence(timeout: 6) else { return }
        let picker = app.staticTexts["Who’s watching?"].firstMatch
        for _ in 0..<5 where choice.exists {
            pause(1.5)
            if choice.isHittable { choice.tap() }
            pause(1.5)
            if !picker.exists { break }
        }
    }

    private func tab(_ label: String) {
        let byLabel = NSPredicate(format: "label == %@", label)
        let inTabBar = app.tabBars.buttons.matching(byLabel).firstMatch
        (inTabBar.exists ? inTabBar : app.buttons.matching(byLabel).firstMatch).tap()
    }

    private func pause(_ seconds: TimeInterval) {
        Thread.sleep(forTimeInterval: seconds)
    }
}
