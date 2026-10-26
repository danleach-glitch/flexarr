import FlexarrKit
import XCTest

/// A film library's two views with the Siri Remote: Browse, the switch to All, the A–Z rail, a row's See all and
/// Settings › Libraries. Read-only, so safe against a live server.
///
///     TEST_RUNNER_FLEXARR_SERVER=http://192.168.1.20:8383 TEST_RUNNER_FLEXARR_KEY=... \
///     TEST_RUNNER_FLEXARR_SCREENSHOTS=/tmp/flexarr-shots \
///     xcodebuild test -project Flexarr.xcodeproj -scheme FlexarrTV -only-testing:FlexarrTVUITests/LibraryTour \
///       -destination 'platform=tvOS Simulator,name=Apple TV 4K (3rd generation) (at 1080p)'
@MainActor
final class LibraryTour: XCTestCase {
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
        // Every run starts on Browse, whatever an earlier run left the setting at.
        app.launchArguments = ["-FlexarrServer", address.absoluteString, "-FlexarrApiKey", key, "-FlexarrNoHistory", "YES",
                               "-FlexarrLibraryOpensOn", "browse"]
        app.launch()
        pause(6)
    }

    func test1BrowseAndAll() throws {
        openTab(named: env("FLEXARR_LIBRARY") ?? "Movies")
        shot("40-library-browse")
        press(.down, wait: 2)
        shot("41-browse-hero-play")
        XCTAssertTrue(app.buttons["Browse"].exists, "Browse should have the view switch")

        // Up from Play reaches the switch.
        press(.up, wait: 1.5)
        shot("42-browse-switch-focused")
        let all = app.buttons.matching(NSPredicate(format: "label BEGINSWITH 'All '")).firstMatch
        for _ in 0..<3 where !all.hasFocus { press(.right, wait: 0.8) }
        XCTAssertTrue(all.hasFocus, "All films should have focus")
        press(.select, wait: 4)
        shot("43-all")
        XCTAssertTrue(all.exists && all.hasFocus, "focus should stay on All films after switching")

        // Down into the grid, then right to the A–Z rail.
        press(.down, wait: 1.5)
        press(.down, wait: 1.5)
        shot("44-all-grid")
        press(.right, times: 6, wait: 0.6)
        shot("45-rail-focused")
        let letter = app.buttons["M"]
        if letter.exists {
            for _ in 0..<27 where !letter.hasFocus {
                press(app.buttons.allElementsBoundByIndex.first(where: \.hasFocus).map { $0.frame.minY < letter.frame.minY } ?? true ? .down : .up, wait: 0.4)
            }
            shot("46-rail-on-m")
            press(.select, wait: 6)
            shot("47-jumped-to-m")
        }
    }

    func test2SeeAll() throws {
        openTab(named: env("FLEXARR_LIBRARY") ?? "Movies")
        press(.down, wait: 2)
        // Down past the hero to the rows.
        press(.down, times: 3, wait: 1.5)
        shot("48-browse-rows")
        // Along the row to its end; the card only exists once the row has scrolled to it.
        let seeAll = app.buttons.matching(NSPredicate(format: "label CONTAINS 'See all'")).firstMatch
        for _ in 0..<25 where !(seeAll.exists && seeAll.hasFocus) { press(.right, wait: 0.5) }
        XCTAssertTrue(seeAll.exists && seeAll.hasFocus, "the row should end in See all")
        if seeAll.exists && seeAll.hasFocus {
            shot("49-see-all-focused")
            press(.select, wait: 4)
            shot("50-see-all-opened")
            press(.menu, wait: 2)
            shot("51-back-to-browse")
        }
    }

    func test3Settings() throws {
        press(.up, wait: 1.5)
        let gear = app.buttons["Settings"]
        XCTAssertTrue(gear.waitForExistence(timeout: 3))
        for _ in 0..<10 where !gear.hasFocus { press(.right, wait: 0.6) }
        pause(2)
        press(.down, wait: 2)
        let libraries = app.buttons.matching(NSPredicate(format: "label BEGINSWITH 'Libraries'")).firstMatch
        XCTAssertTrue(libraries.waitForExistence(timeout: 3), "Settings should list Libraries")
        for _ in 0..<8 where !libraries.hasFocus { press(.down, wait: 0.8) }
        shot("52-settings-libraries-row")
        press(.select, wait: 2)
        shot("53-settings-libraries")
    }

    func test4MusicAndLiveTv() throws {
        openTab(named: "Music")
        shot("54-music")
        if app.buttons["Live TV"].exists {
            openTab(named: "Live TV")
            shot("55-live-tv")
        }
    }

    /// Picks up the first tab in Settings › Tab bar, moves it down one and looks at the tab bar. It leaves the order
    /// changed: afterwards, delete `FlexarrTabBarOrder` from the app's preferences to go back to the server's order.
    func test5TabBarOrder() throws {
        press(.up, wait: 1.5)
        let gear = app.buttons["Settings"]
        XCTAssertTrue(gear.waitForExistence(timeout: 3))
        for _ in 0..<10 where !gear.hasFocus { press(.right, wait: 0.6) }
        pause(2)
        press(.down, wait: 2)
        let tabBar = app.buttons.matching(NSPredicate(format: "label BEGINSWITH 'Tab bar'")).firstMatch
        XCTAssertTrue(tabBar.waitForExistence(timeout: 3), "Settings should list Tab bar")
        for _ in 0..<8 where !tabBar.hasFocus { press(.down, wait: 0.8) }
        press(.select, wait: 2.5)
        shot("56-tab-bar-settings")

        press(.select, wait: 1.5)
        shot("57-tab-picked-up")
        press(.down, wait: 1.5)
        shot("58-tab-moved-down")
        press(.select, wait: 1.5)

        // The tab bar shows the new order.
        press(.menu, wait: 2)
        press(.up, wait: 1.5)
        press(.up, wait: 1.5)
        shot("59-tab-bar-reordered")

    }

    /// A long-press on Home's Continue watching, then Go to: the title opens on its library's tab, and Home stays Home.
    func test6GoToFromHome() throws {
        // Down past the hero to Continue watching.
        press(.down, wait: 2)
        press(.down, wait: 2)
        shot("61-home-continue-focused")
        remote.press(.select, forDuration: 1.5)
        pause(2)
        shot("62-home-long-press-menu")
        // Down the menu until the item with focus is Go to. The menu also has Mark as watched, which would change
        // the server's watch history, so Select is pressed only once Go to is known to have focus.
        let focused = app.descendants(matching: .any).matching(NSPredicate(format: "hasFocus == true"))
        var onGoTo = false
        for _ in 0..<4 {
            if focused.allElementsBoundByIndex.contains(where: { $0.label.hasPrefix("Go to") }) {
                onGoTo = true
                break
            }
            press(.down, wait: 0.8)
        }
        guard onGoTo else {
            press(.menu, wait: 1.5)
            return XCTFail("couldn't tell that Go to had focus; closed the menu without choosing anything")
        }
        press(.select, wait: 4)
        shot("63-go-to-opened")
        press(.menu, wait: 2)
        shot("64-back-on-library-tab")
        let home = app.buttons["Home"]
        press(.up, wait: 1.5)
        for _ in 0..<8 where !home.hasFocus { press(.left, wait: 0.8) }
        pause(3)
        shot("65-home-is-home")
    }

    /// Both ends of the tab bar: Home focused, then Settings, so nothing falls off either end.
    func test7TabBarEnds() throws {
        press(.up, wait: 2)
        shot("66-tab-bar-home-end")
        XCTAssertFalse(app.buttons["Recordings"].exists, "Recordings belongs in Live TV, not the tab bar")
        let gear = app.buttons["Settings"]
        for _ in 0..<10 where !gear.hasFocus { press(.right, wait: 0.6) }
        pause(2)
        shot("67-tab-bar-settings-end")
    }

    /// Opening a library leaves focus in the tab bar, even after its switch has been used; down goes to the first
    /// title, and up from there to the switch.
    func test8FocusOnArrival() throws {
        let name = env("FLEXARR_LIBRARY") ?? "Movies"
        openTab(named: name)
        let tab = app.buttons[name]
        XCTAssertTrue(tab.hasFocus, "arriving on \(name) should leave focus on its tab")
        press(.down, wait: 2)
        shot("68-browse-down-from-tab-bar")
        XCTAssertFalse(app.buttons["Browse"].hasFocus, "down from the tab bar shouldn't land on the switch")
        press(.up, wait: 1.5)
        XCTAssertTrue(app.buttons["Browse"].hasFocus, "up from the hero should reach the switch")

        // Switch to All: focus stays on the switch, as the view changes under it.
        let all = app.buttons.matching(NSPredicate(format: "label BEGINSWITH 'All '")).firstMatch
        press(.right, wait: 0.8)
        press(.select, wait: 4)
        XCTAssertTrue(all.hasFocus, "focus should stay on All after switching")

        // Away to Home and back: the tab keeps focus this time too.
        press(.up, wait: 1.5)
        let home = app.buttons["Home"]
        for _ in 0..<6 where !home.hasFocus { press(.left, wait: 0.8) }
        pause(2)
        for _ in 0..<6 where !tab.hasFocus { press(.right, wait: 0.8) }
        pause(4)
        shot("69-back-on-library-tab")
        XCTAssertTrue(tab.hasFocus, "coming back to \(name) should leave focus on its tab")
        press(.down, wait: 2)
        shot("70-all-down-from-tab-bar")
        XCTAssertFalse(all.hasFocus, "down from the tab bar shouldn't land on the switch")
        let focusedFrame = app.buttons.allElementsBoundByIndex.first(where: \.hasFocus)?.frame ?? .zero
        XCTAssertLessThan(focusedFrame.minX, 200, "down from the tab bar should land on the first title, at the left")
        press(.up, wait: 1.5)
        shot("71-all-up-to-switch")
        XCTAssertTrue(all.hasFocus || app.buttons["Browse"].hasFocus, "up from the first title should reach the switch")
    }

    private func env(_ name: String) -> String? { ProcessInfo.processInfo.environment[name] }

    /// Up to the tab bar and along it to the library's tab, which opens as it's focused.
    private func openTab(named name: String) {
        press(.up, wait: 1.5)
        let tab = app.buttons[name]
        XCTAssertTrue(tab.waitForExistence(timeout: 3), "no \(name) tab")
        for _ in 0..<8 where !tab.hasFocus { press(.right, wait: 0.8) }
        XCTAssertTrue(tab.hasFocus, "\(name) should have focus")
        pause(4)
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
