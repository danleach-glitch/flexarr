import AppKit
import AVFoundation
import AVKit
import FlexarrKit
import SwiftUI

private let log = FlexarrLog("player")

/// The Mac's player screen: the transport's picture filling the window, the controls over it, driven by the pointer
/// and the keyboard. The same API as the iPhone's and Apple TV's (Shared/Player/PlayerViewController.swift), so the
/// shared PlaybackController drives it unchanged.
final class PlayerViewController: NSViewController {
    weak var delegate: PlayerScreenDelegate?

    /// Where an episode ends for the sleep timer's End of episode (as the iPhone's).
    struct EpisodeEnd {
        var credits: Double?
        var stopsAtCredits: Bool
    }

    /// How far the skip buttons and ⌘← ⌘→ jump: 30 seconds unless Settings says otherwise. The arrow keys alone jump 10.
    static var skipSeconds: Double {
        let saved = UserDefaults.standard.double(forKey: "FlexarrSkipSeconds")
        return saved > 0 ? saved : 30
    }

    static let delayChoices: [Double] = [-2, -1, -0.5, 0, 0.5, 1, 2]
    static let rateChoices: [Float] = [0.5, 0.75, 1, 1.25, 1.5, 2]

    let state = MacPlayerState()
    let sleep = SleepTimer()
    private let nowPlaying = NowPlaying()
    private var finished = false
    private var artwork: NSImage?
    private var hideTask: Task<Void, Never>?
    private var flashTask: Task<Void, Never>?
    private var sleepWatch: Task<Void, Never>?
    private var isScrubbing = false
    private var seekTarget: (seconds: Double, at: Date)?
    private var lastNowPlayingUpdate = Date.distantPast
    private var countingDownToNext = false
    private var pictureInPicture: AVPictureInPictureController?
    private var isRestoringFromPictureInPicture = false
    private var fullScreenObservers: [NSObjectProtocol] = []
    /// Keeps the Mac awake (display and all) while the picture plays.
    private var awake: NSObjectProtocol?

    /// A sheet or alert over the player (the subtitle search): the controller waits for it to go.
    var presentedViewController: NSViewController? { presentedViewControllers?.first }

    /// Swapped when a session is replaced (the next episode, a recovered stream, the engine's fallback).
    var transport: PlayerTransport? {
        didSet {
            oldValue?.delegate = nil
            oldValue?.view.removeFromSuperview()
            guard let transport else { return }
            transport.delegate = self
            transport.subtitleSize = state.subtitleSize
            transport.subtitlePlacement = state.subtitlePlacement
            if isViewLoaded { install(transport) }
            phaseChanged(transport.phase)
        }
    }

    var markers: [Marker] = [] {
        didSet { state.markers = markers.filter { $0.type == .intro || $0.type == .credits || $0.type == .recap } }
    }

    var previews: TrickplayPreviews? {
        didSet {
            state.previews = previews
            previews?.onLoaded = { [weak self] in self?.state.previews = self?.previews }
        }
    }

    /// What End of episode means for this title (nil: not an episode, so it isn't offered).
    var episodeEnd: EpisodeEnd? {
        didSet {
            if episodeEnd == nil, sleep.setting == .endOfEpisode { sleep.set(nil) }
            state.offersEndOfEpisode = episodeEnd != nil
            updateSleepState()
        }
    }

    private enum SleepPhase { case off, asking, asleep }
    private var sleepPhase = SleepPhase.off

    /// The sleep timer has this episode stopping at its end, or has run out.
    var sleepHoldsEpisode: Bool { sleep.setting == .endOfEpisode || sleepPhase != .off }

    init() {
        super.init(nibName: nil, bundle: nil)
        sleep.onExpire = { [weak self] in self?.fallAsleep() }
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) is not used") }

    // MARK: Lifecycle

    override func loadView() {
        let root = PlayerRootView()
        root.screen = self
        root.wantsLayer = true
        root.layer?.backgroundColor = NSColor.black.cgColor
        let controls = NSHostingView(rootView: MacPlayerControls(state: state, screen: self))
        controls.translatesAutoresizingMaskIntoConstraints = false
        root.addSubview(controls)
        NSLayoutConstraint.activate([
            controls.leadingAnchor.constraint(equalTo: root.leadingAnchor),
            controls.trailingAnchor.constraint(equalTo: root.trailingAnchor),
            controls.topAnchor.constraint(equalTo: root.topAnchor),
            controls.bottomAnchor.constraint(equalTo: root.bottomAnchor),
        ])
        view = root
        if let transport { install(transport) }
        wireNowPlaying()
        scheduleHide()
    }

    override func viewDidAppear() {
        super.viewDidAppear()
        view.window?.makeFirstResponder(view)
        guard let window = view.window, fullScreenObservers.isEmpty else { return }
        state.isFullScreen = window.styleMask.contains(.fullScreen)
        let center = NotificationCenter.default
        fullScreenObservers = [NSWindow.didEnterFullScreenNotification, NSWindow.didExitFullScreenNotification].map { name in
            center.addObserver(forName: name, object: window, queue: .main) { [weak self] _ in
                MainActor.assumeIsolated { self?.state.isFullScreen = window.styleMask.contains(.fullScreen) }
            }
        }
    }

    private func install(_ transport: PlayerTransport) {
        let video = transport.view
        video.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(video, positioned: .below, relativeTo: view.subviews.first)
        NSLayoutConstraint.activate([
            video.leadingAnchor.constraint(equalTo: view.leadingAnchor),
            video.trailingAnchor.constraint(equalTo: view.trailingAnchor),
            video.topAnchor.constraint(equalTo: view.topAnchor),
            video.bottomAnchor.constraint(equalTo: view.bottomAnchor),
        ])
        state.avPlayer = transport.avPlayer
        state.maxRate = transport.maxRate
        #if DEBUG
        // `-FlexarrMuted YES` plays silently (test runs on someone's Mac).
        if UserDefaults.standard.bool(forKey: "FlexarrMuted") { transport.volume = 0 }
        #endif
        updateTime(transport.currentTime, transport)
        refreshTracks()
        pictureInPicture = nil
        preparePictureInPicture(transport)
    }

    // MARK: What the controller tells the screen

    func setTitle(_ title: String, subtitle: String?) {
        state.title = title
        state.subtitle = subtitle
        nowPlaying.setTitle(title, subtitle: subtitle, artwork: artwork)
    }

    /// "1080p   E-AC-3 5.1", or what the server is doing to the stream.
    func setDetail(_ detail: String?) { state.detail = detail }

    func setArtwork(_ image: NSImage?) {
        artwork = image
        nowPlaying.setTitle(state.title, subtitle: state.subtitle, artwork: image)
    }

    func setEpisodeNavigation(hasPrevious: Bool, hasNext: Bool) {
        state.hasPrevious = hasPrevious
        state.hasNext = hasNext
        nowPlaying.setEpisodeNavigation(hasPrevious: hasPrevious, hasNext: hasNext)
    }

    func setPrompt(_ title: String?, symbol: String = "forward.fill", countingDown: Bool, takesFocus: Bool = true) {
        state.prompt = title.map { ($0, symbol, countingDown) }
        countingDownToNext = countingDown
        updateAwake()
    }

    /// The file's audio offset for the audio menu's Audio sync (nil: not adjustable here).
    func setAudioSync(seconds: Double?) {
        if !state.audioSyncOpen || seconds == nil { state.audioSync = seconds }
        if seconds == nil { state.audioSyncOpen = false }
    }

    func setSubtitleOptions(delay: Double?, size: SubtitleSize, placement: SubtitlePlacement) {
        state.subtitleDelay = delay
        state.subtitleSize = size
        state.subtitlePlacement = placement
        transport?.subtitleSize = size
        transport?.subtitlePlacement = placement
    }

    func setSubtitleSearch(available: Bool, files: Set<String>, resyncing: Bool) {
        state.subtitleSearch = (available, files, resyncing)
    }

    /// A moment's message in the middle of the picture ("Re-syncing subtitles…").
    func flash(_ message: String) {
        flashTask?.cancel()
        withAnimation { state.flash = message }
        flashTask = Task { [weak self] in
            try? await Task.sleep(for: .seconds(1.6))
            guard !Task.isCancelled else { return }
            withAnimation { self?.state.flash = nil }
        }
    }

    func showLoading() { phaseChanged(.loading) }

    func applyRate(_ rate: Float) {
        guard let transport, rate <= transport.maxRate else { return }
        transport.setRate(rate)
        state.rate = transport.rate
    }

    // MARK: Live TV

    private var onChannels: (() -> Void)?

    /// Live TV: a Channels button, and the arrow keys up and down change channel.
    func configureLive(onChannels: @escaping () -> Void) {
        state.isLive = true
        self.onChannels = onChannels
    }

    func showChannels() { onChannels?() }

    // MARK: A remote on another device

    var remoteTracks: PlayerTracks? {
        guard let transport else { return nil }
        return PlayerTracks(
            audio: transport.audioTracks.map { PlayerTrack(id: $0.id, label: $0.label, selected: $0.id == transport.activeAudioTrack) },
            subtitles: transport.subtitleTracks.map { PlayerTrack(id: $0.id, label: $0.label, selected: $0.id == transport.activeSubtitleTrack) })
    }

    func remoteSelectAudio(_ id: String) { chooseAudio(id) }
    func remoteSelectSubtitle(_ id: String?) { chooseSubtitle(id: id) }

    func remoteChooseRate(_ rate: Float) { chooseRate(rate) }

    // MARK: The viewer's choices

    func chooseAudio(_ id: String) {
        guard let transport, id != transport.activeAudioTrack, transport.audioTracks.contains(where: { $0.id == id }) else { return }
        if !transport.selectAudioTrack(id) { delegate?.playerScreen(self, selectAudio: id) }
        refreshTracks()
    }

    func chooseSubtitle(id: String?) {
        guard let transport, id != transport.activeSubtitleTrack else { return }
        let track = id.flatMap { id in transport.subtitleTracks.first { $0.id == id } }
        if id != nil, track == nil { return }
        transport.selectSubtitleTrack(track?.id)
        delegate?.playerScreen(self, choseSubtitle: .some(track?.key))
        refreshTracks()
    }

    func chooseSubtitleDelay(_ seconds: Double) {
        guard seconds != state.subtitleDelay else { return }
        delegate?.playerScreen(self, subtitleDelay: seconds)
    }

    func chooseSubtitleSize(_ size: SubtitleSize) {
        state.subtitleSize = size
        transport?.subtitleSize = size
        delegate?.playerScreen(self, subtitleSize: size)
    }

    func chooseSubtitlePlacement(_ placement: SubtitlePlacement) {
        state.subtitlePlacement = placement
        transport?.subtitlePlacement = placement
        delegate?.playerScreen(self, subtitlePlacement: placement)
    }

    func chooseRate(_ rate: Float) {
        guard let transport, rate != transport.rate, rate > 0, rate <= transport.maxRate else { return }
        transport.setRate(rate)
        state.rate = transport.rate
        delegate?.playerScreen(self, choseRate: transport.rate)
    }

    func searchSubtitles() { delegate?.playerScreenSearchSubtitles(self) }
    func resyncSubtitle(_ key: String) { delegate?.playerScreen(self, resyncSubtitle: key) }

    func setAudioSync(_ seconds: Double) {
        guard state.audioSync != nil else { return }
        let clamped = max(-2, min(2, seconds))
        state.audioSync = clamped
        delegate?.playerScreen(self, audioDelay: clamped)
    }

    func nudgeAudio(by step: Double) {
        setAudioSync((((state.audioSync ?? 0) + step) * 100).rounded() / 100)
    }

    private func refreshTracks() {
        guard let transport else { return }
        state.audioTracks = transport.audioTracks
        state.activeAudio = transport.activeAudioTrack
        state.subtitleTracks = transport.subtitleTracks
        state.activeSubtitle = transport.activeSubtitleTrack
        state.rate = transport.rate
        state.maxRate = transport.maxRate
    }

    // MARK: Moving

    func togglePlayPause() {
        if handleSleepTouch() { return }
        guard let transport, transport.phase != .ended else { return }
        transport.isPlaying ? transport.pause() : transport.play()
        showControls()
    }

    func next() { delegate?.playerScreenNext(self) }
    func previous() { delegate?.playerScreenPrevious(self) }
    func promptTapped() { delegate?.playerScreenPromptTapped(self) }
    func cancelNext() { delegate?.playerScreenCancelNext(self) }

    func skip(_ seconds: Double, flash: Bool = false) {
        guard let transport else { return }
        seek(to: (seekTarget?.seconds ?? transport.currentTime) + seconds)
        if flash { self.flash(seconds < 0 ? t("−{seconds} s", ["seconds": Int(abs(seconds))]) : t("+{seconds} s", ["seconds": Int(abs(seconds))])) }
    }

    func seek(to seconds: Double) {
        guard let transport else { return }
        let target = max(0, min(seconds, transport.duration > 0 ? transport.duration : seconds))
        seekTarget = (target, Date())
        transport.seek(to: target)
        updateTime(target, transport)
        delegate?.playerScreen(self, didSeekTo: target)
        showControls()
    }

    func scrubbing(_ active: Bool) {
        isScrubbing = active
        if active { hideTask?.cancel() } else { scheduleHide() }
    }

    func toggleFullScreen() {
        view.window?.toggleFullScreen(nil)
    }

    func close() {
        log.info("Player closed")
        delegate?.playerScreenDidClose(self)
    }

    /// Closes the screen: the controller asks, having stopped the session.
    func finish(completion: (() -> Void)? = nil) {
        finished = true
        sleepWatch?.cancel()
        sleepWatch = nil
        sleep.set(nil)
        hideTask?.cancel()
        nowPlaying.deactivate()
        setAwake(false)
        NSCursor.setHiddenUntilMouseMoves(false)
        pictureInPicture?.stopPictureInPicture()
        if let window = view.window, window.styleMask.contains(.fullScreen) { window.toggleFullScreen(nil) }
        fullScreenObservers.forEach(NotificationCenter.default.removeObserver)
        fullScreenObservers = []
        PlayerHost.shared.remove(self)
        completion?()
    }

    // MARK: Sheets and alerts

    /// A SwiftUI sheet over the player.
    func presentSheet(_ content: some View) -> PlayerSheetHost {
        let controller = NSHostingController(rootView: content.frame(minWidth: 520, minHeight: 440).preferredColorScheme(.dark))
        presentAsSheet(controller)
        return PlayerSheetHost(controller: controller)
    }

    func showAlert(_ title: String, message: String) {
        guard let window = view.window else { return }
        let alert = NSAlert()
        alert.messageText = title
        alert.informativeText = message
        alert.addButton(withTitle: t("OK"))
        alert.beginSheetModal(for: window)
    }

    // MARK: Showing and hiding

    func showControls() {
        hideTask?.cancel()
        if !state.controlsVisible { state.controlsVisible = true }
        scheduleHide()
    }

    private func hideControls() {
        guard state.controlsVisible else { return }
        state.controlsVisible = false
        hideTask?.cancel()
        // The pointer goes with the controls, and comes back as soon as it moves.
        NSCursor.setHiddenUntilMouseMoves(true)
    }

    /// The controls go after three seconds of playing untouched (never while paused, loading, scrubbing, or with a
    /// menu or the audio sync open).
    private func scheduleHide() {
        hideTask?.cancel()
        #if DEBUG
        if UserDefaults.standard.bool(forKey: "FlexarrKeepControls") { return }
        #endif
        hideTask = Task { [weak self] in
            try? await Task.sleep(for: .seconds(3))
            while !Task.isCancelled {
                guard let self, state.controlsVisible else { return }
                if let transport, transport.isPlaying, !isScrubbing, !state.audioSyncOpen, presentedViewController == nil,
                   !(NSApp.windows.contains { $0.className.contains("Menu") && $0.isVisible }) {
                    return hideControls()
                }
                if transport?.phase == .paused { hideTask = nil; return }
                try? await Task.sleep(for: .seconds(1))
            }
        }
    }

    // MARK: Keyboard and pointer

    /// Space plays and pauses; ← → jump 10 seconds (⌘ for the longer skip); ↑ ↓ change channel on Live TV; F goes full
    /// screen; Esc leaves full screen, then the player.
    fileprivate func keyDown(_ event: NSEvent) -> Bool {
        if sleepPhase == .asleep { wakeFromSleep(); return true }
        let command = event.modifierFlags.contains(.command)
        switch event.keyCode {
        case 49: togglePlayPause() // space
        case 123: skip(command ? -Self.skipSeconds : -10, flash: !state.controlsVisible) // ←
        case 124: skip(command ? Self.skipSeconds : 10, flash: !state.controlsVisible) // →
        case 126 where state.isLive: previous() // ↑
        case 125 where state.isLive: next() // ↓
        case 53: // Esc
            if state.audioSyncOpen { state.audioSyncOpen = false }
            else if state.isFullScreen { toggleFullScreen() }
            else if countingDownToNext { cancelNext() }
            else { close() }
        case 3: toggleFullScreen() // F
        case 36 where state.prompt != nil: promptTapped() // Return takes the pill
        default: return false
        }
        return true
    }

    fileprivate func pointerMoved() {
        if sleepPhase == .asking { return }
        showControls()
    }

    // MARK: Phase

    private func updateAwake(_ phase: TransportPhase? = nil) {
        switch phase ?? transport?.phase {
        case .playing: setAwake(true)
        case .loading, .buffering: break
        case .paused: setAwake(false)
        default: setAwake(countingDownToNext)
        }
    }

    private func setAwake(_ on: Bool) {
        if on, awake == nil {
            awake = ProcessInfo.processInfo.beginActivity(options: [.idleDisplaySleepDisabled, .userInitiated], reason: "Playing video")
        } else if !on, let activity = awake {
            ProcessInfo.processInfo.endActivity(activity)
            awake = nil
        }
    }

    private func phaseChanged(_ phase: TransportPhase) {
        state.phase = phase
        updateAwake(phase)
        if phase == .playing, sleepPhase == .asleep { wakeFromSleep() }
        switch phase {
        case .playing: if hideTask == nil || hideTask!.isCancelled { scheduleHide() }
        case .buffering: break
        default: hideTask?.cancel(); if phase == .paused { showControls() }
        }
        if let transport {
            nowPlaying.setTime(position: transport.currentTime, duration: transport.duration, rate: phase == .playing ? transport.rate : 0)
        }
    }

    private func updateTime(_ seconds: Double, _ transport: PlayerTransport) {
        state.position = seconds
        state.duration = transport.duration
        state.buffered = transport.bufferedTime
        if let live = transport as? LiveTransport { state.behindLive = live.behindLive }
    }

    private func wireNowPlaying() {
        nowPlaying.onPlay = { [weak self] in self?.transport?.play() }
        nowPlaying.onPause = { [weak self] in self?.transport?.pause() }
        nowPlaying.onSkip = { [weak self] seconds in self?.skip(seconds) }
        nowPlaying.onSeek = { [weak self] seconds in self?.seek(to: seconds) }
        nowPlaying.onPrevious = { [weak self] in self?.previous() }
        nowPlaying.onNext = { [weak self] in self?.next() }
        nowPlaying.activate(skipSeconds: Self.skipSeconds)
    }

    // MARK: Sleep timer

    func setSleep(_ setting: SleepTimer.Setting?) {
        log.info("Sleep timer: \(setting?.label ?? "off")")
        sleep.set(setting)
        if sleepPhase == .asking { endAsking() }
        updateSleepState()
        delegate?.playerScreenSleepChanged(self)
        if setting != nil { watchSleep() }
    }

    private func updateSleepState() {
        state.sleepSetting = sleep.setting
        state.sleepMinutesLeft = sleep.remaining.map { Int(($0 / 60).rounded(.up)) }
    }

    private func watchSleep() {
        guard sleepWatch == nil else { return }
        sleepWatch = Task { [weak self] in
            while let self, !Task.isCancelled, self.checkSleep() {
                try? await Task.sleep(for: .milliseconds(250))
            }
            self?.sleepWatch = nil
        }
    }

    private func checkSleep() -> Bool {
        guard sleepPhase != .asleep else { return false }
        guard let setting = sleep.setting else {
            if sleepPhase == .asking { endAsking() }
            return false
        }
        guard let transport else { return true }
        let left: Double
        let asking: Bool
        switch setting {
        case .minutes:
            left = sleep.remaining ?? 0
            asking = left <= 60
        case .endOfEpisode:
            guard let end = episodeEnd, transport.duration > 0 else { return true }
            let stop = end.stopsAtCredits ? end.credits ?? transport.duration : transport.duration
            let question = end.stopsAtCredits ? stop - 60 : end.credits ?? stop - 60
            let position = transport.currentTime
            left = (stop - position) / Double(max(transport.rate, 0.25))
            if left <= 0.3 {
                fallAsleep()
                return false
            }
            asking = position >= min(question, stop - 10)
        case .endOfChapter:
            return false
        }
        if asking {
            if sleepPhase == .off { beginAsking() }
            state.sleepAsking = t("Pausing in {time}", ["time": Format.timestamp(left.rounded(.up))])
            transport.volume = Float(max(0, min(1, left / 10)))
        } else if sleepPhase == .asking {
            endAsking()
        }
        updateSleepState()
        return true
    }

    private func beginAsking() {
        log.info("Sleep timer asking whether anyone's still watching")
        sleepPhase = .asking
        state.audioSyncOpen = false
        hideControls()
        delegate?.playerScreenSleepChanged(self)
    }

    private func endAsking() {
        sleepPhase = .off
        state.sleepAsking = nil
        transport?.volume = 1
    }

    private func fallAsleep() {
        guard sleepPhase != .asleep, !finished else { return }
        log.info("Sleep timer ran out; pausing")
        if sleep.setting != nil { sleep.set(nil) }
        sleepPhase = .asleep
        state.sleepAsking = nil
        state.asleep = true
        transport?.pause()
        hideControls()
        updateSleepState()
        delegate?.playerScreenSleepChanged(self)
    }

    func sleepAtEndOfEpisode() { fallAsleep() }

    func wakeFromSleep() {
        guard sleepPhase == .asleep else { return }
        sleepPhase = .off
        state.asleep = false
        transport?.volume = 1
        showControls()
        delegate?.playerScreenSleepChanged(self)
    }

    private func handleSleepTouch() -> Bool {
        switch sleepPhase {
        case .asking: setSleep(.minutes(15))
        case .asleep: wakeFromSleep()
        case .off: return false
        }
        return true
    }

    // MARK: Picture in Picture

    /// The engine has no picture to hand Picture in Picture until it has opened the file, so this is tried again as
    /// the transport reports in, until there is one.
    private func preparePictureInPicture(_ transport: PlayerTransport) {
        guard pictureInPicture == nil, AVPictureInPictureController.isPictureInPictureSupported(),
              let source = transport.pictureInPictureContentSource() else {
            state.canPictureInPicture = pictureInPicture != nil
            return
        }
        let controller = AVPictureInPictureController(contentSource: source)
        controller.delegate = self
        pictureInPicture = controller
        state.canPictureInPicture = true
    }

    func startPictureInPicture() {
        pictureInPicture?.startPictureInPicture()
    }

    // MARK: Labels

    static func clock(_ seconds: Double) -> String {
        guard seconds.isFinite else { return "--:--" }
        let total = Int(seconds.rounded())
        let hours = total / 3600, minutes = (total / 60) % 60, secs = total % 60
        return hours > 0 ? String(format: "%d:%02d:%02d", hours, minutes, secs) : String(format: "%d:%02d", minutes, secs)
    }

    /// "In time", "0.5s later", "1s earlier".
    static func delayLabel(_ seconds: Double) -> String {
        guard seconds != 0 else { return t("In time") }
        let amount = abs(seconds).formatted(.number.precision(.fractionLength(0...2)).locale(Localization.locale))
        return seconds > 0 ? t("{amount}s later", ["amount": amount]) : t("{amount}s earlier", ["amount": amount])
    }

    static func rateLabel(_ rate: Float) -> String {
        rate == 1 ? t("Normal") : "\(rate.formatted(.number.precision(.fractionLength(0...2)).locale(Localization.locale)))×"
    }

    /// "+0.25 s", as the iPhone's audio sync panel says it.
    static func audioSyncLabel(_ seconds: Double) -> String {
        let amount = abs(seconds).formatted(.number.precision(.fractionLength(2)).locale(Localization.locale))
        return seconds > 0.0005 ? t("+{amount} s", ["amount": amount])
            : seconds < -0.0005 ? t("−{amount} s", ["amount": amount]) : t("{amount} s", ["amount": amount])
    }
}

extension PlayerViewController: PlayerTransportDelegate {
    func transport(_ transport: PlayerTransport, phaseChanged phase: TransportPhase) {
        if isViewLoaded { preparePictureInPicture(transport) }
        phaseChanged(phase)
        delegate?.playerScreen(self, phaseChanged: phase)
    }

    func transport(_ transport: PlayerTransport, tick seconds: Double) {
        if let pending = seekTarget {
            if abs(seconds - pending.seconds) < 2 || Date().timeIntervalSince(pending.at) > 15 {
                seekTarget = nil
            } else {
                delegate?.playerScreen(self, tick: seconds)
                return
            }
        }
        if !isScrubbing { updateTime(seconds, transport) }
        if Date().timeIntervalSince(lastNowPlayingUpdate) > 1 {
            lastNowPlayingUpdate = Date()
            nowPlaying.setTime(position: seconds, duration: transport.duration, rate: transport.isPlaying ? transport.rate : 0)
        }
        delegate?.playerScreen(self, tick: seconds)
    }

    func transportTracksChanged(_ transport: PlayerTransport) {
        if isViewLoaded { preparePictureInPicture(transport) }
        refreshTracks()
        delegate?.playerScreenTracksChanged(self)
    }
}

extension PlayerViewController: AVPictureInPictureControllerDelegate {
    nonisolated func pictureInPictureControllerWillStartPictureInPicture(_ controller: AVPictureInPictureController) {
        MainActor.assumeIsolated {
            transport?.setPictureInPictureActive(true)
            if let window = view.window, window.styleMask.contains(.fullScreen) { window.toggleFullScreen(nil) }
            // The picture floats; the window goes back to browsing while it plays.
            PlayerHost.shared.setFloating(true)
        }
    }

    nonisolated func pictureInPictureController(_ controller: AVPictureInPictureController, failedToStartPictureInPictureWithError error: any Error) {
        MainActor.assumeIsolated {
            log.warning("Picture in Picture didn't start", error: error)
            transport?.setPictureInPictureActive(false)
            PlayerHost.shared.setFloating(false)
        }
    }

    nonisolated func pictureInPictureController(_ controller: AVPictureInPictureController,
                                                restoreUserInterfaceForPictureInPictureStopWithCompletionHandler completionHandler: @escaping (Bool) -> Void) {
        nonisolated(unsafe) let completionHandler = completionHandler
        MainActor.assumeIsolated {
            isRestoringFromPictureInPicture = true
            PlayerHost.shared.setFloating(false)
            NSApp.activate()
            completionHandler(true)
        }
    }

    nonisolated func pictureInPictureControllerDidStopPictureInPicture(_ controller: AVPictureInPictureController) {
        MainActor.assumeIsolated {
            transport?.setPictureInPictureActive(false)
            // Closed from the floating picture rather than brought back: that's the end of watching.
            if !isRestoringFromPictureInPicture, !finished { close() }
            isRestoringFromPictureInPicture = false
        }
    }
}

/// The player's root view: it takes the keyboard, and the pointer moving over it brings the controls up. Double-click
/// on the picture goes full screen.
final class PlayerRootView: NSView {
    weak var screen: PlayerViewController?
    private var tracking: NSTrackingArea?

    override var acceptsFirstResponder: Bool { true }

    override func keyDown(with event: NSEvent) {
        if screen?.keyDown(event) != true { super.keyDown(with: event) }
    }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let tracking { removeTrackingArea(tracking) }
        let area = NSTrackingArea(rect: bounds, options: [.mouseMoved, .activeInKeyWindow, .inVisibleRect], owner: self)
        addTrackingArea(area)
        tracking = area
    }

    override func mouseMoved(with event: NSEvent) {
        screen?.pointerMoved()
    }

    override func mouseDown(with event: NSEvent) {
        if event.clickCount == 2 { screen?.toggleFullScreen() } else { screen?.pointerMoved() }
    }
}
