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
    /// A downloaded book: its manifest and its files on this device, in order.
    func localBook(for bookId: String) -> (manifest: BookManifest, files: [URL])?
    /// Where listening to a downloaded book got to (on the whole book's timeline), sent to the server with its time.
    func recordBook(itemId: String, position: Double, ended: Bool)
    /// A downloaded book was heard to the end on this device (and not started again since).
    func isBookFinished(_ bookId: String) -> Bool
    /// The server's manifest for a downloaded book, fetched as it starts: its speed, bookmarks and listening settings
    /// are kept with the download (a speed changed offline and not yet sent stays). The download's manifest after.
    func refreshBook(_ fresh: BookManifest) -> BookManifest?
    /// A downloaded book's speed changed: kept with it, and sent to the server once there's a connection.
    func recordBookSpeed(itemId: String, speed: Double)
}

extension OfflineMusicLibrary {
    func localBook(for bookId: String) -> (manifest: BookManifest, files: [URL])? { nil }
    func recordBook(itemId: String, position: Double, ended: Bool) {}
    func isBookFinished(_ bookId: String) -> Bool { false }
    func refreshBook(_ fresh: BookManifest) -> BookManifest? { nil }
    func recordBookSpeed(itemId: String, speed: Double) {}
}

/// Where this device last was in each book, and when: the server's place is offered as "Jump to …" only when it's
/// newer than this and from another device.
enum BookPlaces {
    private static let key = "books.places"

    static func place(of bookId: String) -> (position: Double, at: Date)? {
        guard let entry = (UserDefaults.standard.dictionary(forKey: key) ?? [:])[bookId] as? [Double], entry.count == 2 else { return nil }
        return (entry[0], Date(timeIntervalSince1970: entry[1]))
    }

    static func save(_ bookId: String, position: Double) {
        var all = UserDefaults.standard.dictionary(forKey: key) ?? [:]
        all[bookId] = [position, Date().timeIntervalSince1970]
        // A few hundred books at most: the oldest go.
        if all.count > 300, let oldest = all.min(by: { (($0.value as? [Double])?.last ?? 0) < (($1.value as? [Double])?.last ?? 0) })?.key {
            all.removeValue(forKey: oldest)
        }
        UserDefaults.standard.set(all, forKey: key)
    }

    static func forget(_ bookId: String) {
        var all = UserDefaults.standard.dictionary(forKey: key) ?? [:]
        all.removeValue(forKey: bookId)
        UserDefaults.standard.set(all, forKey: key)
    }
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
    /// A podcast episode was heard to the end (its id): its download goes, and screens listing it look again.
    @ObservationIgnored var onPodcastEnded: ((String) -> Void)?
    /// What's playing, or whether it is, changed (for the iPhone's widgets).
    @ObservationIgnored var onChange: (() -> Void)?
    /// A book was heard to the end (its id): a download set to go once finished goes, and screens look again.
    @ObservationIgnored var onBookFinished: ((String) -> Void)?

    // MARK: Book mode

    /// The book playing: its files on one timeline, chapters, and how this profile listens. `position` and `duration`
    /// are the whole book's while one plays.
    private(set) var book: BookManifest?
    private(set) var bookmarks: [Bookmark] = []
    /// The server has a newer place in the book from another device: offered as "Jump to …", never jumped to alone.
    private(set) var jumpOffer: BookProgress?
    /// The book has ended and its series has another: offered with a Play button, never started alone.
    private(set) var nextBookOffer: ItemSummary?
    /// What adding a bookmark came to, shown for a moment: "Bookmarked 1:23:04", or that it couldn't be.
    private(set) var bookmarkNotice: BookmarkNotice?
    @ObservationIgnored private var noticeClear: Task<Void, Never>?
    @ObservationIgnored private var bookTimeline: BookTimeline?
    /// Which of the book's files is in the player.
    @ObservationIgnored private var bookFile = 0
    @ObservationIgnored private var bookURLs: [URL] = []
    /// Where `playBook` was asked to start (a chapter, a bookmark); nil: where this profile is.
    @ObservationIgnored private var bookStart: Double?
    /// When the book was paused, for smart rewind (forgotten when the listener moves it while paused).
    @ObservationIgnored private var bookPause = BookPause()
    @ObservationIgnored private var speedSave: Task<Void, Never>?
    /// Where an End of chapter timer stops: the end of the chapter playing (worked out again after every seek).
    @ObservationIgnored private var sleepChapterEnd: Double?
    @ObservationIgnored private var fade: Task<Void, Never>?
    /// The chapter the lock screen names.
    @ObservationIgnored private var nowPlayingChapter: Int?
    /// The book commands the lock screen was given (its skip steps), so new steps take effect.
    @ObservationIgnored private var nowPlayingCommands: String?
    /// The speeds the lock screen and a car offer for a book.
    static let bookLockScreenSpeeds: [Double] = [0.75, 1, 1.25, 1.5, 1.75, 2, 2.5, 3]
    #if os(iOS)
    @ObservationIgnored private let shake = ShakeDetector()
    #endif

    @ObservationIgnored private var client: FlexarrClient?
    /// The queue as it was given, for turning shuffle back off.
    @ObservationIgnored private var original: [ItemSummary] = []
    /// Album names by album id: a track summary carries its artist but not its album, so the screen that queued the
    /// tracks passes the albums it knows.
    @ObservationIgnored private var albumTitles: [String: String] = [:]
    @ObservationIgnored private let player = AVPlayer()
    /// The music's bass, middle and treble as it plays, for the visualiser behind Now Playing.
    @ObservationIgnored let spectrum = AudioSpectrum()
    @ObservationIgnored private var sessionId: String?
    /// The book the session is for (nil for a track or episode): a report the server no longer has a session for goes
    /// to the book's own progress instead.
    @ObservationIgnored private var sessionBookId: String?
    /// The book whose session the server forgot (12 hours without a report): its place goes to the book's progress.
    @ObservationIgnored private var expiredBookId: String?
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
    /// The server forgets a session after 12 hours without a report; a book paused this long starts a new one.
    private static let sessionIdleLimit: TimeInterval = 6 * 60 * 60

    init() {
        #if DEBUG
        // `-FlexarrMuted YES`: silent, for trying the player on a Mac someone is working at.
        player.isMuted = UserDefaults.standard.bool(forKey: "FlexarrMuted")
        #endif
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
        sleep.onExpire = { [weak self] in self?.fallAsleep() }
        #if os(iOS)
        // A shake while a sleep timer runs (or fades the sound out) gives more time.
        shake.onShake = { [weak self] in self?.addSleepTime() }
        #endif
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
    /// A book is what's playing (or paused).
    var isBook: Bool { current?.kind == .audiobook }
    /// The rate the player runs at while playing.
    private var playRate: Float { isPodcast || isBook ? Float(speed) : 1 }

    /// The book's skip steps (the profile's choice), or a podcast's 15 back and 30 forward.
    var skipBack: Double { isBook ? Double(book?.listening.skipBackSeconds ?? 30) : 15 }
    var skipForward: Double { isBook ? Double(book?.listening.skipForwardSeconds ?? 30) : 30 }

    /// The chapter playing in the book.
    var chapter: BookChapter? { bookTimeline?.chapter(at: position) }
    var chapterIndex: Int? { bookTimeline?.chapterIndex(at: position) }
    var chapters: [BookChapter] { bookTimeline?.chapters ?? [] }

    /// What this device is called on the server, for "Jump to where you were on …".
    static var deviceName: String {
        #if os(tvOS)
        "Apple TV"
        #else
        UIDevice.current.name
        #endif
    }

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

    /// Plays a book from where this profile is in it, or from `position` (a chapter, a bookmark, 0 to listen again).
    /// Already playing it: it goes to `position`, or carries on.
    func playBook(_ item: ItemSummary, at position: Double? = nil, client: FlexarrClient) {
        self.client = client
        if current?.id == item.id, book != nil, player.currentItem != nil {
            if let position { seek(to: position) }
            if !isPlaying { resume() }
            return
        }
        original = [item]
        queue = [item]
        albumTitles = [:]
        index = 0
        isShuffled = false
        repeatMode = .off
        bookStart = position
        startCurrent()
    }

    /// A podcast's speed, remembered as the one to use from now on; a book's, remembered for that book.
    func setSpeed(_ speed: Double) {
        if isBook {
            self.speed = BookSpeed.clamped(speed)
            saveBookSpeed()
        } else {
            guard isPodcast else { return }
            self.speed = speed
            Self.podcastSpeed = speed
            onSpeedChange?(speed)
        }
        player.defaultRate = playRate
        if isPlaying { player.rate = playRate }
        pushNowPlaying()
    }

    /// The book's own speed, kept on the server once the listener stops changing it. A downloaded book keeps it
    /// with its download too, and the server hears of it once there's a connection.
    private func saveBookSpeed() {
        guard let id = current?.id else { return }
        let speed = speed
        let offline = isPlayingOffline
        let client = client
        if let manifest = book { book = manifest.withSpeed(speed) }
        speedSave?.cancel()
        speedSave = Task {
            try? await Task.sleep(for: .seconds(1))
            guard !Task.isCancelled else { return }
            if offline {
                offlineLibrary?.recordBookSpeed(itemId: id, speed: speed)
            } else {
                try? await client?.setPlaybackSpeed(itemId: id, speed: speed)
            }
        }
    }

    /// Back or forward a few seconds (a podcast's 15 and 30, a book's chosen steps).
    func skip(by seconds: Double) {
        seek(to: position + seconds)
    }

    /// The start of the next chapter.
    func nextChapter() {
        guard let start = bookTimeline?.nextChapterStart(from: position) else { return }
        seek(to: start)
    }

    /// The start of this chapter, or of the one before when it's only just begun.
    func previousChapter() {
        guard let timeline = bookTimeline else { return }
        seek(to: timeline.previousChapterStart(from: position))
    }

    /// Goes to the place the server had from another device.
    func acceptJump() {
        guard let offer = jumpOffer else { return }
        jumpOffer = nil
        seek(to: offer.positionSeconds)
        if !isPlaying { resume() }
    }

    func dismissJump() {
        jumpOffer = nil
    }

    /// Not now: the next book's offer goes, and the book that ended stays, ready to play again.
    func dismissNextBook() {
        nextBookOffer = nil
    }

    /// Plays the next book in the series, offered at the end of this one.
    func playNextBook() {
        guard let next = nextBookOffer, let client else { return }
        nextBookOffer = nil
        playBook(next, client: client)
    }

    /// A bookmark where the book is now, with an optional note; a tap and a moment's notice say it's done (or not).
    @discardableResult
    func addBookmark(note: String? = nil) async -> Bookmark? {
        guard isBook, let id = current?.id, let client else { return nil }
        let at = position
        do {
            let mark = try await client.addBookmark(id, positionSeconds: at, note: note)
            bookmarks.append(mark)
            show(.added(at))
            return mark
        } catch {
            log.warning("Couldn't add a bookmark", error: error)
            show(.failed(error))
            return nil
        }
    }

    private func show(_ notice: BookmarkNotice) {
        bookmarkNotice = notice
        notice.tap()
        noticeClear?.cancel()
        noticeClear = Task { [weak self] in
            try? await Task.sleep(for: .seconds(notice.isError ? 4 : 2))
            guard !Task.isCancelled else { return }
            self?.bookmarkNotice = nil
        }
    }

    func deleteBookmark(_ mark: Bookmark) async {
        guard let client else { return }
        bookmarks.removeAll { $0.id == mark.id }
        try? await client.deleteBookmark(mark.id)
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
        cancelFade()
        if player.currentItem == nil {
            startCurrent() // Stopped for a failure (or by a video): ask for the track again.
        } else if isBook, !isPlayingOffline, let track = current,
                  expiredBookId == track.id || (sessionId != nil && Date().timeIntervalSince(lastReport) > Self.sessionIdleLimit) {
            // Paused so long the server has forgotten the session (and its file addresses): a new one, from here.
            let place = smartRewindPlace()
            if position > 0 { BookPlaces.save(track.id, position: place) }
            bookStart = place
            startCurrent()
        } else {
            // A book starts a little earlier after a pause, more the longer it was. Not a seek: the End of chapter
            // timer keeps the chapter it was set for.
            if isBook {
                let place = smartRewindPlace()
                if place < position { moveBook(to: place) }
            }
            player.play()
            if nowPlayingIsIdle { pushNowPlaying() }
        }
    }

    /// Where a book plays again from after a pause (forgetting the pause): a little earlier, more the longer it was,
    /// not crossing back into the chapter before unless the pause was right at its start.
    private func smartRewindPlace() -> Double {
        guard let pause = bookPause.resumed(at: Date()), book?.listening.smartRewind ?? true else { return position }
        return SmartRewind.place(from: position, back: SmartRewind.seconds(afterPause: pause), chapterStart: chapter?.startSeconds)
    }

    func next() {
        if isBook { return nextChapter() }
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
        if isBook { return previousChapter() }
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

    /// Moves the book to `place` without it counting as the listener's choice (smart rewind's step back): the pause
    /// and the End of chapter stop stay as they are.
    private func moveBook(to place: Double) {
        guard bookTimeline != nil, player.currentItem != nil else { return }
        loadBookFile(at: max(0, min(place, max(0, duration - 0.5))), play: isPlaying)
        nowPlaying.setTime(position: position, duration: duration, rate: isPlaying ? playRate : 0, defaultRate: playRate)
    }

    func seek(to seconds: Double) {
        guard current != nil else { return }
        if isBook {
            guard let timeline = bookTimeline, player.currentItem != nil else { return }
            let target = max(0, min(seconds, max(0, duration - 0.5)))
            moveBook(to: target)
            // A chapter or bookmark picked while paused plays from there, not a little before it.
            bookPause.seeked()
            // An End of chapter timer stops at the end of the chapter now playing (Next chapter lands on the old end).
            if sleep.setting == .endOfChapter, fade == nil { sleepChapterEnd = timeline.chapterEnd(at: target) }
            return
        }
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
            if isBook {
                offlineLibrary?.recordBook(itemId: track.id, position: position, ended: false)
            } else {
                offlineLibrary?.record(itemId: track.id, position: position, finished: false)
            }
        }
        if isBook, let track = current, position > 0 { BookPlaces.save(track.id, position: position) }
        endSession()
        clearItem()
        clearBook()
        cancelFade()
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
        clearBook()
        position = 0
        duration = track.durationSeconds ?? 0
        artwork = nil
        isLoading = true
        errorMessage = nil
        yieldedToVideo = false
        player.defaultRate = playRate
        activateAudioSession()
        pushNowPlaying()

        if track.kind == .audiobook {
            startBook(track, generation: mine)
            return
        }

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
                sessionBookId = nil
                playbackId = info.playbackId
                if info.durationSeconds > 0 { duration = info.durationSeconds }
                lastReport = Date()
                attach(item(url: url, type: info.method == .directPlay ? Self.audioType(of: info.audioTracks.first?.codec) : nil))
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

    // MARK: Books

    /// A book: from its downloaded files with no session, or through a session whose files the server sends as they
    /// are (or converted, for a sound this device can't decode), all on one timeline.
    private func startBook(_ track: ItemSummary, generation mine: Int) {
        let requested = bookStart
        bookStart = nil
        let local = BookPlaces.place(of: track.id)

        if let downloaded = offlineLibrary?.localBook(for: track.id) {
            isPlayingOffline = true
            artwork = offlineLibrary?.localArtwork(for: track.id)
            let saved = offlineLibrary?.resumePosition(for: track.id) ?? 0
            // Finished (here, or before it was downloaded): it starts again from the beginning, as it does streamed.
            let finished = offlineLibrary?.isBookFinished(track.id) == true || downloaded.manifest.progress.finished
            let place = requested ?? (saved > 0 ? saved : finished ? 0 : local?.position ?? downloaded.manifest.progress.positionSeconds)
            beginBook(downloaded.manifest, urls: downloaded.files, at: place)
            if artwork == nil { loadArtwork(for: track, generation: mine) }
            // With a connection, the server knows the speed, bookmarks and listening settings chosen since the download,
            // and maybe a newer place from another device: offered, not jumped to.
            if let client {
                let at = local?.at
                Task {
                    guard let fresh = try? await client.bookManifest(track.id, codecs: Self.bookCodecs), mine == generation else { return }
                    applyFresh(offlineLibrary?.refreshBook(fresh) ?? downloaded.manifest.refreshed(from: fresh, speed: speed))
                    if requested == nil, BookResume.offersJump(server: fresh.progress, local: position, localAt: at, device: Self.deviceName) {
                        jumpOffer = fresh.progress
                    }
                }
            }
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
                var request = PlaybackRequest(itemId: track.id, profile: Self.bookProfile, startPositionSeconds: requested)
                request.deviceName = Self.deviceName
                let info = try await client.startPlayback(request)
                guard mine == generation else {
                    try? await client.stopPlayback(sessionId: info.sessionId)
                    return
                }
                guard let manifest = info.book else { throw FlexarrClientError.unexpectedResponse }
                let urls = manifest.files.compactMap { client.url(for: $0.url) }
                guard !urls.isEmpty, urls.count == manifest.files.count else { throw FlexarrClientError.invalidResponse }
                sessionId = info.sessionId
                sessionBookId = track.id
                expiredBookId = nil
                playbackId = info.playbackId
                lastReport = Date()
                var start = info.startPositionSeconds
                // Another device got further since this one listened: stay here, and offer to jump there.
                if requested == nil, let local, BookResume.offersJump(server: manifest.progress, local: local.position, localAt: local.at, device: Self.deviceName) {
                    start = local.position
                    jumpOffer = manifest.progress
                }
                beginBook(manifest, urls: urls, at: start)
            } catch {
                guard mine == generation else { return }
                isLoading = false
                log.error("Couldn't start \(track.title)", error: error)
                errorMessage = ConnectionError(error).message
            }
        }
    }

    private func beginBook(_ manifest: BookManifest, urls: [URL], at place: Double) {
        book = manifest
        bookmarks = manifest.bookmarks
        bookTimeline = manifest.timeline
        bookURLs = urls
        speed = BookSpeed.clamped(manifest.speed)
        player.defaultRate = playRate
        duration = manifest.durationSeconds
        nextBookOffer = nil
        bookPause = BookPause()
        bookFile = -1
        loadBookFile(at: min(max(0, place), max(0, duration - 1)), play: true)
        isLoading = false
        pushNowPlaying()
    }

    /// The profile's listening settings changed (in Settings): the book playing takes its new skip steps and smart rewind.
    func listeningChanged(_ listening: BookListening) {
        guard let manifest = book else { return }
        book = BookManifest(itemId: manifest.itemId, durationSeconds: manifest.durationSeconds, files: manifest.files, chapters: manifest.chapters,
                            bookmarks: manifest.bookmarks, progress: manifest.progress, speed: manifest.speed, listening: listening,
                            nextInSeries: manifest.nextInSeries)
        pushNowPlaying()
    }

    /// A downloaded book's manifest brought up to date while it plays: the speed, bookmarks and skip steps change.
    private func applyFresh(_ manifest: BookManifest) {
        guard isBook, book?.itemId == manifest.itemId else { return }
        book = manifest
        bookmarks = manifest.bookmarks
        let fresh = BookSpeed.clamped(manifest.speed)
        if fresh != speed {
            speed = fresh
            player.defaultRate = playRate
            if isPlaying { player.rate = playRate }
        }
        pushNowPlaying()
    }

    /// Puts the file holding `place` in the player (when it isn't already) and goes there in it.
    private func loadBookFile(at place: Double, play: Bool) {
        guard let timeline = bookTimeline, let manifest = book, !bookURLs.isEmpty else { return }
        let (file, offset) = timeline.locate(place)
        position = place
        if file != bookFile || player.currentItem == nil {
            bookFile = file
            attach(item(url: bookURLs[file], type: Self.bookType(manifest.files[file])))
        }
        player.seek(to: CMTime(seconds: offset, preferredTimescale: 600), toleranceBefore: .zero, toleranceAfter: .zero)
        if play { player.play() }
    }

    private func clearBook() {
        book = nil
        bookmarks = []
        bookTimeline = nil
        bookURLs = []
        bookFile = 0
        jumpOffer = nil
        nextBookOffer = nil
        bookPause = BookPause()
        sleepChapterEnd = nil
        speedSave = nil
    }

    /// A file of the book ended: on to the next, or the book has ended.
    private func bookFileEnded() {
        guard let timeline = bookTimeline else { return }
        if bookFile + 1 < timeline.files.count {
            bookFile += 1
            position = timeline.files[bookFile].offsetSeconds
            attach(item(url: bookURLs[bookFile], type: book.map { Self.bookType($0.files[bookFile]) } ?? nil))
            player.play()
            return
        }
        bookEnded()
    }

    /// The end of the book: it's finished (it leaves Continue listening), and the series' next book is offered.
    private func bookEnded() {
        guard let track = current else { return }
        if recordsHistory {
            if isPlayingOffline {
                offlineLibrary?.recordBook(itemId: track.id, position: duration, ended: true)
            } else if let client, let sessionId {
                // Ended, then closed, in that order: a session closed first would forget the end.
                self.sessionId = nil
                let length = duration
                let bookId = track.id
                Task {
                    await Self.send(client, session: sessionId, book: bookId, position: length, paused: true, ended: true)
                    try? await client.stopPlayback(sessionId: sessionId)
                }
            } else if let client, expiredBookId == track.id {
                expiredBookId = nil // reported as ended: nothing more to send for it
                let bookId = track.id, length = duration
                Task { try? await client.reportBookProgress(itemId: bookId, BookProgressUpdate(positionSeconds: length, device: Self.deviceName, recordedAt: Date(), ended: true)) }
            }
        }
        BookPlaces.forget(track.id)
        if sleep.setting != nil { sleep.set(nil) }
        let next = book?.nextInSeries
        clearItem()
        position = 0
        isPlaying = false
        nextBookOffer = next
        onBookFinished?(track.id)
        pushNowPlaying()
    }

    /// The sounds a book's files can come in as they are (AVPlayer plays these in their usual files); the rest come
    /// converted to AAC.
    static let bookCodecs = ["aac", "mp3", "flac", "alac"]

    private static var bookProfile: DeviceProfile {
        var profile = Self.profile
        profile.audioCodecs = bookCodecs
        return profile
    }

    private static func bookType(_ file: BookFile) -> String? {
        if file.converted { return "audio/mp4" }
        switch file.container?.lowercased() {
        case "mp3": return "audio/mpeg"
        case "flac": return "audio/flac"
        case "mp4", "m4a", "m4b", "mov,mp4,m4a,3gp,3g2,mj2": return "audio/mp4"
        default: return audioType(of: file.audioCodec)
        }
    }

    // MARK: Sleep

    /// The sleep timer ran out: a book or episode fades out over ten seconds (a shake brings it back), music stops.
    private func fallAsleep() {
        guard isPlaying else { return }
        guard isBook || isPodcast else { return pause() }
        cancelFade()
        fade = Task { [weak self] in
            for step in 1...20 {
                try? await Task.sleep(for: .milliseconds(500))
                guard let self, !Task.isCancelled else { return }
                self.player.volume = Float(20 - step) / 20
            }
            guard let self, !Task.isCancelled else { return }
            self.pause()
            self.player.volume = 1
            self.fade = nil
            if self.sleep.setting == .endOfChapter { self.sleep.set(nil) }
            self.watchShakes()
        }
        watchShakes()
    }

    private func cancelFade() {
        guard let fade else { return }
        fade.cancel()
        self.fade = nil
        player.volume = 1
    }

    /// A shake: the timer starts again (minutes from now, or to the end of the next chapter), and a fade stops.
    private func addSleepTime() {
        let fading = fade != nil
        cancelFade()
        switch sleep.setting {
        case .minutes(let minutes):
            sleep.set(.minutes(minutes))
        case .endOfChapter:
            sleepChapterEnd = bookTimeline?.nextChapterStart(from: position).flatMap { bookTimeline?.chapterEnd(at: $0) } ?? duration
        case .endOfEpisode:
            break
        case nil:
            // Faded out by a timer of minutes, which has cleared itself: another ten.
            if fading { sleep.set(.minutes(10)) }
        }
        #if os(iOS)
        UINotificationFeedbackGenerator().notificationOccurred(.success)
        #endif
        watchShakes()
    }

    /// Shakes count while a timer runs and something plays (or fades).
    private func watchShakes() {
        #if os(iOS)
        shake.isActive = (sleep.setting != nil && isPlaying) || fade != nil
        #endif
    }

    /// An End of chapter timer stops at the end of the chapter playing: the one playing when it was set, or the one a
    /// seek went to since.
    private func checkChapterSleep() {
        guard isBook, sleep.setting == .endOfChapter else {
            sleepChapterEnd = nil
            return
        }
        let end = sleepChapterEnd ?? bookTimeline?.chapterEnd(at: position) ?? duration
        sleepChapterEnd = end
        // The fade (ten seconds) ends as the chapter does.
        if fade == nil, position >= end - 10 * speed {
            fallAsleep()
        }
    }

    /// An item for a track's file or stream; speech keeps its pitch at other speeds. `type` names what a file sent as
    /// it is holds, for servers that send FLAC and MP3 without saying so (before 0.53).
    private func item(url: URL, type: String? = nil) -> AVPlayerItem {
        let asset = AVURLAsset(url: url, options: type.map { [AVURLAssetOverrideMIMETypeKey: $0] })
        let item = AVPlayerItem(asset: asset)
        if isPodcast || isBook { item.audioTimePitchAlgorithm = .timeDomain }
        return item
    }

    private func attach(_ item: AVPlayerItem) {
        clearItem()
        #if os(iOS)
        // Not for a family guest (who may have signed in since launch): an AirPlay receiver can't reach the tunnel.
        player.allowsExternalPlayback = !GuestMode.isActive
        #endif
        player.replaceCurrentItem(with: item)
        #if os(tvOS)
        // Only the Apple TV shows the visualiser, and with it off nothing listens.
        if AudioSpectrum.isOn { Task { await spectrum.tap(item) } }
        #endif
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

    /// Playing this downloaded track or episode from its file now.
    func isPlayingFile(of itemId: String) -> Bool {
        isPlayingOffline && player.currentItem != nil && current?.id == itemId
    }

    private func clearItem() {
        for observer in itemObservers { NotificationCenter.default.removeObserver(observer) }
        itemObservers = []
        player.replaceCurrentItem(with: nil)
    }

    /// Reports where the track got to and ends its session (best effort; the server times abandoned ones out).
    private func endSession() {
        // A book whose session the server forgot: its last place goes to the book's own progress.
        if sessionId == nil, let bookId = expiredBookId, bookId == current?.id, position > 0, !isPlayingOffline, recordsHistory, let client {
            let update = BookProgressUpdate(positionSeconds: position, device: Self.deviceName, recordedAt: Date(), ended: false)
            Task { try? await client.reportBookProgress(itemId: bookId, update) }
        }
        expiredBookId = nil
        guard let client, let sessionId else { return }
        self.sessionId = nil
        let position = self.position
        let record = recordsHistory
        let bookId = sessionBookId
        Task {
            if record { await Self.send(client, session: sessionId, book: bookId, position: position, paused: true) }
            try? await client.stopPlayback(sessionId: sessionId)
        }
    }

    private func report(paused: Bool, ended: Bool = false) {
        // A book's place is kept on the device too, for "Jump to …" between devices.
        if isBook, let track = current, position > 0 { BookPlaces.save(track.id, position: position) }
        guard recordsHistory, let client else { return }
        let position = ended ? duration : self.position
        if sessionId == nil, isBook, !isPlayingOffline, let track = current, expiredBookId == track.id {
            // The session's gone: the place goes to the book's own progress, as a download's does.
            lastReport = Date()
            Task { try? await client.reportBookProgress(itemId: track.id, BookProgressUpdate(positionSeconds: position, device: Self.deviceName, recordedAt: Date(), ended: ended)) }
            return
        }
        guard let sessionId else { return }
        lastReport = Date()
        let bookId = sessionBookId
        Task {
            if await Self.send(client, session: sessionId, book: bookId, position: position, paused: paused, ended: ended), self.sessionId == sessionId {
                // Forgotten by the server (12 hours without a report): later reports go to the book's progress.
                self.sessionId = nil
                expiredBookId = bookId
            }
        }
    }

    /// Reports on a session; a book's session the server no longer has (404: idle for 12 hours) is reported to the
    /// book's own progress instead. True when the session was gone.
    @discardableResult
    private static func send(_ client: FlexarrClient, session: String, book: String?, position: Double, paused: Bool, ended: Bool = false) async -> Bool {
        do {
            try await client.reportProgress(sessionId: session, positionSeconds: position, paused: paused, ended: ended)
            return false
        } catch FlexarrClientError.http(status: 404, _) {
            guard let book else { return true }
            try? await client.reportBookProgress(itemId: book, BookProgressUpdate(positionSeconds: position, device: deviceName, recordedAt: Date(), ended: ended))
            return true
        } catch {
            return false
        }
    }

    private func trackEnded() {
        if isBook { return bookFileEnded() }
        if isPlayingOffline, recordsHistory, let track = current {
            // Heard through with no session: the play goes to the server when there's a connection.
            offlineLibrary?.record(itemId: track.id, position: 0, finished: true)
        }
        report(paused: true, ended: true)
        if isPodcast, let track = current { onPodcastEnded?(track.id) }
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
        if isBook {
            if playing { bookPause = BookPause() } else { bookPause.paused(at: Date()) }
        }
        if !playing, sessionId != nil || (expiredBookId != nil && expiredBookId == current?.id), player.currentItem != nil { report(paused: true) }
        if !playing, isPlayingOffline, isPodcast || isBook, player.currentItem != nil { recordOffline() }
        watchShakes()
        onChange?()
        nowPlaying.setTime(position: position, duration: duration, rate: playing ? playRate : 0, defaultRate: playRate)
    }

    private func tick(_ seconds: Double) {
        guard seconds.isFinite, player.currentItem != nil else { return }
        if isBook, let timeline = bookTimeline {
            // The file's time on the whole book's timeline.
            position = timeline.position(file: bookFile, offset: seconds)
            checkChapterSleep()
        } else {
            position = seconds
            if let length = player.currentItem?.duration.seconds, length.isFinite, length > 0, abs(length - duration) > 1 {
                duration = length
            }
        }
        if Date().timeIntervalSince(lastNowPlayingTime) > 1 {
            lastNowPlayingTime = Date()
            let chapterChanged = isBook && nowPlayingChapter != chapterIndex
            if chapterChanged { pushNowPlaying() }
            nowPlaying.setTime(position: position, duration: duration, rate: isPlaying ? playRate : 0, defaultRate: playRate)
        }
        watchShakes()
        if isPlaying, Date().timeIntervalSince(lastReport) >= Self.reportInterval { report(paused: false) }
        if isPlaying, isPlayingOffline, isPodcast || isBook, Date().timeIntervalSince(lastOfflineRecord) >= (isBook ? 15 : 30) { recordOffline() }
    }

    /// Where a downloaded podcast episode or book got to, kept on the device and told to the server once there's a
    /// connection.
    private func recordOffline() {
        guard recordsHistory, let track = current, position > 0 else { return }
        lastOfflineRecord = Date()
        if isBook {
            BookPlaces.save(track.id, position: position)
            offlineLibrary?.recordBook(itemId: track.id, position: position, ended: false)
        } else {
            offlineLibrary?.record(itemId: track.id, position: position, finished: false)
        }
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
        nowPlayingChapter = chapterIndex
        if isBook {
            // A book: the listener's skips, chapters on the track buttons, the speed; the chapter named as the title.
            let commands = "book#\(skipBack)#\(skipForward)"
            if nowPlayingIsIdle || nowPlayingCommands != commands {
                nowPlaying.activateForBook(back: skipBack, forward: skipForward, speeds: Self.bookLockScreenSpeeds)
                nowPlayingCommands = commands
                nowPlayingIsPodcast = nil
            }
            nowPlaying.setBook(title: track.title, chapter: chapter?.title, author: track.bookAuthors, artwork: artwork,
                               chapterIndex: chapterIndex, chapterCount: chapters.count)
            let index = chapterIndex ?? 0
            nowPlaying.setEpisodeNavigation(hasPrevious: chapters.count > 1, hasNext: index + 1 < chapters.count)
            nowPlaying.setTime(position: position, duration: duration, rate: isPlaying ? playRate : 0, defaultRate: playRate)
            onChange?()
            return
        }
        nowPlayingCommands = nil
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

    /// The type of a file sent as it is, from its sound's codec: the two the Apple TV asks for beyond MP4.
    private static func audioType(of codec: String?) -> String? {
        switch codec {
        case "flac": "audio/flac"
        case "mp3": "audio/mpeg"
        default: nil
        }
    }

    /// Music asks nothing of the picture: the usual profile with the display's HDR left out, so the server copies the
    /// sound as it is when the player can take it and converts it otherwise. The Apple TV also takes FLAC and MP3 files
    /// as they are (AVPlayer plays both), so the visualiser can hear them: a converted stream can't be tapped.
    private static var profile: DeviceProfile {
        #if os(tvOS)
        var profile = DeviceProfile.tvOS(hdr: false)
        profile.containers += ["flac", "mp3"]
        return profile
        #else
        .iOS(hdr: false)
        #endif
    }
}

/// What adding a bookmark came to: where it was put, or why it wasn't (bookmarks need the server).
struct BookmarkNotice: Equatable {
    let text: String
    let isError: Bool

    static func added(_ position: Double) -> BookmarkNotice {
        BookmarkNotice(text: "Bookmarked \(Format.timestamp(position))", isError: false)
    }

    static func failed(_ error: Error) -> BookmarkNotice {
        let offline = (error as? URLError).map { [.notConnectedToInternet, .networkConnectionLost, .cannotConnectToHost, .timedOut, .cannotFindHost].contains($0.code) } ?? false
        return BookmarkNotice(text: offline ? "No connection: bookmark not added" : "Bookmark not added", isError: true)
    }

    /// A light tap for one added, the warning buzz for one that wasn't.
    func tap() {
        #if os(iOS)
        UINotificationFeedbackGenerator().notificationOccurred(isError ? .error : .success)
        #endif
    }
}
