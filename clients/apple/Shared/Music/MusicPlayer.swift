import AVFoundation
import FlexarrKit
import MediaPlayer
import Observation
import os
import UIKit

private let log = Logger(subsystem: Bundle.main.bundleIdentifier ?? "io.github.flexarr", category: "music")

/// Plays a queue of tracks through one AVPlayer: each track is its own server session (started as it comes up,
/// reported on as it plays, ended as it finishes), so the server's history and stream limits see music the way they
/// see video. Lives for the app's lifetime so music carries on while the viewer browses, and tells the lock screen,
/// Control Center and the Siri Remote what's playing.
@MainActor
@Observable
final class MusicPlayer {
    enum RepeatMode {
        case off, all, one

        var next: RepeatMode {
            switch self {
            case .off: .all
            case .all: .one
            case .one: .off
            }
        }
    }

    /// The tracks in the order they'll play (shuffled when `isShuffled`).
    private(set) var queue: [ItemSummary] = []
    private(set) var index = 0
    private(set) var isPlaying = false
    /// A session is being started for the current track.
    private(set) var isLoading = false
    private(set) var position: Double = 0
    private(set) var duration: Double = 0
    private(set) var isShuffled = false
    private(set) var repeatMode: RepeatMode = .off
    /// The current track's cover, once fetched (also what the lock screen shows).
    private(set) var artwork: UIImage?
    var errorMessage: String?
    /// When false, sessions are still started and stopped but progress is never reported.
    @ObservationIgnored var recordsHistory = true

    @ObservationIgnored private var client: FlexarrClient?
    /// The queue as it was given, for turning shuffle back off.
    @ObservationIgnored private var original: [ItemSummary] = []
    /// Album names by album id: a track summary carries its artist but not its album, so the screen that queued the
    /// tracks passes the albums it knows.
    @ObservationIgnored private var albumTitles: [String: String] = [:]
    @ObservationIgnored private let player = AVPlayer()
    @ObservationIgnored private var sessionId: String?
    @ObservationIgnored private var timeObserver: Any?
    @ObservationIgnored private var itemObservers: [NSObjectProtocol] = []
    @ObservationIgnored private var statusObservation: NSKeyValueObservation?
    @ObservationIgnored private var interruptionObserver: NSObjectProtocol?
    @ObservationIgnored private let nowPlaying = NowPlaying()
    @ObservationIgnored private var lastReport = Date.distantPast
    @ObservationIgnored private var lastNowPlayingTime = Date.distantPast
    /// Bumped by every track change, so a session that arrives for a track skipped past is ended rather than played.
    @ObservationIgnored private var generation = 0
    /// Paused because a video started; Now Playing is handed back when it ends.
    @ObservationIgnored private var yieldedToVideo = false

    private static let reportInterval: TimeInterval = 10

    init() {
        player.audiovisualBackgroundPlaybackPolicy = .continuesIfPossible
        #if os(iOS)
        player.allowsExternalPlayback = true
        #endif
        statusObservation = player.observe(\.timeControlStatus, options: [.new]) { [weak self] player, _ in
            let status = player.timeControlStatus
            Task { @MainActor [weak self] in self?.statusChanged(status) }
        }
        timeObserver = player.addPeriodicTimeObserver(forInterval: CMTime(seconds: 0.5, preferredTimescale: 600), queue: .main) { [weak self] time in
            MainActor.assumeIsolated { self?.tick(time.seconds) }
        }
        interruptionObserver = NotificationCenter.default.addObserver(forName: AVAudioSession.interruptionNotification, object: nil, queue: .main) { [weak self] note in
            // Only plain numbers cross to the main actor; the notification itself isn't Sendable.
            let type = note.userInfo?[AVAudioSessionInterruptionTypeKey] as? UInt
            let options = note.userInfo?[AVAudioSessionInterruptionOptionKey] as? UInt
            MainActor.assumeIsolated { self?.interrupted(type: type, options: options) }
        }
        nowPlaying.onPlay = { [weak self] in self?.resume() }
        nowPlaying.onPause = { [weak self] in self?.pause() }
        nowPlaying.onNext = { [weak self] in self?.next() }
        nowPlaying.onPrevious = { [weak self] in self?.previous() }
        nowPlaying.onSeek = { [weak self] seconds in self?.seek(to: seconds) }
    }

    var current: ItemSummary? { queue.indices.contains(index) ? queue[index] : nil }
    /// What plays after the current track, in order.
    var upNext: [ItemSummary] { Array(queue.dropFirst(index + 1)) }
    var hasNext: Bool { index + 1 < queue.count || (repeatMode == .all && !queue.isEmpty) }
    var hasPrevious: Bool { index > 0 || repeatMode == .all }

    /// The name of the album a queued track is from, when the screen that queued it said.
    func albumTitle(of track: ItemSummary) -> String? {
        track.parentId.flatMap { albumTitles[$0] }
    }

    // MARK: Playing

    /// Replaces the queue with `tracks` and starts the one at `start`; with `shuffled`, a fresh order starts from its
    /// first track instead. `albums` lends the tracks their album names for Now Playing.
    func play(_ tracks: [ItemSummary], from start: Int = 0, shuffled: Bool = false, albums: [ItemSummary] = [], client: FlexarrClient) {
        guard !tracks.isEmpty else { return }
        self.client = client
        original = tracks
        albumTitles = Dictionary(albums.map { ($0.id, $0.title) }, uniquingKeysWith: { first, _ in first })
        isShuffled = shuffled
        if shuffled {
            queue = tracks.shuffled()
            index = 0
        } else {
            queue = tracks
            index = min(max(0, start), tracks.count - 1)
        }
        startCurrent()
    }

    func togglePlayPause() {
        isPlaying ? pause() : resume()
    }

    func pause() {
        player.pause()
    }

    func resume() {
        guard current != nil else { return }
        yieldedToVideo = false
        activateAudioSession()
        if player.currentItem == nil {
            startCurrent() // Stopped for a failure (or by a video): ask for the track again.
        } else {
            player.play()
            if nowPlayingIsIdle { pushNowPlaying() }
        }
    }

    func next() {
        guard !queue.isEmpty else { return }
        if index + 1 < queue.count {
            index += 1
        } else if repeatMode == .all {
            index = 0
        } else {
            return
        }
        startCurrent()
    }

    /// Like every music player: back restarts the track unless it's just begun.
    func previous() {
        guard !queue.isEmpty else { return }
        if position > 3 {
            seek(to: 0)
            return
        }
        if index > 0 {
            index -= 1
        } else if repeatMode == .all {
            index = queue.count - 1
        } else {
            seek(to: 0)
            return
        }
        startCurrent()
    }

    /// Jumps to a track in the queue (from the Up Next list).
    func skip(to target: Int) {
        guard queue.indices.contains(target) else { return }
        index = target
        startCurrent()
    }

    func seek(to seconds: Double) {
        guard current != nil else { return }
        let target = max(0, min(seconds, duration > 0 ? duration : seconds))
        position = target
        player.seek(to: CMTime(seconds: target, preferredTimescale: 600), toleranceBefore: .zero, toleranceAfter: .zero)
        nowPlaying.setTime(position: target, duration: duration, rate: isPlaying ? 1 : 0)
    }

    /// Shuffles what's left to play (the current track keeps playing), or restores the given order.
    func toggleShuffle() {
        guard let current else { isShuffled.toggle(); return }
        if isShuffled {
            isShuffled = false
            queue = original
            index = original.firstIndex(of: current) ?? 0
        } else {
            isShuffled = true
            queue = [current] + original.filter { $0.id != current.id }.shuffled()
            index = 0
        }
        pushNowPlaying()
    }

    func cycleRepeat() {
        repeatMode = repeatMode.next
        pushNowPlaying()
    }

    /// Ends the session and empties the queue (the now-playing bar goes away).
    func stop() {
        generation += 1
        endSession()
        clearItem()
        queue = []
        original = []
        albumTitles = [:]
        index = 0
        position = 0
        duration = 0
        artwork = nil
        isLoading = false
        nowPlaying.deactivate()
    }

    // MARK: Giving way to video

    /// A video is starting: music pauses and lets go of the remote's commands, which the video player takes.
    func yieldToVideo() {
        guard current != nil else { return }
        pause()
        yieldedToVideo = true
        nowPlaying.deactivate()
    }

    /// The video has finished: the lock screen shows the paused music again, and play resumes it.
    func videoEnded() {
        guard yieldedToVideo, current != nil else { return }
        yieldedToVideo = false
        pushNowPlaying()
    }

    // MARK: Sessions

    private func startCurrent() {
        guard let client, let track = current else { return }
        generation += 1
        let mine = generation
        endSession()
        clearItem()
        position = 0
        duration = track.durationSeconds ?? 0
        artwork = nil
        isLoading = true
        errorMessage = nil
        yieldedToVideo = false
        activateAudioSession()
        pushNowPlaying()
        loadArtwork(for: track, generation: mine)
        Task {
            do {
                // Tracks always start over: a half-heard song isn't resumed the way a film is.
                let request = PlaybackRequest(itemId: track.id, profile: Self.profile, startPositionSeconds: 0)
                let info = try await client.startPlayback(request)
                guard mine == generation else {
                    try? await client.stopPlayback(sessionId: info.sessionId) // Skipped past before it arrived.
                    return
                }
                guard let url = client.url(for: info.url) else { throw FlexarrClientError.invalidResponse }
                sessionId = info.sessionId
                if info.durationSeconds > 0 { duration = info.durationSeconds }
                lastReport = Date()
                attach(AVPlayerItem(url: url))
                player.play()
                isLoading = false
                pushNowPlaying()
            } catch {
                guard mine == generation else { return }
                isLoading = false
                log.error("Couldn't start \(track.title, privacy: .public): \(error)")
                errorMessage = ConnectionError(error).message
            }
        }
    }

    private func attach(_ item: AVPlayerItem) {
        clearItem()
        player.replaceCurrentItem(with: item)
        itemObservers = [
            NotificationCenter.default.addObserver(forName: .AVPlayerItemDidPlayToEndTime, object: item, queue: .main) { [weak self] _ in
                MainActor.assumeIsolated { self?.trackEnded() }
            },
            NotificationCenter.default.addObserver(forName: .AVPlayerItemFailedToPlayToEndTime, object: item, queue: .main) { [weak self] note in
                let reason = (note.userInfo?[AVPlayerItemFailedToPlayToEndTimeErrorKey] as? Error)?.localizedDescription
                MainActor.assumeIsolated { self?.trackFailed(reason) }
            },
        ]
    }

    private func clearItem() {
        for observer in itemObservers { NotificationCenter.default.removeObserver(observer) }
        itemObservers = []
        player.replaceCurrentItem(with: nil)
    }

    /// Reports where the track got to and ends its session (best effort; the server times abandoned ones out).
    private func endSession() {
        guard let client, let sessionId else { return }
        self.sessionId = nil
        let position = self.position
        let record = recordsHistory
        Task {
            if record { try? await client.reportProgress(sessionId: sessionId, positionSeconds: position, paused: true) }
            try? await client.stopPlayback(sessionId: sessionId)
        }
    }

    private func report(paused: Bool, ended: Bool = false) {
        guard recordsHistory, let client, let sessionId else { return }
        lastReport = Date()
        let position = ended ? duration : self.position
        Task { try? await client.reportProgress(sessionId: sessionId, positionSeconds: position, paused: paused, ended: ended) }
    }

    private func trackEnded() {
        report(paused: true, ended: true)
        if repeatMode == .one {
            startCurrent()
        } else if index + 1 < queue.count {
            index += 1
            startCurrent()
        } else if repeatMode == .all {
            index = 0
            startCurrent()
        } else {
            // The end of the queue: stay on the last track, ready to play again.
            endSession()
            clearItem()
            position = 0
            isPlaying = false
            pushNowPlaying()
        }
    }

    private func trackFailed(_ reason: String?) {
        log.error("Track failed: \(reason ?? "no reason", privacy: .public)")
        errorMessage = reason ?? "This track couldn't be played."
        endSession()
        clearItem()
        // Move on rather than fall silent, unless it was the last one.
        if index + 1 < queue.count { next() }
    }

    // MARK: Observing the player

    private func statusChanged(_ status: AVPlayer.TimeControlStatus) {
        let playing = status == .playing
        guard playing != isPlaying else { return }
        isPlaying = playing
        if !playing, sessionId != nil, player.currentItem != nil { report(paused: true) }
        nowPlaying.setTime(position: position, duration: duration, rate: playing ? 1 : 0)
    }

    private func tick(_ seconds: Double) {
        guard seconds.isFinite, player.currentItem != nil else { return }
        position = seconds
        if let length = player.currentItem?.duration.seconds, length.isFinite, length > 0, abs(length - duration) > 1 {
            duration = length
        }
        if Date().timeIntervalSince(lastNowPlayingTime) > 1 {
            lastNowPlayingTime = Date()
            nowPlaying.setTime(position: seconds, duration: duration, rate: isPlaying ? 1 : 0)
        }
        if isPlaying, Date().timeIntervalSince(lastReport) >= Self.reportInterval { report(paused: false) }
    }

    /// A call or Siri: the system pauses the music, and it comes back after if the system says it may.
    private func interrupted(type: UInt?, options: UInt?) {
        guard let type, AVAudioSession.InterruptionType(rawValue: type) == .ended else { return }
        if AVAudioSession.InterruptionOptions(rawValue: options ?? 0).contains(.shouldResume), current != nil, !yieldedToVideo { player.play() }
    }

    // MARK: Now Playing and sound

    private func activateAudioSession() {
        // Sound on with the ring switch off, and playing on with the screen locked.
        try? AVAudioSession.sharedInstance().setCategory(.playback, mode: .default)
        try? AVAudioSession.sharedInstance().setActive(true)
    }

    private var nowPlayingIsIdle: Bool { MPNowPlayingInfoCenter.default().nowPlayingInfo == nil }

    private func pushNowPlaying() {
        guard let track = current else { return }
        if nowPlayingIsIdle { nowPlaying.activateForMusic() }
        nowPlaying.setTrack(title: track.title, artist: track.artistName, album: albumTitle(of: track), artwork: artwork)
        nowPlaying.setEpisodeNavigation(hasPrevious: hasPrevious || position > 3, hasNext: hasNext)
        nowPlaying.setTime(position: position, duration: duration, rate: isPlaying ? 1 : 0)
    }

    private func loadArtwork(for track: ItemSummary, generation mine: Int) {
        guard let client, let url = client.imageURL(track.artwork.poster, width: 600) else { return }
        Task {
            let image = await ImageLoader.shared.load(url)
            guard mine == generation, let image else { return }
            artwork = image
            pushNowPlaying()
        }
    }

    /// Music asks nothing of the picture: the usual profile with the display's HDR left out, so the server copies the
    /// sound as it is when the player can take it and converts it otherwise.
    private static var profile: DeviceProfile {
        #if os(tvOS)
        .tvOS(hdr: false)
        #else
        .iOS(hdr: false)
        #endif
    }
}
