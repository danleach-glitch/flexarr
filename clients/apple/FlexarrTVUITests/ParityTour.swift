import FlexarrKit
import XCTest

/// The same eight screens on every client, for a before-and-after comparison of how the apps look side by side:
/// 01-home, 02-library, 03-movie, 04-show, 05-player, 06-search, 07-settings, 08-profiles. It finds its titles on any
/// server (the first title in the first movie library and in the first TV library), plays with `-FlexarrNoHistory` so
/// watch history is untouched, and closes the player straight away. It uses only what the app has had for a while
/// (and Sidebar.swift's helpers), so it runs on main as well as on a branch.
///
///     TEST_RUNNER_FLEXARR_SERVER=http://192.168.1.20:8383 TEST_RUNNER_FLEXARR_KEY=... \
///     TEST_RUNNER_PARITY_SHOTS_DIR=/tmp/parity \
///     xcodebuild test -project Flexarr.xcodeproj -scheme FlexarrTV -destination 'platform=tvOS Simulator,name=Apple TV 4K (Flexarr)' \
///       -only-testing:FlexarrTVUITests/ParityTour
///
/// Shots are saved as `tv-<nn>-<name>.png` in PARITY_SHOTS_DIR, and attached to the test result too.
@MainActor
final class ParityTour: XCTestCase {
    private var app: XCUIApplication!
    private var client: FlexarrClient!
    private var directory: URL?
    private var arguments: [String] = []
    private let remote = XCUIRemote.shared

    override func setUp() async throws {
        let env = ProcessInfo.processInfo.environment
        guard let server = env["FLEXARR_SERVER"], let key = env["FLEXARR_KEY"],
              let address = FlexarrClient.normalizeAddress(server) else {
            throw XCTSkip("Set TEST_RUNNER_FLEXARR_SERVER and TEST_RUNNER_FLEXARR_KEY to run the tour.")
        }
        if let path = env["PARITY_SHOTS_DIR"] {
            directory = URL(fileURLWithPath: path)
            try? FileManager.default.createDirectory(at: directory!, withIntermediateDirectories: true)
        }
        client = FlexarrClient(baseURL: address, apiKey: key)
        arguments = ["-FlexarrServer", address.absoluteString, "-FlexarrApiKey", key, "-FlexarrNoHistory", "YES"]
        app = XCUIApplication()
        app.launchArguments = arguments
        app.launch()
        pause(8)
    }

    func testParityTour() async throws {
        let libraries = try await client.libraries()
        let movies = libraries.first { $0.type == .movies }
        let shows = libraries.first { $0.type == .tvShows }
        var movie: ItemSummary?
        var show: ItemSummary?
        if let movies { movie = try await client.libraryItems(movies.id, take: 1).items.first }
        if let shows { show = try await client.libraryItems(shows.id, take: 1).items.first }

        shot("01-home")

        if let movies {
            app.openSidebarRow(movies.name, wait: 5)
            // Into the screen, off the sidebar.
            press(.right, wait: 2)
            shot("02-library")
        }

        if let movie {
            open("flexarr://item/\(movie.id)", wait: 6)
            shot("03-movie")
        }
        if let show {
            open("flexarr://item/\(show.id)", wait: 6)
            shot("04-show")
        }

        if let movie {
            // Part way in, the transport bar up (paused), then closed straight away. History is off.
            open("flexarr://play/\(movie.id)?t=600", wait: 10)
            press(.playPause, wait: 2)
            shot("05-player")
            // Menu closes the paused player (which stops the stream on the server); once more would leave the app.
            press(.menu, wait: 3)
            if app.state != .runningForeground {
                app.activate()
                pause(3)
            }
        }

        if app.openSidebarRow("Search", wait: 3) {
            // Into the on-screen keyboard so the field takes the typing; typed only once it has keyboard focus (typing
            // into a field without it waits a long time and fails).
            press(.right, wait: 1.5)
            let field = app.searchFields.firstMatch
            for _ in 0..<3 where !field.hasFocus && app.keyboards.count == 0 { press(.down, wait: 1.5) }
            if field.exists, field.hasFocus || app.keyboards.count > 0 {
                field.typeText(movie.map { String($0.title.prefix(4)) } ?? "the")
                pause(4)
            }
        }
        shot("06-search")

        app.openSidebarRow("Settings", wait: 3)
        shot("07-settings")

        // The profile picker, as the app opens when it asks who's watching.
        app.terminate()
        app.launchArguments = arguments + ["-FlexarrAskProfile", "YES"]
        app.launch()
        pause(8)
        shot("08-profiles")
    }

    // MARK: Helpers

    private func open(_ link: String, wait: TimeInterval) {
        app.open(URL(string: link)!)
        pause(wait)
    }

    private func press(_ button: XCUIRemote.Button, times: Int = 1, wait: TimeInterval) {
        for _ in 0..<times {
            remote.press(button)
            pause(wait)
        }
    }

    private func pause(_ seconds: TimeInterval) {
        Thread.sleep(forTimeInterval: seconds)
    }

    private func shot(_ name: String) {
        let screenshot = XCUIScreen.main.screenshot()
        let file = "tv-\(name).png"
        let attachment = XCTAttachment(screenshot: screenshot)
        attachment.name = file
        attachment.lifetime = .keepAlways
        add(attachment)
        if let directory { try? screenshot.pngRepresentation.write(to: directory.appending(path: file)) }
    }
}
