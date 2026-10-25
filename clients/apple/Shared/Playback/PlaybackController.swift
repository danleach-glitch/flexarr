import AVFoundation
import AVKit
#if canImport(AetherEngine)
import AetherEngine
#endif
import FlexarrKit
import Observation
import os
import SwiftUI
import UIKit

private let log = Logger(subsystem: Bundle.main.bundleIdentifier ?? "io.github.flexarr", category: "playback")

/// Where a downloaded file's watching is recorded (to be sent to the server once there's a connection), and which
/// downloaded episode comes after it.
@MainActor
protocol OfflineLibrary: AnyObject {
    func record(itemId: String, position: Double, finished: Bool)
    func nextInfo(after info: PlaybackInfo) -> PlaybackInfo?
}

/// Owns one playback session at a time: starts it on the server, presents
/// AVPlayerViewController, reports progress, offers Skip Intro / Next Episode (contextual actions on
/// Apple TV, a floating button on iPhone and iPad), auto-advances, and stops the session on exit.
@MainActor
@Observable
final class PlaybackController: NSObject {
    /// Why playback stopped, and so what the viewer can do about it (see `retry`).
    enum Problem {
        /// Its stream was ended so another screen could play: watching here again takes the place back.
        case takenOver
        /// The server is already playing all it can on other screens.
        case busy
        case other
    }

    private(set) var isStarting = false
    var errorMessage: String?
    private(set) var problem: Problem = .other
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
    /// What `retry` asks for (nil: nothing to offer).
    @ObservationIgnored private var retryRequest: PlaybackRequest?
    /// The viewer's own subtitle choice in the player's menu for this title: nil if they haven't made one, `.some(nil)`
    /// for off. It outlives a recovered stream, and "off" carries on to the next episode.
    @ObservationIgnored private var viewerSubtitleKey: String??
    /// The current item's subtitle options, once loaded, and the one last selected (by us or the viewer).
    @ObservationIgnored private var legibleGroup: AVMediaSelectionGroup?
    @ObservationIgnored private var selectedSubtitleName: String??
    @ObservationIgnored private var selectionObserver: NSObjectProtocol?
    @ObservationIgnored private var jumpObserver: NSObjectProtocol?
    /// Until when a subtitle change is the player's own rather than the viewer's: it re-chooses subtitles as a seek
    /// lands (with the device muted, iOS's "On (Mute)" turns them back on), which can take a while on a transcode.
    @ObservationIgnored private var subtitleGuardUntil = Date.distantPast
    /// The last position the clock reported, and when, for telling a seek from playing on.
    @ObservationIgnored private var lastTick: (position: Double, at: Date)?
    /// Where downloaded files' watching goes, and their next episodes come from (set by the iOS app).
    @ObservationIgnored weak var offlineLibrary: OfflineLibrary?

    /// Experimental: the file played as it is on the device (AetherEngine demuxes, VideoToolbox decodes), with Apple's
    /// player over the top. On when Settings says so, or `-FlexarrEngine YES`.
    static var usesEngine: Bool { UserDefaults.standard.bool(forKey: "FlexarrEngine") }
    #if canImport(AetherEngine)
    @ObservationIgnored private var engine: AetherEngine?
    #endif
    /// Playing a show on shuffle: each next episode is a random one.
    @ObservationIgnored private var shuffling = false
    /// The profile's auto-play and skip choices, from the session.
    @ObservationIgnored private var preferences = PlaybackPreferences.standard
    /// Intros and recaps already skipped by themselves, so seeking back to watch one isn't overruled.
    @ObservationIgnored private var autoSkipped: Set<String> = []
    /// The episode is over (it ended, or its credits started with Skip credits on): counting down to the next
    /// (`countdown` seconds left), or waiting to be asked with auto-play off (nil).
    @ObservationIgnored private var isFinishing = false
    @ObservationIgnored private var countdown: Int?
    @ObservationIgnored private var countdownTask: Task<Void, Never>?
    /// Cancel was chosen: no more offers of the next episode for this one.
    @ObservationIgnored private var nextCancelled = false
    /// The next episode's session, started during the credits so it opens at once, and the request for it.
    @ObservationIgnored private var prepared: (request: PlaybackRequest, info: PlaybackInfo)?
    @ObservationIgnored private var preparing: Task<Void, Never>?
    private static let countdownSeconds = 5
    #if os(iOS)
    /// Skip Intro / Next Episode, over the player.
    @ObservationIgnored private var promptButton: UIButton?
    /// Beside it while counting down to the next episode.
    @ObservationIgnored private var cancelButton: UIButton?
    /// Playing on in Picture in Picture: the player was put away, but the session goes on.
    @ObservationIgnored private var isInPictureInPicture = false
    /// Picture in Picture is ending by coming back to the full player, rather than by being closed.
    @ObservationIgnored private var isRestoringFromPictureInPicture = false
    #endif

    private static let reportInterval: TimeInterval = 10
    private static let takenOverMessage = "Someone started watching on another screen, so this one had to stop."

    // MARK: Start

    /// Starts playback. `startAt` nil lets the server choose the resume point; 0 starts over. With `shuffle`,
    /// `itemId` is a show: a random episode starts, and every next one is random too.
    func play(itemId: String, preview: ItemSummary?, startAt: Double?, client: FlexarrClient, shuffle: Bool = false,
              subtitleKey: String? = nil) {
        guard !isStarting else { return }
        self.client = client
        shuffling = shuffle
        viewerSubtitleKey = subtitleKey.map { .some($0) }
        var request = request(for: itemId, startAt: startAt)
        if let subtitleKey { request.subtitleKey = subtitleKey }
        start(request)
    }

    /// Plays a downloaded file: no server, no connection needed.
    func playOffline(_ info: PlaybackInfo) {
        guard !isStarting else { return }
        shuffling = false
        viewerSubtitleKey = nil
        do {
            try present(info)
        } catch {
            show(.other, message: "This download couldn't be played. Delete it and download it again.", retry: nil)
        }
    }

    /// The error's main button, Watch here instead or Try again: asks for the stream that stopped again.
    func retry() {
        guard !isStarting, let retryRequest else { return }
        start(retryRequest)
    }

    private func start(_ request: PlaybackRequest) {
        guard let client else { return }
        isStarting = true
        Task {
            defer { isStarting = false }
            do {
                let info = try await client.startPlayback(request)
                try present(info)
            } catch {
                show(error, retry: request)
            }
        }
    }

    /// Says why playback stopped. A stream ended for another screen is asked for afresh, without the
    /// `replacesSessionId` the server refuses for a while afterwards, so watching here takes the place back. A busy
    /// server's refusal keeps it: asking with a stream the server no longer has never takes another screen's place.
    private func show(_ error: Error, retry: PlaybackRequest?) {
        let problem: Problem = switch error {
        case FlexarrClientError.http(status: 410, _): .takenOver
        case FlexarrClientError.http(status: 503, _): .busy
        default: .other
        }
        show(problem, message: ConnectionError(error).message, retry: retry)
    }

    private func show(_ problem: Problem, message: String, retry: PlaybackRequest?) {
        var retry = retry
        if problem == .takenOver { retry?.replacesSessionId = nil }
        self.problem = problem
        retryRequest = problem == .other ? nil : retry
        errorMessage = message
    }

    private func request(for itemId: String, startAt: Double?) -> PlaybackRequest {
        var request = PlaybackRequest(itemId: itemId, profile: Self.deviceProfile, startPositionSeconds: startAt, shuffle: shuffling ? true : nil)
        #if canImport(AetherEngine)
        if Self.usesEngine {
            // The engine carries the file's own subtitles; a picture subtitle mustn't make the server burn one in.
            request.profile = Self.engineProfile
            request.subtitleKey = "off"
        }
        #endif
        return request
    }

    #if canImport(AetherEngine)
    private static var engineProfile: DeviceProfile {
        let modes = AVPlayer.availableHDRModes
        #if os(tvOS)
        let client = "tvos"
        #else
        let client = "ios"
        #endif
        return .engine(client: client, hdr: AVPlayer.eligibleForHDRPlayback, dolbyVision: supportsDolbyVision,
                       hdr10: modes.contains(.hdr10), hlg: modes.contains(.hlg))
    }
    #endif

    private static var deviceProfile: DeviceProfile {
        // What the display shows right now: an Apple TV on a fixed Dolby Vision output, say, has no HDR10 mode and
        // refuses plain HDR10 streams, so those are converted for it instead.
        let modes = AVPlayer.availableHDRModes
        #if os(tvOS)
        return .tvOS(hdr: AVPlayer.eligibleForHDRPlayback, dolbyVision: supportsDolbyVision, hdr10: modes.contains(.hdr10), hlg: modes.contains(.hlg))
        #else
        return .iOS(hdr: AVPlayer.eligibleForHDRPlayback, dolbyVision: supportsDolbyVision, hdr10: modes.contains(.hdr10), hlg: modes.contains(.hlg))
        #endif
    }

    /// The display (or the TV the Apple TV is plugged into) shows Dolby Vision.
    private static var supportsDolbyVision: Bool {
        #if DEBUG
        // `-FlexarrNoDolbyVision YES` asks for HDR10 only, to tell a Dolby Vision problem from an HDR one.
        if UserDefaults.standard.bool(forKey: "FlexarrNoDolbyVision") { return false }
        #endif
        return AVPlayer.availableHDRModes.contains(.dolbyVision)
    }

    private func makeItem(for info: PlaybackInfo) throws -> AVPlayerItem {
        let local = info.isOffline ? URL(string: info.url) : nil
        guard let url = local ?? client?.url(for: info.url) else { throw FlexarrClientError.invalidResponse }
        let item = AVPlayerItem(url: url)
        item.externalMetadata = Self.metadata(for: info, artwork: nil, overview: nil)
        Self.applySubtitleSize(info.preferences?.subtitleSize, to: item)
        return item
    }

    #if DEBUG
    /// What the player received for sound (codec and channels) and where it's going: for checking passthrough.
    private static func logAudioFormat(of item: AVPlayerItem) {
        Task {
            for track in item.tracks {
                guard let assetTrack = track.assetTrack, assetTrack.mediaType == .audio,
                      let formats = try? await assetTrack.load(.formatDescriptions) else { continue }
                for format in formats {
                    let asbd = CMAudioFormatDescriptionGetStreamBasicDescription(format)?.pointee
                    let id = asbd.map { String(format: "%c%c%c%c", ($0.mFormatID >> 24) & 255, ($0.mFormatID >> 16) & 255, ($0.mFormatID >> 8) & 255, $0.mFormatID & 255) } ?? "?"
                    log.info("Audio: \(id, privacy: .public) \(asbd?.mChannelsPerFrame ?? 0) ch")
                }
            }
            let route = AVAudioSession.sharedInstance().currentRoute.outputs.map { "\($0.portType.rawValue) \($0.portName)" }
            log.info("Audio route: \(route.joined(separator: ", "), privacy: .public)")
        }
    }
    #endif

    // MARK: Subtitle timing and size

    /// The timings offered in the player: a pick restarts the stream (about two seconds) with the subtitles moved.
    private static let delayChoices: [Double] = [-2, -1, -0.5, 0, 0.5, 1, 2]

    /// Draws subtitles at the profile's size, as a percentage of the player's own.
    private static func applySubtitleSize(_ size: SubtitleSize?, to item: AVPlayerItem) {
        let percent = (size ?? .standard).percent
        item.textStyleRules = percent == 100 ? nil
            : AVTextStyleRule(textMarkupAttributes: [kCMTextMarkupAttribute_RelativeFontSize as String: percent]).map { [$0] }
    }

    /// "In time", "0.5s later", "1s earlier".
    static func delayLabel(_ seconds: Double) -> String {
        guard seconds != 0 else { return "In time" }
        let amount = abs(seconds).formatted(.number.precision(.fractionLength(0...2)))
        return "\(amount)s \(seconds > 0 ? "later" : "earlier")"
    }

    /// Shows this title's subtitles later (or earlier): remembered on the server, which shifts them, and the stream
    /// opened again where it was so the player fetches them anew.
    func setSubtitleDelay(_ seconds: Double) {
        guard let client, let current = info, !current.isOffline, !isRecovering else { return }
        Task {
            do {
                try await client.setSubtitleDelay(itemId: current.item.id, seconds: seconds)
            } catch {
                log.error("Couldn't move subtitles: \(error.localizedDescription, privacy: .public)")
                return
            }
            guard self.info?.sessionId == current.sessionId else { return }
            let request = PlaybackRequest(
                itemId: current.item.id, profile: Self.deviceProfile, fileId: current.fileId,
                audioStreamIndex: current.selectedAudioIndex, subtitleKey: (viewerSubtitleKey ?? current.selectedSubtitleKey) ?? "off",
                startPositionSeconds: position, replacesSessionId: current.sessionId, shuffle: shuffling ? true : nil)
            isRecovering = true
            defer { isRecovering = false }
            do {
                let info = try await client.startPlayback(request)
                guard self.info?.sessionId == current.sessionId else {
                    try? await client.stopPlayback(sessionId: info.sessionId)
                    return
                }
                let item = try makeItem(for: info)
                player?.replaceCurrentItem(with: item)
                attach(info)
            } catch {
                dismissPlayer { self.show(error, retry: request) }
            }
        }
    }

    /// The profile's subtitle size, changed while watching: drawn at once, and saved for every device.
    func setSubtitleSize(_ size: SubtitleSize) {
        preferences.subtitleSize = size
        if let item = player?.currentItem { Self.applySubtitleSize(size, to: item) }
        updateSubtitleMenu()
        if let client {
            let saved = preferences
            Task { try? await client.setPlaybackPreferences(saved) }
        }
    }

    /// On Apple TV, a Subtitles menu in the player's bar for timing and size (when there are subtitles to adjust).
    private func updateSubtitleMenu() {
        #if os(tvOS)
        guard let viewController, let info else { return }
        guard !info.isOffline, !info.subtitleBurnedIn, info.subtitles.contains(where: \.isText) else {
            viewController.transportBarCustomMenuItems = []
            return
        }
        let current = info.subtitleDelaySeconds ?? 0
        let choices = Set(Self.delayChoices + [current]).sorted()
        let timing = UIMenu(title: "Timing", options: .displayInline, children: choices.map { seconds in
            UIAction(title: Self.delayLabel(seconds), state: seconds == current ? .on : .off) { [weak self] _ in
                MainActor.assumeIsolated { if seconds != current { self?.setSubtitleDelay(seconds) } }
            }
        })
        let size = UIMenu(title: "Size", options: .displayInline, children: SubtitleSize.allCases.map { size in
            UIAction(title: size.label, state: size == (preferences.subtitleSize ?? .standard) ? .on : .off) { [weak self] _ in
                MainActor.assumeIsolated { self?.setSubtitleSize(size) }
            }
        })
        viewController.transportBarCustomMenuItems = [
            UIMenu(title: "Subtitle timing and size", image: UIImage(systemName: "captions.bubble"), children: [timing, size]),
        ]
        #endif
    }

    private func present(_ info: PlaybackInfo) throws {
        stop(reportingTo: nil)
        var throughEngine = false
        #if canImport(AetherEngine)
        throughEngine = Self.usesEngine && !info.isOffline && info.method == .directPlay
        #endif
        // Through the engine, the controller opens empty and gets the engine's own player once the file is open.
        let player = throughEngine ? AVPlayer() : AVPlayer(playerItem: try makeItem(for: info))
        // Subtitles are chosen here (`applySubtitles`), from the server's choice or the viewer's. Left to itself AVPlayer
        // re-chooses by the device's language, turning subtitles back on after the viewer turned them off.
        player.appliesMediaSelectionCriteriaAutomatically = false

        #if os(tvOS)
        let controller = AVPlayerViewController()
        // The engine sets the display's mode (HDR, Dolby Vision) itself; AVKit does so from the server's playlists.
        controller.appliesPreferredDisplayCriteriaAutomatically = !throughEngine
        #else
        let controller = PlayerViewController()
        controller.modalPresentationStyle = .fullScreen
        controller.allowsPictureInPicturePlayback = true
        controller.onDismissed = { [weak self, weak controller] in
            // Put away for Picture in Picture, the session carries on there.
            guard let self, let controller, controller === viewController, !isInPictureInPicture else { return }
            viewController = nil
            stop()
        }
        // Sound on with the ring switch off, and playing on in Picture in Picture or with the screen locked.
        try? AVAudioSession.sharedInstance().setCategory(.playback, mode: .moviePlayback)
        try? AVAudioSession.sharedInstance().setActive(true)
        (promptButton, cancelButton) = makePromptButtons(in: controller)
        #endif
        controller.player = player
        controller.delegate = self

        self.player = player
        self.viewController = controller
        if !throughEngine {
            attach(info)
        }

        guard let presenter = Self.topViewController() else { return }
        presenter.present(controller, animated: true)
        #if canImport(AetherEngine)
        if throughEngine {
            startEngine(info, in: controller)
        }
        #endif
    }

    #if canImport(AetherEngine)
    /// Opens the file itself through AetherEngine and hands its AVPlayer to the controller already on screen, so
    /// everything else here (markers, the countdown, reporting, subtitles) works as it does for a server stream.
    private func startEngine(_ info: PlaybackInfo, in controller: AVPlayerViewController) {
        guard let client, let url = client.url(for: info.url) else { return }
        let engine: AetherEngine
        do {
            engine = try AetherEngine()
        } catch {
            return dismissPlayer { self.show(.other, message: "The player engine couldn't start: \(error.localizedDescription)", retry: nil) }
        }

        self.engine = engine
        var options = LoadOptions()
        options.prepareNativeSubtitles = true   // text subtitles as renditions: Apple's menu, PiP and AirPlay have them
        options.serveIFramePlaylist = true      // Apple's scrubbing thumbnails
        let modes = AVPlayer.availableHDRModes
        options.panelIsInHDRMode = !modes.isEmpty
        options.panelPresentsDolbyVision = modes.contains(.dolbyVision)
        #if os(tvOS)
        options.matchContentEnabled = controller.view.window?.avDisplayManager.isDisplayCriteriaMatchingEnabled ?? false
        #endif
        let start = info.startPositionSeconds
        log.info("Engine: opening \(info.item.title, privacy: .public) at \(start)")
        Task { [weak self] in
            do {
                // The server's choice of audio track (the profile's languages) is the engine's to apply.
                _ = try await engine.load(url: url, startPosition: start > 1 ? start : nil, options: options,
                                          audioSourceStreamIndex: info.selectedAudioIndex.map(Int32.init))
            } catch {
                guard let self, self.engine === engine else { return }
                log.error("Engine failed: \(error.localizedDescription, privacy: .public)")
                return dismissPlayer { self.show(.other, message: "This file couldn't be played on the device: \(error.localizedDescription)", retry: nil) }
            }

            guard let self, self.engine === engine, viewController === controller else { return }
            guard let player = engine.currentAVPlayer else {
                // Software decoding draws into the engine's own view, which this build doesn't have yet.
                return dismissPlayer { self.show(.other, message: "This file needs software decoding, which isn't in this build yet.", retry: nil) }
            }

            player.appliesMediaSelectionCriteriaAutomatically = false
            controller.player = player
            self.player = player
            attach(info)
            // The engine opened the file where playback starts; the controller's own time is the file's.
            pendingSeek = 0
            log.info("Engine route \(engine.videoRoute.rawValue, privacy: .public): player at \(player.currentTime().seconds), engine clock \(engine.clock.currentTime)")
        }
    }
    #endif

    /// Wires observers for the session in `info` onto the current player item.
    private func attach(_ info: PlaybackInfo) {
        guard let player, let item = player.currentItem else { return }
        // The session id is the stream URLs' only credential, so keep it out of shareable logs.
        log.info("Session \(info.sessionId, privacy: .private) \(info.method.rawValue, privacy: .public) for \(info.item.title, privacy: .public), start \(info.startPositionSeconds)")
        self.info = info
        hasPlayed = false
        lastPosition = info.startPositionSeconds
        prompt = .none
        preferences = info.preferences ?? .standard
        autoSkipped = []
        nextCancelled = false
        endFinishing()
        lastReport = Date()
        pendingSeek = info.startPositionSeconds
        updateContextualActions()
        updateSubtitleMenu()
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

        legibleGroup = nil
        selectedSubtitleName = nil
        lastTick = nil
        subtitleGuardUntil = .distantPast
        if let jumpObserver { NotificationCenter.default.removeObserver(jumpObserver) }
        jumpObserver = NotificationCenter.default.addObserver(forName: AVPlayerItem.timeJumpedNotification, object: item, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.seeked() }
        }
        if let selectionObserver { NotificationCenter.default.removeObserver(selectionObserver) }
        selectionObserver = NotificationCenter.default.addObserver(forName: AVPlayerItem.mediaSelectionDidChangeNotification, object: item, queue: .main) { [weak self, weak item] _ in
            MainActor.assumeIsolated {
                if let item { self?.subtitleSelectionChanged(item) }
            }
        }
    }

    // MARK: Subtitles

    /// Selects the viewer's subtitle choice for this title if they've made one, else the server's (its language
    /// preferences, or forced subtitles), else none.
    private func applySubtitles(to item: AVPlayerItem) {
        Task {
            guard let group = try? await item.asset.loadMediaSelectionGroup(for: .legible),
                  let info, item === player?.currentItem else { return }
            let wanted = viewerSubtitleKey ?? info.selectedSubtitleKey
            let index = SubtitleMatch.option(for: wanted, in: info, among: group.options.map(Self.subtitleOption))
            let option = index.map { group.options[$0] }
            legibleGroup = group
            selectedSubtitleName = .some(option?.displayName)
            item.select(option, in: group)
            log.info("Subtitles at start: \(option?.displayName ?? "off", privacy: .public)")
        }
    }

    /// The viewer picked subtitles (or Off) in the player's menu: remember it for this title. A change just after a
    /// seek is the player's own (subtitles when muted), so what was showing before is put back.
    private func subtitleSelectionChanged(_ item: AVPlayerItem) {
        guard item === player?.currentItem, let group = legibleGroup, let info else { return }
        let selected = item.currentMediaSelection.selectedMediaOption(in: group)
        guard selectedSubtitleName != .some(selected?.displayName) else { return } // our own selection
        if Date() < subtitleGuardUntil, case .some(let previous) = selectedSubtitleName {
            log.info("Keeping subtitles \(previous ?? "off", privacy: .public) after a seek (the player chose \(selected?.displayName ?? "off", privacy: .public))")
            item.select(previous.flatMap { name in group.options.first { $0.displayName == name } }, in: group)
            return
        }
        selectedSubtitleName = .some(selected?.displayName)
        if let selected {
            guard let key = SubtitleMatch.key(for: Self.subtitleOption(selected), in: info) else { return }
            viewerSubtitleKey = .some(key)
        } else {
            viewerSubtitleKey = .some(nil)
        }
        log.info("Subtitles chosen in the player: \(selected?.displayName ?? "off", privacy: .public)")
    }

    private static func subtitleOption(_ option: AVMediaSelectionOption) -> SubtitleOption {
        SubtitleOption(name: option.displayName, language: option.extendedLanguageTag ?? option.locale?.identifier,
                       isForced: option.hasMediaCharacteristic(.containsOnlyForcedSubtitles))
    }

    private func itemStatusChanged(_ status: AVPlayerItem.Status, message: String?) {
        switch status {
        case .readyToPlay:
            guard let player else { return }
            hasPlayed = true
            #if DEBUG
            if let item = player.currentItem { Self.logAudioFormat(of: item) }
            #endif
            if let item = player.currentItem { applySubtitles(to: item) }
            // A stream that says where to open (EXT-X-START) is there already: no second trip to the server.
            let opened = player.currentTime().seconds
            if pendingSeek > 1, !(opened.isFinite && abs(opened - pendingSeek) < 2) {
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
            // AVFoundation's own account of what went wrong with the playlists (the error alone says little).
            for event in player?.currentItem?.errorLog()?.events.prefix(5) ?? [] {
                log.error("Item failed, detail: \(event.errorStatusCode) \(event.errorComment ?? "", privacy: .public) \(event.uri ?? "", privacy: .private)")
            }
            if hasPlayed {
                recover(reason: reason)
            } else {
                dismissPlayer {
                    self.show(.other, message: reason, retry: nil)
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
        // A downloaded file that stops won't be any better from the server; say so instead.
        if info?.isOffline == true {
            return dismissPlayer { self.show(.other, message: reason, retry: nil) }
        }
        guard !isRecovering, let client, let current = info else { return }
        let position = self.position > 0 ? self.position : lastPosition
        let failed = current.sessionId
        let retry = PlaybackRequest(
            itemId: current.item.id, profile: Self.deviceProfile, fileId: current.fileId,
            audioStreamIndex: current.selectedAudioIndex, subtitleKey: (viewerSubtitleKey ?? current.selectedSubtitleKey) ?? "off",
            startPositionSeconds: position, replacesSessionId: failed, shuffle: shuffling ? true : nil)
        guard Date().timeIntervalSince(lastRecovery) > 60 else {
            // A stream ended to make way for another screen says so, even inside the once-a-minute limit.
            Task {
                let takenOver = await Self.takenOverReason(sessionId: failed, baseURL: client.baseURL)
                // The player may have closed, or moved on to another title, while that was out.
                guard self.info?.sessionId == failed else { return }
                dismissPlayer {
                    if let takenOver {
                        self.show(.takenOver, message: takenOver, retry: retry)
                    } else {
                        self.show(.other, message: reason, retry: nil)
                    }
                }
            }
            return
        }
        log.info("Recovering playback at \(position)")
        isRecovering = true
        lastRecovery = Date()
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
                    // The server is at its transcode limit, or ended this stream for another screen (410): say so
                    // rather than retrying, which would only take the stream back from that screen.
                    if case FlexarrClientError.http(status: 503, _) = error { break attempts }
                    if case FlexarrClientError.http(status: 410, _) = error { break attempts }
                }
            }
            dismissPlayer {
                if let lastError {
                    self.show(lastError, retry: retry)
                } else {
                    self.show(.other, message: reason, retry: nil)
                }
            }
        }
    }

    /// Why the server ended this transcode for another viewer, if it did: its playlist answers 410 with the reason.
    /// Gives up after a few seconds, so a server that's hung doesn't keep the player waiting.
    private static func takenOverReason(sessionId: String, baseURL: URL) async -> String? {
        var request = URLRequest(url: baseURL.appending(path: "api/v1/playback/\(sessionId)/main.m3u8"))
        request.timeoutInterval = 5
        guard let (data, response) = try? await URLSession.shared.data(for: request),
              (response as? HTTPURLResponse)?.statusCode == 410 else { return nil }
        struct Problem: Decodable { let detail: String? }
        return (try? JSONDecoder().decode(Problem.self, from: data))?.detail ?? takenOverMessage
    }

    // MARK: Progress

    private var position: Double {
        let seconds = player?.currentTime().seconds ?? 0
        return seconds.isFinite ? seconds : 0
    }

    /// A seek began (or landed): guard the subtitles until playback has settled again.
    private func seeked() {
        subtitleGuardUntil = max(subtitleGuardUntil, Date().addingTimeInterval(20))
    }

    private func tick(_ seconds: Double) {
        guard let info, seconds.isFinite, pendingSeek == 0 else { return }
        // The clock moving further than playing would take it is a seek, reported as it starts.
        let now = Date()
        if let lastTick, abs(seconds - lastTick.position - now.timeIntervalSince(lastTick.at) * Double(player?.rate ?? 0)) > 3 {
            seeked()
        }
        lastTick = (seconds, now)
        lastPosition = seconds
        let next = info.prompt(at: seconds)
        // Skip intros and recaps by themselves when the profile asks, once each.
        if case .skip(let type, let end) = next, type == .intro ? preferences.skipIntros : preferences.skipRecaps,
           autoSkipped.insert("\(type.rawValue)@\(end)").inserted {
            log.info("Skipping the \(type.rawValue, privacy: .public) by itself")
            skip(to: end)
            return
        }
        // Skip credits: the episode is over as soon as they start.
        if preferences.skipCredits, info.next != nil, !nextCancelled, !isFinishing,
           let credits = info.markers.first(where: { $0.type == .credits }), seconds >= credits.start {
            player?.pause()
            report(paused: true, ended: true)
            beginFinishing()
        }
        if next != prompt {
            log.info("Prompt \(String(describing: next), privacy: .public) at \(seconds)")
            prompt = next
            updateContextualActions()
            // The credits started: get the next episode ready. Back out of them: let it go.
            if next == .nextEpisode { prepareNext() } else if !isFinishing { dropPrepared() }
        }
        if Date().timeIntervalSince(lastReport) >= Self.reportInterval, player?.timeControlStatus == .playing {
            report(paused: false)
        }
    }

    private func timeControlChanged(_ status: AVPlayer.TimeControlStatus) {
        // Playing again after a seek: the player has made its choice by now, give or take a moment.
        if status == .playing, subtitleGuardUntil > Date() {
            subtitleGuardUntil = min(subtitleGuardUntil, Date().addingTimeInterval(4))
        }
        if status == .paused, pendingSeek == 0, info != nil {
            report(paused: true)
        }
    }

    private func report(paused: Bool, ended: Bool = false) {
        if let info, info.isOffline {
            lastReport = Date()
            if recordsHistory { offlineLibrary?.record(itemId: info.item.id, position: position, finished: ended || isPastCredits(info)) }
            return
        }
        guard recordsHistory, let client, let sessionId = info?.sessionId else { return }
        lastReport = Date()
        let position = self.position
        Task { try? await client.reportProgress(sessionId: sessionId, positionSeconds: position, paused: paused, ended: ended) }
    }

    // MARK: Markers and next episode

    /// The request for the episode after `current`. Subtitles turned off carry on to it; a chosen track is the
    /// server's to match there. The player's Off keeps forced subtitles (foreign dialogue) on, which counts as off.
    private func nextRequest(_ next: ItemSummary, after current: PlaybackInfo) -> PlaybackRequest {
        var request = request(for: next.id, startAt: nil)
        let forced = Set(current.subtitles.filter(\.isForced).map(\.key))
        if case .some(let key) = viewerSubtitleKey, key.map(forced.contains) ?? true {
            request.subtitleKey = "off"
        }
        return request
    }

    /// Starts the next episode's session now, during the credits, so it opens at once when its turn comes. A busy
    /// server says no, and then it starts the usual way.
    /// Into the credits, or nearly at the end: as good as watched.
    private func isPastCredits(_ info: PlaybackInfo) -> Bool {
        let credits = info.markers.first(where: { $0.type == .credits })?.start
        return position >= (credits ?? info.durationSeconds * 0.92) && position > 0
    }

    private func prepareNext() {
        guard let client, let current = info, !current.isOffline, let next = current.next, !nextCancelled,
              prepared?.info.item.id != next.id, preparing == nil else { return }
        var request = nextRequest(next, after: current)
        request.prepare = true
        let playing = current.sessionId
        preparing = Task {
            defer { preparing = nil }
            guard let ready = try? await client.startPlayback(request) else { return }
            // Still wanted? (Not if this episode was closed, or moved on from, meanwhile.)
            guard info?.sessionId == playing, !nextCancelled else {
                try? await client.stopPlayback(sessionId: ready.sessionId)
                return
            }
            log.info("Next episode ready: \(ready.item.title, privacy: .public)")
            prepared = (request, ready)
        }
    }

    /// Lets the prepared session go (best effort): it wasn't wanted, or playback ended.
    private func dropPrepared() {
        preparing?.cancel()
        preparing = nil
        guard let prepared, let client else { self.prepared = nil; return }
        self.prepared = nil
        Task { try? await client.stopPlayback(sessionId: prepared.info.sessionId) }
    }

    /// The episode is over: count down to the next one if auto-play is on, else wait to be asked.
    private func beginFinishing() {
        guard info?.next != nil, !isFinishing else { return }
        isFinishing = true
        prepareNext()
        countdownTask?.cancel()
        if preferences.autoPlayNext {
            countdown = Self.countdownSeconds
            countdownTask = Task { [weak self] in
                while let self, let left = countdown, left > 0 {
                    guard (try? await Task.sleep(for: .seconds(1))) != nil, isFinishing else { return }
                    countdown = left - 1
                    updateContextualActions()
                }
                guard let self, isFinishing, countdown == 0 else { return }
                advance()
            }
        } else {
            countdown = nil
        }
        updateContextualActions()
    }

    private func endFinishing() {
        isFinishing = false
        countdown = nil
        countdownTask?.cancel()
        countdownTask = nil
        updateContextualActions()
    }

    /// Not now: stop counting, and if the credits were skipped, let them play.
    private func cancelNext() {
        nextCancelled = true
        dropPrepared()
        let ended = player.map { $0.currentTime() >= ($0.currentItem?.duration ?? .positiveInfinity) } ?? true
        endFinishing()
        if !ended { player?.play() }
    }

    /// "Next Episode in 4" (on shuffle too: the next one just happens to be random).
    private var nextTitle: String {
        countdown.map { "Next Episode in \($0)" } ?? "Next Episode"
    }

    private func updateContextualActions() {
        #if os(tvOS)
        guard let viewController else { return }
        if isFinishing {
            var actions = [UIAction(title: nextTitle, image: UIImage(systemName: "forward.fill")) { [weak self] _ in
                MainActor.assumeIsolated { self?.advance() }
            }]
            if countdown != nil {
                actions.append(UIAction(title: "Cancel", image: UIImage(systemName: "xmark")) { [weak self] _ in
                    MainActor.assumeIsolated { self?.cancelNext() }
                })
            }
            viewController.contextualActions = actions
            return
        }
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
                UIAction(title: nextTitle, image: UIImage(systemName: "forward.fill")) { [weak self] _ in
                    MainActor.assumeIsolated { self?.advance() }
                },
            ]
        }
        #else
        guard let promptButton, let cancelButton else { return }
        let title: String? = if isFinishing {
            nextTitle
        } else {
            switch prompt {
            case .none: nil
            case .skip(let type, _): type == .recap ? "Skip Recap" : "Skip Intro"
            case .nextEpisode: nextTitle
            }
        }
        if let title {
            // Updated every second while counting: no animation, so the text doesn't flicker.
            UIView.performWithoutAnimation {
                promptButton.configuration?.title = title
                promptButton.configuration?.image = UIImage(systemName: isFinishing || prompt == .nextEpisode ? "forward.fill" : "forward.end.fill")
                promptButton.layoutIfNeeded()
            }
        }
        let cancelShown = isFinishing && countdown != nil
        UIView.animate(withDuration: 0.2) {
            promptButton.alpha = title == nil ? 0 : 1
            cancelButton.alpha = cancelShown ? 1 : 0
        }
        promptButton.isUserInteractionEnabled = title != nil
        cancelButton.isUserInteractionEnabled = cancelShown
        #endif
    }

    #if os(iOS)
    /// A pill in the corner above the scrubber, like the TV's contextual action, and Cancel to its left for the
    /// countdown to the next episode.
    private func makePromptButtons(in controller: AVPlayerViewController) -> (UIButton, UIButton) {
        var configuration = UIButton.Configuration.filled()
        configuration.baseBackgroundColor = UIColor(Color.screen).withAlphaComponent(0.95)
        configuration.baseForegroundColor = UIColor(Color.room)
        configuration.cornerStyle = .capsule
        configuration.imagePadding = 8
        configuration.contentInsets = NSDirectionalEdgeInsets(top: 12, leading: 18, bottom: 12, trailing: 20)
        configuration.titleTextAttributesTransformer = UIConfigurationTextAttributesTransformer { attributes in
            var attributes = attributes
            attributes.font = .systemFont(ofSize: 16, weight: .semibold)
            return attributes
        }
        let button = UIButton(configuration: configuration, primaryAction: UIAction { [weak self] _ in
            MainActor.assumeIsolated { self?.promptTapped() }
        })
        button.alpha = 0
        button.isUserInteractionEnabled = false
        button.translatesAutoresizingMaskIntoConstraints = false
        controller.view.addSubview(button)
        var cancelConfiguration = UIButton.Configuration.filled()
        cancelConfiguration.baseBackgroundColor = UIColor.black.withAlphaComponent(0.55)
        cancelConfiguration.baseForegroundColor = .white
        cancelConfiguration.cornerStyle = .capsule
        cancelConfiguration.contentInsets = NSDirectionalEdgeInsets(top: 12, leading: 18, bottom: 12, trailing: 18)
        cancelConfiguration.title = "Cancel"
        cancelConfiguration.titleTextAttributesTransformer = configuration.titleTextAttributesTransformer
        let cancel = UIButton(configuration: cancelConfiguration, primaryAction: UIAction { [weak self] _ in
            MainActor.assumeIsolated { self?.cancelNext() }
        })
        cancel.alpha = 0
        cancel.isUserInteractionEnabled = false
        cancel.translatesAutoresizingMaskIntoConstraints = false
        controller.view.addSubview(cancel)
        NSLayoutConstraint.activate([
            button.trailingAnchor.constraint(equalTo: controller.view.safeAreaLayoutGuide.trailingAnchor, constant: -20),
            button.bottomAnchor.constraint(equalTo: controller.view.safeAreaLayoutGuide.bottomAnchor, constant: -84),
            cancel.trailingAnchor.constraint(equalTo: button.leadingAnchor, constant: -10),
            cancel.centerYAnchor.constraint(equalTo: button.centerYAnchor),
        ])
        return (button, cancel)
    }

    private func promptTapped() {
        if isFinishing { return advance() }
        switch prompt {
        case .none: break
        case .skip(_, let end): skip(to: end)
        case .nextEpisode: advance()
        }
    }
    #endif

    private func skip(to end: Double) {
        player?.seek(to: CMTime(seconds: end, preferredTimescale: 600), toleranceBefore: .zero, toleranceAfter: .zero)
        prompt = .none
        updateContextualActions()
    }

    /// At the end: the countdown to the next episode (or the button, with auto-play off); the player closes when
    /// there's nothing next or Cancel was chosen.
    private func itemEnded() {
        report(paused: true, ended: true)
        if info?.next != nil, !nextCancelled {
            beginFinishing()
        } else {
            dismissPlayer()
        }
    }

    /// Ends this session and plays `info.next` in the same player.
    private func advance() {
        // A downloaded episode moves on to the next downloaded one, in the same player.
        if let current = info, current.isOffline {
            report(paused: false, ended: true)
            endFinishing()
            guard let nextInfo = offlineLibrary?.nextInfo(after: current), let item = try? makeItem(for: nextInfo) else {
                return dismissPlayer()
            }
            player?.replaceCurrentItem(with: item)
            attach(nextInfo)
            return
        }
        guard !isAdvancing, let client, let current = info, let next = current.next else { return }
        log.info("Advancing to \(next.title, privacy: .public)")
        isAdvancing = true
        endFinishing()
        report(paused: false, ended: true)
        let finished = current.sessionId
        Task {
            defer { isAdvancing = false }
            try? await client.stopPlayback(sessionId: finished)
            do {
                let nextRequest = nextRequest(next, after: current)
                viewerSubtitleKey = nextRequest.subtitleKey == "off" ? .some(nil) : nil
                // Got ready during the credits (with the same subtitles): it opens straight away.
                let info: PlaybackInfo
                if let ready = prepared, ready.info.item.id == next.id, ready.request.subtitleKey == nextRequest.subtitleKey {
                    prepared = nil
                    log.info("Using the next episode got ready during the credits")
                    info = ready.info
                } else {
                    dropPrepared()
                    info = try await client.startPlayback(nextRequest)
                }
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
                    self.show(error, retry: self.request(for: next.id, startAt: nil))
                }
            }
        }
    }

    // MARK: Stop

    /// Stops the current session (reporting the final position) and tears down observers.
    func stop() {
        stop(reportingTo: client, recording: true)
    }

    /// `recording`: a real stop, so a downloaded file's position is kept (not when one session replaces another).
    private func stop(reportingTo client: FlexarrClient?, recording: Bool = false) {
        if let info, info.isOffline {
            if recording, recordsHistory { offlineLibrary?.record(itemId: info.item.id, position: position, finished: isPastCredits(info)) }
        } else if let info, let client {
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
        #if canImport(AetherEngine)
        engine?.stop()
        engine = nil
        #endif
        if let timeObserver { player?.removeTimeObserver(timeObserver) }
        if let endObserver { NotificationCenter.default.removeObserver(endObserver) }
        if let selectionObserver { NotificationCenter.default.removeObserver(selectionObserver) }
        if let jumpObserver { NotificationCenter.default.removeObserver(jumpObserver) }
        timeObserver = nil
        endObserver = nil
        selectionObserver = nil
        jumpObserver = nil
        legibleGroup = nil
        observations = []
        info = nil
        player = nil
        prompt = .none
        isFinishing = false
        countdown = nil
        countdownTask?.cancel()
        countdownTask = nil
        dropPrepared()
        if client != nil { onSessionEnded?() }
    }

    private func dismissPlayer(then completion: (() -> Void)? = nil) {
        stop()
        guard let controller = viewController else { completion?(); return }
        viewController = nil
        // In Picture in Picture the player isn't on screen, and dismissing it would never call back.
        guard controller.presentingViewController != nil else { completion?(); return }
        controller.dismiss(animated: true) { completion?() }
    }

    // MARK: Metadata

    /// Fetches the overview and artwork and refreshes the info panel once they arrive.
    private func loadRichMetadata(for info: PlaybackInfo, into item: AVPlayerItem) {
        guard let client, !info.isOffline else { return }
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
    #if os(tvOS)
    nonisolated func playerViewControllerWillBeginDismissalTransition(_ playerViewController: AVPlayerViewController) {
        MainActor.assumeIsolated {
            guard playerViewController === viewController else { return }
            viewController = nil
            stop()
        }
    }
    #else
    nonisolated func playerViewControllerWillStartPictureInPicture(_ playerViewController: AVPlayerViewController) {
        MainActor.assumeIsolated { isInPictureInPicture = true }
    }

    nonisolated func playerViewController(_ playerViewController: AVPlayerViewController,
                                          failedToStartPictureInPictureWithError error: any Error) {
        MainActor.assumeIsolated { isInPictureInPicture = false }
    }

    /// Back to the full player from Picture in Picture.
    nonisolated func playerViewController(_ playerViewController: AVPlayerViewController,
                                          restoreUserInterfaceForPictureInPictureStopWithCompletionHandler completionHandler: @escaping (Bool) -> Void) {
        // AVKit asks, and takes the answer, on the main thread; the handler just isn't marked Sendable.
        nonisolated(unsafe) let completionHandler = completionHandler
        MainActor.assumeIsolated {
            guard playerViewController === viewController, let presenter = Self.topViewController() else {
                completionHandler(false)
                return
            }
            isRestoringFromPictureInPicture = true
            presenter.present(playerViewController, animated: true) { completionHandler(true) }
        }
    }

    /// Picture in Picture closed without coming back to the player: that's the end of watching.
    nonisolated func playerViewControllerDidStopPictureInPicture(_ playerViewController: AVPlayerViewController) {
        MainActor.assumeIsolated {
            isInPictureInPicture = false
            defer { isRestoringFromPictureInPicture = false }
            guard !isRestoringFromPictureInPicture, playerViewController === viewController,
                  playerViewController.presentingViewController == nil else { return }
            viewController = nil
            stop()
        }
    }
    #endif
}

#if os(iOS)
/// The player, saying when it's been closed (its X, or a swipe down). On Apple TV the delegate says so.
final class PlayerViewController: AVPlayerViewController {
    var onDismissed: (() -> Void)?

    override func viewDidDisappear(_ animated: Bool) {
        super.viewDidDisappear(animated)
        if isBeingDismissed { onDismissed?() }
    }
}
#endif
