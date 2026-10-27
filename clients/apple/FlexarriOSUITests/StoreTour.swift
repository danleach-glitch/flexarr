import FlexarrKit
import XCTest

/// The screens behind the App Store screenshots, on titles you choose. Same environment as `ScreenshotTour`
/// (`-FlexarrNoHistory`, so nothing lands in watch history), plus the titles to show:
///
///     TEST_RUNNER_FLEXARR_SERIES=<id> TEST_RUNNER_FLEXARR_MOVIE=<id> TEST_RUNNER_FLEXARR_EPISODE=<id>
///     TEST_RUNNER_FLEXARR_EPISODE_AT=<seconds> TEST_RUNNER_FLEXARR_ALBUM=<id>
///
/// A test whose title isn't set is skipped.
@MainActor
final class StoreTour: XCTestCase {
    private var app: XCUIApplication!
    private var outputDirectory: URL?
    private var prefix = ""
    private let env = ProcessInfo.processInfo.environment

    override func setUp() async throws {
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
        XCUIDevice.shared.orientation = .portrait
        app = XCUIApplication()
        app.launchArguments = ["-FlexarrServer", address.absoluteString, "-FlexarrApiKey", key, "-FlexarrNoHistory", "YES"]
        app.launch()
        pause(8)
    }

    func test1Home() {
        shot("store-home")
        app.swipeUp()
        pause(3)
        shot("store-home-rows")
    }

    func test2Titles() throws {
        guard env["FLEXARR_SERIES"] != nil || env["FLEXARR_MOVIE"] != nil else { throw XCTSkip("Set TEST_RUNNER_FLEXARR_SERIES or _MOVIE") }
        if let series = env["FLEXARR_SERIES"] {
            open("flexarr://item/\(series)")
            shot("store-series")
            app.swipeUp()
            pause(3)
            shot("store-series-episodes")
        }
        if let movie = env["FLEXARR_MOVIE"] {
            open("flexarr://item/\(movie)")
            shot("store-movie")
        }
    }

    func test3Player() throws {
        guard let episode = env["FLEXARR_EPISODE"] else { throw XCTSkip("Set TEST_RUNNER_FLEXARR_EPISODE") }
        XCUIDevice.shared.orientation = .landscapeLeft
        open("flexarr://play/\(episode)?t=\(env["FLEXARR_EPISODE_AT"] ?? "600")", settle: Double(env["FLEXARR_EPISODE_SETTLE"] ?? "") ?? 14)
        // Pause on the frame, so both shots show the same picture.
        // The first tap shows the controls; the second lands on play/pause, in the middle.
        let middle = app.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.5))
        middle.tap()
        pause(0.8)
        middle.tap()
        pause(1.5)
        shot("store-player")
        app.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.5)).tap()
        pause(1)
        // Closing the player stops the stream on the server.
        let close = app.buttons["Close"].firstMatch
        if !close.isHittable { app.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.5)).tap() }
        if close.waitForExistence(timeout: 3) { close.tap() }
        pause(3)
        XCUIDevice.shared.orientation = .portrait
    }

    func test4Music() throws {
        guard let album = env["FLEXARR_ALBUM"] else { throw XCTSkip("Set TEST_RUNNER_FLEXARR_ALBUM") }
        open("flexarr://item/\(album)")
        shot("store-album")
        app.buttons["Play"].firstMatch.tap()
        pause(5)
        let bar = app.descendants(matching: .any).matching(NSPredicate(format: "label BEGINSWITH 'Now playing'")).firstMatch
        guard bar.waitForExistence(timeout: 5) else { return XCTFail("the now-playing bar should be up") }
        bar.buttons.firstMatch.tap()
        pause(4)
        shot("store-now-playing")
        let lyrics = app.buttons.matching(NSPredicate(format: "label ==[c] 'Lyrics'")).firstMatch
        if lyrics.exists {
            lyrics.tap()
            pause(4)
            shot("store-lyrics")
        }
        app.buttons["Pause"].firstMatch.tap()
        pause(1)
    }

    // MARK: Helpers

    private func open(_ link: String, settle: TimeInterval = 7) {
        app.open(URL(string: link)!)
        // iOS asks before following a link from outside the app.
        let allow = XCUIApplication(bundleIdentifier: "com.apple.springboard").buttons["Open"]
        if allow.waitForExistence(timeout: 3) { allow.tap() }
        pause(settle)
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
