import FlexarrKit
import XCTest

/// The player's Quality menu beside Speed, for checking it by eye (docs/design.md, the Quality menu): focus opens on
/// the choice in use. Skipped unless FLEXARR_SERVER, FLEXARR_KEY and FLEXARR_MOVIE (a film's id) are passed (as
/// TEST_RUNNER_FLEXARR_*); screenshots go to FLEXARR_SHOTS. Watch state isn't touched (-FlexarrNoHistory).
@MainActor
final class QualityMenuTour: XCTestCase {
    private let env = ProcessInfo.processInfo.environment
    private let remote = XCUIRemote.shared
    private var app: XCUIApplication!

    func testQualityMenu() throws {
        guard let server = env["FLEXARR_SERVER"], let key = env["FLEXARR_KEY"], let movie = env["FLEXARR_MOVIE"],
              let address = FlexarrClient.normalizeAddress(server) else {
            throw XCTSkip("Set FLEXARR_SERVER, FLEXARR_KEY and FLEXARR_MOVIE to run the Quality menu tour.")
        }
        app = XCUIApplication()
        app.launchArguments = ["-FlexarrServer", address.absoluteString, "-FlexarrApiKey", key, "-FlexarrNoHistory", "YES",
                               "-FlexarrMuted", "YES", "-FlexarrPlay", movie]
        app.launch()

        let quality = app.buttons["Quality"].firstMatch
        XCTAssertTrue(quality.waitForExistence(timeout: 40), "No Quality button")
        pause(8)
        // Paused, the controls stay up.
        remote.press(.playPause)
        pause(2)
        shot("1-controls")

        focus(app.buttons["Speed"].firstMatch)
        remote.press(.select)
        pause(1.5)
        shot("2-speed-menu")
        print("FOCUS speed menu opens on: \(focused())")
        remote.press(.menu)
        pause(1.5)

        focus(quality)
        note("button", quality)
        remote.press(.select)
        pause(1.5)
        shot("3-quality-menu")
        print("FOCUS quality menu opens on: \(focused())")

        // Down to 720p and choose it: the picture stays with a small spinner until it plays.
        for _ in 0..<5 where !focused().hasPrefix("720p") { remote.press(.down); pause(0.6) }
        print("FOCUS before choosing: \(focused())")
        remote.press(.select)
        pause(0.5)
        shot("4-switching")
        pause(12)
        if !quality.isHittable { remote.press(.playPause); pause(2) }
        shot("5-badge")
        note("button after 720p", quality)
        focus(quality)
        remote.press(.select)
        pause(1.5)
        shot("6-quality-menu-720p")
        print("FOCUS quality menu (720p) opens on: \(focused())")
        for _ in 0..<5 where !focused().hasPrefix("Automatic") { remote.press(.up); pause(0.6) }
        remote.press(.select)
        pause(10)
        shot("7-automatic-again")
        note("button after Automatic", quality)
        remote.press(.menu)
        pause(2)
    }

    private func focused() -> String {
        let element = app.descendants(matching: .any).matching(NSPredicate(format: "hasFocus == true")).firstMatch
        guard element.exists else { return "(nothing)" }
        return element.label.isEmpty ? element.descendants(matching: .staticText).allElementsBoundByIndex.map(\.label).joined(separator: " / ") : element.label
    }

    /// Moves focus to `target`: up off the timeline, right along the circles, down into the menus' row, along it.
    private func focus(_ target: XCUIElement) {
        let row = ["Audio", "Subtitles", "Speed", "Quality", "Sleep timer"]
        var downs = 0
        for _ in 0..<16 {
            let label = focused()
            print("FOCUS step: \(label)")
            if label == target.label { break }
            if let index = row.firstIndex(of: label), let goal = row.firstIndex(of: target.label) {
                remote.press(goal > index ? .right : .left)
            } else if label == "Timeline" || label == "(nothing)" {
                remote.press(.up)
            } else if label.hasPrefix("Next") || downs > 0 {
                remote.press(.down)
                downs += 1
            } else {
                remote.press(.right)
                if focused() == label { remote.press(.down); downs += 1 }
            }
            pause(0.8)
        }
        print("FOCUS on \(target.label): \(focused())")
    }

    private func note(_ what: String, _ element: XCUIElement) {
        print("NOTE \(what): label=\(element.label) value=\(String(describing: element.value)) enabled=\(element.isEnabled)")
    }

    private func pause(_ seconds: TimeInterval) {
        Thread.sleep(forTimeInterval: seconds)
    }

    private func shot(_ name: String) {
        let png = XCUIScreen.main.screenshot().pngRepresentation
        if let folder = env["FLEXARR_SHOTS"] {
            try? png.write(to: URL(fileURLWithPath: folder).appendingPathComponent("tv-\(name).png"))
        }
        let attachment = XCTAttachment(data: png, uniformTypeIdentifier: "public.png")
        attachment.name = name
        attachment.lifetime = .keepAlways
        add(attachment)
    }
}
