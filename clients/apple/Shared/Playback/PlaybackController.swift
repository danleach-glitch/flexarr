import AVFoundation
import FlexarrKit
import Observation
import os
import UIKit

private let log = Logger(subsystem: Bundle.main.bundleIdentifier ?? "io.github.flexarr", category: "playback")

/// Where a downloaded file's watching is recorded (to be sent to the server once there's a connection), and which
/// downloaded episode comes after it.
@MainActor
protocol OfflineLibrary: AnyObject {
    func record(itemId: String, position: Double, finished: Bool)
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
    /// When false, sessions are still started and stopped but progress is never reported.
    @ObservationIgnored var recordsHistory = true

    @ObservationIgnored private var client: FlexarrClient?
    @ObservationIgnored private var info: PlaybackInfo?
    @ObservationIgnored private var screen: PlayerViewController?
    @ObservationIgnored private var transport: PlayerTransport?
    @ObservationIgnored private var lastReport = Date.distantPast
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

        let transport = try makeTransport(for: info)
        self.transport = transport
        screen.transport = transport
        screen.setTitle(info.item.displayTitle, subtitle: info.item.episodeSubtitle)
        screen.markers = info.markers
        screen.setDetail(Self.detail(for: info))
        screen.previews = client.flatMap { info.isOffline ? nil : TrickplayPreviews(client: $0, sessionId: info.sessionId) }
        screen.setSubtitleOptions(delay: info.isOffline || info.subtitleBurnedIn || transport is EngineTransportMarker ? nil : (info.subtitleDelaySeconds ?? 0),
                                  size: preferences.subtitleSize ?? .standard)
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
        log.info("Engine fell back to the server for \(info.item.title, privacy: .public): \(reason, privacy: .public)")
        engineSuppressedItem = info.item.id
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
        let hasPrevious = !trail.isEmpty || info.previous != nil || (info.isOffline && false)
        let hasNext = info.next != nil || (info.isOffline && offlineLibrary?.nextInfo(after: info) != nil)
        screen?.setEpisodeNavigation(hasPrevious: hasPrevious, hasNext: hasNext)
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
                log.error("Couldn't move subtitles: \(error.localizedDescription, privacy: .public)")
                return
            }
            guard self.info?.sessionId == current.sessionId else { return }
            reopen(reopenRequest(current), replacing: current)
        }
    }

    /// The profile's subtitle size, changed while watching: drawn at once, and saved for every device.
    func setSubtitleSize(_ size: SubtitleSize) {
        preferences.subtitleSize = size
        if let client {
            let saved = preferences
            Task { try? await client.setPlaybackPreferences(saved) }
        }
    }

    /// Opens the current title again with `request` (another audio track, moved subtitles) in the same screen.
    private func reopen(_ request: PlaybackRequest, replacing current: PlaybackInfo) {
        guard let client, let screen else { return }
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
            log.info("Skipping the \(type.rawValue, privacy: .public) by itself")
            skip(to: end)
            return
        }
        // Skip credits: the episode is over as soon as they start.
        if preferences.skipCredits, info.next != nil, !nextCancelled, !isFinishing,
           let credits = info.markers.first(where: { $0.type == .credits }), seconds >= credits.start {
            transport?.pause()
            report(paused: true, ended: true)
            beginFinishing()
        }
        if next != prompt {
            log.info("Prompt \(String(describing: next), privacy: .public) at \(seconds)")
            prompt = next
            updatePrompt()
            // The credits started: get the next episode ready. Back out of them: let it go.
            if next == .nextEpisode { prepareNext() } else if !isFinishing { dropPrepared() }
        }
        if Date().timeIntervalSince(lastReport) >= Self.reportInterval, transport?.phase == .playing {
            report(paused: false)
        }
    }

    private func phaseChanged(_ phase: TransportPhase) {
        guard let info else { return }
        switch phase {
        case .playing:
            hasPlayed = true
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
        log.info("Recovering playback at \(retry.startPositionSeconds ?? 0)")
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
    /// server says no, and then it starts the usual way.
    private func prepareNext() {
        #if canImport(AetherEngine)
        if transport is EngineTransport { return } // the next file opens in a moment through the engine; nothing to get ready
        #endif
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
            case .nextEpisode: nextTitle
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
        if info?.next != nil, !nextCancelled {
            beginFinishing()
        } else {
            dismissPlayer()
        }
    }

    /// Ends this session and plays `info.next` in the same screen.
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
    private func previous() {
        guard let current = info else { return }
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
        log.info("Moving on to \(target.title, privacy: .public)")
        isAdvancing = true
        endFinishing()
        report(paused: false, ended: leavingTrail && isPastCredits(current))
        let finished = current.sessionId
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

    // MARK: Stop

    /// Stops the current session (reporting the final position) and tears down the transport.
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
    }

    private func dismissPlayer(then completion: (() -> Void)? = nil) {
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

    private static func topViewController() -> UIViewController? {
        let scene = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first
        var top = scene?.keyWindow?.rootViewController ?? scene?.windows.first?.rootViewController
        while let presented = top?.presentedViewController { top = presented }
        return top
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

    func playerScreenTracksChanged(_ screen: PlayerViewController) {}

    func playerScreen(_ screen: PlayerViewController, didSeekTo seconds: Double) {
        lastPosition = seconds
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
        log.info("Subtitles chosen in the player: \(key.flatMap { $0 } ?? "off", privacy: .public)")
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
}
