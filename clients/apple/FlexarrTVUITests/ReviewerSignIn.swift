import XCTest

/// App Review's way in on the Apple TV, from a fresh install and with the Siri Remote: "Enter an address instead",
/// the address, "Use an API key" and the key, then the App Review profile and Home. Run it before submitting a version, against the
/// review server:
///
///     TEST_RUNNER_FLEXARR_REVIEW_SERVER=<https address> TEST_RUNNER_FLEXARR_REVIEW_KEY=<key>
///
/// Delete the app from the simulator first, so it starts as a reviewer's does.
@MainActor
final class ReviewerSignIn: XCTestCase {
    private let env = ProcessInfo.processInfo.environment
    private let remote = XCUIRemote.shared
    private var app: XCUIApplication!

    func testSignIn() throws {
        guard let server = env["FLEXARR_REVIEW_SERVER"], let key = env["FLEXARR_REVIEW_KEY"] else {
            throw XCTSkip("Set TEST_RUNNER_FLEXARR_REVIEW_SERVER and TEST_RUNNER_FLEXARR_REVIEW_KEY")
        }
        app = XCUIApplication()
        app.launch()

        let manual = app.buttons["Enter an address instead"]
        XCTAssertTrue(manual.waitForExistence(timeout: 15), "a fresh install should open on the connect screen")
        snap("connect")
        // A server found on the network takes focus first; the address button is below the list.
        for _ in 0..<8 where !manual.hasFocus { press(.down, wait: 0.8) }
        XCTAssertTrue(manual.hasFocus, "focus should reach Enter an address instead")
        press(.select, wait: 2)

        // Focus starts on the address field. Menu closes the keyboard.
        XCTAssertTrue(app.textFields.firstMatch.waitForExistence(timeout: 5))
        press(.select, wait: 2)
        app.typeText(server)
        press(.menu, wait: 1.5)
        // The review server is reached from outside, where codes aren't offered: the key is behind the link under Connect.
        let useKey = app.buttons["Connecting from outside your home? Use an API key"]
        for _ in 0..<3 where !useKey.hasFocus { press(.down, wait: 1) }
        XCTAssertTrue(useKey.hasFocus, "focus should reach the API key link")
        press(.select, wait: 2)
        // The key field appears with the focus.
        XCTAssertTrue(app.secureTextFields.firstMatch.waitForExistence(timeout: 5))
        press(.select, wait: 2)
        app.typeText(key)
        press(.menu, wait: 1.5)
        snap("address-and-key")
        // Down lands on Connect or Back, side by side.
        press(.down, wait: 1)
        let connect = app.buttons["Connect"].firstMatch
        for _ in 0..<3 where !connect.hasFocus { press(.left, wait: 1) }
        XCTAssertTrue(connect.hasFocus, "focus should reach Connect")
        press(.select, wait: 2)
        snap("connecting")

        // Signed in: pick the App Review profile (the main one has a PIN).
        let profile = app.buttons.matching(NSPredicate(format: "label CONTAINS 'App Review'")).firstMatch
        let asked = profile.waitForExistence(timeout: 30)
        snap("after-connect")
        XCTAssertTrue(asked, "connecting should ask who's watching")
        snap("profiles")
        for _ in 0..<6 where !profile.hasFocus { press(.right, wait: 1) }
        XCTAssertTrue(profile.hasFocus, "focus should reach the App Review profile")
        press(.select, wait: 8)
        XCTAssertTrue(app.buttons["Home"].firstMatch.waitForExistence(timeout: 15), "the profile should land on Home")
        snap("home")
    }

    private func press(_ button: XCUIRemote.Button, wait: TimeInterval) {
        remote.press(button)
        Thread.sleep(forTimeInterval: wait)
    }

    private func snap(_ name: String) {
        let screenshot = XCUIScreen.main.screenshot()
        if let path = env["FLEXARR_SCREENSHOTS"] {
            try? screenshot.pngRepresentation.write(to: URL(fileURLWithPath: path).appending(path: "review-\(name).png"))
        }
        let attachment = XCTAttachment(screenshot: screenshot)
        attachment.name = "review-\(name)"
        attachment.lifetime = .keepAlways
        add(attachment)
    }
}
