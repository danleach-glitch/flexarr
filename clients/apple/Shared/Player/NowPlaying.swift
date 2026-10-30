import FlexarrKit
import MediaPlayer
import UIKit

/// What a device in the house is playing, as the server says (this app's own `NowPlaying` is the lock screen's).
typealias DeviceNowPlaying = FlexarrKit.NowPlaying

/// What the lock screen, Control Center, a paired watch and (on Apple TV) the Remote app show and can do while a
/// title plays. Apple's player did this for us; now it's ours.
@MainActor
final class NowPlaying {
    var onPlay: (() -> Void)?
    var onPause: (() -> Void)?
    var onSkip: ((Double) -> Void)?
    var onSeek: ((Double) -> Void)?
    var onPrevious: (() -> Void)?
    var onNext: (() -> Void)?
    var onRate: ((Double) -> Void)?

    private var info: [String: Any] = [:]
    private var targets: [(MPRemoteCommand, Any)] = []

    func activate(skipSeconds: Double) {
        let center = MPRemoteCommandCenter.shared()
        deactivate()
        func on(_ command: MPRemoteCommand, _ action: @escaping (MPRemoteCommandEvent) -> Void) {
            command.isEnabled = true
            targets.append((command, command.addTarget { event in
                MainActor.assumeIsolated { action(event) }
                return .success
            }))
        }
        on(center.playCommand) { [weak self] _ in self?.onPlay?() }
        on(center.pauseCommand) { [weak self] _ in self?.onPause?() }
        on(center.togglePlayPauseCommand) { [weak self] _ in
            guard let self else { return }
            (self.info[MPNowPlayingInfoPropertyPlaybackRate] as? Double ?? 0) > 0 ? self.onPause?() : self.onPlay?()
        }
        center.skipForwardCommand.preferredIntervals = [NSNumber(value: skipSeconds)]
        center.skipBackwardCommand.preferredIntervals = [NSNumber(value: skipSeconds)]
        on(center.skipForwardCommand) { [weak self] event in self?.onSkip?((event as? MPSkipIntervalCommandEvent)?.interval ?? skipSeconds) }
        on(center.skipBackwardCommand) { [weak self] event in self?.onSkip?(-((event as? MPSkipIntervalCommandEvent)?.interval ?? skipSeconds)) }
        on(center.changePlaybackPositionCommand) { [weak self] event in
            if let event = event as? MPChangePlaybackPositionCommandEvent { self?.onSeek?(event.positionTime) }
        }
        on(center.nextTrackCommand) { [weak self] _ in self?.onNext?() }
        on(center.previousTrackCommand) { [weak self] _ in self?.onPrevious?() }
        center.nextTrackCommand.isEnabled = false
        center.previousTrackCommand.isEnabled = false
        center.changePlaybackRateCommand.isEnabled = false
    }

    /// For music: previous and next track in place of the skip buttons.
    func activateForMusic() {
        activate(skipSeconds: 15)
        let center = MPRemoteCommandCenter.shared()
        center.skipForwardCommand.isEnabled = false
        center.skipBackwardCommand.isEnabled = false
        center.nextTrackCommand.isEnabled = true
        center.previousTrackCommand.isEnabled = true
    }

    /// For podcasts: skip back 15 seconds and forward 30, as podcast apps do, and the speed the lock screen can change.
    func activateForPodcast(back: Double = 15, forward: Double = 30) {
        activate(skipSeconds: forward)
        let center = MPRemoteCommandCenter.shared()
        center.skipBackwardCommand.preferredIntervals = [NSNumber(value: back)]
        center.nextTrackCommand.isEnabled = false
        center.previousTrackCommand.isEnabled = false
        center.changePlaybackRateCommand.supportedPlaybackRates = MusicPlayer.speeds.map { NSNumber(value: $0) }
        center.changePlaybackRateCommand.isEnabled = true
        targets.append((center.changePlaybackRateCommand, center.changePlaybackRateCommand.addTarget { [weak self] event in
            if let rate = (event as? MPChangePlaybackRateCommandEvent)?.playbackRate {
                MainActor.assumeIsolated { self?.onRate?(Double(rate)) }
            }
            return .success
        }))
    }

    func deactivate() {
        for (command, target) in targets { command.removeTarget(target) }
        targets = []
        info = [:]
        MPNowPlayingInfoCenter.default().nowPlayingInfo = nil
    }

    func setTitle(_ title: String, subtitle: String?, artwork: UIImage?) {
        info[MPMediaItemPropertyTitle] = title
        info[MPMediaItemPropertyArtist] = subtitle
        info[MPNowPlayingInfoPropertyMediaType] = MPNowPlayingInfoMediaType.video.rawValue
        if let artwork {
            info[MPMediaItemPropertyArtwork] = Self.artwork(artwork)
        } else {
            info[MPMediaItemPropertyArtwork] = nil
        }
        push()
    }

    /// A track: the lock screen shows the artist and album under the title, and the system treats it as music
    /// (the skip buttons give way to previous and next track).
    func setTrack(title: String, artist: String?, album: String?, artwork: UIImage?) {
        info[MPMediaItemPropertyTitle] = title
        info[MPMediaItemPropertyArtist] = artist
        info[MPMediaItemPropertyAlbumTitle] = album
        info[MPNowPlayingInfoPropertyMediaType] = MPNowPlayingInfoMediaType.audio.rawValue
        info[MPMediaItemPropertyArtwork] = artwork.map(Self.artwork)
        push()
    }

    func setEpisodeNavigation(hasPrevious: Bool, hasNext: Bool) {
        MPRemoteCommandCenter.shared().nextTrackCommand.isEnabled = hasNext
        MPRemoteCommandCenter.shared().previousTrackCommand.isEnabled = hasPrevious
    }

    /// `rate` is 0 while paused, else the speed playing; `defaultRate` is the speed it plays at (a podcast's 1.5×).
    func setTime(position: Double, duration: Double, rate: Float, defaultRate: Float = 1) {
        info[MPNowPlayingInfoPropertyElapsedPlaybackTime] = position
        info[MPMediaItemPropertyPlaybackDuration] = duration
        info[MPNowPlayingInfoPropertyPlaybackRate] = Double(rate)
        info[MPNowPlayingInfoPropertyDefaultPlaybackRate] = Double(defaultRate)
        push()
    }

    /// The system asks for the image on its own queue, so the request handler must not be main-actor bound.
    nonisolated private static func artwork(_ image: UIImage) -> MPMediaItemArtwork {
        MPMediaItemArtwork(boundsSize: image.size) { _ in image }
    }

    private func push() {
        MPNowPlayingInfoCenter.default().nowPlayingInfo = info
    }
}
