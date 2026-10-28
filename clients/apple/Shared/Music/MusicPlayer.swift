import AVFoundation
import FlexarrKit
import MediaPlayer
import Observation
import UIKit

private let log = FlexarrLog("music")

/// Where downloaded tracks are kept, so one plays from its file with no server session (and no connection): the
/// iPhone's downloads. A play heard through is recorded here, to be told to the server once there's a connection.
@MainActor
protocol OfflineMusicLibrary: AnyObject {
    /// The track's file on this device, if it's downloaded.
    func localFile(for trackId: String) -> URL?
    /// The cover saved with it.
    func localArtwork(for trackId: String) -> UIImage?
    /// Where listening to a downloaded podcast episode got to on this device.
    func resumePosition(for trackId: String) -> Double
    /// With no connection, what plays after a podcast episode: the next downloaded, unplayed one of its show.
    func nextDownloadedEpisode(after episode: ItemSummary) -> ItemSummary?
    func record(itemId: String, position: Double, finished: Bool)
}

/// Plays a queue of tracks through one AVPlayer: each track is its own server session (started as it comes up,
/// reported on as it plays, ended as it finishes), so the server's history and stream limits see music the way they
/// see video. A downloaded track plays from its file instead, with no session. Lives for the app's lifetime so music
/// carries on while the viewer browses, and tells the lock screen, Control Center and the Siri Remote what's playing.
/// Podcast episodes play here too, with what podcasts need: they resume, play faster or slower, skip back 15 seconds
/// and forward 30, can stop at a sleep timer, and go on by themselves to the next episode the server suggests.
@MainActor
@Observable
final class MusicPlayer {
    /// The speeds a podcast can play at.
    static let speeds: [Double] = [0.8, 1, 1.2, 1.5, 1.75, 2, 2.5, 3]

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
    /// The current track is playing from its downloaded file rather than the server.
    private(set) var isPlayingOffline = false
    var errorMessage: String?
    /// How fast the podcast episode plays (music always plays at 1×).
    private(set) var speed: Double = 1
    /// When a podcast should stop by itself: after so many minutes (it pauses), or at the end of the episode.
    let sleep = SleepTimer()
    /// When false, sessions are still started and stopped but progress is never reported.
    @ObservationIgnored var recordsHistory = true
    /// Where downloaded tracks come from (set by the iOS app; the Apple TV has none).
    @ObservationIgnored weak var offlineLibrary: OfflineMusicLibrary?
    /// The listener chose a podcast speed in the player (it's saved to their preferences).
    @ObservationIgnored var onSpeedChange: ((Double) -> Void)?
    /// A podcast episode has started on the server (which takes it off Up Next).
    @ObservationIgnored var onPodcastStarted: (() -> Void)?
    /// What's playing, or whether it is, changed (for the iPhone's widgets).
    @ObservationIgnored var onChange: (() -> Void)?

    @ObservationIgnored private var client: FlexarrClient?
    /// The queue as it was given, for turning shuffle back off.
    @ObservationIgnored private var original: [ItemSummary] = []
    /// Album names by album id: a track summary carries its artist but not its album, so the screen that queued the
    /// tracks passes the albums it knows.
    @ObservationIgnored private var albumTitles: [String: String] = [:]
    @ObservationIgnored private let player = AVPlayer()
    @ObservationIgnored private var sessionId: String?
    /// The session's id for logs (never the session id itself, the stream's credential).
    @ObservationIgnored private var playbackId: String?
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
    /// Which remote commands the lock screen has: music's previous and next, or a podcast's skips and speed.
    @ObservationIgnored private var nowPlayingIsPodcast: Bool?
    @ObservationIgnored private var lastOfflineRecord = Date.distantPast

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
        sleep.onExpire = { [weak self] in self?.pause() }
        nowPlaying.onPlay = { [weak self] in self?.resume() }
        nowPlaying.onPause = { [weak self] in self?.pause() }
        nowPlaying.onNext = { [weak self] in self?.next() }
        nowPlaying.onPrevious = { [weak self] in self?.previous() }
        nowPlaying.onSeek = { [weak self] seconds in self?.seek(to: seconds) }
        nowPlaying.onSkip = { [weak self] seconds in self?.skip(by: seconds) }
        nowPlaying.onRate = { [weak self] rate in self?.setSpeed(rate) }
    }

    var current: ItemSummary? { queue.indices.contains(index) ? queue[index] : nil }
    /// What plays after the current track, in order.
    var upNext: [ItemSummary] { Array(queue.dropFirst(index + 1)) }
    var hasNext: Bool { index + 1 < queue.count || (repeatMode == .all && !queue.isEmpty) }
    var hasPrevious: Bool { index > 0 || repeatMode == .all }
    /// A podcast episode is what's playing (or paused).
    var isPodcast: Bool { current?.kind == .podcastEpisode }
    /// The rate the player runs at while playing.
    private var playRate: Float { isPodcast ? Float(speed) : 1 }

    /// The speed podcasts play at when the profile's preferences aren't known: the last one chosen here.
    static var podcastSpeed: Double {
        get { UserDefaults.standard.object(forKey: "podcasts.speed") as? Double ?? 1 }
        set { UserDefaults.standard.set(newValue, forKey: "podcasts.speed") }
    }

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

    /// Plays a podcast episode from where it was left, at the profile's speed (or the last one chosen here).
    func playPodcast(_ episode: ItemSummary, speed preferred: Double?, client: FlexarrClient) {
        self.client = client
        original = [episode]
        queue = [episode]
        albumTitles = [:]
        index = 0
        isShuffled = false
        repeatMode = .off
        speed = preferred ?? Self.podcastSpeed
        startCurrent()
    }

    /// A podcast's speed, remembered as the one to use from now on.
    func setSpeed(_ speed: Double) {
        guard isPodcast else { return }
        self.speed = speed
        Self.podcastSpeed = speed
        onSpeedChange?(speed)
        player.defaultRate = playRate
        if isPlaying { player.rate = playRate }
        pushNowPlaying()
    }

    /// Back or forward a few seconds (a podcast's 15 and 30).
    func skip(by seconds: Double) {
        seek(to: position + seconds)
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
        nowPlaying.setTime(position: target, duration: duration, rate: isPlaying ? playRate : 0, defaultRate: playRate)
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
        // A downloaded episode or track playing with no session keeps its place on the phone, as one streamed does on
        // the server (endSession).
        if isPlayingOffline, recordsHistory, let track = current, position > 0 {
            offlineLibrary?.record(itemId: track.id, position: position, finished: false)
        }
        endSession()
        clearItem()
        queue = []
        original = []
        albumTitles = [:]
        index = 0
        position = 0
        duration = 0
        artwork = nil
        isPlayingOffline = false
        isLoading = false
        sleep.set(nil)
        nowPlaying.deactivate()
        nowPlayingIsPodcast = nil
    }

    // MARK: Giving way to video

    /// A video is starting: music pauses and lets go of the remote's commands, which the video player takes.
    func yieldToVideo() {
        guard current != nil else { return }
        pause()
        yieldedToVideo = true
        nowPlaying.deactivate()
        nowPlayingIsPodcast = nil
    }

    /// The video has finished: the lock screen shows the paused music again, and play resumes it.
    func videoEnded() {
        guard yieldedToVideo, current != nil else { return }
        yieldedToVideo = false
        pushNowPlaying()
    }

    // MARK: Sessions

    private func startCurrent() {
        guard let track = current else { return }
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
        player.defaultRate = playRate
        activateAudioSession()
        pushNowPlaying()

        // Downloaded: its file, its saved cover, no session (and no connection needed).
        if let file = offlineLibrary?.localFile(for: track.id) {
            isPlayingOffline = true
            artwork = offlineLibrary?.localArtwork(for: track.id)
            attach(item(url: file))
            if isPodcast, let resume = offlineLibrary?.resumePosition(for: track.id), resume > 0 { seek(to: resume) }
            // Played from the phone, so the server isn't asked for it: tell it the episode has left Up Next (when it can).
            if isPodcast, let client { Task { try? await client.removeFromUpNext(track.id) } }
            player.play()
            isLoading = false
            pushNowPlaying()
            if artwork == nil { loadArtwork(for: track, generation: mine) }
            return
        }

        isPlayingOffline = false
        loadArtwork(for: track, generation: mine)
        guard let client else {
            isLoading = false
            return
        }
        Task {
            do {
                // Tracks always start over: a half-heard song isn't resumed the way a film is. Podcasts are.
                let podcast = track.kind == .podcastEpisode
                let request = PlaybackRequest(itemId: track.id, profile: Self.profile, startPositionSeconds: podcast ? nil : 0)
                let info = try await client.startPlayback(request)
                guard mine == generation else {
                    try? await client.stopPlayback(sessionId: info.sessionId) // Skipped past before it arrived.
                    return
                }
                guard let url = client.url(for: info.url) else { throw FlexarrClientError.invalidResponse }
                sessionId = info.sessionId
                playbackId = info.playbackId
                if info.durationSeconds > 0 { duration = info.durationSeconds }
                lastReport = Date()
                attach(item(url: url))
                if podcast, info.startPositionSeconds > 0 { seek(to: info.startPositionSeconds) }
                player.play()
                if podcast { onPodcastStarted?() }
                isLoading = false
                pushNowPlaying()
            } catch {
                guard mine == generation else { return }
                isLoading = false
                log.error("Couldn't start \(track.title)", error: error)
                errorMessage = ConnectionError(error).message
            }
        }
    }

    /// An item for a track's file or stream; speech keeps its pitch at other speeds.
    private func item(url: URL) -> AVPlayerItem {
        let item = AVPlayerItem(url: url)
        if isPodcast { item.audioTimePitchAlgorithm = .timeDomain }
        return item
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
        if isPlayingOffline, recordsHistory, let track = current {
            // Heard through with no session: the play goes to the server when there's a connection.
            offlineLibrary?.record(itemId: track.id, position: 0, finished: true)
        }
        report(paused: true, ended: true)
        if sleep.setting == .endOfEpisode {
            sleep.set(nil)
            endSession()
            clearItem()
            position = 0
            isPlaying = false
            pushNowPlaying()
            return
        }
        if isPodcast, index + 1 >= queue.count, let episode = current {
            playNextEpisode(after: episode)
            return
        }
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

    /// The end of a podcast episode with nothing queued after it: on to the episode the server suggests (the next of
    /// the show, else the newest of the shows followed), or with no connection the show's next downloaded one.
    private func playNextEpisode(after episode: ItemSummary) {
        endSession()
        clearItem()
        position = 0
        isPlaying = false
        generation += 1
        let mine = generation
        isLoading = true
        Task {
            var next: ItemSummary?
            do {
                next = try await client?.nextEpisode(after: episode.id)?.summary
            } catch {
                next = offlineLibrary?.nextDownloadedEpisode(after: episode)
            }
            guard mine == generation else { return }
            isLoading = false
            guard let next else {
                pushNowPlaying() // nothing left: stay on this one, ready to play again
                return
            }
            queue = [next]
            original = [next]
            index = 0
            startCurrent()
        }
    }

    private func trackFailed(_ reason: String?) {
        log.error("Track failed: \(reason ?? "no reason")", ["playbackId": playbackId])
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
        if !playing, isPlayingOffline, isPodcast, player.currentItem != nil { recordOffline() }
        onChange?()
        nowPlaying.setTime(position: position, duration: duration, rate: playing ? playRate : 0, defaultRate: playRate)
    }

    private func tick(_ seconds: Double) {
        guard seconds.isFinite, player.currentItem != nil else { return }
        position = seconds
        if let length = player.currentItem?.duration.seconds, length.isFinite, length > 0, abs(length - duration) > 1 {
            duration = length
        }
        if Date().timeIntervalSince(lastNowPlayingTime) > 1 {
            lastNowPlayingTime = Date()
            nowPlaying.setTime(position: seconds, duration: duration, rate: isPlaying ? playRate : 0, defaultRate: playRate)
        }
        if isPlaying, Date().timeIntervalSince(lastReport) >= Self.reportInterval { report(paused: false) }
        if isPlaying, isPlayingOffline, isPodcast, Date().timeIntervalSince(lastOfflineRecord) >= 30 { recordOffline() }
    }

    /// Where a downloaded podcast episode got to, kept on the device and told to the server once there's a connection.
    private func recordOffline() {
        guard recordsHistory, let track = current, position > 0 else { return }
        lastOfflineRecord = Date()
        offlineLibrary?.record(itemId: track.id, position: position, finished: false)
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
        let podcast = isPodcast
        if nowPlayingIsIdle || nowPlayingIsPodcast != podcast {
            if podcast { nowPlaying.activateForPodcast() } else { nowPlaying.activateForMusic() }
            nowPlayingIsPodcast = podcast
        }
        nowPlaying.setTrack(title: track.title, artist: track.artistName, album: podcast ? nil : albumTitle(of: track), artwork: artwork)
        if !podcast { nowPlaying.setEpisodeNavigation(hasPrevious: hasPrevious || position > 3, hasNext: hasNext) }
        nowPlaying.setTime(position: position, duration: duration, rate: isPlaying ? playRate : 0, defaultRate: playRate)
        onChange?()
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
