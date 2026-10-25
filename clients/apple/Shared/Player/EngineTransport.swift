#if canImport(AetherEngine)
import AetherEngine
import AVFoundation
import AVKit
import Combine
import FlexarrKit
import os
import UIKit

private let log = Logger(subsystem: Bundle.main.bundleIdentifier ?? "io.github.flexarr", category: "engine")

/// The file played as it is on the device by AetherEngine: it demuxes the file from the server, and VideoToolbox
/// decodes it when it can (the hardware route) or FFmpeg does (the software route, for AV1, VC-1, MPEG-2, XviD and
/// the like). Either way the picture lands in the engine's own view, with the file's subtitles drawn over it here.
@MainActor
final class EngineTransport: PlayerTransport {
    weak var delegate: PlayerTransportDelegate?
    let view: UIView
    let engine: AetherEngine
    private let surface = AetherPlayerView()
    private let overlay = SubtitleOverlayView()
    private let info: PlaybackInfo
    private let client: FlexarrClient
    private var cancellables: Set<AnyCancellable> = []
    private var loading = true
    private(set) var phase: TransportPhase = .loading {
        didSet { if phase != oldValue { delegate?.transport(self, phaseChanged: phase) } }
    }
    private(set) var rate: Float = 1

    init(info: PlaybackInfo, client: FlexarrClient) throws {
        self.info = info
        self.client = client
        engine = try AetherEngine()
        let container = UIView()
        container.backgroundColor = .black
        view = container
        for subview in [surface, overlay] as [UIView] {
            subview.translatesAutoresizingMaskIntoConstraints = false
            container.addSubview(subview)
            NSLayoutConstraint.activate([
                subview.leadingAnchor.constraint(equalTo: container.leadingAnchor),
                subview.trailingAnchor.constraint(equalTo: container.trailingAnchor),
                subview.topAnchor.constraint(equalTo: container.topAnchor),
                subview.bottomAnchor.constraint(equalTo: container.bottomAnchor),
            ])
        }
        engine.bind(view: surface)
        subtitleSize = info.preferences?.subtitleSize ?? .standard
    }

    /// Opens the file where playback starts, with the server's choice of audio track and the wanted subtitles (the
    /// viewer's choice, `.some(nil)` for off, or the server's).
    func start(at seconds: Double, audioIndex: Int?, subtitleKey: String??, matchContent: Bool) {
        guard let url = client.url(for: info.url) else {
            phase = .failed("The server's address couldn't be read.")
            return
        }
        var options = LoadOptions()
        options.prepareNativeSubtitles = true   // text subtitles as renditions too: Picture in Picture and AirPlay keep them
        options.serveIFramePlaylist = true
        options.externalSubtitles = sidecars()
        // Surround as lossy Dolby Digital Plus (which any TV or bar takes), or lossless FLAC as multichannel PCM for a
        // receiver or eARC soundbar that takes it: the viewer's choice in Settings.
        options.audioBridgeMode = UserDefaults.standard.bool(forKey: "FlexarrEngineLossless") ? .lossless : .surroundCompat
        let modes = AVPlayer.availableHDRModes
        options.panelIsInHDRMode = !modes.isEmpty
        options.panelPresentsDolbyVision = modes.contains(.dolbyVision)
        #if os(tvOS)
        options.matchContentEnabled = matchContent
        #endif

        observe()
        log.info("Opening \(self.info.item.title, privacy: .public) at \(seconds)")
        Task { [weak self] in
            guard let self else { return }
            do {
                _ = try await self.engine.load(url: url, startPosition: seconds > 1 ? seconds : nil, options: options,
                                               audioSourceStreamIndex: audioIndex.map(Int32.init))
            } catch is CancellationError {
                return
            } catch {
                log.error("Couldn't open: \(error.localizedDescription, privacy: .public)")
                self.phase = .failed(error.localizedDescription)
                return
            }
            self.loading = false
            log.info("Route \(self.engine.videoRoute.rawValue, privacy: .public), audio \(self.engine.audioTracks.count) tracks, subtitles \(self.engine.subtitleTracks.count)")
            self.selectInitialSubtitle(subtitleKey ?? self.info.selectedSubtitleKey)
            self.engine.play()
            self.updatePhase()
            self.delegate?.transportTracksChanged(self)
        }
    }

    var currentTime: Double { engine.clock.currentTime }
    var duration: Double { engine.duration > 0 ? engine.duration : info.durationSeconds }
    var bufferedTime: Double { max(engine.clock.bufferedPosition, currentTime) }
    var maxRate: Float { engine.maxSupportedRate }

    var videoSize: CGSize {
        if let size = engine.softwareDisplaySize { return size }
        if let size = engine.currentAVPlayerItem?.presentationSize, size != .zero { return size }
        return CGSize(width: CGFloat(engine.sourceVideoWidth) * CGFloat(engine.sourceVideoPixelAspectRatio), height: CGFloat(engine.sourceVideoHeight))
    }

    var avPlayer: AVPlayer? { engine.currentAVPlayer }
    var playerLayer: AVPlayerLayer? { engine.nativePlayerLayer }

    // MARK: Transport

    func play() { engine.play() }
    func pause() { engine.pause() }

    func seek(to seconds: Double) {
        let target = max(0, min(seconds, duration))
        Task { [weak self] in
            guard let self else { return }
            await self.engine.seek(to: target)
            self.delegate?.transport(self, tick: self.currentTime)
        }
    }

    func setRate(_ rate: Float) {
        self.rate = max(0.25, min(rate, maxRate))
        engine.setRate(self.rate)
    }

    func stop() {
        cancellables.removeAll()
        engine.stop()
        engine.unbind(view: surface)
    }

    // MARK: Tracks

    var audioTracks: [TransportTrack] {
        engine.audioTracks.map { TransportTrack(id: String($0.id), label: Self.label($0), key: String($0.id)) }
    }

    var activeAudioTrack: String? { engine.activeAudioTrackIndex.map(String.init) }

    func selectAudioTrack(_ id: String) -> Bool {
        guard let index = Int(id) else { return false }
        engine.selectAudioTrack(index: index)
        return true
    }

    /// The file's subtitles, embedded and sidecar, by the server's keys: "e<stream index>" for embedded ones, "x<n>" for
    /// the nth sidecar beside the file (the engine lists sidecars in the order they were given).
    var subtitleTracks: [TransportTrack] {
        var sidecar = 0
        return engine.subtitleTracks.map { track in
            let key: String
            if track.isExternal {
                key = sidecarKeys.indices.contains(sidecar) ? sidecarKeys[sidecar] : "x\(sidecar)"
                sidecar += 1
            } else {
                key = "e\(track.id)"
            }
            return TransportTrack(id: String(track.id), label: Self.label(track), key: key, isPicture: Self.isPicture(track.codec))
        }
    }

    var activeSubtitleTrack: String? { engine.activeSubtitleTrackIndex.map(String.init) }

    func selectSubtitleTrack(_ id: String?) {
        if let id, let index = Int(id) {
            engine.selectSubtitleTrack(index: index)
        } else {
            engine.clearSubtitle()
            overlay.show([])
        }
    }

    var subtitleSize: SubtitleSize = .standard {
        didSet {
            overlay.subtitleSize = subtitleSize
            applyNativeSubtitleSize()
        }
    }

    /// Text subtitles the engine serves as renditions are drawn by the player layer, at the profile's size.
    private func applyNativeSubtitleSize() {
        guard let item = engine.currentAVPlayerItem else { return }
        let percent = subtitleSize.percent
        item.textStyleRules = percent == 100 ? nil
            : AVTextStyleRule(textMarkupAttributes: [kCMTextMarkupAttribute_RelativeFontSize as String: percent]).map { [$0] }
    }

    private func selectInitialSubtitle(_ key: String?) {
        guard let key, let track = subtitleTracks.first(where: { $0.key == key }) else { return }
        selectSubtitleTrack(track.id)
    }

    private var sidecarKeys: [String] {
        info.subtitles.filter { $0.key.hasPrefix("x") && $0.isText && $0.url != nil }.map(\.key)
    }

    /// Subtitle files beside the video, which the demuxer doesn't see: the server's WebVTT copies of them.
    private func sidecars() -> [ExternalSubtitleTrack] {
        info.subtitles.compactMap { track in
            guard track.key.hasPrefix("x"), track.isText, let path = track.url, let url = client.url(for: path) else { return nil }
            return ExternalSubtitleTrack(url: url, name: track.label, language: track.language, isForced: track.isForced, isDefault: track.isDefault, formatHint: "webvtt")
        }
    }

    private static func isPicture(_ codec: String) -> Bool {
        codec.contains("pgs") || codec.contains("dvb") || codec.contains("dvd") || codec == "xsub"
    }

    /// "English · E-AC-3 5.1", "Commentary · AAC stereo", "English (PGS)".
    static func label(_ track: TrackInfo) -> String {
        let language = Locale.current.localizedString(forLanguageCode: track.language ?? "") ?? track.language ?? ""
        let name = track.name.isEmpty || track.name == track.language ? language : track.name
        let channels = switch track.channels {
        case 8: "7.1"
        case 6: "5.1"
        case 2: "stereo"
        case 1: "mono"
        case 0: ""
        default: "\(track.channels)-channel"
        }
        let codec = track.codec.uppercased()
            .replacingOccurrences(of: "EAC3", with: "E-AC-3").replacingOccurrences(of: "AC3", with: "AC-3")
            .replacingOccurrences(of: "HDMV_PGS_SUBTITLE", with: "PGS").replacingOccurrences(of: "DVB_SUBTITLE", with: "DVB")
            .replacingOccurrences(of: "SUBRIP", with: "SRT").replacingOccurrences(of: "WEBVTT", with: "VTT")
        var parts = [name]
        if track.channels > 0 {
            parts.append([track.isAtmos ? "\(codec) Atmos" : codec, channels].filter { !$0.isEmpty }.joined(separator: " "))
        } else if isPicture(track.codec) || track.isForced || track.isHearingImpaired {
            parts.append([isPicture(track.codec) ? codec : nil, track.isForced ? "forced" : nil, track.isHearingImpaired ? "SDH" : nil].compactMap { $0 }.joined(separator: ", "))
        }
        return parts.filter { !$0.isEmpty }.joined(separator: " · ")
    }

    // MARK: Picture in Picture

    #if os(iOS)
    func pictureInPictureContentSource() -> AVPictureInPictureController.ContentSource? {
        if let layer = engine.nativePlayerLayer, engine.currentAVPlayer != nil {
            return AVPictureInPictureController.ContentSource(playerLayer: layer)
        }
        if let source = engine.softwarePiPSource {
            return AVPictureInPictureController.ContentSource(sampleBufferDisplayLayer: source.layer, playbackDelegate: SoftwarePictureInPicture(source: source, transport: self))
        }
        return nil
    }

    func setPictureInPictureActive(_ active: Bool) {
        engine.pictureInPictureActive = active
        // Out of our view, the player layer draws the subtitles itself.
        engine.setNativeSubtitleRendering(active)
    }

    /// Answers AVKit's questions about the software route's picture, which has no AVPlayer behind it.
    private final class SoftwarePictureInPicture: NSObject, AVPictureInPictureSampleBufferPlaybackDelegate, @unchecked Sendable {
        // AVKit calls on the main thread; the engine's source is main-actor bound, so every answer hops there.
        nonisolated(unsafe) private let source: SoftwarePiPSource

        init(source: SoftwarePiPSource, transport: EngineTransport) {
            self.source = source
        }

        func pictureInPictureController(_ controller: AVPictureInPictureController, setPlaying playing: Bool) {
            MainActor.assumeIsolated { source.setPlaying(playing) }
        }

        func pictureInPictureControllerTimeRangeForPlayback(_ controller: AVPictureInPictureController) -> CMTimeRange {
            MainActor.assumeIsolated { source.timeRange() }
        }

        func pictureInPictureControllerIsPlaybackPaused(_ controller: AVPictureInPictureController) -> Bool {
            MainActor.assumeIsolated { source.isPaused }
        }

        func pictureInPictureController(_ controller: AVPictureInPictureController, didTransitionToRenderSize newRenderSize: CMVideoDimensions) {}

        func pictureInPictureController(_ controller: AVPictureInPictureController, skipByInterval skipInterval: CMTime) async {
            let seconds = skipInterval.seconds
            await MainActor.run { [source] in source.skip(by: seconds) }
        }
    }
    #endif

    // MARK: Observation

    private func observe() {
        engine.$playbackPhase
            .receive(on: DispatchQueue.main)
            .sink { [weak self] _ in self?.updatePhase() }
            .store(in: &cancellables)
        engine.clock.$currentTime
            .receive(on: DispatchQueue.main)
            .sink { [weak self] seconds in
                guard let self, !self.loading else { return }
                self.delegate?.transport(self, tick: seconds)
            }
            .store(in: &cancellables)
        engine.$subtitleCues
            .receive(on: DispatchQueue.main)
            .sink { [weak self] cues in
                guard let self else { return }
                self.overlay.videoSize = self.videoSize
                self.overlay.show(cues)
            }
            .store(in: &cancellables)
        engine.$audioTracks.combineLatest(engine.$subtitleTracks, engine.$activeAudioTrackIndex)
            .receive(on: DispatchQueue.main)
            .sink { [weak self] _, _, _ in
                guard let self else { return }
                self.delegate?.transportTracksChanged(self)
            }
            .store(in: &cancellables)
        engine.$currentAVPlayerItem
            .receive(on: DispatchQueue.main)
            .sink { [weak self] _ in self?.applyNativeSubtitleSize() }
            .store(in: &cancellables)
    }

    private func updatePhase() {
        if case .failed = phase { return }
        switch engine.playbackPhase {
        case .idle, .loading:
            phase = loading ? .loading : phase
        case .playing:
            phase = .playing
        case .paused:
            phase = .paused
        case .seeking, .rebuffering, .stalled:
            phase = engine.state == .paused ? .paused : .buffering
        case .ended:
            phase = .ended
        case .error(let message):
            phase = .failed(message)
        }
    }
}
#endif
