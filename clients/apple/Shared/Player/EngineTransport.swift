#if canImport(AetherEngine)
import AetherEngine
import AVFoundation
import AVKit
import Combine
import FlexarrKit
import UIKit

private let log = FlexarrLog("engine")

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
    /// How long opening the file may take before the server's stream is used instead.
    private static let openDeadlineSeconds: Double = 60

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
        log.info("Opening \(self.info.item.title) at \(Int(seconds))s", ["playbackId": self.info.playbackId])
        // An open that never finishes (re-encoding a lossless soundtrack can hang at the start) counts as a failed one,
        // so the controller falls back to the server's stream rather than leaving a spinner up for ever.
        let deadline = Task { [weak self] in
            guard (try? await Task.sleep(for: .seconds(Self.openDeadlineSeconds))) != nil, let self, self.loading else { return }
            log.error("Still opening after \(Int(Self.openDeadlineSeconds))s; giving up", ["playbackId": self.info.playbackId])
            self.engine.stop()
            self.phase = .failed("The file took too long to open.")
        }
        Task { [weak self] in
            defer { deadline.cancel() }
            guard let self else { return }
            do {
                _ = try await self.engine.load(url: url, startPosition: seconds > 1 ? seconds : nil, options: options,
                                               audioSourceStreamIndex: audioIndex.map(Int32.init))
            } catch is CancellationError {
                return
            } catch let error as URLError where error.code == .cancelled {
                return
            } catch {
                if case .failed = self.phase { return } // the deadline gave up on it already
                log.error("Couldn't open", error: error, ["playbackId": self.info.playbackId])
                self.phase = .failed(error.localizedDescription)
                return
            }
            if case .failed = self.phase { return }
            self.loading = false
            log.info("Route \(self.engine.videoRoute.rawValue), audio \(self.engine.audioTracks.count) tracks, subtitles \(self.engine.subtitleTracks.count)",
                     ["playbackId": self.info.playbackId])
            #if DEBUG
            // `-FlexarrDumpEngine YES`: what the engine's own HLS server hands AVPlayer (playlists and init segment),
            // for comparing with what our server serves when the TV refuses ours.
            if UserDefaults.standard.bool(forKey: "FlexarrDumpEngine") {
                Task { [weak self] in
                    try? await Task.sleep(for: .seconds(4))
                    guard let self else { return }
                    let item = self.engine.currentAVPlayerItem ?? self.engine.currentAVPlayer?.currentItem
                    guard let url = (item?.asset as? AVURLAsset)?.url else {
                        log.debug("DUMP: no player item (route \(self.engine.videoRoute.rawValue))")
                        return
                    }
                    await Self.dumpServed(url)
                }
            }
            #endif
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
            decodedCues = []
            shownCueIDs = []
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

    #if DEBUG
    private static func dumpServed(_ master: URL) async {
        guard let (data, _) = try? await URLSession.shared.data(from: master), let text = String(data: data, encoding: .utf8) else {
            log.debug("DUMP master: couldn't fetch \(master.absoluteString)")
            return
        }
        for line in text.split(separator: "\n") { log.debug("DUMP master: \(line)") }
        // The first media playlist, and its init segment.
        let lines = text.split(separator: "\n").map(String.init)
        guard let mediaLine = lines.first(where: { !$0.hasPrefix("#") && !$0.isEmpty }), let media = URL(string: mediaLine, relativeTo: master),
              let (mediaData, _) = try? await URLSession.shared.data(from: media), let mediaText = String(data: mediaData, encoding: .utf8) else { return }
        for line in mediaText.split(separator: "\n").prefix(14) { log.debug("DUMP media: \(line)") }
        guard let mapLine = mediaText.split(separator: "\n").first(where: { $0.hasPrefix("#EXT-X-MAP") }),
              let uriStart = mapLine.range(of: "URI=\""), let uriEnd = mapLine[uriStart.upperBound...].firstIndex(of: "\""),
              let initURL = URL(string: String(mapLine[uriStart.upperBound..<uriEnd]), relativeTo: media),
              let (initData, _) = try? await URLSession.shared.data(from: initURL) else { return }
        log.debug("DUMP init: \(initData.count) bytes, boxes \(Self.boxes(initData))")
        if let hvcC = Self.find(box: "hvcC", in: initData) {
            log.debug("DUMP init hvcC: \(hvcC.prefix(23).map { String(format: "%02x", $0) }.joined())")
        }
        for name in ["colr", "mdcv", "clli", "dvcC", "dvvC", "pasp"] {
            if let box = Self.find(box: name, in: initData) { log.debug("DUMP init \(name): \(box.map { String(format: "%02x", $0) }.joined())") }
        }
    }

    /// Box names in order, with their sizes, through the whole buffer (containers descended into).
    private static func boxes(_ data: Data) -> String {
        var names: [String] = []
        func walk(_ start: Int, _ end: Int, _ depth: Int) {
            var offset = start
            while offset + 8 <= end {
                let size = Int(data[offset]) << 24 | Int(data[offset + 1]) << 16 | Int(data[offset + 2]) << 8 | Int(data[offset + 3])
                let type = String(bytes: data[(offset + 4)..<(offset + 8)], encoding: .isoLatin1) ?? "????"
                let length = size == 0 ? end - offset : size
                guard length >= 8 else { return }
                names.append(String(repeating: ".", count: depth) + type + "(\(length))")
                if ["moov", "trak", "mdia", "minf", "stbl", "mvex", "dinf"].contains(type) { walk(offset + 8, min(end, offset + length), depth + 1) }
                if type == "stsd" { walk(offset + 16, min(end, offset + length), depth + 1) }
                if ["hvc1", "hev1", "dvh1", "dvhe", "avc1"].contains(type) { walk(offset + 8 + 78, min(end, offset + length), depth + 1) }
                offset += length
            }
        }
        walk(0, data.count, 0)
        return names.joined(separator: " ")
    }

    /// The payload of the first box called `name` (a flat search).
    private static func find(box name: String, in data: Data) -> [UInt8]? {
        let needle = Array(name.utf8)
        let bytes = [UInt8](data)
        var i = 4
        while i + 4 <= bytes.count {
            if Array(bytes[i..<(i + 4)]) == needle {
                let size = Int(bytes[i - 4]) << 24 | Int(bytes[i - 3]) << 16 | Int(bytes[i - 2]) << 8 | Int(bytes[i - 1])
                if size >= 8, i - 4 + size <= bytes.count { return Array(bytes[(i + 4)..<(i - 4 + size)]) }
            }
            i += 1
        }
        return nil
    }
    #endif

    // MARK: Subtitles on screen

    /// The engine publishes a window of decoded cues around the playhead, not just what's on screen now, so the line
    /// to draw is whichever cues' spans the clock is inside. Picked again on every tick, so a line goes when its time
    /// is up rather than piling up under the next ones.
    private var decodedCues: [SubtitleCue] = []
    private var shownCueIDs: [Int] = []

    private func showCues(at seconds: Double) {
        overlay.videoSize = videoSize
        // A rewind can decode a stretch again under new ids: the same line twice is drawn once.
        var seen = Set<String>()
        let active = decodedCues.filter { cue in
            guard cue.startTime <= seconds && seconds < cue.endTime else { return false }
            guard let text = Self.text(of: cue) else { return true }
            return seen.insert("\(cue.startTime)|\(cue.endTime)|\(text)").inserted
        }
        let ids = active.map(\.id)
        guard ids != shownCueIDs else { return }
        shownCueIDs = ids
        overlay.show(active)
    }

    private static func text(of cue: SubtitleCue) -> String? {
        switch cue.body {
        case .text(let string): string
        case .richText(let runs): runs.map(\.text).joined()
        case .image: nil
        }
    }

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
                self.showCues(at: seconds)
                self.delegate?.transport(self, tick: seconds)
            }
            .store(in: &cancellables)
        engine.$subtitleCues
            .receive(on: DispatchQueue.main)
            .sink { [weak self] cues in
                guard let self else { return }
                self.decodedCues = cues
                self.showCues(at: self.currentTime)
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
