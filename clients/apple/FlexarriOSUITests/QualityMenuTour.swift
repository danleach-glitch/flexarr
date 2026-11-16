import FlexarrKit
import XCTest

/// The player's Quality menu beside Speed and Audio, for checking it by eye (docs/design.md, the Quality menu).
/// Skipped unless FLEXARR_SERVER, FLEXARR_KEY and FLEXARR_MOVIE (a film's id) are passed (as TEST_RUNNER_FLEXARR_*);
/// screenshots go to FLEXARR_SHOTS. Watch state isn't touched (-FlexarrNoHistory).
@MainActor
final class QualityMenuTour: XCTestCase {
    private let env = ProcessInfo.processInfo.environment
    private var app: XCUIApplication!

    func testQualityMenu() throws {
        guard let server = env["FLEXARR_SERVER"], let key = env["FLEXARR_KEY"], let movie = env["FLEXARR_MOVIE"],
              let address = FlexarrClient.normalizeAddress(server) else {
            throw XCTSkip("Set FLEXARR_SERVER, FLEXARR_KEY and FLEXARR_MOVIE to run the Quality menu tour.")
        }
        app = XCUIApplication()
        app.launchArguments = ["-FlexarrServer", address.absoluteString, "-FlexarrApiKey", key, "-FlexarrNoHistory", "YES",
                               "-FlexarrMuted", "YES", "-FlexarrPlay", movie]
        XCUIDevice.shared.orientation = .landscapeLeft
        app.launch()

        let quality = app.buttons["Quality"].firstMatch
        XCTAssertTrue(quality.waitForExistence(timeout: 30), "No Quality button")
        sleep(20)
        dismissSystemSheets()
        showControls()
        shot("1-controls")
        note("button", quality)

        open(app.buttons["Speed"].firstMatch)
        shot("2-speed-menu")
        dismissMenu()
        open(quality)
        shot("3-quality-menu")
        for title in ["Automatic", "Original", "1080p", "720p", "480p"] {
            let item = self.row(title)
            print("ROW \(title): exists=\(item.exists) label=\(item.exists ? item.label : "") value=\(item.exists ? String(describing: item.value) : "")")
        }

        // Another quality: the picture stays with a small spinner until it plays.
        row("720p").tap()
        usleep(400_000)
        shot("4-switching")
        sleep(20)
        showControls()
        shot("5-badge")
        note("button after 720p", quality)
        open(quality)
        shot("6-quality-menu-720p")
        // Back to Automatic, as it was.
        row("Automatic").tap()
        sleep(8)
        showControls()
        shot("7-automatic-again")
        note("button after Automatic", quality)
    }

    /// A menu row by its title (its label carries the second line too: "Automatic, 1080p").
    private func row(_ title: String) -> XCUIElement {
        app.buttons.matching(NSPredicate(format: "label == %@ OR label BEGINSWITH %@", title, title + ",")).firstMatch
    }

    /// The simulator's first-run keyboard tips.
    private func dismissSystemSheets() {
        let next = app.buttons["Continue"].firstMatch
        if next.exists { next.tap(); sleep(1) }
    }

    private func showControls() {
        let quality = app.buttons["Quality"].firstMatch
        if !quality.isHittable {
            app.coordinate(withNormalizedOffset: CGVector(dx: 0.3, dy: 0.3)).tap()
            sleep(1)
        }
    }

    private func open(_ button: XCUIElement) {
        showControls()
        button.tap()
        sleep(1)
    }

    private func dismissMenu() {
        app.coordinate(withNormalizedOffset: CGVector(dx: 0.15, dy: 0.15)).tap()
        sleep(1)
    }

    private func note(_ what: String, _ element: XCUIElement) {
        print("NOTE \(what): label=\(element.label) value=\(String(describing: element.value)) enabled=\(element.isEnabled)")
    }

    private func shot(_ name: String) {
        let png = XCUIScreen.main.screenshot().pngRepresentation
        if let folder = env["FLEXARR_SHOTS"] {
            try? png.write(to: URL(fileURLWithPath: folder).appendingPathComponent("ios-\(name).png"))
        }
        let attachment = XCTAttachment(data: png, uniformTypeIdentifier: "public.png")
        attachment.name = name
        attachment.lifetime = .keepAlways
        add(attachment)
    }
}
