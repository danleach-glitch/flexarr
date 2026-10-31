import AVFoundation
import AVKit
import FlexarrKit

private let log = FlexarrLog("transport")

/// A server stream (HLS) or a downloaded file through AVPlayer. Subtitles are the stream's own renditions, drawn by
/// the player layer; audio is whichever track the server put in the stream.
@MainActor
final class AVPlayerTransport: NSObject, PlayerTransport {
    weak var delegate: PlayerTransportDelegate?
    let view: UIView
    private let layerView = PlayerLayerView()
    let player: AVPlayer
    private let item: AVPlayerItem
    private let info: PlaybackInfo
    private let startAt: Double
    /// Which subtitles to show once the renditions are known: the viewer's choice (`.some(nil)` for off) or the server's.
    private var wantedSubtitleKey: String??
    private var legibleGroup: AVMediaSelectionGroup?
    private var selectedOption: AVMediaSelectionOption?
    private var observations: [NSKeyValueObservation] = []
    private var notificationObservers: [NSObjectProtocol] = []
    private var timeObserver: Any?
    private var ended = false
    private var pendingSeek: Double
    private(set) var phase: TransportPhase = .loading {
        didSet { if phase != oldValue { delegate?.transport(self, phaseChanged: phase) } }
    }
    private(set) var rate: Float = 1
    var maxRate: Float { 2 }
    /// The server shapes the sound of a stream it converts (servers from 0.56); the original file or a download plays
    /// as it comes.
    var soundReach: SoundReach { !info.isOffline && info.method == .transcode && info.soundShaped != nil ? .applied : .unsupported }

    init(url: URL, info: PlaybackInfo, startAt: Double, subtitleKey: String??) {
        self.info = info
        self.startAt = startAt
        pendingSeek = startAt
        wantedSubtitleKey = subtitleKey
        item = AVPlayerItem(url: url)
        // Rates other than 1× keep the pitch, like Apple's player.
        item.audioTimePitchAlgorithm = .spectral
        player = AVPlayer(playerItem: item)
        // Subtitles are chosen here, from the server's choice or the viewer's. Left to itself AVPlayer re-chooses by the
        // device's language, turning subtitles back on after the viewer turned them off.
        player.appliesMediaSelectionCriteriaAutomatically = false
        #if os(iOS)
        player.allowsExternalPlayback = true
        player.usesExternalPlaybackWhileExternalScreenIsActive = true
        #endif
        view = layerView
        super.init()
        layerView.playerLayer.player = player
        layerView.playerLayer.videoGravity = .resizeAspect
        layerView.backgroundColor = .black
        subtitleSize = info.preferences?.subtitleSize ?? .standard
        subtitlePlacement = info.preferences?.subtitlePlacement ?? SubtitlePlacement()
        observe()
    }

    var currentTime: Double {
        let seconds = player.currentTime().seconds
        return seconds.isFinite ? seconds : 0
    }

    var duration: Double {
        let seconds = item.duration.seconds
        return seconds.isFinite && seconds > 0 ? seconds : info.durationSeconds
    }

    var bufferedTime: Double {
        let now = currentTime
        for range in item.loadedTimeRanges.map(\.timeRangeValue) where range.start.seconds <= now + 1 && range.end.seconds >= now {
            return range.end.seconds
        }
        return now
    }

    var videoSize: CGSize { item.presentationSize }
    var avPlayer: AVPlayer? { player }
    var playerLayer: AVPlayerLayer? { layerView.playerLayer }

    // MARK: Transport

    func play() {
        if ended { return }
        player.rate = rate
    }

    func pause() {
        player.pause()
    }

    func seek(to seconds: Double) {
        let target = max(0, min(seconds, duration))
        ended = false
        player.seek(to: CMTime(seconds: target, preferredTimescale: 600), toleranceBefore: .zero, toleranceAfter: .zero) { [weak self] _ in
            Task { @MainActor in
                guard let self else { return }
                self.delegate?.transport(self, tick: self.currentTime)
                self.updatePhase()
            }
        }
    }

    func setRate(_ rate: Float) {
        self.rate = max(0.25, min(rate, maxRate))
        if player.rate > 0 { player.rate = self.rate }
    }

    var volume: Float {
        get { player.volume }
        set { player.volume = newValue }
    }

    func stop() {
        player.pause()
        if let timeObserver { player.removeTimeObserver(timeObserver) }
        timeObserver = nil
        notificationObservers.forEach { NotificationCenter.default.removeObserver($0) }
        notificationObservers = []
        observations = []
        #if os(tvOS)
        layerView.window?.avDisplayManager.preferredDisplayCriteria = nil
        #endif
        player.replaceCurrentItem(with: nil)
    }

    // MARK: Tracks

    /// The server chose the audio track; another one means a new stream (the controller does that).
    var audioTracks: [TransportTrack] {
        info.audioTracks.map { TransportTrack(id: String($0.index), label: $0.label, key: String($0.index)) }
    }

    var activeAudioTrack: String? { info.selectedAudioIndex.map(String.init) }

    func selectAudioTrack(_ id: String) -> Bool { false }

    var subtitleTracks: [TransportTrack] {
        guard !info.subtitleBurnedIn, let group = legibleGroup else { return [] }
        return group.options.enumerated().map { index, option in
            TransportTrack(id: String(index), label: option.displayName,
                           key: SubtitleMatch.key(for: Self.subtitleOption(option), in: info))
        }
    }

    var activeSubtitleTrack: String? {
        guard let legibleGroup, let selectedOption, let index = legibleGroup.options.firstIndex(of: selectedOption) else { return nil }
        return String(index)
    }

    func selectSubtitleTrack(_ id: String?) {
        guard let group = legibleGroup else {
            wantedSubtitleKey = .some(nil)
            return
        }
        let option = id.flatMap(Int.init).flatMap { group.options.indices.contains($0) ? group.options[$0] : nil }
        selectedOption = option
        wantedSubtitleKey = .some(option.flatMap { SubtitleMatch.key(for: Self.subtitleOption($0), in: info) })
        item.select(option, in: group)
    }

    var subtitleSize: SubtitleSize = .standard {
        didSet { item.textStyleRules = subtitleSize.textStyleRules(placement: subtitlePlacement) }
    }

    var subtitlePlacement = SubtitlePlacement() {
        didSet { item.textStyleRules = subtitleSize.textStyleRules(placement: subtitlePlacement) }
    }

    // MARK: Picture in Picture

    #if os(iOS)
    func pictureInPictureContentSource() -> AVPictureInPictureController.ContentSource? {
        AVPictureInPictureController.ContentSource(playerLayer: layerView.playerLayer)
    }

    func setPictureInPictureActive(_ active: Bool) {}
    #endif

    // MARK: Observation

    private func observe() {
        observations = [
            item.observe(\.status, options: [.new]) { [weak self] item, _ in
                let status = item.status
                let message = item.error?.localizedDescription
                Task { @MainActor in self?.statusChanged(status, message: message) }
            },
            player.observe(\.timeControlStatus, options: [.new]) { [weak self] _, _ in
                Task { @MainActor in self?.updatePhase() }
            },
            item.observe(\.presentationSize, options: [.new]) { [weak self] _, _ in
                Task { @MainActor in self?.delegate?.transportTracksChanged(self!) }
            },
        ]
        timeObserver = player.addPeriodicTimeObserver(forInterval: CMTime(seconds: 0.25, preferredTimescale: 600), queue: .main) { [weak self] time in
            MainActor.assumeIsolated {
                guard let self, time.seconds.isFinite else { return }
                self.delegate?.transport(self, tick: time.seconds)
            }
        }
        notificationObservers = [
            NotificationCenter.default.addObserver(forName: AVPlayerItem.didPlayToEndTimeNotification, object: item, queue: .main) { [weak self] _ in
                MainActor.assumeIsolated {
                    guard let self else { return }
                    self.ended = true
                    self.phase = .ended
                }
            },
            NotificationCenter.default.addObserver(forName: AVPlayerItem.mediaSelectionDidChangeNotification, object: item, queue: .main) { [weak self] _ in
                MainActor.assumeIsolated { self?.selectionChanged() }
            },
        ]
    }

    private func statusChanged(_ status: AVPlayerItem.Status, message: String?) {
        switch status {
        case .readyToPlay:
            applyDisplayCriteria()
            loadSubtitles()
            // A stream that says where to open (EXT-X-START) is there already: no second trip to the server.
            let opened = currentTime
            if pendingSeek > 1, !(abs(opened - pendingSeek) < 2) {
                let target = pendingSeek
                pendingSeek = 0
                seek(to: target)
            } else {
                pendingSeek = 0
            }
            play()
        case .failed:
            let reason = message ?? "The stream couldn't be opened."
            log.error("Item failed: \(reason)", ["playbackId": info.playbackId])
            // AVFoundation's own account of what went wrong with the playlists (the error alone says little).
            for event in item.errorLog()?.events.prefix(5) ?? [] {
                log.warning("Item failed, detail: \(event.errorStatusCode) \(event.errorComment ?? "")", ["uri": event.uri, "playbackId": info.playbackId])
            }
            phase = .failed(reason)
        default:
            break
        }
    }

    private func updatePhase() {
        if case .failed = phase { return }
        if ended { phase = .ended; return }
        guard item.status == .readyToPlay else { phase = .loading; return }
        switch player.timeControlStatus {
        case .playing: phase = .playing
        case .waitingToPlayAtSpecifiedRate: phase = .buffering
        default: phase = pendingSeek > 0 ? .loading : .paused
        }
    }

    /// On Apple TV, the stream's dynamic range (HDR, Dolby Vision) is what the display is asked for, when the viewer
    /// has Match Content on. Apple's own player did this for us.
    private func applyDisplayCriteria() {
        #if os(tvOS)
        guard let manager = layerView.window?.avDisplayManager, manager.isDisplayCriteriaMatchingEnabled else { return }
        manager.preferredDisplayCriteria = item.asset.preferredDisplayCriteria
        #endif
    }

    // MARK: Subtitles

    /// Selects the wanted subtitles once the renditions are known: the viewer's choice for this title if they've made
    /// one, else the server's (its language preferences, or forced subtitles), else none.
    private func loadSubtitles() {
        Task {
            guard let group = try? await item.asset.loadMediaSelectionGroup(for: .legible) else { return }
            legibleGroup = group
            let wanted = wantedSubtitleKey ?? info.selectedSubtitleKey
            let index = SubtitleMatch.option(for: wanted, in: info, among: group.options.map(Self.subtitleOption))
            selectedOption = index.map { group.options[$0] }
            item.select(selectedOption, in: group)
            log.info("Subtitles at start: \(self.selectedOption?.displayName ?? "off")", ["playbackId": info.playbackId])
            delegate?.transportTracksChanged(self)
        }
    }

    /// Only this transport chooses subtitles. A change that isn't ours is the system's (iOS turns subtitles on when
    /// the device is muted), and what was showing is put back.
    private func selectionChanged() {
        guard let group = legibleGroup else { return }
        let selected = item.currentMediaSelection.selectedMediaOption(in: group)
        guard selected != selectedOption else { return }
        log.info("Keeping subtitles \(self.selectedOption?.displayName ?? "off") (the player chose \(selected?.displayName ?? "off"))", ["playbackId": info.playbackId])
        item.select(selectedOption, in: group)
    }

    static func subtitleOption(_ option: AVMediaSelectionOption) -> SubtitleOption {
        SubtitleOption(name: option.displayName, language: option.extendedLanguageTag ?? option.locale?.identifier,
                       isForced: option.hasMediaCharacteristic(.containsOnlyForcedSubtitles))
    }
}
