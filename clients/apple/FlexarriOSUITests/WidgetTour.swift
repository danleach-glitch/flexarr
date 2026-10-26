import XCTest

/// Puts the Continue listening widget on the Home Screen and takes a picture of it. Run after the app has played an
/// episode (the widget shows what it saved). Springboard's menus move between iOS versions, so this is best effort.
@MainActor
final class WidgetTour: XCTestCase {
    func testAddWidget() throws {
        guard let path = ProcessInfo.processInfo.environment["FLEXARR_SCREENSHOTS"] else { throw XCTSkip("Set TEST_RUNNER_FLEXARR_SCREENSHOTS") }
        let springboard = XCUIApplication(bundleIdentifier: "com.apple.springboard")
        XCUIDevice.shared.press(.home)
        sleep(2)
        springboard.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.55)).press(forDuration: 2)
        sleep(2)
        let edit = springboard.buttons["Edit"]
        if edit.waitForExistence(timeout: 3) {
            edit.tap()
            sleep(1)
        }
        let add = springboard.buttons["Add Widget"]
        if add.waitForExistence(timeout: 3) { add.tap() } else { springboard.buttons["Add"].firstMatch.tap() }
        sleep(2)
        let search = springboard.searchFields.firstMatch
        if search.waitForExistence(timeout: 3) {
            search.tap()
            search.typeText("Flexarr")
            sleep(2)
        }
        let flexarr = springboard.cells.containing(NSPredicate(format: "label CONTAINS 'Flexarr'")).firstMatch
        XCTAssertTrue(flexarr.waitForExistence(timeout: 5), "Flexarr's widgets should be listed")
        flexarr.tap()
        sleep(2)
        save(springboard, "60-widget-gallery", path)
        let addWidget = springboard.buttons["Add Widget"].firstMatch
        if addWidget.waitForExistence(timeout: 3) { addWidget.tap() }
        sleep(2)
        XCUIDevice.shared.press(.home)
        sleep(3)
        save(springboard, "61-widget-home", path)
    }

    private func save(_ app: XCUIApplication, _ name: String, _ path: String) {
        let shot = XCUIScreen.main.screenshot()
        try? shot.pngRepresentation.write(to: URL(fileURLWithPath: path).appending(path: "iphone-\(name).png"))
    }
}
