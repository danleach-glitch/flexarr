#if canImport(AetherEngine)
import AetherEngine
#endif
import AVFoundation
import FlexarrKit
import Observation
import SwiftUI
import UIKit

private let log = FlexarrLog("playback")

/// Where a downloaded file's watching is recorded (to be sent to the server once there's a connection), and which
/// downloaded episode comes after it.
@MainActor
protocol OfflineLibrary: AnyObject {
    func record(itemId: String, position: Double, finished: Bool)
    /// Marked unwatched part way through: a download starts from the top again, and its unsent place is dropped.
    func forgetPosition(itemId: String)
    /// Marked watched (an item, or a whole show): a podcast episode's download goes.
    func markedPlayed(itemId: String)
    func nextInfo(after info: PlaybackInfo) -> PlaybackInfo?
}

/// Owns one playback session at a time: starts it on the server, puts the player screen up with the right transport
/// (Apple's player over a server stream or a download, or the engine over the file itself), reports progress, offers
/// Skip Intro and Next Episode, auto-advances, moves between episodes, and stops the session on exit.
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
    /// Called as a title is asked for, before its screen comes up (so music can give way).
    @ObservationIgnored var onWillStart: (() -> Void)?
    /// Called when something is watched into its credits; `closing` when the player is going away with it, rather
    /// than moving on to the next episode.
    @ObservationIgnored var onWatchedToEnd: ((_ closing: Bool) -> Void)?
    /// When false, sessions are still started and stopped but progress is never reported.
    @ObservationIgnored var recordsHistory = true
    /// Whether the subtitles menu may offer a search and Re-sync to audio (not on a kids profile).
    @ObservationIgnored var offersSubtitleSearch: () -> Bool = { true }

    @ObservationIgnored private var client: FlexarrClient?
    @ObservationIgnored private var info: PlaybackInfo?
    @ObservationIgnored private var screen: PlayerViewController?
    @ObservationIgnored private var transport: PlayerTransport?
    @ObservationIgnored private var lastReport = Date.distantPast
    /// The last progress report or stop sent: each waits for the one before, so the server hears them in order.
    @ObservationIgnored private var reporting: Task<Void, Never>?
    /// The last report said paused, so playing again is reported straight away (automations follow it).
    @ObservationIgnored private var reportedPaused = false
    /// The tracks and sound settings last told to remotes, so a report only carries them when they've changed.
    @ObservationIgnored private var reportedTracks: PlayerTracks?
    @ObservationIgnored private var reportedSound: DeviceSound?
    @ObservationIgnored private var prompt: PlaybackPrompt = .none
    @ObservationIgnored private var isAdvancing = false
    @ObservationIgnored private var isRecovering = false
    @ObservationIgnored private var hasPlayed = false
    @ObservationIgnored private var lastRecovery = Date.distantPast
    /// Where playback last was, for recovering after the stream fails (when its own time may be gone).
    @ObservationIgnored private var lastPosition: Double = 0
    /// What `retry` asks for (nil: nothing to offer).
    @ObservationIgnored private var retryRequest: PlaybackRequest?
    /// The viewer's own subtitle choice in the player's menu for this title: nil if they haven't made one, `.some(nil)`
    /// for off. It outlives a recovered stream, and "off" carries on to the next episode.
    @ObservationIgnored private var viewerSubtitleKey: String??
    /// Where downloaded files' watching goes, and their next episodes come from (set by the iOS app).
    @ObservationIgnored weak var offlineLibrary: OfflineLibrary?
    /// Saving a nudge (and, for the server's stream, opening it again with the sound moved), once the nudging stops.
    @ObservationIgnored private var audioDelaySave: Task<Void, Never>?
    /// The speed the title was playing at when its stream was opened again (another audio track, moved sound, a
    /// recovery), to carry on at.
    @ObservationIgnored private var carriedRate: Float?
    /// Follows the sound settings, to open a server-shaped stream again when they change.
    @ObservationIgnored private var soundObserver: NSObjectProtocol?

    /// The file played as it is on the device (AetherEngine demuxes it, the device decodes it): the normal way files
    /// play. Off (Settings, or the launch argument `-FlexarrEngine NO`), the server sends a stream instead. Both
    /// Settings screens read and write this, so the default lives in one place.
    static var usesEngine: Bool {
        get {
            // The key only exists once someone has chosen, so a user who turned it off before the default changed stays off.
            let defaults = UserDefaults.standard
            return defaults.object(forKey: engineKey) == nil || defaults.bool(forKey: engineKey)
        }
        set { UserDefaults.standard.set(newValue, forKey: engineKey) }
    }
    private static let engineKey = "FlexarrEngine"

    /// TrueHD, DTS-HD and FLAC sent as multichannel PCM, rather than converted to Dolby Digital Plus. Until the viewer
    /// chooses, on when what the device is plugged into takes six channels or more of PCM (a receiver, an eARC
    /// soundbar): the conversion can't keep up with a 4K film's lossless soundtrack on some Apple TVs.
    static var losslessAudio: Bool {
        get {
            let defaults = UserDefaults.standard
            if defaults.object(forKey: losslessKey) != nil { return defaults.bool(forKey: losslessKey) }
            return AVAudioSession.sharedInstance().maximumOutputNumberOfChannels >= 6
        }
        set { UserDefaults.standard.set(newValue, forKey: losslessKey) }
    }
    private static let losslessKey = "FlexarrEngineLossless"
    /// A title the engine couldn't play: its server stream instead, until the next title.
    @ObservationIgnored private var engineSuppressedItem: String?
    /// Playing a show on shuffle: each next episode is a random one.
    @ObservationIgnored private var shuffling = false
    /// What was played before the current title in this sitting, for Previous Episode (on shuffle, the server can't say).
    @ObservationIgnored private var trail: [ItemSummary] = []
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
    /// A re-sync of the file's subtitles being followed: which title and file, and the polling.
    @ObservationIgnored private var subtitleSync: (itemId: String, fileId: String, task: Task<Void, Never>)?
    private static let countdownSeconds = 5
    private static let reportInterval: TimeInterval = 10
    private static let takenOverMessage = "Someone started watching on another screen, so this one had to stop."

    // MARK: Start

    /// Starts playback. `startAt` nil lets the server choose the resume point; 0 starts over. With `shuffle`,
    /// `itemId` is a show: a random episode starts, and every next one is random too.
    func play(itemId: String, preview: ItemSummary?, startAt: Double?, client: FlexarrClient, shuffle: Bool = false,
              subtitleKey: String? = nil) {
        guard !isStarting else { return }
        onWillStart?()
        self.client = client
        shuffling = shuffle
        trail = []
        carriedRate = nil
        viewerSubtitleKey = subtitleKey.map { .some($0) }
        var request = request(for: itemId, startAt: startAt)
        if let subtitleKey { request.subtitleKey = subtitleKey }
        start(request)
    }

    /// Plays a downloaded file: no server, no connection needed.
    func playOffline(_ info: PlaybackInfo) {
        guard !isStarting else { return }
        onWillStart?()
        shuffling = false
        trail = []
        carriedRate = nil
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
        onWillStart?()
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
                log.error("Couldn't start playback of \(request.itemId)", error: error)
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
        request.sound = SoundSettings.current.requested(at: Date())
        #if canImport(AetherEngine)
        if Self.usesEngine, engineSuppressedItem != itemId {
            request.profile = Self.engineProfile
        }
        #endif
        return request
    }

    /// The request that opens the current title again where it is: after moving its subtitles, or with another audio
    /// track, or when its stream failed.
    private func reopenRequest(_ current: PlaybackInfo, audioIndex: Int? = nil) -> PlaybackRequest {
        var request = request(for: current.item.id, startAt: position > 0 ? position : lastPosition)
        request.fileId = current.fileId
        request.audioStreamIndex = audioIndex ?? current.selectedAudioIndex
        // The viewer's pick ("off" included) wins; otherwise what's showing, or nothing, and the server decides.
        request.subtitleKey = viewerSubtitleKey.map { $0 ?? "off" } ?? current.selectedSubtitleKey
        request.replacesSessionId = current.sessionId
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

    // MARK: The screen and its transport

    /// Puts the player screen up (if it isn't already) with a transport for `info`, and attaches the session.
    private func present(_ info: PlaybackInfo) throws {
        stop(reportingTo: nil)
        let screen: PlayerViewController
        if let current = self.screen {
            screen = current
        } else {
            screen = PlayerViewController()
            screen.delegate = self
            self.screen = screen
            guard let presenter = Self.topViewController() else { throw FlexarrClientError.invalidResponse }
            presenter.present(screen, animated: true)
        }
        try attach(info, to: screen)
    }

    /// Makes the transport for `info` and wires the session: the engine for a file it can play, else Apple's player.
    private func attach(_ info: PlaybackInfo, to screen: PlayerViewController) throws {
        // Never the session id, which is the stream URLs' only credential: the playback id stands for it.
        log.info("Playing \(info.item.title): \(info.method.rawValue) from \(Int(info.startPositionSeconds))s",
                 ["playbackId": info.playbackId, "reasons": info.reasons.isEmpty ? nil : info.reasons.joined(separator: "; ")])
        self.info = info
        if soundObserver == nil {
            soundObserver = NotificationCenter.default.addObserver(forName: SoundSettings.changed, object: nil, queue: .main) { [weak self] _ in
                MainActor.assumeIsolated { self?.soundSettingsChanged() }
            }
        }
        hasPlayed = false
        lastPosition = info.startPositionSeconds
        prompt = .none
        preferences = info.preferences ?? .standard
        autoSkipped = []
        nextCancelled = false
        endFinishing()
        lastReport = Date()
        // A new session on the server knows nothing yet: its first report tells remotes the tracks and sound.
        reportedTracks = nil
        reportedSound = nil

        let transport = try makeTransport(for: info)
        self.transport = transport
        screen.transport = transport
        screen.setTitle(info.item.displayTitle, subtitle: info.item.episodeSubtitle)
        screen.markers = info.markers
        screen.setDetail(Self.detail(for: info))
        screen.previews = client.flatMap { info.isOffline ? nil : TrickplayPreviews(client: $0, sessionId: info.sessionId) }
        screen.setSubtitleOptions(delay: info.isOffline || info.subtitleBurnedIn || transport is EngineTransportMarker ? nil : (info.subtitleDelaySeconds ?? 0),
                                  size: preferences.subtitleSize ?? .standard, placement: preferences.subtitlePlacement)
        // Audio sync needs the server (which keeps the offset for the household); a download plays as it was made.
        screen.setAudioSync(seconds: info.isOffline || !info.hasPlayerControls ? nil : info.audioDelaySeconds ?? 0)
        screen.episodeEnd = info.item.kind == .episode
            ? .init(credits: info.markers.first(where: { $0.type == .credits })?.start, stopsAtCredits: preferences.skipCredits && info.next != nil)
            : nil
        updateSubtitleSearch()
        updateEpisodeNavigation()
        loadArtwork(for: info, into: screen)
    }

    private func makeTransport(for info: PlaybackInfo) throws -> PlayerTransport {
        let local = info.isOffline ? URL(string: info.url) : nil
        guard let url = local ?? client?.url(for: info.url) else { throw FlexarrClientError.invalidResponse }
        #if canImport(AetherEngine)
        if Self.usesEngine, !info.isOffline, info.method == .directPlay, let client, engineSuppressedItem != info.item.id {
            let engine = try EngineTransport(info: info, client: client)
            var matchContent = false
            #if os(tvOS)
            matchContent = screen?.view.window?.avDisplayManager.isDisplayCriteriaMatchingEnabled ?? false
            #endif
            engine.start(at: info.startPositionSeconds, audioIndex: info.selectedAudioIndex, subtitleKey: viewerSubtitleKey, matchContent: matchContent)
            return engine
        }
        #endif
        return AVPlayerTransport(url: url, info: info, startAt: info.startPositionSeconds, subtitleKey: viewerSubtitleKey)
    }

    /// The engine couldn't play this title: ask the server for its stream instead, from the same place.
    private func fallBackFromEngine(_ info: PlaybackInfo, reason: String) {
        log.warning("Engine fell back to the server for \(info.item.title): \(reason)", ["playbackId": info.playbackId])
        engineSuppressedItem = info.item.id
        carriedRate = transport?.rate
        transport?.stop()
        transport = nil
        guard let client else { return }
        let position = lastPosition > 0 ? lastPosition : info.startPositionSeconds
        Task { try? await client.stopPlayback(sessionId: info.sessionId) }
        var request = request(for: info.item.id, startAt: position)
        request.subtitleKey = viewerSubtitleKey.map { $0 ?? "off" } ?? info.selectedSubtitleKey
        self.info = nil
        start(request)
    }

    private func updateEpisodeNavigation() {
        guard let info else { return }
        let hasNext = info.next != nil || (info.isOffline && offlineLibrary?.nextInfo(after: info) != nil)
        // Previous always works: with nothing before, it starts this title again.
        screen?.setEpisodeNavigation(hasPrevious: true, hasNext: hasNext)
    }

    // MARK: Subtitles and audio

    /// Shows this title's subtitles later (or earlier): remembered on the server, which shifts them, and the stream
    /// opened again where it was so the player fetches them anew.
    func setSubtitleDelay(_ seconds: Double) {
        guard let client, let current = info, !current.isOffline, !isRecovering else { return }
        Task {
            do {
                try await client.setSubtitleDelay(itemId: current.item.id, seconds: seconds)
            } catch {
                log.warning("Couldn't move subtitles", error: error, ["playbackId": current.playbackId])
                return
            }
            guard self.info?.sessionId == current.sessionId else { return }
            reopen(reopenRequest(current), replacing: current)
        }
    }

    /// The profile's subtitle size, changed while watching: drawn at once, and saved for every device.
    func setSubtitleSize(_ size: SubtitleSize) {
        preferences.subtitleSize = size
        savePreferences()
    }

    /// Where the profile's subtitles sit, changed while watching: drawn at once, and saved for every device.
    func setSubtitlePlacement(_ placement: SubtitlePlacement) {
        preferences.subtitlePosition = placement.position
        preferences.subtitlesInBlackBars = placement.inBlackBars
        savePreferences()
    }

    private func savePreferences() {
        if let client {
            let saved = preferences
            Task { try? await client.setPlaybackPreferences(saved) }
        }
    }

    /// Moves the file's sound later (earlier when negative) for everyone.
    /// The engine moves it at once and the server is told once the nudging stops; the server's stream has it moved
    /// there, so it's opened again where it was once the nudging has settled.
    func setAudioDelay(_ seconds: Double) {
        guard let client, let current = info, !current.isOffline, current.hasPlayerControls else { return }
        let seconds = max(-2, min(2, (seconds * 1000).rounded() / 1000))
        let itemId = current.item.id
        audioDelaySave?.cancel()
        #if canImport(AetherEngine)
        if let engine = transport as? EngineTransport, current.audioDelayInStream != true {
            engine.setAudioDelay(seconds)
            audioDelaySave = Task {
                guard (try? await Task.sleep(for: .milliseconds(500))) != nil else { return }
                do {
                    try await client.setAudioDelay(itemId: itemId, seconds: seconds)
                } catch {
                    log.warning("Couldn't save the audio sync", error: error, ["playbackId": current.playbackId])
                }
            }
            return
        }
        #endif
        audioDelaySave = Task {
            guard (try? await Task.sleep(for: .seconds(1))) != nil else { return }
            do {
                try await client.setAudioDelay(itemId: itemId, seconds: seconds)
            } catch {
                log.warning("Couldn't save the audio sync", error: error, ["playbackId": current.playbackId])
                return
            }
            // Another audio track or a recovery may be opening meanwhile: once it's open, it's the one to redo.
            while isRecovering {
                guard (try? await Task.sleep(for: .milliseconds(200))) != nil else { return }
            }
            guard !Task.isCancelled, let now = self.info, now.item.id == itemId, !isRecovering else { return }
            let inStream = now.audioDelayInStream == true ? now.audioDelaySeconds ?? 0 : 0
            guard abs(inStream - seconds) > 0.0005 else { return }
            log.info("Opening the stream again with the sound moved \(seconds)s", ["playbackId": now.playbackId])
            reopen(reopenRequest(now), replacing: now)
        }
    }

    /// The server shapes the sound of a stream it converts: with other settings, it's opened again where it is. The
    /// engine shapes what it plays itself, at once.
    private func soundSettingsChanged() {
        guard let current = info, transport is AVPlayerTransport, current.method == .transcode, current.soundShaped != nil,
              !isRecovering else { return }
        let wanted = SoundSettings.current.requested(at: Date())
        guard current.soundShaped == true || wanted != nil else { return }
        log.info("Opening the stream again with the sound settings changed", ["playbackId": current.playbackId])
        reopen(reopenRequest(current), replacing: current)
    }

    /// Opens the current title again with `request` (another audio track, moved subtitles) in the same screen.
    private func reopen(_ request: PlaybackRequest, replacing current: PlaybackInfo) {
        guard let client, let screen else { return }
        carriedRate = transport?.rate
        isRecovering = true
        Task {
            defer { isRecovering = false }
            do {
                let info = try await client.startPlayback(request)
                guard self.info?.sessionId == current.sessionId else {
                    try? await client.stopPlayback(sessionId: info.sessionId)
                    return
                }
                transport?.stop()
                try attach(info, to: screen)
            } catch {
                dismissPlayer { self.show(error, retry: request) }
            }
        }
    }

    // MARK: Finding and re-syncing subtitles

    /// What the subtitles menu offers: searching (a server with Bazarr, a title it manages, streamed rather than
    /// downloaded), and re-syncing the subtitle files beside the video.
    private func updateSubtitleSearch() {
        guard let screen else { return }
        let available = offersSubtitleSearch() && info.map { !$0.isOffline && $0.canSearchSubtitles == true } ?? false
        let files = Set(info?.subtitles.filter { $0.isExternal == true }.map(\.key) ?? [])
        let resyncing = subtitleSync.map { $0.itemId == info?.item.id && $0.fileId == info?.fileId } ?? false
        screen.setSubtitleSearch(available: available, files: files, resyncing: resyncing)
    }

    /// The search over the picture, paused meanwhile. Subtitles chosen there are saved beside the file, and the title
    /// opens again where it was with them on.
    private func searchSubtitles() {
        guard let client, let current = info, !current.isOffline, let screen, screen.presentedViewController == nil else { return }
        let wasPlaying = transport?.isPlaying ?? false
        transport?.pause()
        weak var host: UIViewController?
        let search = SubtitleSearchView(client: client, itemId: current.item.id, fileId: current.fileId, onDownloaded: { [weak self] saved in
            log.info("Subtitles downloaded: \(saved.label) (\(saved.key))", ["playbackId": current.playbackId])
            host?.dismiss(animated: true)
            guard let self, self.info?.sessionId == current.sessionId, !self.isRecovering else { return }
            self.viewerSubtitleKey = .some(saved.key)
            self.reopen(self.reopenRequest(current), replacing: current)
        }, onClose: { [weak self] in
            host?.dismiss(animated: true)
            if wasPlaying, self?.info?.sessionId == current.sessionId { self?.transport?.play() }
        })
        let controller = UIHostingController(rootView: search)
        #if os(tvOS)
        controller.modalPresentationStyle = .overFullScreen
        controller.view.backgroundColor = .clear
        #else
        controller.modalPresentationStyle = .pageSheet
        controller.sheetPresentationController?.detents = [.medium(), .large()]
        controller.sheetPresentationController?.prefersGrabberVisible = true
        // Closed with Done (not swiped away), so playing carries on through `onClose`.
        controller.isModalInPresentation = true
        #endif
        host = controller
        screen.present(controller, animated: true)
    }

    /// Asks the server to line the subtitle file `key` up with the audio, then follows it: when it's done the title
    /// opens again where it is (the file has changed), when it fails the viewer is told why. Playing carries on.
    private func resyncSubtitles(key: String) {
        guard let client, offersSubtitleSearch(), let current = info, !current.isOffline else { return }
        let itemId = current.item.id
        let fileId = current.fileId
        if let running = subtitleSync {
            // Already following this file's; one for a title played earlier is let go.
            if running.itemId == itemId, running.fileId == fileId { return }
            cancelSubtitleSync()
        }
        log.info("Re-syncing subtitles \(key)", ["playbackId": current.playbackId])
        let task = Task { [weak self] in
            do {
                try await client.startSubtitleSync(itemId: itemId, fileId: fileId, key: key)
            } catch FlexarrClientError.http(status: 409, _) {
                // One is already running for this file: follow that.
            } catch {
                self?.endSubtitleSync(failure: error.localizedDescription)
                return
            }
            self?.screen?.flash("Re-syncing subtitles…")
            var misses = 0
            // A film's re-sync takes a minute or more; given up on after half an hour.
            for _ in 0..<600 {
                guard (try? await Task.sleep(for: .seconds(3))) != nil else { return }
                let status: SubtitleSyncStatus
                do {
                    status = try await client.subtitleSync(itemId: itemId, fileId: fileId)
                    misses = 0
                } catch {
                    misses += 1
                    if misses >= 5 { self?.endSubtitleSync(failure: error.localizedDescription); return }
                    continue
                }
                switch status.state {
                case .running, .unknown:
                    continue
                case .done:
                    self?.endSubtitleSync(doneWith: status.key ?? key)
                    return
                case .failed:
                    self?.endSubtitleSync(failure: status.error ?? "The subtitles couldn't be re-synced.")
                    return
                case .idle:
                    self?.endSubtitleSync(failure: "The re-sync stopped before it finished.")
                    return
                }
            }
            self?.endSubtitleSync(failure: "The re-sync is taking too long. Try again later.")
        }
        subtitleSync = (itemId, fileId, task)
        updateSubtitleSearch()
    }

    /// The re-sync finished: still watching that file, it opens again where it is with the re-synced subtitles on.
    private func endSubtitleSync(doneWith key: String) {
        guard let sync = subtitleSync else { return }
        subtitleSync = nil
        updateSubtitleSearch()
        guard let current = info, current.item.id == sync.itemId, current.fileId == sync.fileId, !isRecovering else { return }
        log.info("Subtitles re-synced", ["playbackId": current.playbackId])
        screen?.flash("Subtitles re-synced")
        viewerSubtitleKey = .some(key)
        reopen(reopenRequest(current), replacing: current)
    }

    private func endSubtitleSync(failure message: String) {
        guard let sync = subtitleSync else { return }
        subtitleSync = nil
        updateSubtitleSearch()
        log.warning("Subtitles weren't re-synced: \(message)", ["playbackId": info?.playbackId])
        guard let current = info, current.item.id == sync.itemId, let screen, screen.presentedViewController == nil else { return }
        let alert = UIAlertController(title: "Couldn't re-sync subtitles", message: message, preferredStyle: .alert)
        alert.addAction(UIAlertAction(title: "OK", style: .default))
        screen.present(alert, animated: true)
    }

    private func cancelSubtitleSync() {
        subtitleSync?.task.cancel()
        subtitleSync = nil
    }

    // MARK: Progress

    private var position: Double {
        let seconds = transport?.currentTime ?? 0
        return seconds.isFinite ? seconds : 0
    }

    private func tick(_ seconds: Double) {
        guard let info, seconds.isFinite, hasPlayed else { return }
        lastPosition = seconds
        let next = info.prompt(at: seconds)
        // Skip intros and recaps by themselves when the profile asks, once each.
        if case .skip(let type, let end) = next, type == .intro ? preferences.skipIntros : preferences.skipRecaps,
           autoSkipped.insert("\(type.rawValue)@\(end)").inserted {
            log.info("Skipping the \(type.rawValue) by itself", ["playbackId": info.playbackId])
            skip(to: end)
            return
        }
        // The sleep timer stops this episode at its end (or has run out): it doesn't go on to the next by itself.
        let sleepHolds = screen?.sleepHoldsEpisode == true
        // Skip credits: the episode is over as soon as they start.
        if preferences.skipCredits, info.next != nil, !nextCancelled, !isFinishing, !sleepHolds,
           let credits = info.markers.first(where: { $0.type == .credits }), seconds >= credits.start {
            transport?.pause()
            report(paused: true, ended: true)
            beginFinishing()
        }
        if next != prompt {
            log.debug("Prompt \(String(describing: next)) at \(Int(seconds))s", ["playbackId": info.playbackId])
            prompt = next
            updatePrompt()
            reportForRemotes()
            // The credits started: get the next episode ready. Back out of them: let it go.
            if next == .nextEpisode { if !sleepHolds { prepareNext() } } else if !isFinishing { dropPrepared() }
        }
        if Date().timeIntervalSince(lastReport) >= Self.reportInterval, transport?.phase == .playing {
            report(paused: false)
        }
    }

    private func phaseChanged(_ phase: TransportPhase) {
        guard let info else { return }
        switch phase {
        case .playing:
            // Reported as soon as it's under way, so a remote on another device picks it up at once.
            let first = !hasPlayed
            if first { applyStartingRate(info) }
            hasPlayed = true
            if reportedPaused || first { report(paused: false) }
        case .paused:
            if hasPlayed { report(paused: true) }
        case .ended:
            itemEnded()
        case .failed(let reason):
            #if canImport(AetherEngine)
            if transport is EngineTransport, !hasPlayed {
                fallBackFromEngine(info, reason: reason)
                return
            }
            #endif
            if hasPlayed {
                recover(reason: reason)
            } else {
                dismissPlayer { self.show(.other, message: reason, retry: nil) }
            }
        default:
            break
        }
    }

    /// Once the title is playing: the speed it was at before its stream was opened again, or the show's remembered one.
    private func applyStartingRate(_ info: PlaybackInfo) {
        let rate = carriedRate ?? info.speed.map(Float.init)
        carriedRate = nil
        guard let rate, rate != 1, let screen else { return }
        log.info("Playing at \(rate)×", ["playbackId": info.playbackId])
        screen.applyRate(rate)
    }

    /// `at`: where it's going (a seek that hasn't landed yet), rather than where the player's clock is.
    private func report(paused: Bool, ended: Bool = false, at target: Double? = nil) {
        if let info, info.isOffline {
            lastReport = Date()
            if recordsHistory { offlineLibrary?.record(itemId: info.item.id, position: position, finished: ended || isPastCredits(info)) }
            return
        }
        guard recordsHistory, let client, let sessionId = info?.sessionId else { return }
        lastReport = Date()
        reportedPaused = paused && !ended
        let position = target ?? self.position
        let remote = remoteReport()
        send { [weak self] in
            // Tracks and sound count as told only once a report carrying them has gone through.
            guard (try? await client.reportProgress(sessionId: sessionId, positionSeconds: position, paused: paused, ended: ended, remote: remote)) != nil,
                  remote.tracks != nil || remote.sound != nil else { return }
            await MainActor.run {
                guard let self, self.info?.sessionId == sessionId else { return }
                if let tracks = remote.tracks { self.reportedTracks = tracks }
                if let sound = remote.sound { self.reportedSound = sound }
            }
        }
    }

    /// What a remote on another device needs, with the tracks and sound only when they've changed since the last report
    /// that went through.
    private func remoteReport() -> RemoteReport {
        let tracks = screen?.remoteTracks
        let settings = SoundSettings.current
        let sound = DeviceSound(dialogueBoost: settings.dialogueBoost.rawValue, nightMode: settings.nightMode.rawValue, speakers: settings.speakers.rawValue)
        return RemoteReport(rate: Double(transport?.rate ?? 1), prompt: remotePrompt, hasNext: info?.next != nil,
                            tracks: tracks == reportedTracks ? nil : tracks, sound: sound == reportedSound ? nil : sound)
    }

    /// The button over the picture, as a remote offers it.
    private var remotePrompt: PlayerPrompt? {
        if isFinishing { return info?.next != nil ? .nextEpisode : nil }
        switch prompt {
        case .none: return nil
        case .skip(let type, _): return type == .recap ? .skipRecap : .skipIntro
        case .nextEpisode: return screen?.sleepHoldsEpisode == true ? nil : .nextEpisode
        }
    }

    /// Something a remote shows changed (a seek, the tracks, the button over the picture, the sound): tells the
    /// server now rather than at the next regular report.
    func reportForRemotes(at target: Double? = nil) {
        guard hasPlayed, let transport, info?.isOffline == false else { return }
        switch transport.phase {
        case .playing: report(paused: false, at: target)
        case .paused: report(paused: true, at: target)
        // Catching up after a seek: as it was.
        case .buffering: report(paused: reportedPaused, at: target)
        default: break
        }
    }

    /// Sends `request` after whatever was sent before it, with time to finish if the app goes to the background
    /// meanwhile: leaving mid-film would otherwise suspend the app before the last position reached the server.
    private func send(_ request: @escaping @Sendable () async -> Void) {
        let previous = reporting
        let background = BackgroundTime(named: "Playback progress")
        reporting = Task {
            await previous?.value
            await request()
            background.end()
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
        guard !isRecovering, let client, let current = info, let screen else { return }
        let failed = current.sessionId
        let retry = reopenRequest(current)
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
        log.warning("Stream stopped (\(reason)); recovering at \(Int(retry.startPositionSeconds ?? 0))s", ["playbackId": current.playbackId])
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
                    carriedRate = transport?.rate
                    transport?.stop()
                    try attach(info, to: screen)
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

    /// Into the credits, or nearly at the end: as good as watched.
    private func isPastCredits(_ info: PlaybackInfo) -> Bool {
        let credits = info.markers.first(where: { $0.type == .credits })?.start
        return position >= (credits ?? info.durationSeconds * 0.92) && position > 0
    }

    /// Starts the next episode's session now, during the credits, so it opens at once when its turn comes. A busy
    /// server says no, and then it starts the usual way. A file the engine will play has its opening bytes fetched
    /// too, so the engine starts on them rather than waiting for the server.
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
            log.info("Next episode ready: \(ready.item.title)", ["playbackId": ready.playbackId])
            prepared = (request, ready)
            #if canImport(AetherEngine)
            // The same URL the engine will open, with no headers, as it opens it: anything else isn't adopted.
            if Self.usesEngine, ready.method == .directPlay, let url = client.url(for: ready.url) {
                let report = await AetherEngine.prewarm(url: url)
                log.debug("Next episode warmed: \(report.retainedBytes) bytes\(report.declined.map { " (\($0))" } ?? "")", ["playbackId": ready.playbackId])
            }
            #endif
        }
    }

    /// Lets the prepared session go (best effort): it wasn't wanted, or playback ended.
    private func dropPrepared() {
        preparing?.cancel()
        preparing = nil
        #if canImport(AetherEngine)
        AetherEngine.discardPrewarmedSources()
        #endif
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
                while let self, let left = self.countdown, left > 0 {
                    guard (try? await Task.sleep(for: .seconds(1))) != nil, self.isFinishing else { return }
                    self.countdown = left - 1
                    self.updatePrompt()
                }
                guard let self, self.isFinishing, self.countdown == 0 else { return }
                self.advance()
            }
        } else {
            countdown = nil
        }
        updatePrompt()
    }

    private func endFinishing() {
        isFinishing = false
        countdown = nil
        countdownTask?.cancel()
        countdownTask = nil
        updatePrompt()
    }

    /// Not now: stop counting, and if the credits were skipped, let them play.
    private func cancelNext() {
        nextCancelled = true
        dropPrepared()
        let ended = transport?.phase == .ended
        endFinishing()
        if !ended { transport?.play() }
    }

    /// "Next Episode in 4" (on shuffle too: the next one just happens to be random).
    private var nextTitle: String {
        countdown.map { "Next Episode in \($0)" } ?? "Next Episode"
    }

    private func updatePrompt() {
        let title: String? = if isFinishing {
            nextTitle
        } else {
            switch prompt {
            case .none: nil
            case .skip(let type, _): type == .recap ? "Skip Recap" : "Skip Intro"
            // Not over the sleep timer's question, or for an episode it's stopping at the end of.
            case .nextEpisode: screen?.sleepHoldsEpisode == true ? nil : nextTitle
            }
        }
        screen?.setPrompt(title, countingDown: isFinishing && countdown != nil)
    }

    private func promptTapped() {
        if isFinishing { return advance() }
        switch prompt {
        case .none: break
        case .skip(_, let end): skip(to: end)
        case .nextEpisode: advance()
        }
    }

    private func skip(to end: Double) {
        transport?.seek(to: end)
        prompt = .none
        updatePrompt()
    }

    /// At the end: the countdown to the next episode (or the button, with auto-play off); the player closes when
    /// there's nothing next or Cancel was chosen.
    private func itemEnded() {
        report(paused: true, ended: true)
        // The sleep timer stops here: the screen stays up, dimmed, on the end of this episode.
        if let screen, screen.sleepHoldsEpisode {
            screen.sleepAtEndOfEpisode()
            return
        }
        if info?.next != nil, !nextCancelled {
            beginFinishing()
        } else {
            dismissPlayer()
        }
    }

    /// Ends this session and plays `info.next` in the same screen.
    /// Past this, Previous starts the title again before going to the one before.
    private static let restartAfterSeconds: Double = 3

    private func advance() {
        guard let current = info else { return }
        // A downloaded episode moves on to the next downloaded one.
        if current.isOffline {
            report(paused: false, ended: true)
            endFinishing()
            guard let nextInfo = offlineLibrary?.nextInfo(after: current), let screen else { return dismissPlayer() }
            trail.append(current.item)
            transport?.stop()
            try? attach(nextInfo, to: screen)
            return
        }
        guard let next = current.next else { return }
        move(to: next, from: current, leavingTrail: true)
    }

    /// Back to the episode before this one: the one played before it in this sitting, else the one before it in order.
    /// Like a music player's back: a few seconds in, or with nothing before (a film, a first episode), it starts
    /// this title again; otherwise it goes to the one before.
    private func previous() {
        guard let current = info else { return }
        if position > Self.restartAfterSeconds || (trail.isEmpty && current.previous == nil) {
            screen?.seek(to: 0)
            return
        }
        if let last = trail.popLast() {
            if current.isOffline, let screen, let previousInfo = offlineLibrary?.nextInfo(after: current).flatMap({ _ in nil as PlaybackInfo? }) {
                transport?.stop()
                try? attach(previousInfo, to: screen)
                return
            }
            move(to: last, from: current, leavingTrail: false)
        } else if let before = current.previous {
            move(to: before, from: current, leavingTrail: false)
        }
    }

    /// Ends this session and plays `target` in the same screen. Got ready during the credits (with the same
    /// subtitles), the next episode opens straight away.
    private func move(to target: ItemSummary, from current: PlaybackInfo, leavingTrail: Bool) {
        guard !isAdvancing, let client else { return }
        log.info("Moving on to \(target.title)", ["playbackId": current.playbackId])
        isAdvancing = true
        endFinishing()
        report(paused: false, ended: leavingTrail && isPastCredits(current))
        if recordsHistory, isPastCredits(current) { onWatchedToEnd?(false) }
        let finished = current.sessionId
        carriedRate = nil
        Task {
            defer { isAdvancing = false }
            try? await client.stopPlayback(sessionId: finished)
            do {
                let request = nextRequest(target, after: current)
                viewerSubtitleKey = request.subtitleKey == "off" ? .some(nil) : nil
                let info: PlaybackInfo
                if let ready = prepared, ready.info.item.id == target.id, ready.request.subtitleKey == request.subtitleKey {
                    prepared = nil
                    log.info("Using the next episode got ready during the credits")
                    info = ready.info
                } else {
                    dropPrepared()
                    info = try await client.startPlayback(request)
                }
                guard self.info?.sessionId == finished, let screen else { // Closed meanwhile.
                    try? await client.stopPlayback(sessionId: info.sessionId)
                    return
                }
                if leavingTrail { trail.append(current.item) }
                transport?.stop()
                try attach(info, to: screen)
                onSessionEnded?()
            } catch {
                self.info = nil
                dismissPlayer {
                    self.show(error, retry: self.request(for: target.id, startAt: nil))
                }
            }
        }
    }

    // MARK: Commands from elsewhere

    /// Pauses the picture, as a command from another device asks (nothing to do with no title up).
    func pause() {
        transport?.pause()
    }

    func resume() {
        transport?.play()
    }

    /// Closes the player screen, ending the session.
    func closePlayer() {
        dismissPlayer()
    }

    /// A title is up in the player.
    var isShowingTitle: Bool { screen != nil && info != nil }

    /// The title up in the player, if there is one (its page is where Continue on iPhone leaves the TV).
    var showingItem: ItemSummary? { screen != nil ? info?.item : nil }

    /// The title up is showing Skip Intro (or Skip Recap).
    var canSkipSegment: Bool {
        if case .skip = prompt { return true }
        return false
    }

    /// The title up has a next episode to move on to.
    var hasNextTitle: Bool { info?.next != nil }

    /// The subtitles the title up can show (Siri's "Turn on subtitles" chooses among them).
    var subtitleTracks: [PlayerTrack] { screen?.remoteTracks?.subtitles ?? [] }

    /// What a remote on another device asked of the title that's up: false when there's none, or it isn't a remote's command.
    @discardableResult
    func remote(_ command: DeviceCommand) -> Bool {
        guard let screen, info != nil else { return false }
        switch command.kind {
        case .seek:
            guard let seconds = command.seconds, seconds.isFinite else { return true }
            screen.seek(to: seconds)
        case .skip:
            guard let seconds = command.seconds, seconds.isFinite else { return true }
            screen.skip(seconds, flash: true)
        case .next: advance()
        case .previous: previous()
        case .skipSegment:
            if case .skip(_, let end) = prompt { skip(to: end) }
        case .audioTrack:
            if let id = command.trackId { screen.remoteSelectAudio(id) }
            reportForRemotes()
        case .subtitleTrack:
            screen.remoteSelectSubtitle(command.trackId)
            reportForRemotes()
        case .rate:
            if let rate = command.rate { screen.remoteChooseRate(Float(rate)) }
            reportForRemotes()
        default:
            return false
        }
        return true
    }

    // MARK: Stop

    /// Stops the current session (reporting the final position) and tears down the transport.
    func stop() {
        stop(reportingTo: client, recording: true)
    }

    /// `recording`: a real stop, so a downloaded file's position is kept (not when one session replaces another).
    private func stop(reportingTo client: FlexarrClient?, recording: Bool = false) {
        let watchedToEnd = recording && recordsHistory && info.map(isPastCredits) == true
        if let info, info.isOffline {
            if recording, recordsHistory { offlineLibrary?.record(itemId: info.item.id, position: position, finished: isPastCredits(info)) }
        } else if let info, let client {
            log.info("Stopping", ["playbackId": info.playbackId])
            let position = self.position
            let sessionId = info.sessionId
            let record = recordsHistory
            send {
                if record {
                    try? await client.reportProgress(sessionId: sessionId, positionSeconds: position, paused: true)
                }
                try? await client.stopPlayback(sessionId: sessionId)
            }
        }
        transport?.stop()
        transport = nil
        if info?.item.id != engineSuppressedItem { engineSuppressedItem = nil }
        info = nil
        prompt = .none
        isFinishing = false
        countdown = nil
        countdownTask?.cancel()
        countdownTask = nil
        dropPrepared()
        if client != nil { onSessionEnded?() }
        if watchedToEnd { onWatchedToEnd?(true) }
    }

    private func dismissPlayer(then completion: (() -> Void)? = nil) {
        cancelSubtitleSync()
        stop()
        guard let screen else { completion?(); return }
        self.screen = nil
        screen.finish(completion: completion)
    }

    // MARK: Artwork and detail

    /// "1080p · E-AC-3 5.1 · Original", or the server's account of a converted stream.
    private static func detail(for info: PlaybackInfo) -> String? {
        if let summary = info.transcodeSummary, !summary.isEmpty { return summary }
        let audio = info.audioTracks.first { $0.index == info.selectedAudioIndex } ?? info.audioTracks.first
        let sound = audio.map { track -> String in
            let codec = track.codec.uppercased().replacingOccurrences(of: "EAC3", with: "E-AC-3").replacingOccurrences(of: "AC3", with: "AC-3")
            let channels: String = switch track.channels {
            case 8: "7.1"
            case 6: "5.1"
            case 2: "stereo"
            default: ""
            }
            return [codec, channels].filter { !$0.isEmpty }.joined(separator: " ")
        }
        let parts = [info.item.resolution, sound, info.isOffline ? "Downloaded" : info.method == .directPlay ? "Original" : nil].compactMap { $0 }.filter { !$0.isEmpty }
        return parts.isEmpty ? nil : parts.joined(separator: " · ")
    }

    /// The poster for the lock screen and Now Playing.
    private func loadArtwork(for info: PlaybackInfo, into screen: PlayerViewController) {
        guard let client, !info.isOffline else { return }
        let summary = info.item
        let path = summary.kind == .episode ? (summary.artwork.thumb ?? info.ancestors.first?.artwork.poster) : summary.artwork.poster
        guard let url = client.imageURL(path, width: 600) else { return }
        Task {
            let image = await ImageLoader.shared.load(url)
            guard self.info?.sessionId == info.sessionId else { return }
            screen.setArtwork(image)
        }
    }

    /// The screen to present the player from: never one on its way out (a sheet still closing), which would refuse it
    /// and leave nothing on screen.
    private static func topViewController() -> UIViewController? {
        let scene = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first
        var top = scene?.keyWindow?.rootViewController ?? scene?.windows.first?.rootViewController
        while let presented = top?.presentedViewController, !presented.isBeingDismissed { top = presented }
        return top
    }

    /// Waits (up to two seconds) for a sheet that's closing to be gone, so the player can come up in its place.
    static func sheetsSettled() async {
        for _ in 0..<40 {
            let scene = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first
            var top = scene?.keyWindow?.rootViewController
            var closing = false
            while let presented = top?.presentedViewController {
                if presented.isBeingDismissed || presented.isBeingPresented { closing = true }
                top = presented
            }
            if !closing { return }
            try? await Task.sleep(for: .milliseconds(50))
        }
    }
}

/// Marks the engine's transport without naming its type where the engine isn't compiled in.
protocol EngineTransportMarker {}
#if canImport(AetherEngine)
extension EngineTransport: EngineTransportMarker {}
#endif

extension PlaybackController: PlayerScreenDelegate {
    func playerScreen(_ screen: PlayerViewController, phaseChanged phase: TransportPhase) {
        phaseChanged(phase)
    }

    func playerScreen(_ screen: PlayerViewController, tick seconds: Double) {
        tick(seconds)
    }

    func playerScreenTracksChanged(_ screen: PlayerViewController) {
        if screen.remoteTracks != reportedTracks { reportForRemotes() }
    }

    func playerScreen(_ screen: PlayerViewController, didSeekTo seconds: Double) {
        lastPosition = seconds
        reportForRemotes(at: seconds)
        if isFinishing, seconds < (info?.durationSeconds ?? 0) - 1 {
            // Scrubbed back out of the end: not finishing after all.
            nextCancelled = false
            endFinishing()
        }
    }

    func playerScreenDidClose(_ screen: PlayerViewController) {
        guard screen === self.screen else { return }
        dismissPlayer()
    }

    func playerScreenPrevious(_ screen: PlayerViewController) {
        previous()
    }

    func playerScreenNext(_ screen: PlayerViewController) {
        advance()
    }

    func playerScreenPromptTapped(_ screen: PlayerViewController) {
        promptTapped()
    }

    func playerScreenCancelNext(_ screen: PlayerViewController) {
        cancelNext()
    }

    func playerScreen(_ screen: PlayerViewController, selectAudio id: String) {
        guard let current = info, !current.isOffline, !isRecovering, let index = Int(id) else { return }
        reopen(reopenRequest(current, audioIndex: index), replacing: current)
    }

    func playerScreen(_ screen: PlayerViewController, choseSubtitle key: String??) {
        viewerSubtitleKey = key
        log.info("Subtitles chosen in the player: \(key.flatMap { $0 } ?? "off")", ["playbackId": info?.playbackId])
        // The server remembers the pick for this title and its show, so it holds next time.
        if let client, let info, !info.isOffline, case .some(let chosen) = key {
            Task { try? await client.setSubtitle(sessionId: info.sessionId, key: chosen) }
        }
    }

    func playerScreen(_ screen: PlayerViewController, subtitleDelay seconds: Double) {
        setSubtitleDelay(seconds)
    }

    func playerScreen(_ screen: PlayerViewController, subtitleSize size: SubtitleSize) {
        setSubtitleSize(size)
    }

    func playerScreen(_ screen: PlayerViewController, subtitlePlacement placement: SubtitlePlacement) {
        setSubtitlePlacement(placement)
    }

    func playerScreen(_ screen: PlayerViewController, audioDelay seconds: Double) {
        setAudioDelay(seconds)
    }

    /// A speed chosen during an episode is kept for its show (for this profile); a film's is for this time only.
    func playerScreen(_ screen: PlayerViewController, choseRate rate: Float) {
        guard let client, let info, !info.isOffline, info.hasPlayerControls, info.item.kind == .episode else { return }
        let playbackId = info.playbackId
        Task {
            do {
                try await client.setPlaybackSpeed(itemId: info.item.id, speed: Double(rate))
            } catch {
                log.warning("Couldn't remember the show's speed", error: error, ["playbackId": playbackId])
            }
        }
    }

    func playerScreenSleepChanged(_ screen: PlayerViewController) {
        updatePrompt()
    }

    func playerScreenSearchSubtitles(_ screen: PlayerViewController) {
        searchSubtitles()
    }

    func playerScreen(_ screen: PlayerViewController, resyncSubtitle key: String) {
        resyncSubtitles(key: key)
    }
}

/// A little time from iOS to finish something after the app leaves the screen. Ended when the work is done, or by
/// iOS when the time runs out.
@MainActor
private final class BackgroundTime {
    private var id = UIBackgroundTaskIdentifier.invalid

    init(named name: String) {
        id = UIApplication.shared.beginBackgroundTask(withName: name) { [weak self] in
            MainActor.assumeIsolated { self?.end() }
        }
    }

    nonisolated func end() {
        Task { @MainActor in
            guard self.id != .invalid else { return }
            UIApplication.shared.endBackgroundTask(self.id)
            self.id = .invalid
        }
    }
}
