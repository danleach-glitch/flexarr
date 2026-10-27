import FlexarrKit
import XCTest

/// Opens Discover from the sidebar and walks it with the Siri Remote: the rows from Seerr, a title from them, a genre,
/// and searching for anything, saving a screenshot at each stop. Same environment as `ScreenshotTour`. It changes
/// nothing on the server: it never presses Request, and checks what has focus before every Select.
@MainActor
final class DiscoverTour: XCTestCase {
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
        // Stop at the first surprise: pressing on regardless could press something real.
        continueAfterFailure = false
        app = XCUIApplication()
        app.launchArguments = ["-FlexarrServer", address.absoluteString, "-FlexarrApiKey", key, "-FlexarrNoHistory", "YES"]
        app.launch()
        pause(6)
    }

    func testDiscover() throws {
        app.openSidebarRow("Discover", wait: 5)
        shot("60-discover")
        // Choosing Discover puts focus on its first title, as a library's tab does.
        let card = try XCTUnwrap(focused(), "a title should have focus")
        XCTAssertGreaterThan(card.frame.height, card.frame.width, "focus should be on a poster")
        press(.select, wait: 4)
        shot("62-external-title")
        // Down the title page to its cast and recommendations, never pressing Select here.
        press(.down, times: 2, wait: 1.5)
        shot("63-external-title-more")
        press(.menu, wait: 2)

        // Down to the genres.
        for _ in 0..<16 {
            press(.down, wait: 1)
            if let tile = focused(), tile.frame.width > tile.frame.height * 1.5 { break }
        }
        shot("64-discover-genres")
        let tile = try XCTUnwrap(focused())
        XCTAssertGreaterThan(tile.frame.width, tile.frame.height * 1.5, "focus should be on a genre tile")
        press(.select, wait: 3)
        shot("65-genre")
        press(.menu, wait: 2)

        // Up to the switch, across to Search, and look for something.
        // Up reaches whichever word of the switch is nearer. Only Discover has a Browse; the sidebar has a Search of
        // its own, so that one is told apart by where it is.
        let browse = app.buttons["Browse"]
        var searchWord: XCUIElement?
        for _ in 0..<16 where !browse.hasFocus && searchWord?.hasFocus != true {
            press(.up, wait: 0.8)
            searchWord = switchWord("Search")
        }
        let search = try XCTUnwrap(searchWord, "Discover should have its Browse · Search switch")
        XCTAssertTrue(browse.hasFocus || search.hasFocus, "Up should reach the switch")
        if browse.hasFocus { press(.right, wait: 1) }
        XCTAssertTrue(search.hasFocus, "Search should have focus")
        press(.select, wait: 3)
        // Straight to the keyboard, full screen.
        shot("66-discover-search")
        app.typeText("severance")
        pause(4)
        shot("67-discover-search-results")
        // Menu comes back to Discover's Browse.
        press(.menu, wait: 2.5)
        shot("68-discover-back-from-search")
        XCTAssertTrue(app.buttons["Browse"].exists, "Menu should come back to Discover")
    }

    /// A word of the switch at the top of Discover (the sidebar has a Search too, further down).
    private func switchWord(_ label: String) -> XCUIElement? {
        app.buttons.matching(NSPredicate(format: "label == %@", label)).allElementsBoundByIndex
            .first { $0.frame.minY > 100 && $0.frame.minY < 260 && $0.frame.minX > 30 }
    }

    private func focused() -> XCUIElement? {
        app.buttons.allElementsBoundByIndex.first(where: \.hasFocus)
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
