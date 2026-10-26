import FlexarrKit
import os
import UIKit

private let log = Logger(subsystem: Bundle.main.bundleIdentifier ?? "io.github.flexarr", category: "livetv")

/// Watching a live channel: asks the server for it, puts it on the player screen with the rewind window as the
/// timeline, keeps the server told the viewer is there, and changes channel (Previous and Next, or the remote's
/// top and bottom). "Back to live" appears once the picture is well behind.
@MainActor
@Observable
final class LiveTvController: NSObject {
    private(set) var isStarting = false
    /// The channel being started, for its card to show a spinner.
    private(set) var startingChannel: String?
    var errorMessage: String?
    /// Called as a channel is asked for, before its screen comes up (so music can give way).
    @ObservationIgnored var onWillStart: (() -> Void)?
    /// Called once watching ends (so paused music can be offered again).
    @ObservationIgnored var onEnded: (() -> Void)?

    @ObservationIgnored private var client: FlexarrClient?
    @ObservationIgnored private var channels: [LiveChannel] = []
    @ObservationIgnored private var watching: LiveWatch?
    @ObservationIgnored private var screen: PlayerViewController?
    @ObservationIgnored private var transport: LiveTransport?
    @ObservationIgnored private var heartbeat: Task<Void, Never>?
    @ObservationIgnored private var starting: Task<Void, Never>?
    @ObservationIgnored private var promptShown = false

    private static let heartbeatInterval: Duration = .seconds(15)

    /// Plays `channel`; `channels` is the order Previous and Next go through (favourites first, as listed).
    func watch(_ channel: LiveChannel, among channels: [LiveChannel], client: FlexarrClient) {
        self.client = client
        self.channels = channels.isEmpty ? [channel] : channels
        start(channel)
    }

    private func start(_ channel: LiveChannel) {
        guard let client else { return }
        onWillStart?()
        starting?.cancel()
        isStarting = true
        startingChannel = channel.id
        errorMessage = nil
        // The old channel's picture goes first: the server lets it go as this one starts.
        let previous = watching?.viewer
        transport?.stop()
        transport = nil
        screen?.transport = nil
        screen?.showLoading()
        showTitle(for: channel, programme: channel.now)
        starting = Task {
            defer {
                if !Task.isCancelled {
                    isStarting = false
                    startingChannel = nil
                }
            }
            do {
                let started = try await client.watchLiveChannel(channel.id, replacing: previous)
                guard !Task.isCancelled else {
                    Task { try? await client.stopLiveChannel(viewer: started.viewer) }
                    return
                }
                try present(started)
            } catch is CancellationError {
                return
            } catch {
                if Task.isCancelled { return }
                log.error("Couldn't start \(channel.name, privacy: .public): \(error.localizedDescription, privacy: .public)")
                watching = nil
                close(message: Self.message(for: error))
            }
        }
    }

    private func present(_ started: LiveWatch) throws {
        guard let client, let url = client.url(for: started.playlist) else { throw FlexarrClientError.invalidResponse }
        log.info("Watching \(started.channel.name, privacy: .public) (\(started.method, privacy: .public))")
        watching = started
        promptShown = false
        let screen: PlayerViewController
        if let current = self.screen {
            screen = current
        } else {
            screen = PlayerViewController()
            screen.delegate = self
            screen.zapsChannels = true
            screen.controls.isLive = true
            self.screen = screen
            guard let presenter = Self.topViewController() else { throw FlexarrClientError.invalidResponse }
            presenter.present(screen, animated: true)
        }
        let transport = LiveTransport(url: url)
        self.transport = transport
        screen.transport = transport
        screen.setEpisodeNavigation(hasPrevious: channels.count > 1, hasNext: channels.count > 1)
        screen.controls.previousButton.accessibilityLabel = "Previous channel"
        screen.controls.nextButton.accessibilityLabel = "Next channel"
        screen.setDetail(started.method == "Original" ? nil : started.method)
        showTitle(for: started.channel, programme: started.channel.now)
        loadLogo(started.channel, into: screen)
        keepAlive(viewer: started.viewer)
    }

    private func showTitle(for channel: LiveChannel, programme: LiveProgramme?) {
        let times = programme.map { " · " + Self.timeRange($0) } ?? ""
        screen?.setTitle(programme?.displayTitle ?? channel.name, subtitle: "\(channel.numberText) \(channel.name)\(times)")
    }

    /// Paused players fetch nothing, so the server is told the viewer is still here. What's on is refreshed with it.
    private func keepAlive(viewer: String) {
        heartbeat?.cancel()
        heartbeat = Task { [weak self] in
            var beats = 0
            while !Task.isCancelled {
                try? await Task.sleep(for: Self.heartbeatInterval)
                guard let self, !Task.isCancelled, let client = self.client, self.watching?.viewer == viewer else { return }
                try? await client.liveHeartbeat(viewer: viewer)
                beats += 1
                if beats % 4 == 0, let fresh = try? await client.liveChannels(), let id = self.watching?.channel.id,
                   let channel = fresh.first(where: { $0.id == id }) {
                    self.showTitle(for: channel, programme: channel.now)
                }
            }
        }
    }

    /// The next channel up or down, in the order the screen listed them.
    private func step(_ by: Int) {
        guard let current = watching?.channel ?? startingChannel.flatMap({ id in channels.first { $0.id == id } }),
              let index = channels.firstIndex(where: { $0.id == current.id }), channels.count > 1 else { return }
        start(channels[(index + by + channels.count) % channels.count])
    }

    func stop() {
        starting?.cancel()
        starting = nil
        heartbeat?.cancel()
        heartbeat = nil
        transport?.stop()
        transport = nil
        isStarting = false
        startingChannel = nil
        if let viewer = watching?.viewer, let client {
            Task { try? await client.stopLiveChannel(viewer: viewer) }
        }
        watching = nil
    }

    private func close(message: String? = nil) {
        stop()
        let finish = { [weak self] in
            if let message { self?.errorMessage = message }
        }
        onEnded?()
        guard let screen else { finish(); return }
        self.screen = nil
        screen.finish(completion: finish)
    }

    private static func message(for error: Error) -> String {
        if case FlexarrClientError.http(_, let message?) = error { return message }
        return error.localizedDescription
    }

    private static let clock: DateFormatter = {
        let formatter = DateFormatter()
        formatter.timeStyle = .short
        return formatter
    }()

    static func startTime(_ programme: LiveProgramme) -> String {
        clock.string(from: programme.start)
    }

    static func timeRange(_ programme: LiveProgramme) -> String {
        "\(clock.string(from: programme.start)) – \(clock.string(from: programme.end))"
    }

    /// The channel's logo for the lock screen and Now Playing.
    private func loadLogo(_ channel: LiveChannel, into screen: PlayerViewController) {
        guard let client, let url = client.imageURL(channel.logoUrl, width: 600) else { return }
        Task {
            let image = await ImageLoader.shared.load(url)
            guard self.watching?.channel.id == channel.id else { return }
            screen.setArtwork(image)
        }
    }

    private static func topViewController() -> UIViewController? {
        let scene = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first
        var top = scene?.keyWindow?.rootViewController ?? scene?.windows.first?.rootViewController
        while let presented = top?.presentedViewController { top = presented }
        return top
    }
}

extension LiveTvController: PlayerScreenDelegate {
    func playerScreen(_ screen: PlayerViewController, phaseChanged phase: TransportPhase) {
        if case .failed(let message) = phase { close(message: message) }
    }

    /// Offers "Back to live" once the picture is well behind, and takes it away at live.
    func playerScreen(_ screen: PlayerViewController, tick seconds: Double) {
        guard let transport else { return }
        let behind = !transport.isAtLive
        if behind != promptShown {
            promptShown = behind
            screen.setPrompt(behind ? "Back to live" : nil, countingDown: false)
        }
    }

    func playerScreenTracksChanged(_ screen: PlayerViewController) {}
    func playerScreen(_ screen: PlayerViewController, didSeekTo seconds: Double) {}

    func playerScreenDidClose(_ screen: PlayerViewController) {
        guard screen === self.screen else { return }
        close()
    }

    func playerScreenPrevious(_ screen: PlayerViewController) { step(-1) }
    func playerScreenNext(_ screen: PlayerViewController) { step(1) }

    func playerScreenPromptTapped(_ screen: PlayerViewController) {
        transport?.goLive()
    }

    func playerScreenCancelNext(_ screen: PlayerViewController) {}
    func playerScreen(_ screen: PlayerViewController, selectAudio id: String) {}
    func playerScreen(_ screen: PlayerViewController, choseSubtitle key: String??) {}
    func playerScreen(_ screen: PlayerViewController, subtitleDelay seconds: Double) {}
    func playerScreen(_ screen: PlayerViewController, subtitleSize size: SubtitleSize) {}
}
