import AppKit
import AVFoundation
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

    /// How far the skip buttons and the arrow keys jump: 30 seconds unless Settings says otherwise.
    static var skipSeconds: Double {
        let saved = UserDefaults.standard.double(forKey: "FlexarrSkipSeconds")
        return saved > 0 ? saved : 30
    }

    private var finished = false
    private var title_: (String, String?) = ("", nil)
    private var artwork: NSImage?
    let sleep = SleepTimer()

    /// A sheet or alert over the player (the subtitle search): the controller waits for it to go.
    var presentedViewController: NSViewController? { presentedViewControllers?.first }

    var transport: PlayerTransport? {
        didSet {
            oldValue?.delegate = nil
            oldValue?.view.removeFromSuperview()
            guard let transport else { return }
            transport.delegate = self
            if isViewLoaded { install(transport) }
        }
    }

    var markers: [Marker] = []
    var previews: TrickplayPreviews?
    var episodeEnd: EpisodeEnd?
    var sleepHoldsEpisode: Bool { sleep.setting == .endOfEpisode }

    init() {
        super.init(nibName: nil, bundle: nil)
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) is not used") }

    override func loadView() {
        let root = NSView()
        root.wantsLayer = true
        root.layer?.backgroundColor = NSColor.black.cgColor
        view = root
        if let transport { install(transport) }
    }

    private func install(_ transport: PlayerTransport) {
        let video = transport.view
        video.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(video, positioned: .below, relativeTo: nil)
        NSLayoutConstraint.activate([
            video.leadingAnchor.constraint(equalTo: view.leadingAnchor),
            video.trailingAnchor.constraint(equalTo: view.trailingAnchor),
            video.topAnchor.constraint(equalTo: view.topAnchor),
            video.bottomAnchor.constraint(equalTo: view.bottomAnchor),
        ])
    }

    // MARK: What the controller tells the screen

    func setTitle(_ title: String, subtitle: String?) { title_ = (title, subtitle) }
    func setDetail(_ detail: String?) {}
    func setArtwork(_ image: NSImage?) { artwork = image }
    func setEpisodeNavigation(hasPrevious: Bool, hasNext: Bool) {}
    func setPrompt(_ title: String?, symbol: String = "forward.fill", countingDown: Bool, takesFocus: Bool = true) {}
    func setAudioSync(seconds: Double?) {}
    func setSubtitleOptions(delay: Double?, size: SubtitleSize, placement: SubtitlePlacement) {
        transport?.subtitleSize = size
        transport?.subtitlePlacement = placement
    }
    func setSubtitleSearch(available: Bool, files: Set<String>, resyncing: Bool) {}
    func flash(_ message: String) {}
    func showLoading() {}

    /// Live TV: a Channels button, and the arrow keys up and down change channel.
    private(set) var isLive = false
    private var onChannels: (() -> Void)?
    func configureLive(onChannels: @escaping () -> Void) {
        isLive = true
        self.onChannels = onChannels
    }

    func applyRate(_ rate: Float) {
        guard let transport, rate <= transport.maxRate else { return }
        transport.setRate(rate)
    }

    var remoteTracks: PlayerTracks? {
        guard let transport else { return nil }
        return PlayerTracks(
            audio: transport.audioTracks.map { PlayerTrack(id: $0.id, label: $0.label, selected: $0.id == transport.activeAudioTrack) },
            subtitles: transport.subtitleTracks.map { PlayerTrack(id: $0.id, label: $0.label, selected: $0.id == transport.activeSubtitleTrack) })
    }

    func remoteSelectAudio(_ id: String) {
        guard let transport, id != transport.activeAudioTrack else { return }
        if !transport.selectAudioTrack(id) { delegate?.playerScreen(self, selectAudio: id) }
    }

    func remoteSelectSubtitle(_ id: String?) {
        transport?.selectSubtitleTrack(id)
        delegate?.playerScreen(self, choseSubtitle: .some(transport?.subtitleTracks.first { $0.id == id }?.key))
    }

    func remoteChooseRate(_ rate: Float) {
        guard let transport, rate > 0, rate <= transport.maxRate else { return }
        transport.setRate(rate)
        delegate?.playerScreen(self, choseRate: transport.rate)
    }

    func togglePlayPause() {
        guard let transport, transport.phase != .ended else { return }
        transport.isPlaying ? transport.pause() : transport.play()
    }

    func next() { delegate?.playerScreenNext(self) }
    func previous() { delegate?.playerScreenPrevious(self) }

    func skip(_ seconds: Double, flash: Bool = false) {
        guard let transport else { return }
        seek(to: transport.currentTime + seconds)
    }

    func seek(to seconds: Double) {
        guard let transport else { return }
        let target = max(0, min(seconds, transport.duration > 0 ? transport.duration : seconds))
        transport.seek(to: target)
        delegate?.playerScreen(self, didSeekTo: target)
    }

    func sleepAtEndOfEpisode() {
        transport?.pause()
    }

    /// Closes the screen: the controller asks, having stopped the session.
    func finish(completion: (() -> Void)? = nil) {
        finished = true
        sleep.set(nil)
        PlayerHost.shared.remove(self)
        completion?()
    }

    // MARK: Sheets and alerts

    /// A SwiftUI sheet over the player.
    func presentSheet(_ content: some View) -> PlayerSheetHost {
        let controller = NSHostingController(rootView: content.frame(minWidth: 520, minHeight: 420))
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

    func close() {
        log.info("Player closed")
        delegate?.playerScreenDidClose(self)
    }
}

extension PlayerViewController: PlayerTransportDelegate {
    func transport(_ transport: PlayerTransport, phaseChanged phase: TransportPhase) {
        delegate?.playerScreen(self, phaseChanged: phase)
    }

    func transport(_ transport: PlayerTransport, tick seconds: Double) {
        delegate?.playerScreen(self, tick: seconds)
    }

    func transportTracksChanged(_ transport: PlayerTransport) {
        delegate?.playerScreenTracksChanged(self)
    }
}
