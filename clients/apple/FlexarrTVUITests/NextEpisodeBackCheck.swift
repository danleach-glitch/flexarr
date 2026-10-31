import XCTest

/// Temporary: after Next Episode, does Back leave the player?
@MainActor
final class NextEpisodeBackCheck: XCTestCase {
    func testBackAfterNextEpisode() throws {
        let env = ProcessInfo.processInfo.environment
        guard let server = env["FLEXARR_SERVER"], let key = env["FLEXARR_KEY"], let item = env["FLEXARR_ITEM"] else { throw XCTSkip("no server") }
        let app = XCUIApplication()
        app.launchArguments = ["-FlexarrServer", server, "-FlexarrApiKey", key, "-FlexarrNoHistory", "YES", "-FlexarrPlay", "\(item)?t=3400"]
        app.launch()
        let remote = XCUIRemote.shared
        func shot(_ name: String) { let a = XCTAttachment(screenshot: app.screenshot()); a.name = name; a.lifetime = .keepAlways; add(a) }
        sleep(20)
        shot("1-playing-near-end")
        remote.press(.select)
        sleep(20)
        shot("2-after-select")
        remote.press(.menu)
        sleep(4)
        shot("3-after-menu-1")
        remote.press(.menu)
        sleep(4)
        shot("4-after-menu-2")
    }
}
