import FlexarrKit
import XCTest

/// The screens behind the App Store screenshots, on titles you choose. Same environment as `ScreenshotTour`
/// (`-FlexarrNoHistory`, so nothing lands in watch history), plus the titles to show:
///
///     TEST_RUNNER_FLEXARR_SERIES=<id> TEST_RUNNER_FLEXARR_MOVIE=<id> TEST_RUNNER_FLEXARR_EPISODE=<id>
///     TEST_RUNNER_FLEXARR_EPISODE_AT=<seconds> TEST_RUNNER_FLEXARR_ALBUM=<id>
///
/// `TEST_RUNNER_FLEXARR_PROFILE=<name>` picks that profile at launch (one without a PIN), so the shots can come from
/// a profile set up for them rather than someone's own history.
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
        if env["FLEXARR_PROFILE"] != nil {
            app.launchArguments += ["-FlexarrAskProfile", "YES"]
            app.launch()
            chooseProfile()
        } else {
            app.launch()
        }
        pause(8)
    }

    func test1Home() {
        shot("store-home")
        // Scroll a little at a time until the row named by TEST_RUNNER_FLEXARR_HOME_ROW is near the top.
        let screen = app.windows.firstMatch.frame
        if let row = env["FLEXARR_HOME_ROW"] {
            let title = app.staticTexts[row].firstMatch
            for _ in 0..<25 {
                if title.exists, title.frame.minY > 0, title.frame.minY < screen.height * 0.16 { break }
                let step = title.exists && title.frame.minY < screen.height * 0.5 ? 0.12 : 0.3
                app.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.7))
                    .press(forDuration: 0.05, thenDragTo: app.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.7 - step)),
                           withVelocity: 300, thenHoldForDuration: 0.6)
                pause(0.8)
            }
        } else {
            app.swipeUp()
        }
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
        if let wait = env["FLEXARR_EPISODE_WAIT"].flatMap(Double.init) {
            // Play from the title's page, so it plays as the chosen profile (a play link can start before the
            // profile is picked), then wait until the moment wanted.
            open("flexarr://item/\(episode)")
            app.buttons.matching(NSPredicate(format: "label BEGINSWITH 'Play'")).firstMatch.tap()
            XCUIDevice.shared.orientation = .landscapeLeft
            pause(wait)
        } else {
            XCUIDevice.shared.orientation = .landscapeLeft
            open("flexarr://play/\(episode)?t=\(env["FLEXARR_EPISODE_AT"] ?? "600")", settle: Double(env["FLEXARR_EPISODE_SETTLE"] ?? "") ?? 14)
        }
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

    func test5LiveTv() throws {
        guard env["FLEXARR_LIVE"] != nil else { throw XCTSkip("Set TEST_RUNNER_FLEXARR_LIVE") }
        tab("Libraries")
        pause(2)
        app.buttons["Live TV"].firstMatch.tap()
        pause(4)
        shot("store-live")
        let guide = app.buttons["Guide"].firstMatch
        if guide.waitForExistence(timeout: 3) {
            guide.tap()
            pause(5)
            shot("store-guide")
            app.buttons["List"].firstMatch.tap()
            pause(2)
        }
        let recordings = app.buttons["Recordings"].firstMatch
        if recordings.waitForExistence(timeout: 3) {
            recordings.tap()
            pause(3)
            shot("store-recordings")
        }
    }

    func test6Podcasts() throws {
        guard env["FLEXARR_PODCASTS"] != nil else { throw XCTSkip("Set TEST_RUNNER_FLEXARR_PODCASTS") }
        tab("Libraries")
        pause(2)
        app.buttons["Podcasts"].firstMatch.tap()
        pause(8)
        shot("store-podcasts")
    }

    /// Downloads a title or two (`TEST_RUNNER_FLEXARR_DOWNLOADS`, ids separated by commas), then shows Downloads.
    func test7Downloads() throws {
        guard let ids = env["FLEXARR_DOWNLOADS"] else { throw XCTSkip("Set TEST_RUNNER_FLEXARR_DOWNLOADS") }
        for id in ids.split(separator: ",") {
            open("flexarr://item/\(id)")
            let download = app.buttons["Download"].firstMatch
            if download.waitForExistence(timeout: 5) {
                download.tap()
                pause(2)
                // Some titles ask which version or quality first.
                let confirm = app.buttons.matching(NSPredicate(format: "label BEGINSWITH 'Download'")).element(boundBy: 1)
                if confirm.exists { confirm.tap() }
            }
            _ = app.buttons["Downloaded"].firstMatch.waitForExistence(timeout: 900)
        }
        tab("Downloads")
        pause(3)
        // A download that failed has no place in the picture: swipe it away.
        for _ in 0..<3 {
            let failed = app.descendants(matching: .any).matching(NSPredicate(format: "label CONTAINS[c] %@", "didn")).firstMatch
            guard failed.exists else { break }
            failed.swipeLeft()
            let delete = app.buttons.matching(NSPredicate(format: "label ==[c] 'Delete' OR label ==[c] 'Remove'")).firstMatch
            if delete.waitForExistence(timeout: 2) { delete.tap() }
            pause(1.5)
        }
        shot("store-downloads")
    }

    // MARK: Helpers

    /// Tabs are found by their label: in the tab bar on iPhone, the bar along the top on iPad.
    private func tab(_ label: String) {
        let byLabel = NSPredicate(format: "label == %@", label)
        let inTabBar = app.tabBars.buttons.matching(byLabel).firstMatch
        (inTabBar.exists ? inTabBar : app.buttons.matching(byLabel).firstMatch).tap()
    }

    private func open(_ link: String, settle: TimeInterval = 7) {
        app.open(URL(string: link)!)
        // iOS asks before following a link from outside the app.
        let allow = XCUIApplication(bundleIdentifier: "com.apple.springboard").buttons["Open"]
        if allow.waitForExistence(timeout: 3) { allow.tap() }
        // Opening a link relaunches the app, which asks who's watching again.
        chooseProfile()
        pause(settle)
    }

    /// Picks `FLEXARR_PROFILE` in "Who's watching?", if it's showing. The picker can take a moment to accept taps.
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
