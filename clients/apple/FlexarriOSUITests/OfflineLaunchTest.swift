import XCTest

/// Opening the app with the server out of reach: offline mode within moments, on Downloads under the Offline bar, with
/// Home pointing back at the downloads. Needs no server: `-FlexarrSimulateOffline` makes every request fail as if there
/// were no network.
@MainActor
final class OfflineLaunchTest: XCTestCase {
    func testOpensOfflineOnDownloads() {
        XCUIDevice.shared.orientation = .portrait
        let app = XCUIApplication()
        app.launchArguments = ["-FlexarrServer", "http://192.0.2.1:8383", "-FlexarrApiKey", "offline-test",
                               "-FlexarrNoHistory", "YES", "-FlexarrWelcomed", "YES", "-FlexarrSimulateOffline", "YES"]
        app.launch()

        // Within moments of the app being up (launch() returns once it is), not after any request's timeout.
        XCTAssertTrue(app.staticTexts["Offline"].firstMatch.waitForExistence(timeout: 3), "offline mode should show in seconds")
        XCTAssertTrue(app.tabBars.buttons["Downloads"].isSelected || app.buttons["Downloads"].isSelected)

        app.buttons["Home"].firstMatch.tap()
        XCTAssertTrue(app.staticTexts["You’re offline"].waitForExistence(timeout: 2))
        app.buttons["Go to Downloads"].tap()
        XCTAssertTrue(app.navigationBars["Downloads"].waitForExistence(timeout: 2))
    }
}
