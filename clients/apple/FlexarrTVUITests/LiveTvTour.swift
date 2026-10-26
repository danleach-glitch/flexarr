import FlexarrKit
import XCTest

/// Drives Live TV with the Siri Remote against a server with Live TV set up, saving a screenshot at each stop: the
/// tab, a channel playing, the next channel (the remote's bottom edge) and back. Same environment as `ScreenshotTour`.
@MainActor
final class LiveTvTour: XCTestCase {
    private var app: XCUIApplication!
    private var client: FlexarrClient!
    private var outputDirectory: URL?
    private let remote = XCUIRemote.shared

    override func setUp() async throws {
        let env = ProcessInfo.processInfo.environment
        guard let server = env["FLEXARR_SERVER"], let key = env["FLEXARR_KEY"],
              let address = FlexarrClient.normalizeAddress(server) else {
            throw XCTSkip("Set TEST_RUNNER_FLEXARR_SERVER and TEST_RUNNER_FLEXARR_KEY to run the tour.")
        }
        if let path = env["FLEXARR_SCREENSHOTS"] {
            let directory = URL(fileURLWithPath: path)
            try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            outputDirectory = directory
        }
        client = FlexarrClient(baseURL: address, apiKey: key)
        app = XCUIApplication()
        app.launchArguments = ["-FlexarrServer", address.absoluteString, "-FlexarrApiKey", key, "-FlexarrNoHistory", "YES"]
        app.launch()
        pause(6)
    }

    func testLiveTv() async throws {
        guard try await client.liveTvStatus()?.configured == true else { throw XCTSkip("no Live TV source") }
        // The Live TV tab comes after Home and the libraries (podcasts aren't on the Apple TV).
        let libraries = try await client.libraries().filter { $0.type != .podcasts }
        shot("49-home")
        press(.up, wait: 1.5)
        press(.right, times: libraries.count + 1, wait: 1.2)
        pause(4)
        shot("50-live-tv")
        // Into the categories, across to the channels, and down a few.
        press(.down, wait: 2)
        shot("51-live-tv-categories")
        press(.down, wait: 2)
        shot("51b-live-tv-all")
        press(.right, wait: 2)
        press(.down, times: 3, wait: 0.8)
        pause(1)
        shot("51c-live-tv-list")

        press(.select, wait: 9)
        shot("52-live-playing")
        // Controls hidden: the bottom edge changes channel.
        pause(6)
        press(.down, wait: 10)
        shot("53-live-next-channel")
        press(.select, wait: 1)
        press(.select, wait: 1.5)
        shot("54-live-controls")
        press(.menu, wait: 1.5)
        press(.menu, wait: 3)
        shot("55-live-back")
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
        let attachment = XCTAttachment(screenshot: screenshot)
        attachment.name = name
        attachment.lifetime = .keepAlways
        add(attachment)
        if let outputDirectory {
            try? screenshot.pngRepresentation.write(to: outputDirectory.appending(path: "\(name).png"))
        }
    }
}
