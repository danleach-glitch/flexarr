import XCTest

/// App Review's way in, on a fresh install: past the welcome cards, "Enter an address instead", the API key link,
/// the App Review profile, then Home. Run it before submitting a version, against the review server:
///
///     TEST_RUNNER_FLEXARR_REVIEW_SERVER=<https address> TEST_RUNNER_FLEXARR_REVIEW_KEY=<key>
///
/// Delete the app from the simulator first, so it starts as a reviewer's does.
@MainActor
final class ReviewerSignIn: XCTestCase {
    private let env = ProcessInfo.processInfo.environment

    func testSignIn() throws {
        guard let server = env["FLEXARR_REVIEW_SERVER"], let key = env["FLEXARR_REVIEW_KEY"] else {
            throw XCTSkip("Set TEST_RUNNER_FLEXARR_REVIEW_SERVER and TEST_RUNNER_FLEXARR_REVIEW_KEY")
        }
        XCUIDevice.shared.orientation = .portrait
        let app = XCUIApplication()
        app.launch()
        snap("welcome")

        let skip = app.buttons["Skip"]
        XCTAssertTrue(skip.waitForExistence(timeout: 10), "a fresh install should open on the welcome cards")
        skip.tap()

        let manual = app.buttons["Enter an address instead"]
        XCTAssertTrue(manual.waitForExistence(timeout: 10), "the connect screen should offer an address")
        snap("connect")
        manual.tap()

        let address = app.textFields.firstMatch
        XCTAssertTrue(address.waitForExistence(timeout: 5))
        address.tap()
        address.typeText(server)
        app.buttons["Connecting from outside your home? Use an API key"].tap()
        let keyField = app.secureTextFields.firstMatch
        XCTAssertTrue(keyField.waitForExistence(timeout: 5), "the API key field should appear")
        keyField.tap()
        keyField.typeText(key)
        snap("address-and-key")
        app.buttons["Connect"].tap()

        // iOS offers to save the key as a password.
        let notNow = XCUIApplication(bundleIdentifier: "com.apple.springboard").buttons["Not Now"]
        if notNow.waitForExistence(timeout: 8) { notNow.tap() }
        let notNowInApp = app.buttons["Not Now"]
        if notNowInApp.waitForExistence(timeout: 2) { notNowInApp.tap() }

        // Signed in: pick the App Review profile (the main one has a PIN).
        let profile = app.buttons.matching(NSPredicate(format: "label BEGINSWITH 'App Review'")).firstMatch
        XCTAssertTrue(profile.waitForExistence(timeout: 30), "connecting should ask who's watching")
        snap("profiles")
        profile.tap()
        let home = app.buttons.matching(NSPredicate(format: "label == 'Home'")).firstMatch
        XCTAssertTrue(home.waitForExistence(timeout: 15), "the profile should land on Home")
        Thread.sleep(forTimeInterval: 6)
        snap("home")
    }

    private func snap(_ name: String) {
        let attachment = XCTAttachment(screenshot: XCUIScreen.main.screenshot())
        attachment.name = "review-\(name)"
        attachment.lifetime = .keepAlways
        add(attachment)
    }
}
