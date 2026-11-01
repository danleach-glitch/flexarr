import FlexarrKit
import XCTest

/// The same eight screens on every client, for a before-and-after comparison of how the apps look side by side:
/// 01-home, 02-library, 03-movie, 04-show, 05-player, 06-search, 07-settings, 08-profiles. It finds its titles on any
/// server (the first title in the first movie library and in the first TV library), plays with `-FlexarrNoHistory` so
/// watch history is untouched, and closes the player straight away. It uses only what the app has had for a while, so
/// it runs on main as well as on a branch.
///
///     TEST_RUNNER_FLEXARR_SERVER=http://192.168.1.20:8383 TEST_RUNNER_FLEXARR_KEY=... \
///     TEST_RUNNER_PARITY_SHOTS_DIR=/tmp/parity \
///     xcodebuild test -project Flexarr.xcodeproj -scheme FlexarriOS -destination 'platform=iOS Simulator,name=iPhone Air' \
///       -only-testing:FlexarriOSUITests/ParityTour
///
/// Shots are saved as `iphone-<nn>-<name>.png` in PARITY_SHOTS_DIR, and attached to the test result too.
@MainActor
final class ParityTour: XCTestCase {
    private var app: XCUIApplication!
    private var client: FlexarrClient!
    private var directory: URL?
    private var arguments: [String] = []

    override func setUp() async throws {
        let env = ProcessInfo.processInfo.environment
        guard let server = env["FLEXARR_SERVER"], let key = env["FLEXARR_KEY"],
              let address = FlexarrClient.normalizeAddress(server) else {
            throw XCTSkip("Set TEST_RUNNER_FLEXARR_SERVER and TEST_RUNNER_FLEXARR_KEY to run the tour.")
        }
        if let path = env["PARITY_SHOTS_DIR"] {
            directory = URL(fileURLWithPath: path)
            try? FileManager.default.createDirectory(at: directory!, withIntermediateDirectories: true)
        }
        client = FlexarrClient(baseURL: address, apiKey: key)
        XCUIDevice.shared.orientation = .portrait
        arguments = ["-FlexarrServer", address.absoluteString, "-FlexarrApiKey", key, "-FlexarrNoHistory", "YES",
                     "-FlexarrWelcomed", "YES"]
        app = XCUIApplication()
        app.launchArguments = arguments
        app.launch()
        pause(7)
        // A run that ended on the profile picker leaves the app asking who's watching: the main profile watches.
        if app.staticTexts["Who’s watching?"].exists, let main = try await client.profiles().first(where: \.isMain) {
            app.buttons[main.name].firstMatch.tap()
            pause(6)
        }
    }

    func testParityTour() async throws {
        let libraries = try await client.libraries()
        let movies = libraries.first { $0.type == .movies }
        let shows = libraries.first { $0.type == .tvShows }
        var movie: ItemSummary?
        var show: ItemSummary?
        if let movies { movie = try await client.libraryItems(movies.id, take: 1).items.first }
        if let shows { show = try await client.libraryItems(shows.id, take: 1).items.first }

        shot("01-home")

        if let movies {
            tab("Libraries")
            pause(2)
            let row = app.buttons.matching(NSPredicate(format: "label BEGINSWITH %@", movies.name)).firstMatch
            if row.waitForExistence(timeout: 4) { row.tap() } else { app.staticTexts[movies.name].firstMatch.tap() }
            pause(5)
            shot("02-library")
            tab("Home")
            pause(1)
        }

        if let movie {
            open("flexarr://item/\(movie.id)", wait: 6)
            shot("03-movie")
        }
        if let show {
            open("flexarr://item/\(show.id)", wait: 6)
            shot("04-show")
        }

        if let movie {
            // Part way in, controls up, then closed straight away (history is off: the server's watch state stays).
            open("flexarr://play/\(movie.id)?t=600", wait: 10)
            app.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.5)).tap()
            pause(1)
            shot("05-player")
            let close = app.buttons["Close"].firstMatch
            if !close.isHittable { app.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.5)).tap() }
            if close.waitForExistence(timeout: 3) { close.tap() }
            pause(3)
        }

        tab("Settings")
        pause(3)
        shot("07-settings")

        // Search last: on iOS 26 it turns the tab bar into its search field.
        tab("Search")
        pause(2)
        if app.searchFields.firstMatch.waitForExistence(timeout: 3) {
            // A fresh simulator's keyboard first explains slide-to-type.
            let tip = app.buttons["Continue"].firstMatch
            if tip.waitForExistence(timeout: 2) { tip.tap(); pause(1) }
            app.typeText(movie.map { String($0.title.prefix(4)) } ?? "the")
            pause(4)
        }
        shot("06-search")

        // The profile picker, as the app opens when it asks who's watching.
        app.terminate()
        app.launchArguments = arguments + ["-FlexarrAskProfile", "YES"]
        app.launch()
        pause(7)
        shot("08-profiles")
    }

    // MARK: Helpers

    private func open(_ link: String, wait: TimeInterval) {
        app.open(URL(string: link)!)
        // iOS may ask before following a link from outside the app.
        let allow = XCUIApplication(bundleIdentifier: "com.apple.springboard").buttons["Open"]
        if allow.waitForExistence(timeout: 2) { allow.tap() }
        pause(wait)
    }

    /// Tabs by their label: in the tab bar on iPhone, the bar along the top on iPad.
    private func tab(_ label: String) {
        let byLabel = NSPredicate(format: "label == %@", label)
        let inTabBar = app.tabBars.buttons.matching(byLabel).firstMatch
        let button = inTabBar.exists ? inTabBar : app.buttons.matching(byLabel).firstMatch
        if button.exists { button.tap() }
    }

    private func pause(_ seconds: TimeInterval) {
        Thread.sleep(forTimeInterval: seconds)
    }

    private func shot(_ name: String) {
        let screenshot = XCUIScreen.main.screenshot()
        let file = "iphone-\(name).png"
        let attachment = XCTAttachment(screenshot: screenshot)
        attachment.name = file
        attachment.lifetime = .keepAlways
        add(attachment)
        if let directory { try? screenshot.pngRepresentation.write(to: directory.appending(path: file)) }
    }
}
