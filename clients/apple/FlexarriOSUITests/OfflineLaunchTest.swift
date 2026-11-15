import XCTest

/// Opening the app with the server out of reach: offline mode within moments, under the Offline bar. With nothing
/// downloaded it stays on Home, which says the server can't be reached, with Try again, and Downloads says there's nothing there. Needs no server:
/// `-FlexarrSimulateOffline` makes every request fail as if there were no network.
@MainActor
final class OfflineLaunchTest: XCTestCase {
    func testOpensOfflineWithNothingDownloaded() {
        XCUIDevice.shared.orientation = .portrait
        let app = XCUIApplication()
        app.launchArguments = ["-FlexarrServer", "http://192.0.2.1:8383", "-FlexarrApiKey", "offline-test",
                               "-FlexarrNoHistory", "YES", "-FlexarrWelcomed", "YES", "-FlexarrSimulateOffline", "YES"]
        app.launch()

        // Within moments of the app being up (launch() returns once it is), not after any request's timeout.
        XCTAssertTrue(app.staticTexts["Offline"].firstMatch.waitForExistence(timeout: 3), "offline mode should show in seconds")
        XCTAssertTrue(app.staticTexts["Can’t reach this Flexarr"].waitForExistence(timeout: 2))
        // The server may only be restarting: Try again asks it now.
        XCTAssertTrue(app.buttons["offlineTryAgain"].exists)
        // Nothing to go to.
        XCTAssertFalse(app.buttons["Go to Downloads"].exists)

        app.buttons["Downloads"].firstMatch.tap()
        XCTAssertTrue(app.staticTexts["Nothing downloaded."].waitForExistence(timeout: 2))
    }
}
