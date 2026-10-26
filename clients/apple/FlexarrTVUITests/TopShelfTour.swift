import FlexarrKit
import XCTest

/// What selecting a title on the Top Shelf does: it opens `flexarr://item/<id>`, which should show the title on the
/// Home tab, and Menu should then go back to Home as it was. Same environment as `ScreenshotTour`.
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
        let home = try await client.home()
        guard let title = (home.rows.first(where: { $0.id == "continue" })?.items ?? home.featured).first else {
            throw XCTSkip("nothing on the Top Shelf")
        }
        shot("70-home")
        // As the Top Shelf does: from the Home screen, so the app goes to the background first.
        XCUIDevice.shared.press(.home)
        pause(2)
        app.open(URL(string: "flexarr://item/\(title.id)")!)
        pause(6)
        shot("71-opened")
        logFocus("opened")
        press(.menu, wait: 3)
        shot("72-after-menu")
        logFocus("after menu")
        XCTAssertEqual(app.state, .runningForeground, "Menu left the app instead of going back to Home")

        // Again, but up to the tab bar first: Menu there still comes back to Home rather than leaving.
        XCUIDevice.shared.press(.home)
        pause(2)
        app.open(URL(string: "flexarr://item/\(title.id)")!)
        pause(6)
        press(.up, times: 4, wait: 0.8)
        logFocus("tab bar")
        press(.menu, wait: 3)
        shot("73-after-menu-from-tab-bar")
        logFocus("after menu from tab bar")
        XCTAssertEqual(app.state, .runningForeground, "Menu on the tab bar left the app instead of going back to Home")
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
