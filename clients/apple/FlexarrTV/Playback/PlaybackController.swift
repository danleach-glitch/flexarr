import AVFoundation
import AVKit
import FlexarrKit
import Observation
import os
import UIKit

private let log = Logger(subsystem: "io.github.flexarr.tv", category: "playback")

/// Owns one playback session at a time: starts it on the server, presents
/// AVPlayerViewController, reports progress, offers Skip Intro / Next Episode as
/// contextual actions, auto-advances, and stops the session on exit.
@MainActor
@Observable
final class PlaybackController: NSObject {
    private(set) var isStarting = false
    var errorMessage: String?
    @ObservationIgnored var onSessionEnded: (() -> Void)?
    /// When false, sessions are still started and stopped but progress is never reported.
    @ObservationIgnored var recordsHistory = true

    @ObservationIgnored private var client: FlexarrClient?
    @ObservationIgnored private var info: PlaybackInfo?
    @ObservationIgnored private var player: AVPlayer?
    @ObservationIgnored private var viewController: AVPlayerViewController?
    @ObservationIgnored private var timeObserver: Any?
    @ObservationIgnored private var observations: [NSKeyValueObservation] = []
    @ObservationIgnored private var endObserver: NSObjectProtocol?
    @ObservationIgnored private var lastReport = Date.distantPast
    @ObservationIgnored private var prompt: PlaybackPrompt = .none
    @ObservationIgnored private var pendingSeek: Double = 0
    @ObservationIgnored private var isAdvancing = false
    @ObservationIgnored private var isRecovering = false
    @ObservationIgnored private var hasPlayed = false
    @ObservationIgnored private var lastRecovery = Date.distantPast
    /// Where playback last was, for recovering after the item fails (when its own time may be gone).
    @ObservationIgnored private var lastPosition: Double = 0

    private static let reportInterval: TimeInterval = 10

    // MARK: Start

    /// Starts playback. `startAt` nil lets the server choose the resume point; 0 starts over.
    func play(itemId: String, preview: ItemSummary?, startAt: Double?, client: FlexarrClient) {
        guard !isStarting else { return }
        self.client = client
        isStarting = true
        Task {
            defer { isStarting = false }
            do {
                let info = try await client.startPlayback(request(for: itemId, startAt: startAt))
                try present(info)
            } catch {
                errorMessage = ConnectionError(error).message
            }
        }
    }

    private func request(for itemId: String, startAt: Double?) -> PlaybackRequest {
        PlaybackRequest(itemId: itemId, profile: .tvOS(hdr: AVPlayer.eligibleForHDRPlayback), startPositionSeconds: startAt)
    }

    private func makeItem(for info: PlaybackInfo) throws -> AVPlayerItem {
        guard let client, let url = client.url(for: info.url) else { throw FlexarrClientError.invalidResponse }
        let item = AVPlayerItem(url: url)
        item.externalMetadata = Self.metadata(for: info, artwork: nil, overview: nil)
        return item
    }

    private func present(_ info: PlaybackInfo) throws {
        stop(reportingTo: nil)
        let item = try makeItem(for: info)
        let player = AVPlayer(playerItem: item)
        player.appliesMediaSelectionCriteriaAutomatically = true

        let controller = AVPlayerViewController()
        controller.player = player
        controller.delegate = self
        controller.appliesPreferredDisplayCriteriaAutomatically = true

        self.player = player
        self.viewController = controller
        attach(info)

        guard let presenter = Self.topViewController() else { return }
        presenter.present(controller, animated: true)
    }

    /// Wires observers for the session in `info` onto the current player item.
    private func attach(_ info: PlaybackInfo) {
        guard let player, let item = player.currentItem else { return }
        // The session id is the stream URLs' only credential, so keep it out of shareable logs.
        log.info("Session \(info.sessionId, privacy: .private) \(info.method.rawValue, privacy: .public) for \(info.item.title, privacy: .public), start \(info.startPositionSeconds)")
        self.info = info
        hasPlayed = false
        lastPosition = info.startPositionSeconds
        prompt = .none
        lastReport = Date()
        pendingSeek = info.startPositionSeconds
        updateContextualActions()
        loadRichMetadata(for: info, into: item)

        observations = [
            item.observe(\.status, options: [.new]) { [weak self] item, _ in
                let status = item.status
                let message = item.error?.localizedDescription
                Task { @MainActor in self?.itemStatusChanged(status, message: message) }
            },
            player.observe(\.timeControlStatus, options: [.new]) { [weak self] player, _ in
                let status = player.timeControlStatus
                Task { @MainActor in self?.timeControlChanged(status) }
            },
        ]

        if timeObserver == nil {
            timeObserver = player.addPeriodicTimeObserver(forInterval: CMTime(seconds: 1, preferredTimescale: 600), queue: .main) { [weak self] time in
                MainActor.assumeIsolated { self?.tick(time.seconds) }
            }
        }

        if let endObserver { NotificationCenter.default.removeObserver(endObserver) }
        endObserver = NotificationCenter.default.addObserver(forName: AVPlayerItem.didPlayToEndTimeNotification, object: item, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.itemEnded() }
        }
    }

    private func itemStatusChanged(_ status: AVPlayerItem.Status, message: String?) {
        switch status {
        case .readyToPlay:
            guard let player else { return }
            hasPlayed = true
            if pendingSeek > 1 {
                let target = CMTime(seconds: pendingSeek, preferredTimescale: 600)
                pendingSeek = 0
                player.seek(to: target, toleranceBefore: .zero, toleranceAfter: .zero) { [weak player] _ in
                    Task { @MainActor in player?.play() }
                }
            } else {
                pendingSeek = 0
                player.play()
            }
        case .failed:
            let reason = message ?? "The stream couldn't be opened."
            log.error("Item failed: \(reason, privacy: .public)")
            if hasPlayed {
                recover(reason: reason)
            } else {
                dismissPlayer {
                    self.errorMessage = reason
                }
            }
        default:
            break
        }
    }

    /// A stream that was playing stopped: the server restarted, say, or forgot a transcode. Ask for a fresh session
    /// from where we were (retrying while the server comes back) before giving up, at most once a minute: a stream
    /// that keeps stopping is a real problem worth showing.
    private func recover(reason: String) {
        guard !isRecovering, let client, let current = info else { return }
        guard Date().timeIntervalSince(lastRecovery) > 60 else {
            dismissPlayer { self.errorMessage = reason }
            return
        }
        let position = self.position > 0 ? self.position : lastPosition
        log.info("Recovering playback at \(position)")
        isRecovering = true
        lastRecovery = Date()
        let failed = current.sessionId
        let retry = PlaybackRequest(
            itemId: current.item.id, profile: .tvOS(hdr: AVPlayer.eligibleForHDRPlayback), fileId: current.fileId,
            audioStreamIndex: current.selectedAudioIndex, subtitleKey: current.selectedSubtitleKey ?? "off",
            startPositionSeconds: position, replacesSessionId: failed)
        Task {
            defer { isRecovering = false }
            var lastError: Error?
            attempts: for attempt in 0..<6 {
                if attempt > 0 { try? await Task.sleep(for: .seconds(3)) }
                guard self.info?.sessionId == failed else { return } // Closed (or moved on) meanwhile.
                do {
                    let info = try await client.startPlayback(retry)
                    // The player may have closed, or moved on to another title, while that was out.
                    guard self.info?.sessionId == failed else {
                        try? await client.stopPlayback(sessionId: info.sessionId)
                        return
                    }
                    let item = try makeItem(for: info)
                    player?.replaceCurrentItem(with: item)
                    attach(info)
                    return
                } catch {
                    lastError = error
                    // The server is at its transcode limit: say so rather than retrying.
                    if case FlexarrClientError.http(status: 503, _) = error { break attempts }
                }
            }
            dismissPlayer {
                self.errorMessage = lastError.map { ConnectionError($0).message } ?? reason
            }
        }
    }

    // MARK: Progress

    private var position: Double {
        let seconds = player?.currentTime().seconds ?? 0
        return seconds.isFinite ? seconds : 0
    }

    private func tick(_ seconds: Double) {
        guard let info, seconds.isFinite, pendingSeek == 0 else { return }
        lastPosition = seconds
        let next = info.prompt(at: seconds)
        if next != prompt {
            log.info("Prompt \(String(describing: next), privacy: .public) at \(seconds)")
            prompt = next
            updateContextualActions()
        }
        if Date().timeIntervalSince(lastReport) >= Self.reportInterval, player?.timeControlStatus == .playing {
            report(paused: false)
        }
    }

    private func timeControlChanged(_ status: AVPlayer.TimeControlStatus) {
        if status == .paused, pendingSeek == 0, info != nil {
            report(paused: true)
        }
    }

    private func report(paused: Bool, ended: Bool = false) {
        guard recordsHistory, let client, let sessionId = info?.sessionId else { return }
        lastReport = Date()
        let position = self.position
        Task { try? await client.reportProgress(sessionId: sessionId, positionSeconds: position, paused: paused, ended: ended) }
    }

    // MARK: Markers and next episode

    private func updateContextualActions() {
        guard let viewController else { return }
        switch prompt {
        case .none:
            viewController.contextualActions = []
        case .skip(let type, let end):
            let title = type == .recap ? "Skip Recap" : "Skip Intro"
            viewController.contextualActions = [
                UIAction(title: title, image: UIImage(systemName: "forward.end.fill")) { [weak self] _ in
                    MainActor.assumeIsolated { self?.skip(to: end) }
                },
            ]
        case .nextEpisode:
            viewController.contextualActions = [
                UIAction(title: "Next Episode", image: UIImage(systemName: "forward.fill")) { [weak self] _ in
                    MainActor.assumeIsolated { self?.advance() }
                },
            ]
        }
    }

    private func skip(to end: Double) {
        player?.seek(to: CMTime(seconds: end, preferredTimescale: 600), toleranceBefore: .zero, toleranceAfter: .zero)
        prompt = .none
        updateContextualActions()
    }

    private func itemEnded() {
        report(paused: true, ended: true)
        if info?.next != nil {
            advance()
        } else {
            dismissPlayer()
        }
    }

    /// Ends this session and plays `info.next` in the same player.
    private func advance() {
        guard !isAdvancing, let client, let current = info, let next = current.next else { return }
        log.info("Advancing to \(next.title, privacy: .public)")
        isAdvancing = true
        report(paused: false, ended: true)
        let finished = current.sessionId
        Task {
            defer { isAdvancing = false }
            try? await client.stopPlayback(sessionId: finished)
            do {
                let info = try await client.startPlayback(request(for: next.id, startAt: nil))
                guard self.info?.sessionId == finished else { // Closed meanwhile.
                    try? await client.stopPlayback(sessionId: info.sessionId)
                    return
                }
                let item = try makeItem(for: info)
                player?.replaceCurrentItem(with: item)
                attach(info)
                onSessionEnded?()
            } catch {
                self.info = nil
                dismissPlayer {
                    self.errorMessage = ConnectionError(error).message
                }
            }
        }
    }

    // MARK: Stop

    /// Stops the current session (reporting the final position) and tears down observers.
    func stop() {
        stop(reportingTo: client)
    }

    private func stop(reportingTo client: FlexarrClient?) {
        if let info, let client {
            log.info("Stopping session \(info.sessionId, privacy: .private)")
            let position = self.position
            let sessionId = info.sessionId
            let record = recordsHistory
            Task {
                if record {
                    try? await client.reportProgress(sessionId: sessionId, positionSeconds: position, paused: true)
                }
                try? await client.stopPlayback(sessionId: sessionId)
            }
        }
        player?.pause()
        if let timeObserver { player?.removeTimeObserver(timeObserver) }
        if let endObserver { NotificationCenter.default.removeObserver(endObserver) }
        timeObserver = nil
        endObserver = nil
        observations = []
        info = nil
        player = nil
        prompt = .none
        if client != nil { onSessionEnded?() }
    }

    private func dismissPlayer(then completion: (() -> Void)? = nil) {
        stop()
        guard let controller = viewController else { completion?(); return }
        viewController = nil
        controller.dismiss(animated: true) { completion?() }
    }

    // MARK: Metadata

    /// Fetches the overview and artwork and refreshes the info panel once they arrive.
    private func loadRichMetadata(for info: PlaybackInfo, into item: AVPlayerItem) {
        guard let client else { return }
        let summary = info.item
        let artworkPath = summary.kind == .episode
            ? (summary.artwork.thumb ?? info.ancestors.first?.artwork.poster)
            : summary.artwork.poster
        Task {
            async let detail = try? client.item(summary.id)
            async let artwork: Data? = {
                guard let url = client.imageURL(artworkPath, width: 600) else { return nil }
                return await ImageLoader.shared.data(url)
            }()
            let (overview, data) = await (detail?.overview, artwork)
            guard self.info?.sessionId == info.sessionId else { return }
            item.externalMetadata = Self.metadata(for: info, artwork: data, overview: overview)
        }
    }

    private static func metadata(for info: PlaybackInfo, artwork: Data?, overview: String?) -> [AVMetadataItem] {
        let item = info.item
        var items: [AVMetadataItem] = [
            metadataItem(.commonIdentifierTitle, item.displayTitle as NSString),
        ]
        if let subtitle = item.episodeSubtitle ?? item.releaseYear.map({ String($0) }) {
            items.append(metadataItem(.iTunesMetadataTrackSubTitle, subtitle as NSString))
        }
        if let overview, !overview.isEmpty {
            items.append(metadataItem(.commonIdentifierDescription, overview as NSString))
        }
        if let rating = item.contentRating ?? info.ancestors.first?.contentRating {
            items.append(metadataItem(.iTunesMetadataContentRating, rating as NSString))
        }
        if let artwork {
            items.append(metadataItem(.commonIdentifierArtwork, artwork as NSData))
        }
        return items
    }

    private static func metadataItem(_ identifier: AVMetadataIdentifier, _ value: NSCopying & NSObjectProtocol) -> AVMetadataItem {
        let item = AVMutableMetadataItem()
        item.identifier = identifier
        item.value = value
        item.extendedLanguageTag = "und"
        return item.copy() as! AVMetadataItem
    }

    private static func topViewController() -> UIViewController? {
        let scene = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first
        var top = scene?.keyWindow?.rootViewController ?? scene?.windows.first?.rootViewController
        while let presented = top?.presentedViewController { top = presented }
        return top
    }
}

extension PlaybackController: AVPlayerViewControllerDelegate {
    nonisolated func playerViewControllerWillBeginDismissalTransition(_ playerViewController: AVPlayerViewController) {
        MainActor.assumeIsolated {
            guard playerViewController === viewController else { return }
            viewController = nil
            stop()
        }
    }
}
