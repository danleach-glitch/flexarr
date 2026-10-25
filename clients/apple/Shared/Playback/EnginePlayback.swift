#if canImport(AetherEngine)
import AetherEngine
import AVKit
import Combine
import FlexarrKit
import os
import UIKit

private let log = Logger(subsystem: Bundle.main.bundleIdentifier ?? "io.github.flexarr", category: "engine")

/// One title played as it is on the device by AetherEngine: it demuxes the file from the server and VideoToolbox
/// decodes it, and its AVPlayer goes into the same AVPlayerViewController a server stream would, so the rest of the
/// player (markers, countdown, reporting, Apple's menus for text subtitles) is shared. What Apple's player can't see
/// is here: the file's audio tracks, its picture subtitles (drawn over the video), and the sidecar files beside it.
@MainActor
final class EnginePlayback {
    let engine: AetherEngine
    private let info: PlaybackInfo
    private let client: FlexarrClient
    private weak var controller: AVPlayerViewController?
    private let overlay = PictureSubtitleView()
    private var cancellables: Set<AnyCancellable> = []

    /// The engine's AVPlayer, once the file is open on the hardware path.
    var onReady: ((AVPlayer) -> Void)?
    /// The file can't be played this way (software decoding, or a failure): the server stream is the fallback.
    var onFailure: ((String) -> Void)?
    /// The audio or subtitle tracks changed (the menus rebuild).
    var onTracksChanged: (() -> Void)?

    init(info: PlaybackInfo, client: FlexarrClient, controller: AVPlayerViewController) throws {
        self.info = info
        self.client = client
        self.controller = controller
        engine = try AetherEngine()
    }

    /// Opens the file where playback starts, with the server's choice of audio track.
    func start(at seconds: Double, audioIndex: Int?) {
        guard let url = client.url(for: info.url), let controller else { return }
        var options = LoadOptions()
        options.prepareNativeSubtitles = true   // text subtitles as renditions: Apple's menu, PiP and AirPlay have them
        options.serveIFramePlaylist = true      // Apple's scrubbing thumbnails
        options.externalSubtitles = sidecars()
        let modes = AVPlayer.availableHDRModes
        options.panelIsInHDRMode = !modes.isEmpty
        options.panelPresentsDolbyVision = modes.contains(.dolbyVision)
        #if os(tvOS)
        options.matchContentEnabled = controller.view.window?.avDisplayManager.isDisplayCriteriaMatchingEnabled ?? false
        #endif

        overlay.translatesAutoresizingMaskIntoConstraints = false
        overlay.isUserInteractionEnabled = false
        if let host = controller.contentOverlayView {
            host.addSubview(overlay)
            NSLayoutConstraint.activate([
                overlay.leadingAnchor.constraint(equalTo: host.leadingAnchor),
                overlay.trailingAnchor.constraint(equalTo: host.trailingAnchor),
                overlay.topAnchor.constraint(equalTo: host.topAnchor),
                overlay.bottomAnchor.constraint(equalTo: host.bottomAnchor),
            ])
        }

        log.info("Opening \(self.info.item.title, privacy: .public) at \(seconds)")
        Task { [weak self] in
            guard let self else { return }
            do {
                _ = try await engine.load(url: url, startPosition: seconds > 1 ? seconds : nil, options: options,
                                          audioSourceStreamIndex: audioIndex.map(Int32.init))
            } catch {
                log.error("Couldn't open: \(error.localizedDescription, privacy: .public)")
                onFailure?(error.localizedDescription)
                return
            }

            guard let player = engine.currentAVPlayer else {
                // Software decoding draws into the engine's own view, which isn't hosted yet: the server converts instead.
                log.info("Route \(self.engine.videoRoute.rawValue, privacy: .public): not hosted, falling back to the server")
                onFailure?("This file needs software decoding, which isn't in this build yet.")
                return
            }

            log.info("Route \(self.engine.videoRoute.rawValue, privacy: .public), audio \(self.engine.audioTracks.count) tracks, subtitles \(self.engine.subtitleTracks.count)")
            observe()
            selectInitialPictureSubtitle()
            onReady?(player)
        }
    }

    func stop() {
        cancellables.removeAll()
        overlay.removeFromSuperview()
        engine.stop()
    }

    // MARK: Tracks

    var audioTracks: [TrackInfo] { engine.audioTracks }
    var activeAudioTrack: Int? { engine.activeAudioTrackIndex }

    /// The file's picture subtitles (PGS, DVB, DVD), which Apple's menu can't list: drawn here when chosen.
    var pictureSubtitleTracks: [TrackInfo] {
        engine.subtitleTracks.filter { !$0.isNativelyRenderedSubtitle && Self.isPicture($0.codec) }
    }

    var activePictureSubtitle: Int? {
        guard let active = engine.activeSubtitleTrackIndex, pictureSubtitleTracks.contains(where: { $0.id == active }) else { return nil }
        return active
    }

    func selectAudioTrack(_ id: Int) {
        engine.selectAudioTrack(index: id)
    }

    /// A picture subtitle chosen here: Apple's own selection (a text track) is turned off so only one shows.
    func selectPictureSubtitle(_ id: Int?) {
        if let id {
            if let player = engine.currentAVPlayer, let item = player.currentItem,
               let group = item.asset.mediaSelectionGroup(forMediaCharacteristic: .legible) {
                item.select(nil, in: group)
            }
            engine.selectSubtitleTrack(index: id)
        } else {
            engine.clearSubtitle()
            overlay.show([])
        }
    }

    /// Apple's menu chose a text subtitle (or Off): a picture one drawn here gives way.
    func nativeSubtitleChanged() {
        if activePictureSubtitle != nil {
            engine.clearSubtitle()
            overlay.show([])
        }
    }

    private func selectInitialPictureSubtitle() {
        guard let key = info.selectedSubtitleKey, let track = info.subtitles.first(where: { $0.key == key }), !track.isText,
              key.hasPrefix("e"), let index = Int(key.dropFirst()), pictureSubtitleTracks.contains(where: { $0.id == index }) else { return }
        engine.selectSubtitleTrack(index: index)
    }

    private static func isPicture(_ codec: String) -> Bool {
        codec.contains("pgs") || codec.contains("dvb") || codec.contains("dvd") || codec == "xsub"
    }

    /// Subtitle files beside the video, which the demuxer doesn't see: the server's WebVTT copies of them.
    private func sidecars() -> [ExternalSubtitleTrack] {
        info.subtitles.compactMap { track in
            guard track.key.hasPrefix("x"), track.isText, let path = track.url, let url = client.url(for: path) else { return nil }
            return ExternalSubtitleTrack(url: url, name: track.label, language: track.language, isForced: track.isForced, isDefault: track.isDefault, formatHint: "webvtt")
        }
    }

    private func observe() {
        engine.$subtitleCues
            .receive(on: DispatchQueue.main)
            .sink { [weak self] cues in
                guard let self else { return }
                let images = cues.compactMap { cue -> SubtitleImage? in
                    if case .image(let image) = cue.body { return image }
                    return nil
                }
                overlay.videoSize = engine.currentAVPlayerItem?.presentationSize ?? .zero
                overlay.show(images)
            }
            .store(in: &cancellables)
        engine.$audioTracks.combineLatest(engine.$subtitleTracks)
            .receive(on: DispatchQueue.main)
            .sink { [weak self] _, _ in self?.onTracksChanged?() }
            .store(in: &cancellables)
    }
}

/// Picture subtitles over the video, placed as the disc authored them: the cue's canvas is mapped width-aligned and
/// centre-anchored onto the video's rectangle (the picture fitted into this view, as the player shows it).
private final class PictureSubtitleView: UIView {
    var videoSize: CGSize = .zero {
        didSet { if videoSize != oldValue { setNeedsLayout() } }
    }

    private var videoRect: CGRect {
        guard videoSize.width > 0, videoSize.height > 0, bounds.width > 0, bounds.height > 0 else { return bounds }
        let scale = min(bounds.width / videoSize.width, bounds.height / videoSize.height)
        let size = CGSize(width: videoSize.width * scale, height: videoSize.height * scale)
        return CGRect(x: (bounds.width - size.width) / 2, y: (bounds.height - size.height) / 2, width: size.width, height: size.height)
    }

    private var images: [SubtitleImage] = []
    private var views: [UIImageView] = []

    func show(_ images: [SubtitleImage]) {
        self.images = images
        while views.count < images.count {
            let view = UIImageView()
            view.contentMode = .scaleToFill
            addSubview(view)
            views.append(view)
        }

        setNeedsLayout()
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        let video = videoRect
        for (i, view) in views.enumerated() {
            guard i < images.count else {
                view.isHidden = true
                continue
            }

            let image = images[i]
            let canvas = image.canvasSize == .zero ? CGSize(width: image.position.maxX, height: image.position.maxY) : image.canvasSize
            let scale = canvas.width > 0 ? video.width / canvas.width : 1
            let top = video.minY + (video.height - canvas.height * scale) / 2
            view.image = UIImage(cgImage: image.cgImage)
            view.frame = CGRect(x: video.minX + image.position.minX * scale, y: top + image.position.minY * scale,
                                width: image.position.width * scale, height: image.position.height * scale)
            view.isHidden = false
        }
    }
}
#endif
