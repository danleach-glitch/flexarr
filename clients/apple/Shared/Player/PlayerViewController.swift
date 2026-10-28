import AVFoundation
import AVKit
import FlexarrKit
import UIKit

private let log = FlexarrLog("player")

@MainActor
protocol PlayerScreenDelegate: AnyObject {
    func playerScreen(_ screen: PlayerViewController, phaseChanged phase: TransportPhase)
    func playerScreen(_ screen: PlayerViewController, tick seconds: Double)
    func playerScreenTracksChanged(_ screen: PlayerViewController)
    /// The viewer moved: the timeline, a skip, the lock screen.
    func playerScreen(_ screen: PlayerViewController, didSeekTo seconds: Double)
    /// The viewer closed the player (or Picture in Picture ended without coming back).
    func playerScreenDidClose(_ screen: PlayerViewController)
    func playerScreenPrevious(_ screen: PlayerViewController)
    func playerScreenNext(_ screen: PlayerViewController)
    func playerScreenPromptTapped(_ screen: PlayerViewController)
    func playerScreenCancelNext(_ screen: PlayerViewController)
    /// An audio track the transport can't switch to itself: the session is started again with it.
    func playerScreen(_ screen: PlayerViewController, selectAudio id: String)
    /// The viewer chose subtitles (`.some(nil)`: off), to remember for this title and the next.
    func playerScreen(_ screen: PlayerViewController, choseSubtitle key: String??)
    func playerScreen(_ screen: PlayerViewController, subtitleDelay seconds: Double)
    func playerScreen(_ screen: PlayerViewController, subtitleSize size: SubtitleSize)
    /// The viewer nudged the file's sound (seconds later, earlier when negative); `restOfSeason`: for the episodes
    /// after this one in its season too.
    func playerScreen(_ screen: PlayerViewController, audioDelay seconds: Double, restOfSeason: Bool)
    /// The viewer chose a speed in the menu.
    func playerScreen(_ screen: PlayerViewController, choseRate rate: Float)
    /// The sleep timer was set, changed, asked, ran out or was woken from (`sleepHoldsEpisode` may have changed).
    func playerScreenSleepChanged(_ screen: PlayerViewController)
}

extension PlayerScreenDelegate {
    func playerScreen(_ screen: PlayerViewController, audioDelay seconds: Double, restOfSeason: Bool) {}
    func playerScreen(_ screen: PlayerViewController, choseRate rate: Float) {}
    func playerScreenSleepChanged(_ screen: PlayerViewController) {}
}

/// The player screen: the transport's picture full-bleed, our controls over it, and the remote (Apple TV) or touch
/// (iPhone and iPad) driving them. The controller owns the session and tells this screen what to show.
final class PlayerViewController: UIViewController {
    weak var delegate: PlayerScreenDelegate?
    let controls = PlayerControlsView()
    private let rootView = PlayerRootView()
    private let spinner = UIActivityIndicatorView(style: .large)
    private let nowPlaying = NowPlaying()
    private var controlsVisible = true
    private var hideTask: Task<Void, Never>?
    private var lastNowPlayingUpdate = Date.distantPast
    private var subtitleDelay: Double?
    private var menuOpen = false
    private var finished = false
    /// Where the viewer last asked to go, until the picture gets there: skips add up from here, and the timeline shows
    /// it rather than the clock (which only moves once the seek has landed).
    private var seekTarget: (seconds: Double, at: Date)?
    #if os(tvOS)
    /// Scanning with the remote's edges held: the direction, the speed (2, 4, 8, 16×), where the head is, and the
    /// press that may turn into a hold.
    private var scan: (direction: Double, rate: Double, position: Double, link: CADisplayLink)?
    private var lastScanFrame: CFTimeInterval = 0
    private var holdTask: Task<Void, Never>?
    #endif
    private var subtitleSize: SubtitleSize = .standard
    /// The file's audio offset and whether it can go to the rest of the season (nil: not adjustable here).
    private var audioSync: (seconds: Double, offersRestOfSeason: Bool)?
    private let audioSyncPanel = AudioSyncPanel()
    private var audioSyncOpen: Bool { !audioSyncPanel.isHidden }
    /// The sleep timer: it lasts as long as the screen, so a timer of minutes carries on into the next episode.
    let sleep = SleepTimer()
    private let sleepPrompt = SleepPromptView()
    private var sleepPhase = SleepPhase.off
    private var sleepWatch: Task<Void, Never>?
    /// The minutes left last shown in the sleep menu, so it's rebuilt only when that changes.
    private var sleepMinutesShown: Int?
    private var title_: (String, String?) = ("", nil)
    private var artwork: UIImage?
    #if os(iOS)
    private var pictureInPicture: AVPictureInPictureController?
    private(set) var isInPictureInPicture = false
    private var isRestoringFromPictureInPicture = false
    #endif

    /// How far the skip buttons (and the remote's edges on Apple TV) jump: 30 seconds unless Settings says otherwise.
    static var skipSeconds: Double {
        let saved = UserDefaults.standard.double(forKey: "FlexarrSkipSeconds")
        return saved > 0 ? saved : 30
    }

    private enum SleepPhase {
        case off
        /// The last minute: "Still watching?" over the dimmed picture.
        case asking
        /// Ran out: paused, dimmed until someone touches something.
        case asleep
    }

    /// Where an episode ends for the sleep timer's End of episode: at its credits when the profile skips them (and
    /// they're marked), else at its very end; the question comes as the credits start, or a minute before the end.
    struct EpisodeEnd {
        var credits: Double?
        var stopsAtCredits: Bool
    }

    /// The timings offered for subtitles: a pick restarts the stream (about two seconds) with the subtitles moved.
    static let delayChoices: [Double] = [-2, -1, -0.5, 0, 0.5, 1, 2]
    static let rateChoices: [Float] = [0.5, 0.75, 1, 1.25, 1.5, 2]

    /// Swapped when a session is replaced (the next episode, a recovered stream, the engine's fallback).
    var transport: PlayerTransport? {
        didSet {
            oldValue?.delegate = nil
            oldValue?.view.removeFromSuperview()
            guard let transport else { return }
            transport.delegate = self
            transport.subtitleSize = subtitleSize
            if isViewLoaded { install(transport) }
            phaseChanged(transport.phase)
        }
    }

    init() {
        super.init(nibName: nil, bundle: nil)
        sleep.onExpire = { [weak self] in self?.fallAsleep() }
        modalPresentationStyle = .fullScreen
        #if os(iOS)
        modalTransitionStyle = .crossDissolve
        #endif
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    // MARK: Lifecycle

    override func loadView() {
        view = rootView
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        view.backgroundColor = .black
        if let transport { install(transport) }
        controls.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(controls)
        for panel in [sleepPrompt, audioSyncPanel] as [UIView] {
            panel.translatesAutoresizingMaskIntoConstraints = false
            view.addSubview(panel)
        }
        audioSyncPanel.isHidden = true
        spinner.color = .white
        spinner.hidesWhenStopped = true
        spinner.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(spinner)
        NSLayoutConstraint.activate([
            controls.leadingAnchor.constraint(equalTo: view.leadingAnchor),
            controls.trailingAnchor.constraint(equalTo: view.trailingAnchor),
            controls.topAnchor.constraint(equalTo: view.topAnchor),
            controls.bottomAnchor.constraint(equalTo: view.bottomAnchor),
            spinner.centerXAnchor.constraint(equalTo: view.centerXAnchor),
            spinner.centerYAnchor.constraint(equalTo: view.centerYAnchor),
            sleepPrompt.leadingAnchor.constraint(equalTo: view.leadingAnchor),
            sleepPrompt.trailingAnchor.constraint(equalTo: view.trailingAnchor),
            sleepPrompt.topAnchor.constraint(equalTo: view.topAnchor),
            sleepPrompt.bottomAnchor.constraint(equalTo: view.bottomAnchor),
            audioSyncPanel.centerXAnchor.constraint(equalTo: view.centerXAnchor),
        ])
        #if os(tvOS)
        // Above the timeline and the menus, below the transport's circles.
        audioSyncPanel.bottomAnchor.constraint(equalTo: view.safeAreaLayoutGuide.bottomAnchor, constant: -190).isActive = true
        #else
        audioSyncPanel.bottomAnchor.constraint(equalTo: view.safeAreaLayoutGuide.bottomAnchor, constant: -100).isActive = true
        #endif
        controls.skipSeconds = Self.skipSeconds
        controls.setTitle(title_.0, subtitle: title_.1)
        wireControls()
        wireNowPlaying()
        wirePanels()
        // Sound on with the ring switch off; playing on in Picture in Picture or with the screen locked. Apple's player
        // did this itself; on Apple TV it's also what lets tvOS negotiate the HDMI route for multichannel sound.
        try? AVAudioSession.sharedInstance().setCategory(.playback, mode: .moviePlayback)
        try? AVAudioSession.sharedInstance().setActive(true)
        #if os(iOS)
        let tap = UITapGestureRecognizer(target: self, action: #selector(tapped))
        tap.delegate = self
        let doubleTap = UITapGestureRecognizer(target: self, action: #selector(doubleTapped))
        doubleTap.numberOfTapsRequired = 2
        doubleTap.delegate = self
        tap.require(toFail: doubleTap)
        view.addGestureRecognizer(tap)
        view.addGestureRecognizer(doubleTap)
        controls.setPictureInPictureAvailable(AVPictureInPictureController.isPictureInPictureSupported())
        #endif
        scheduleHide()
    }

    override func viewDidAppear(_ animated: Bool) {
        super.viewDidAppear(animated)
        #if os(iOS)
        isRestoringFromPictureInPicture = false
        // First responder, for shakes (more time on the sleep timer).
        becomeFirstResponder()
        #else
        // Focus must be in here, or the remote's Menu goes to the screen underneath and takes this one down with it.
        setNeedsFocusUpdate()
        updateFocusIfNeeded()
        #endif
    }

    /// Gone from the screen other than by our own close (the system or the app underneath dismissed it): that's the
    /// end of watching too, or the session would play on unseen. Apple's player told us this through its delegate.
    override func viewDidDisappear(_ animated: Bool) {
        super.viewDidDisappear(animated)
        guard !finished, isBeingDismissed || presentingViewController == nil else { return }
        #if os(iOS)
        if isInPictureInPicture { return }
        #endif
        log.info("Player dismissed from outside; stopping")
        close()
    }

    #if os(iOS)
    override var canBecomeFirstResponder: Bool { true }

    /// A shake while the sleep timer asks "Still watching?" is a yes; after it ran out, it wakes the screen.
    override func motionEnded(_ motion: UIEvent.EventSubtype, with event: UIEvent?) {
        guard motion == .motionShake else { return super.motionEnded(motion, with: event) }
        switch sleepPhase {
        case .asking: extendSleep()
        case .asleep: wake()
        case .off: super.motionEnded(motion, with: event)
        }
    }

    override var prefersStatusBarHidden: Bool { true }
    override var prefersHomeIndicatorAutoHidden: Bool { true }
    override var supportedInterfaceOrientations: UIInterfaceOrientationMask { .allButUpsideDown }
    #endif

    private func install(_ transport: PlayerTransport) {
        let video = transport.view
        video.translatesAutoresizingMaskIntoConstraints = false
        view.insertSubview(video, at: 0)
        NSLayoutConstraint.activate([
            video.leadingAnchor.constraint(equalTo: view.leadingAnchor),
            video.trailingAnchor.constraint(equalTo: view.trailingAnchor),
            video.topAnchor.constraint(equalTo: view.topAnchor),
            video.bottomAnchor.constraint(equalTo: view.bottomAnchor),
        ])
        controls.setTime(position: transport.currentTime, duration: transport.duration, buffered: transport.bufferedTime)
        rebuildMenus()
        #if os(iOS)
        pictureInPicture = nil
        preparePictureInPicture(transport)
        #endif
    }

    #if os(iOS)
    /// The engine has no picture to hand Picture in Picture until it has opened the file, so this is tried again as
    /// the transport reports in, until there is one.
    private func preparePictureInPicture(_ transport: PlayerTransport) {
        guard pictureInPicture == nil, AVPictureInPictureController.isPictureInPictureSupported() else { return }
        if let source = transport.pictureInPictureContentSource() {
            let controller = AVPictureInPictureController(contentSource: source)
            controller.delegate = self
            controller.canStartPictureInPictureAutomaticallyFromInline = true
            pictureInPicture = controller
        }
        controls.pictureInPictureButton.isEnabled = pictureInPicture != nil
    }
    #endif

    // MARK: What the controller tells the screen

    func setTitle(_ title: String, subtitle: String?) {
        title_ = (title, subtitle)
        if isViewLoaded { controls.setTitle(title, subtitle: subtitle) }
        nowPlaying.setTitle(title, subtitle: subtitle, artwork: artwork)
    }

    /// "1080p · E-AC-3 5.1", or what the server is doing to the stream.
    func setDetail(_ detail: String?) {
        controls.setDetail(detail)
    }

    func setArtwork(_ image: UIImage?) {
        artwork = image
        nowPlaying.setTitle(title_.0, subtitle: title_.1, artwork: image)
    }

    var markers: [Marker] = [] {
        didSet { controls.scrubber.markers = markers.filter { $0.type == .intro || $0.type == .credits || $0.type == .recap } }
    }

    var previews: TrickplayPreviews? {
        didSet { controls.scrubber.previews = previews }
    }

    /// Live TV: with the controls hidden, clicking the top or bottom of the remote's touch surface changes channel
    /// (Previous and Next).
    var zapsChannels = false

    /// The spinner while there's nothing to show yet (a channel being changed, before its picture).
    func showLoading() {
        phaseChanged(.loading)
    }

    func setEpisodeNavigation(hasPrevious: Bool, hasNext: Bool) {
        controls.setEpisodeNavigation(hasPrevious: hasPrevious, hasNext: hasNext)
        nowPlaying.setEpisodeNavigation(hasPrevious: hasPrevious, hasNext: hasNext)
    }

    func setPrompt(_ title: String?, countingDown: Bool) {
        controls.setPrompt(title, countingDown: countingDown)
        if title != nil, !controlsVisible {
            // The pill shows on its own, without the rest of the controls.
            controls.promptButton.superview?.alpha = 1
        }
    }

    /// The file's audio offset for the audio menu's Audio sync (nil: not adjustable here, as for a download or an older
    /// server). Left alone while the panel is open: the viewer's nudges there are newer.
    func setAudioSync(seconds: Double?, offersRestOfSeason: Bool) {
        audioSync = seconds.map { (audioSyncOpen ? audioSyncPanel.seconds : $0, offersRestOfSeason) }
        if seconds == nil { closeAudioSync() }
        if !audioSyncOpen, let seconds { audioSyncPanel.configure(seconds: seconds, offersRestOfSeason: offersRestOfSeason) }
        rebuildMenus()
    }

    /// Plays at `rate` (a show's remembered speed), as if chosen in the menu but without remembering it again.
    func applyRate(_ rate: Float) {
        guard let transport, rate <= transport.maxRate else { return }
        transport.setRate(rate)
        rebuildMenus()
    }

    /// What End of episode means for this title (nil: not an episode, so it isn't offered).
    var episodeEnd: EpisodeEnd? {
        didSet {
            // Moved on to something that isn't an episode: there's no end of episode to stop at.
            if episodeEnd == nil, sleep.setting == .endOfEpisode { sleep.set(nil) }
            rebuildMenus()
        }
    }

    /// The sleep timer has this episode stopping at its end, or has run out: the episode doesn't go on to the next
    /// by itself, and Next Episode isn't offered over the question.
    var sleepHoldsEpisode: Bool {
        sleep.setting == .endOfEpisode || sleepPhase != .off
    }

    /// Subtitle timing (nil: not adjustable on this stream) and size, for the subtitles menu.
    func setSubtitleOptions(delay: Double?, size: SubtitleSize) {
        subtitleDelay = delay
        subtitleSize = size
        transport?.subtitleSize = size
        rebuildMenus()
    }

    // MARK: Controls

    private func wireControls() {
        controls.onPlayPause = { [weak self] in self?.togglePlayPause() }
        controls.onSkip = { [weak self] seconds in self?.skip(seconds) }
        controls.onPrevious = { [weak self] in
            guard let self else { return }
            delegate?.playerScreenPrevious(self)
        }
        controls.onNext = { [weak self] in
            guard let self else { return }
            delegate?.playerScreenNext(self)
        }
        controls.onPrompt = { [weak self] in
            guard let self else { return }
            delegate?.playerScreenPromptTapped(self)
        }
        controls.onCancel = { [weak self] in
            guard let self else { return }
            delegate?.playerScreenCancelNext(self)
        }
        controls.onClose = { [weak self] in self?.close() }
        controls.onPictureInPicture = { [weak self] in
            #if os(iOS)
            self?.pictureInPicture?.startPictureInPicture()
            #endif
        }
        controls.scrubber.onScrubBegan = { [weak self] in self?.hideTask?.cancel() }
        controls.scrubber.onScrubMoved = { [weak self] seconds in
            guard let self, let transport else { return }
            controls.setTime(position: seconds, duration: transport.duration, buffered: transport.bufferedTime)
        }
        controls.scrubber.onScrubEnded = { [weak self] seconds in
            guard let self else { return }
            if let seconds { seek(to: seconds) }
            scheduleHide()
        }
        controls.onMenuVisibility = { [weak self] button, visible in
            guard let self else { return }
            menuOpen = visible
            if visible {
                hideTask?.cancel()
            } else {
                #if os(tvOS)
                controls.focusNext = button
                setNeedsFocusUpdate()
                updateFocusIfNeeded()
                #endif
                scheduleHide()
            }
        }
    }

    private func wireNowPlaying() {
        nowPlaying.onPlay = { [weak self] in self?.transport?.play() }
        nowPlaying.onPause = { [weak self] in self?.transport?.pause() }
        nowPlaying.onSkip = { [weak self] seconds in self?.skip(seconds) }
        nowPlaying.onSeek = { [weak self] seconds in self?.seek(to: seconds) }
        nowPlaying.onPrevious = { [weak self] in
            guard let self else { return }
            delegate?.playerScreenPrevious(self)
        }
        nowPlaying.onNext = { [weak self] in
            guard let self else { return }
            delegate?.playerScreenNext(self)
        }
        nowPlaying.activate(skipSeconds: Self.skipSeconds)
    }

    func togglePlayPause() {
        guard let transport else { return }
        if transport.phase == .ended { return }
        transport.isPlaying ? transport.pause() : transport.play()
        showControls()
    }

    /// `flash`: say "+30 s" on the picture, where nothing else shows the jump (a remote click, a double tap).
    func skip(_ seconds: Double, flash: Bool = false) {
        guard let transport else { return }
        // A head parked after a swipe (not yet committed) is let go: the skip is the viewer's new position.
        _ = controls.scrubber.cancelScrubIfNeeded()
        seek(to: (seekTarget?.seconds ?? transport.currentTime) + seconds)
        if flash { controls.flashSkip(seconds) }
    }

    func seek(to seconds: Double) {
        guard let transport else { return }
        let target = max(0, min(seconds, transport.duration > 0 ? transport.duration : seconds))
        seekTarget = (target, Date())
        transport.seek(to: target)
        controls.setTime(position: target, duration: transport.duration, buffered: transport.bufferedTime)
        delegate?.playerScreen(self, didSeekTo: target)
        showControls()
    }

    private func close() {
        delegate?.playerScreenDidClose(self)
    }

    /// Closes the screen when it's done: the controller asks, having stopped the session.
    func finish(completion: (() -> Void)? = nil) {
        finished = true
        sleepWatch?.cancel()
        sleepWatch = nil
        sleep.set(nil)
        UIApplication.shared.isIdleTimerDisabled = false
        nowPlaying.deactivate()
        hideTask?.cancel()
        #if os(iOS)
        if isInPictureInPicture { pictureInPicture?.stopPictureInPicture() }
        #endif
        guard presentingViewController != nil else { completion?(); return }
        dismiss(animated: true) { completion?() }
    }

    // MARK: Showing and hiding

    private func showControls() {
        hideTask?.cancel()
        if !controlsVisible {
            controlsVisible = true
            UIView.animate(withDuration: 0.2) { self.controls.alpha = 1 }
            #if os(tvOS)
            rootView.takesFocus = false
            setNeedsFocusUpdate()
            updateFocusIfNeeded()
            #endif
        }
        scheduleHide()
    }

    private func hideControls() {
        guard controlsVisible else { return }
        controlsVisible = false
        hideTask?.cancel()
        UIView.animate(withDuration: 0.25) { self.controls.alpha = 0 }
        #if os(tvOS)
        rootView.takesFocus = true
        setNeedsFocusUpdate()
        updateFocusIfNeeded()
        #endif
    }

    /// The controls go after a few seconds of playing untouched (never while paused, loading or scrubbing).
    private func scheduleHide() {
        hideTask?.cancel()
        #if DEBUG
        // `-FlexarrKeepControls YES` keeps the controls up, for screenshots.
        if UserDefaults.standard.bool(forKey: "FlexarrKeepControls") { return }
        #endif
        hideTask = Task { [weak self] in
            #if os(tvOS)
            try? await Task.sleep(for: .seconds(4))
            #else
            try? await Task.sleep(for: .seconds(3))
            #endif
            guard !Task.isCancelled, let self, let transport, transport.isPlaying, !controls.scrubber.isScrubbing, !menuOpen, !audioSyncOpen else { return }
            hideControls()
        }
    }

    #if os(iOS)
    @objc private func tapped() {
        if handleSleepTouch() { return }
        if audioSyncOpen { return closeAudioSync() }
        controlsVisible ? hideControls() : showControls()
    }

    /// Two taps on the left or right third of the picture: skip, like Apple's player.
    @objc private func doubleTapped(_ gesture: UITapGestureRecognizer) {
        if handleSleepTouch() { return }
        let x = gesture.location(in: view).x / max(1, view.bounds.width)
        // Two taps at the edges skip 10 seconds (fine positioning); the buttons do the longer jump.
        if x < 0.35 { skip(-10, flash: true) } else if x > 0.65 { skip(10, flash: true) } else { togglePlayPause() }
    }
    #endif

    // MARK: Apple TV remote

    #if os(tvOS)
    override var preferredFocusEnvironments: [UIFocusEnvironment] {
        if sleepPrompt.isAsking { return [sleepPrompt] }
        if audioSyncOpen { return [audioSyncPanel] }
        return controlsVisible || controls.promptShown ? [controls] : [rootView]
    }

    override func touchesBegan(_ touches: Set<UITouch>, with event: UIEvent?) {
        super.touchesBegan(touches, with: event)
        // Asked "Still watching?", a touch alone (moving between its buttons) isn't an answer.
        if sleepPhase == .asking { return }
        if sleepPhase == .asleep { return wake() }
        showControls()
    }

    override func pressesBegan(_ presses: Set<UIPress>, with event: UIPressesEvent?) {
        // The guide or the channel list is open over the picture: the remote is theirs, not the player's (up and
        // down there would otherwise change channel behind them).
        if presentedViewController != nil {
            super.pressesBegan(presses, with: event)
            return
        }
        // Ran out: any button wakes the screen, and only that. Asked "Still watching?": any button but the edges
        // (which move between its two) is a yes; Select on a button is that button's.
        switch sleepPhase {
        case .asleep:
            return wake()
        case .asking:
            if presses.contains(where: { $0.type != .leftArrow && $0.type != .rightArrow }) { return extendSleep() }
            return super.pressesBegan(presses, with: event)
        case .off:
            break
        }
        for press in presses {
            switch press.type {
            case .playPause:
                if scan != nil { endScan(commit: true); return }
                togglePlayPause()
                return
            case .select:
                if scan != nil { endScan(commit: true); return }
                if controls.scrubber.isFocused, controls.scrubber.commitScrubIfNeeded() { return }
                if !controlsVisible && !controls.promptShown {
                    togglePlayPause()
                    return
                }
                if controls.scrubber.isFocused {
                    togglePlayPause()
                    return
                }
            case .menu:
                if scan != nil { endScan(commit: false); return }
                if controls.scrubber.cancelScrubIfNeeded() { return }
                if audioSyncOpen { return closeAudioSync() }
                if controlsVisible {
                    hideControls()
                    return
                }
                close()
                return
            case .upArrow, .downArrow:
                if zapsChannels, !controlsVisible, scan == nil {
                    if press.type == .upArrow {
                        delegate?.playerScreenPrevious(self)
                    } else {
                        delegate?.playerScreenNext(self)
                    }
                    return
                }
            case .leftArrow, .rightArrow:
                let direction: Double = press.type == .leftArrow ? -1 : 1
                // Scanning already: the same edge doubles the speed, the other turns it round.
                if let current = scan {
                    if current.direction == direction {
                        scan?.rate = min(16, current.rate * 2)
                    } else {
                        scan?.direction = direction
                        scan?.rate = 2
                    }
                    showScanRate()
                    return
                }
                // The remote's edges skip when clicked and scan when held, unless focus is on a button row.
                if !controlsVisible || controls.scrubber.isFocused {
                    holdTask?.cancel()
                    holdTask = Task { [weak self] in
                        try? await Task.sleep(for: .milliseconds(450))
                        guard !Task.isCancelled, let self else { return }
                        self.holdTask = nil
                        self.beginScan(direction: direction)
                    }
                    return
                }
            default:
                break
            }
            showControls()
        }
        super.pressesBegan(presses, with: event)
    }

    override func pressesEnded(_ presses: Set<UIPress>, with event: UIPressesEvent?) {
        // The guide or the channel list is open over the picture: the remote is theirs, not the player's (up and
        // down there would otherwise change channel behind them).
        if presentedViewController != nil {
            super.pressesEnded(presses, with: event)
            return
        }
        for press in presses where press.type == .leftArrow || press.type == .rightArrow {
            // Let go before it became a hold: a click, which skips.
            if let holdTask {
                holdTask.cancel()
                self.holdTask = nil
                skip(press.type == .leftArrow ? -Self.skipSeconds : Self.skipSeconds, flash: !controlsVisible)
                return
            }
            if scan != nil { return }
        }
        super.pressesEnded(presses, with: event)
    }

    override func pressesCancelled(_ presses: Set<UIPress>, with event: UIPressesEvent?) {
        // The guide or the channel list is open over the picture: the remote is theirs, not the player's (up and
        // down there would otherwise change channel behind them).
        if presentedViewController != nil {
            super.pressesCancelled(presses, with: event)
            return
        }
        holdTask?.cancel()
        holdTask = nil
        super.pressesCancelled(presses, with: event)
    }

    // MARK: Scanning (Apple TV)

    /// Fast forward or rewind with preview thumbnails: the picture pauses and the timeline's head runs at 2× (then 4,
    /// 8, 16× with each click); Select or play/pause lands there.
    private func beginScan(direction: Double) {
        guard let transport, transport.duration > 0 else { return }
        transport.pause()
        showControls()
        controls.scrubber.beginScan()
        // Every frame, so the head glides even at 16×.
        let link = CADisplayLink(target: self, selector: #selector(scanFrame))
        lastScanFrame = 0
        scan = (direction, 2, transport.currentTime, link)
        link.add(to: .main, forMode: .common)
        showScanRate()
    }

    @objc private func scanFrame(_ link: CADisplayLink) {
        guard let current = scan, let transport else { return }
        let elapsed = lastScanFrame == 0 ? 0 : link.timestamp - lastScanFrame
        lastScanFrame = link.timestamp
        // Plex's scale: 2× runs a minute of video a second, 4× two minutes, and so on.
        let next = max(0, min(transport.duration, current.position + current.direction * current.rate * 30 * elapsed))
        scan?.position = next
        controls.scrubber.moveScan(to: next)
        controls.setTime(position: next, duration: transport.duration, buffered: transport.bufferedTime)
        if next <= 0 || next >= transport.duration { endScan(commit: true) }
    }

    private func endScan(commit: Bool) {
        guard let current = scan else { return }
        current.link.invalidate()
        scan = nil
        _ = commit ? controls.scrubber.commitScrubIfNeeded() : controls.scrubber.cancelScrubIfNeeded()
        if commit { seek(to: current.position) }
        transport?.play()
        controls.scrubber.scanLabel = nil
    }

    private func showScanRate() {
        guard let current = scan else { return }
        controls.scrubber.scanLabel = current.direction < 0 ? "◀◀ \(Int(current.rate))×" : "▶▶ \(Int(current.rate))×"
    }
    #endif

    // MARK: Menus

    private func rebuildMenus() {
        guard let transport else { return }
        let audio = transport.audioTracks
        var audioItems: [UIMenuElement] = []
        if audio.count > 1 {
            audioItems.append(UIMenu(options: .displayInline, children: audio.map { track in
                UIAction(title: track.label, state: track.id == transport.activeAudioTrack ? .on : .off) { [weak self] _ in
                    MainActor.assumeIsolated {
                        guard let self, let transport = self.transport else { return }
                        if !transport.selectAudioTrack(track.id) { self.delegate?.playerScreen(self, selectAudio: track.id) }
                        self.rebuildMenus()
                    }
                }
            }))
        }
        if let audioSync {
            let sync = UIAction(title: "Audio sync", image: UIImage(systemName: "waveform.badge.magnifyingglass")) { [weak self] _ in
                MainActor.assumeIsolated { self?.openAudioSync() }
            }
            sync.subtitle = audioSync.seconds == 0 ? "As the file is" : AudioSyncPanel.label(audioSync.seconds)
            audioItems.append(UIMenu(options: .displayInline, children: [sync]))
        }
        controls.audioButton.isHidden = audioItems.isEmpty
        controls.audioButton.menu = UIMenu(title: "Audio", children: audioItems)

        let subtitles = transport.subtitleTracks
        var items: [UIMenuElement] = []
        if !subtitles.isEmpty {
            let off = UIAction(title: "Off", state: transport.activeSubtitleTrack == nil ? .on : .off) { [weak self] _ in
                MainActor.assumeIsolated { self?.chooseSubtitle(nil) }
            }
            items.append(UIMenu(options: .displayInline, children: [off] + subtitles.map { track in
                UIAction(title: track.label, state: track.id == transport.activeSubtitleTrack ? .on : .off) { [weak self] _ in
                    MainActor.assumeIsolated { self?.chooseSubtitle(track) }
                }
            }))
        }
        if let current = subtitleDelay, !subtitles.isEmpty {
            let choices = Set(Self.delayChoices + [current]).sorted()
            items.append(UIMenu(title: "Timing", subtitle: Self.delayLabel(current), children: choices.map { seconds in
                UIAction(title: Self.delayLabel(seconds), state: seconds == current ? .on : .off) { [weak self] _ in
                    MainActor.assumeIsolated {
                        guard let self, seconds != current else { return }
                        self.delegate?.playerScreen(self, subtitleDelay: seconds)
                    }
                }
            }))
        }
        if !subtitles.isEmpty {
            items.append(UIMenu(title: "Size", subtitle: subtitleSize.label, children: SubtitleSize.allCases.map { size in
                UIAction(title: size.label, state: size == subtitleSize ? .on : .off) { [weak self] _ in
                    MainActor.assumeIsolated {
                        guard let self else { return }
                        self.subtitleSize = size
                        transport.subtitleSize = size
                        self.delegate?.playerScreen(self, subtitleSize: size)
                        self.rebuildMenus()
                    }
                }
            }))
        }
        controls.subtitlesButton.isHidden = items.isEmpty
        controls.subtitlesButton.menu = UIMenu(title: "Subtitles", children: items)

        // A show's remembered speed may be one the menu doesn't list (chosen in the browser, say).
        let rates = Set(Self.rateChoices + [transport.rate]).sorted().filter { $0 <= transport.maxRate }
        // Live TV has no speeds (it can't run ahead of the channel).
        controls.speedButton.isHidden = rates.count < 2 || transport.maxRate <= 1
        controls.speedButton.menu = UIMenu(title: "Speed", children: rates.map { rate in
            UIAction(title: Self.rateLabel(rate), state: rate == transport.rate ? .on : .off) { [weak self] _ in
                MainActor.assumeIsolated {
                    guard let self, let transport = self.transport, rate != transport.rate else { return }
                    transport.setRate(rate)
                    self.rebuildMenus()
                    self.delegate?.playerScreen(self, choseRate: transport.rate)
                }
            }
        })
        controls.setSpeedBadge(controls.speedButton.isHidden || transport.rate == 1 ? nil : Self.rateLabel(transport.rate))
        rebuildSleepMenu()
    }

    /// Off, 15 to 60 minutes, End of episode (episodes only); with the minutes left while one runs.
    private func rebuildSleepMenu() {
        let current = sleep.setting
        var choices: [SleepTimer.Setting] = SleepTimer.minuteChoices.map { .minutes($0) }
        if episodeEnd != nil { choices.append(.endOfEpisode) }
        let off = UIAction(title: "Off", state: current == nil ? .on : .off) { [weak self] _ in
            MainActor.assumeIsolated { self?.setSleep(nil) }
        }
        let items = choices.map { setting in
            UIAction(title: setting.label, state: setting == current ? .on : .off) { [weak self] _ in
                MainActor.assumeIsolated { self?.setSleep(setting) }
            }
        }
        sleepMinutesShown = sleep.remaining.map { Int(($0 / 60).rounded(.up)) }
        let title = sleepMinutesShown.map { "Sleep timer · \($0) min left" } ?? "Sleep timer"
        controls.sleepButton.isHidden = false
        controls.sleepButton.menu = UIMenu(title: title, children: [UIMenu(options: .displayInline, children: [off])] + [UIMenu(options: .displayInline, children: items)])
        controls.setSleepActive(current != nil)
    }

    private func chooseSubtitle(_ track: TransportTrack?) {
        transport?.selectSubtitleTrack(track?.id)
        delegate?.playerScreen(self, choseSubtitle: .some(track?.key))
        rebuildMenus()
    }

    /// "In time", "0.5s later", "1s earlier".
    static func delayLabel(_ seconds: Double) -> String {
        guard seconds != 0 else { return "In time" }
        let amount = abs(seconds).formatted(.number.precision(.fractionLength(0...2)))
        return "\(amount)s \(seconds > 0 ? "later" : "earlier")"
    }

    static func rateLabel(_ rate: Float) -> String {
        rate == 1 ? "Normal" : "\(rate.formatted(.number.precision(.fractionLength(0...2))))×"
    }

    // MARK: Phase

    private func phaseChanged(_ phase: TransportPhase) {
        guard isViewLoaded else { return }
        switch phase {
        case .loading, .buffering: spinner.startAnimating()
        default: spinner.stopAnimating()
        }
        controls.setPlaying(phase == .playing || phase == .buffering)
        // No screensaver while the picture plays. Apple's player saw to this, but the engine's software route has none
        // behind it; loading and buffering leave it as it was, so a stall mid-play doesn't let the screensaver in.
        switch phase {
        case .playing: UIApplication.shared.isIdleTimerDisabled = true
        case .loading, .buffering: break
        default: UIApplication.shared.isIdleTimerDisabled = false
        }
        // Played again from elsewhere (the lock screen, a command) after the sleep timer ran out: awake.
        if phase == .playing, sleepPhase == .asleep { wake() }
        // A moment's buffering mid-play doesn't keep the controls up; a pause does.
        switch phase {
        case .playing: if hideTask == nil || hideTask!.isCancelled { scheduleHide() }
        case .buffering: break
        default: hideTask?.cancel(); if phase == .paused { showControls() }
        }
        if let transport {
            nowPlaying.setTime(position: transport.currentTime, duration: transport.duration, rate: phase == .playing ? transport.rate : 0)
        }
    }

    // MARK: Audio sync

    private func wirePanels() {
        audioSyncPanel.onChange = { [weak self] seconds in
            guard let self, let current = audioSync else { return }
            audioSync = (seconds, current.offersRestOfSeason)
            delegate?.playerScreen(self, audioDelay: seconds, restOfSeason: false)
            rebuildMenus()
        }
        audioSyncPanel.onRestOfSeason = { [weak self] in
            guard let self, let current = audioSync else { return }
            delegate?.playerScreen(self, audioDelay: current.seconds, restOfSeason: true)
        }
        audioSyncPanel.onDone = { [weak self] in self?.closeAudioSync() }
        sleepPrompt.onMore = { [weak self] in self?.extendSleep() }
        sleepPrompt.onCancel = { [weak self] in self?.setSleep(nil) }
    }

    private func openAudioSync() {
        guard let audioSync else { return }
        audioSyncPanel.configure(seconds: audioSync.seconds, offersRestOfSeason: audioSync.offersRestOfSeason)
        audioSyncPanel.alpha = 0
        audioSyncPanel.isHidden = false
        UIView.animate(withDuration: 0.2) { self.audioSyncPanel.alpha = 1 }
        hideTask?.cancel()
        #if os(tvOS)
        // After the menu has gone, or it takes focus back to its button.
        Task { [weak self] in
            try? await Task.sleep(for: .milliseconds(350))
            guard let self, audioSyncOpen else { return }
            controls.focusNext = nil
            setNeedsFocusUpdate()
            updateFocusIfNeeded()
        }
        #endif
    }

    private func closeAudioSync() {
        guard audioSyncOpen else { return }
        audioSyncPanel.isHidden = true
        #if os(tvOS)
        controls.focusNext = controls.audioButton
        setNeedsFocusUpdate()
        updateFocusIfNeeded()
        #endif
        scheduleHide()
    }

    // MARK: Sleep timer

    /// The viewer chose a timer (or Off, or Cancel timer) in the menu or the question.
    private func setSleep(_ setting: SleepTimer.Setting?) {
        log.info("Sleep timer: \(setting?.label ?? "off")")
        sleep.set(setting)
        if sleepPhase == .asking { endAsking() }
        rebuildMenus()
        delegate?.playerScreenSleepChanged(self)
        if setting != nil { watchSleep() }
    }

    /// "+15 min": fifteen minutes from now, whatever the timer was (End of episode included).
    private func extendSleep() {
        setSleep(.minutes(15))
    }

    /// Checks the timer a few times a second while it runs: the question in the last minute (or as End of episode's
    /// credits start), the sound fading over the last ten seconds, and the stop.
    private func watchSleep() {
        guard sleepWatch == nil else { return }
        sleepWatch = Task { [weak self] in
            while let self, !Task.isCancelled, self.checkSleep() {
                try? await Task.sleep(for: .milliseconds(250))
            }
            self?.sleepWatch = nil
        }
    }

    /// Whether to keep watching the timer.
    private func checkSleep() -> Bool {
        guard sleepPhase != .asleep else { return false }
        guard let setting = sleep.setting else {
            if sleepPhase == .asking { endAsking() }
            return false
        }
        // Between transports (the next episode opening): nothing to measure until it's here.
        guard let transport else { return true }
        let left: Double
        let asking: Bool
        switch setting {
        case .minutes:
            left = sleep.remaining ?? 0
            asking = left <= 60
        case .endOfEpisode:
            guard let end = episodeEnd, transport.duration > 0 else { return true }
            let stop = end.stopsAtCredits ? end.credits ?? transport.duration : transport.duration
            let question = end.stopsAtCredits ? stop - 60 : end.credits ?? stop - 60
            let position = transport.currentTime
            left = (stop - position) / Double(max(transport.rate, 0.25))
            if left <= 0.3 {
                fallAsleep()
                return false
            }
            asking = position >= min(question, stop - 10)
        }
        if asking {
            if sleepPhase == .off { beginAsking() }
            sleepPrompt.ask(detail: "Pausing in " + Format.timestamp(left.rounded(.up)))
            transport.volume = Float(max(0, min(1, left / 10)))
        } else if sleepPhase == .asking {
            endAsking()
        }
        let minutes = sleep.remaining.map { Int(($0 / 60).rounded(.up)) }
        if minutes != sleepMinutesShown, !menuOpen { rebuildSleepMenu() }
        return true
    }

    private func beginAsking() {
        log.info("Sleep timer asking whether anyone's still watching")
        sleepPhase = .asking
        closeAudioSync()
        hideControls()
        sleepPrompt.ask(detail: "")
        #if os(tvOS)
        setNeedsFocusUpdate()
        updateFocusIfNeeded()
        #endif
        delegate?.playerScreenSleepChanged(self)
    }

    /// Back to watching: the question and the dim go, the sound comes back up.
    private func endAsking() {
        sleepPhase = .off
        sleepPrompt.dismiss()
        transport?.volume = 1
        #if os(tvOS)
        setNeedsFocusUpdate()
        updateFocusIfNeeded()
        #endif
    }

    /// Run out: the picture pauses (its place reported as for any pause) and stays dimmed until someone's there.
    private func fallAsleep() {
        guard sleepPhase != .asleep, !finished else { return }
        log.info("Sleep timer ran out; pausing")
        if sleep.setting != nil { sleep.set(nil) }
        sleepPhase = .asleep
        transport?.pause()
        hideControls()
        sleepPrompt.sleep()
        rebuildMenus()
        #if os(tvOS)
        setNeedsFocusUpdate()
        updateFocusIfNeeded()
        #endif
        delegate?.playerScreenSleepChanged(self)
    }

    /// The episode reached its end with the timer stopping there: asleep now, if the watch hadn't got there first.
    func sleepAtEndOfEpisode() {
        fallAsleep()
    }

    /// Someone's there after the timer ran out: the dim goes and the controls come up (still paused).
    private func wake() {
        guard sleepPhase == .asleep else { return }
        sleepPhase = .off
        sleepPrompt.dismiss()
        transport?.volume = 1
        showControls()
        delegate?.playerScreenSleepChanged(self)
    }

    /// A touch on the picture while the timer asks (15 minutes more) or after it ran out (awake): true if it was that.
    private func handleSleepTouch() -> Bool {
        switch sleepPhase {
        case .asking: extendSleep()
        case .asleep: wake()
        case .off: return false
        }
        return true
    }

    // MARK: Picture in Picture

    #if os(iOS)
    private static func topViewController() -> UIViewController? {
        let scene = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first
        var top = scene?.keyWindow?.rootViewController ?? scene?.windows.first?.rootViewController
        while let presented = top?.presentedViewController { top = presented }
        return top
    }
    #endif
}

extension PlayerViewController: PlayerTransportDelegate {
    func transport(_ transport: PlayerTransport, phaseChanged phase: TransportPhase) {
        #if os(iOS)
        if isViewLoaded { preparePictureInPicture(transport) }
        #endif
        phaseChanged(phase)
        delegate?.playerScreen(self, phaseChanged: phase)
    }

    func transport(_ transport: PlayerTransport, tick seconds: Double) {
        guard isViewLoaded else { return }
        // Until a seek lands (the clock reaches its target, give or take), the timeline keeps showing the target.
        if let pending = seekTarget {
            if abs(seconds - pending.seconds) < 2 || Date().timeIntervalSince(pending.at) > 15 {
                seekTarget = nil
            } else {
                controls.setTime(position: pending.seconds, duration: transport.duration, buffered: transport.bufferedTime)
                delegate?.playerScreen(self, tick: seconds)
                return
            }
        }
        controls.setTime(position: seconds, duration: transport.duration, buffered: transport.bufferedTime)
        if Date().timeIntervalSince(lastNowPlayingUpdate) > 1 {
            lastNowPlayingUpdate = Date()
            nowPlaying.setTime(position: seconds, duration: transport.duration, rate: transport.isPlaying ? transport.rate : 0)
        }
        delegate?.playerScreen(self, tick: seconds)
    }

    func transportTracksChanged(_ transport: PlayerTransport) {
        #if os(iOS)
        if isViewLoaded { preparePictureInPicture(transport) }
        #endif
        rebuildMenus()
        delegate?.playerScreenTracksChanged(self)
    }
}

#if os(iOS)
extension PlayerViewController: UIGestureRecognizerDelegate {
    /// Taps on the controls (and the audio sync panel) are theirs; taps on the picture show or hide them.
    func gestureRecognizer(_ gestureRecognizer: UIGestureRecognizer, shouldReceive touch: UITouch) -> Bool {
        var view = touch.view
        while let current = view {
            if current is UIControl || current is ScrubberView || current is AVRoutePickerView || current is AudioSyncPanel { return false }
            view = current.superview
        }
        return true
    }
}

extension PlayerViewController: AVPictureInPictureControllerDelegate {
    nonisolated func pictureInPictureControllerWillStartPictureInPicture(_ controller: AVPictureInPictureController) {
        MainActor.assumeIsolated {
            isInPictureInPicture = true
            transport?.setPictureInPictureActive(true)
            // The player is put away; the session goes on in the window.
            if presentingViewController != nil { dismiss(animated: true) }
        }
    }

    nonisolated func pictureInPictureController(_ controller: AVPictureInPictureController, failedToStartPictureInPictureWithError error: any Error) {
        MainActor.assumeIsolated {
            log.warning("Picture in Picture didn't start", error: error)
            isInPictureInPicture = false
            transport?.setPictureInPictureActive(false)
        }
    }

    /// Back to the full player from Picture in Picture.
    nonisolated func pictureInPictureController(_ controller: AVPictureInPictureController,
                                                restoreUserInterfaceForPictureInPictureStopWithCompletionHandler completionHandler: @escaping (Bool) -> Void) {
        // AVKit asks, and takes the answer, on the main thread; the handler just isn't marked Sendable.
        nonisolated(unsafe) let completionHandler = completionHandler
        MainActor.assumeIsolated {
            guard let presenter = Self.topViewController() else {
                completionHandler(false)
                return
            }
            isRestoringFromPictureInPicture = true
            presenter.present(self, animated: true) { completionHandler(true) }
        }
    }

    nonisolated func pictureInPictureControllerDidStopPictureInPicture(_ controller: AVPictureInPictureController) {
        MainActor.assumeIsolated {
            isInPictureInPicture = false
            transport?.setPictureInPictureActive(false)
            // Closed from the window rather than brought back: that's the end of watching.
            if !isRestoringFromPictureInPicture, presentingViewController == nil {
                close()
            }
        }
    }
}
#endif

/// The screen's root view. On Apple TV it takes focus itself while the controls are hidden, so the remote's touches
/// and presses still arrive.
final class PlayerRootView: UIView {
    var takesFocus = false
    #if os(tvOS)
    override var canBecomeFocused: Bool { takesFocus }
    #endif
}
