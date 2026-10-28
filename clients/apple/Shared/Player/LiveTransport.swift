import AVFoundation
import AVKit
import FlexarrKit

private let log = FlexarrLog("transport")

/// A live channel through AVPlayer. The server keeps a rolling window of the channel (the rewind window); this
/// transport shows that window as the player's timeline, so the scrubber, skips and the remote's scanning work as
/// they do for a film: position 0 is the oldest moment kept, `duration` the newest, and "remaining" how far behind
/// live the picture is.
@MainActor
final class LiveTransport: NSObject, PlayerTransport {
    /// Within this of the newest segment counts as live: players sit a few segments back.
    static let liveSlack: Double = 20

    weak var delegate: PlayerTransportDelegate?
    let view: UIView
    private let layerView = PlayerLayerView()
    let player: AVPlayer
    private let item: AVPlayerItem
    private var observations: [NSKeyValueObservation] = []
    private var timeObserver: Any?
    private(set) var phase: TransportPhase = .loading {
        didSet { if phase != oldValue { delegate?.transport(self, phaseChanged: phase) } }
    }

    init(url: URL) {
        item = AVPlayerItem(url: url)
        // Start a little back from the newest segment, so a slow one doesn't stall the picture straight away.
        item.configuredTimeOffsetFromLive = CMTime(seconds: 12, preferredTimescale: 600)
        item.automaticallyPreservesTimeOffsetFromLive = false
        player = AVPlayer(playerItem: item)
        #if os(iOS)
        player.allowsExternalPlayback = true
        player.usesExternalPlaybackWhileExternalScreenIsActive = true
        #endif
        view = layerView
        super.init()
        layerView.playerLayer.player = player
        layerView.playerLayer.videoGravity = .resizeAspect
        layerView.backgroundColor = .black
        observe()
    }

    /// The window the server keeps, in the player's own time.
    private var window: (start: Double, end: Double) {
        guard let range = item.seekableTimeRanges.last?.timeRangeValue else { return (0, 0) }
        let start = range.start.seconds
        let end = range.end.seconds
        return start.isFinite && end.isFinite ? (start, end) : (0, 0)
    }

    var currentTime: Double {
        let seconds = player.currentTime().seconds
        return seconds.isFinite ? max(0, seconds - window.start) : 0
    }

    var duration: Double { max(1, window.end - window.start) }

    var bufferedTime: Double {
        let now = player.currentTime().seconds
        for range in item.loadedTimeRanges.map(\.timeRangeValue) where range.start.seconds <= now + 1 && range.end.seconds >= now {
            return range.end.seconds - window.start
        }
        return currentTime
    }

    /// How far behind the newest moment kept the picture is.
    var behindLive: Double { max(0, duration - currentTime) }
    var isAtLive: Bool { behindLive < Self.liveSlack }

    var rate: Float { 1 }
    /// No speeds for live TV (the player hides the menu).
    var maxRate: Float { 1 }
    var videoSize: CGSize { item.presentationSize }
    var avPlayer: AVPlayer? { player }
    var playerLayer: AVPlayerLayer? { layerView.playerLayer }

    func play() { player.play() }
    func pause() { player.pause() }

    func seek(to seconds: Double) {
        let (start, end) = window
        let target = min(max(start + seconds, start), max(start, end - 6))
        player.seek(to: CMTime(seconds: target, preferredTimescale: 600), toleranceBefore: .zero, toleranceAfter: .zero) { [weak self] _ in
            Task { @MainActor in
                guard let self else { return }
                self.delegate?.transport(self, tick: self.currentTime)
            }
        }
    }

    func goLive() {
        seek(to: duration - 12)
        player.play()
    }

    func setRate(_ rate: Float) {}

    var volume: Float {
        get { player.volume }
        set { player.volume = newValue }
    }

    func stop() {
        player.pause()
        if let timeObserver { player.removeTimeObserver(timeObserver) }
        timeObserver = nil
        observations = []
        player.replaceCurrentItem(with: nil)
    }

    // Broadcast subtitles (teletext) aren't carried yet; the sound is the channel's one track.
    var audioTracks: [TransportTrack] { [] }
    var activeAudioTrack: String? { nil }
    func selectAudioTrack(_ id: String) -> Bool { false }
    var subtitleTracks: [TransportTrack] { [] }
    var activeSubtitleTrack: String? { nil }
    func selectSubtitleTrack(_ id: String?) {}
    var subtitleSize: SubtitleSize = .standard

    #if os(iOS)
    func pictureInPictureContentSource() -> AVPictureInPictureController.ContentSource? {
        AVPictureInPictureController.ContentSource(playerLayer: layerView.playerLayer)
    }

    func setPictureInPictureActive(_ active: Bool) {}
    #endif

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
                Task { @MainActor in
                    guard let self else { return }
                    self.delegate?.transportTracksChanged(self)
                }
            },
        ]
        timeObserver = player.addPeriodicTimeObserver(forInterval: CMTime(seconds: 0.5, preferredTimescale: 600), queue: .main) { [weak self] _ in
            MainActor.assumeIsolated {
                guard let self else { return }
                self.delegate?.transport(self, tick: self.currentTime)
            }
        }
    }

    private func statusChanged(_ status: AVPlayerItem.Status, message: String?) {
        switch status {
        case .readyToPlay:
            player.play()
        case .failed:
            let reason = message ?? "The channel couldn't be opened."
            log.error("Live item failed: \(reason)")
            phase = .failed("The channel stopped sending. Try again, or pick another channel.")
        default:
            break
        }
    }

    private func updatePhase() {
        if case .failed = phase { return }
        guard item.status == .readyToPlay else { phase = .loading; return }
        switch player.timeControlStatus {
        case .playing: phase = .playing
        case .waitingToPlayAtSpecifiedRate: phase = .buffering
        default: phase = .paused
        }
    }
}
