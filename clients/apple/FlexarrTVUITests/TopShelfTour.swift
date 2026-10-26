import FlexarrKit
import XCTest

/// What selecting a title on the Top Shelf does: it opens `flexarr://item/<id>`, which should show the title on its
/// library's tab, leaving Home as it was. Same environment as `ScreenshotTour`.
@MainActor
final class TopShelfTour: XCTestCase {
    private var app: XCUIApplication!
    private var client: FlexarrClient!
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
        client = FlexarrClient(baseURL: address, apiKey: key)
        app = XCUIApplication()
        app.launchArguments = ["-FlexarrServer", address.absoluteString, "-FlexarrApiKey", key, "-FlexarrNoHistory", "YES"]
        app.launch()
        pause(6)
    }

    func testOpenFromTopShelfAndBack() async throws {
        let title = try await topShelfTitle()
        shot("70-home")
        // As the Top Shelf does: from the Home screen, so the app goes to the background first.
        openFromTopShelf(title)
        shot("71-opened")
        logFocus("opened")
        XCTAssertTrue(showingDetail, "the link didn't open the title")

        // Up to the tab bar and across to Home: Home as it was, not the title. (Tabs don't report being selected to
        // UI tests on tvOS; the screenshots show which is.)
        press(.up, times: 4, wait: 0.8)
        for _ in 0..<8 where !app.buttons["Home"].hasFocus { press(.left, wait: 0.8) }
        pause(3)
        shot("72-home-tab")
        logFocus("home tab")
        XCTAssertFalse(showingDetail, "Home showed the title opened from the Top Shelf")

        // Menu from the title's buttons goes back to its library.
        openFromTopShelf(title)
        press(.down, wait: 1)
        logFocus("content")
        press(.menu, wait: 3)
        shot("73-after-menu-from-content")
        logFocus("after menu from content")
        XCTAssertEqual(app.state, .runningForeground, "Menu left the app")
        XCTAssertFalse(showingDetail, "Menu didn't close the title")
    }

    /// The Top Shelf usually finds the app closed, so the link is what launches it.
    func testColdOpenFromTopShelfAndBack() async throws {
        let title = try await topShelfTitle()
        app.terminate()
        pause(2)
        app.open(URL(string: "flexarr://item/\(title.id)")!)
        pause(10)
        shot("80-cold-opened")
        logFocus("cold opened")
        XCTAssertTrue(showingDetail, "the link didn't open the title")

        // Leaving the app and opening it again finds the tab as it was, not the title.
        XCUIDevice.shared.press(.home)
        pause(2)
        app.activate()
        pause(4)
        shot("81-reopened")
        XCTAssertFalse(showingDetail, "reopening the app still showed the title")
    }

    private func topShelfTitle() async throws -> ItemSummary {
        let home = try await client.home()
        guard let title = (home.rows.first(where: { $0.id == "continue" })?.items ?? home.featured).first else {
            throw XCTSkip("nothing on the Top Shelf")
        }
        return title
    }

    private func openFromTopShelf(_ title: ItemSummary) {
        XCUIDevice.shared.press(.home)
        pause(2)
        app.open(URL(string: "flexarr://item/\(title.id)")!)
        pause(6)
    }

    /// A title's page is up: only it has Mark as watched/unwatched.
    private var showingDetail: Bool {
        app.buttons.matching(NSPredicate(format: "label BEGINSWITH 'Mark as'")).firstMatch.exists
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

    private func logFocus(_ label: String) {
        let focused = app.descendants(matching: .any).element(matching: NSPredicate(format: "hasFocus == true"))
        print("FOCUS[\(label)]: \(focused.exists ? focused.debugDescription.prefix(300) : "nothing")")
    }

    private func shot(_ name: String) {
        let attachment = XCTAttachment(screenshot: XCUIScreen.main.screenshot())
        attachment.name = name
        attachment.lifetime = .keepAlways
        add(attachment)
        if let outputDirectory {
            try? XCUIScreen.main.screenshot().pngRepresentation.write(to: outputDirectory.appending(path: "\(name).png"))
        }
    }
}
