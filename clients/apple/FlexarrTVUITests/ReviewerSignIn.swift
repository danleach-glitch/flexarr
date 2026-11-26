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

        // Signed in: the picker starts on the App Review profile, the one the key signs in as, so a single press
        // goes in. App Review rejected 0.67.1 for pressing Select here when focus started on the main profile and its PIN.
        let profile = app.buttons.matching(NSPredicate(format: "label CONTAINS 'App Review'")).firstMatch
        let asked = profile.waitForExistence(timeout: 30)
        snap("after-connect")
        XCTAssertTrue(asked, "connecting should ask who's watching")
        snap("profiles")
        XCTAssertTrue(waitForFocus(profile), "who's watching should start on the App Review profile")
        press(.select, wait: 8)
        XCTAssertTrue(app.buttons["Home"].firstMatch.waitForExistence(timeout: 15), "the profile should land on Home")
        snap("home")
    }

    /// What App Review actually did (rejections of 0.66.0): the address, then straight to Connect, missing the API key
    /// link. The server turns the code down from outside the home, and the app should carry on to the key with the
    /// address kept, not stop on that message with only Back.
    func testConnectBeforeTheKeyLink() throws {
        guard let server = env["FLEXARR_REVIEW_SERVER"], let key = env["FLEXARR_REVIEW_KEY"] else {
            throw XCTSkip("Set TEST_RUNNER_FLEXARR_REVIEW_SERVER and TEST_RUNNER_FLEXARR_REVIEW_KEY")
        }
        app = XCUIApplication()
        app.launch()

        let manual = app.buttons["Enter an address instead"]
        XCTAssertTrue(manual.waitForExistence(timeout: 15), "a fresh install should open on the connect screen")
        for _ in 0..<8 where !manual.hasFocus { press(.down, wait: 0.8) }
        press(.select, wait: 2)
        XCTAssertTrue(app.textFields.firstMatch.waitForExistence(timeout: 5))
        press(.select, wait: 2)
        app.typeText(server)
        press(.menu, wait: 1.5)
        // Connect has the focus once the keyboard closes.
        let connect = app.buttons["Connect"].firstMatch
        for _ in 0..<3 where !connect.hasFocus { press(.down, wait: 1) }
        XCTAssertTrue(connect.hasFocus, "focus should reach Connect")
        press(.select, wait: 3)
        snap("connect-without-key")

        // Turned down for a code: the key field, with the address still there and the focus on the key.
        let keyField = app.secureTextFields.firstMatch
        XCTAssertTrue(keyField.waitForExistence(timeout: 15), "a code turned down from outside should lead to the API key")
        XCTAssertTrue(keyField.hasFocus, "the key field should have the focus")
        press(.select, wait: 2)
        app.typeText(key)
        press(.menu, wait: 1.5)
        press(.down, wait: 1)
        for _ in 0..<3 where !connect.hasFocus { press(.left, wait: 1) }
        XCTAssertTrue(connect.hasFocus, "focus should reach Connect")
        press(.select, wait: 2)

        let profile = app.buttons.matching(NSPredicate(format: "label CONTAINS 'App Review'")).firstMatch
        XCTAssertTrue(profile.waitForExistence(timeout: 30), "connecting should ask who's watching")
        snap("connect-without-key-profiles")
        XCTAssertTrue(waitForFocus(profile), "who's watching should start on the App Review profile")
    }

    /// The picker fades in, and focus lands once it has: give it a few seconds, without pressing anything.
    private func waitForFocus(_ element: XCUIElement, timeout: TimeInterval = 6) -> Bool {
        let focused = expectation(for: NSPredicate(format: "hasFocus == true"), evaluatedWith: element)
        return XCTWaiter.wait(for: [focused], timeout: timeout) == .completed
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
