// The iPhone's and Apple TV's (UIKit); the Mac draws its own in FlexarrMac/Player.
#if canImport(UIKit)
import AVKit
import FlexarrKit
import SwiftUI
import UIKit

/// Our controls over the video: the title, previous and next episode, skip back and forward, play/pause, the
/// timeline, and menus for audio, subtitles, speed and the sleep timer; the Skip Intro / Next Episode pill above the timeline. The
/// controls sit on translucent glass so they read over any picture. On Apple TV the buttons are focusable and the
/// timeline takes the remote's swipes; on iPhone and iPad they're tapped.
final class PlayerControlsView: UIView {
    var onClose: (() -> Void)?
    var onPlayPause: (() -> Void)?
    var onSkip: ((Double) -> Void)?
    var onPrevious: (() -> Void)?
    var onNext: (() -> Void)?
    var onPrompt: (() -> Void)?
    var onCancel: (() -> Void)?
    var onPictureInPicture: (() -> Void)?
    /// Live TV's channel list (the button shows only when this is set).
    var onChannels: (() -> Void)? {
        didSet { channelsButton.isHidden = onChannels == nil }
    }
    /// Live TV's guide over the picture (Apple TV; the button shows only when this is set).
    var onGuide: (() -> Void)? {
        didSet { guideButton.isHidden = onGuide == nil }
    }

    let scrubber = ScrubberView()
    let playButton: UIButton
    let backButton: UIButton
    let forwardButton: UIButton
    let previousButton: UIButton
    let nextButton: UIButton
    let audioButton: UIButton
    let subtitlesButton: UIButton
    let speedButton: UIButton
    let sleepButton: UIButton
    let channelsButton: UIButton
    let guideButton: UIButton
    let promptButton: UIButton
    /// Skip Intro or Next Episode, with its Cancel: stays when the rest of the controls fade (see `setChromeVisible`).
    private var promptRow: UIView?
    let cancelButton: UIButton
    #if os(iOS)
    let closeButton: UIButton
    let pictureInPictureButton: UIButton
    let routePicker = AVRoutePickerView()
    private var pictureInPictureGlass: UIView?

    /// No Picture in Picture on this device: its button goes, glass and all.
    func setPictureInPictureAvailable(_ available: Bool) {
        pictureInPictureGlass?.isHidden = !available
    }
    #endif
    private let titleLabel = UILabel()
    private let subtitleLabel = UILabel()
    private let detailLabel = UILabel()
    private let elapsedLabel = UILabel()
    private let remainingLabel = UILabel()
    private let topShade = GradientView(top: 0.55, bottom: 0)
    private let bottomShade = GradientView(top: 0, bottom: 0.7)
    private let hud = UILabel()
    private var hudTask: Task<Void, Never>?

    #if os(tvOS)
    private static let symbolSize: CGFloat = 40
    private static let margin: CGFloat = 80
    private static let bottomMargin: CGFloat = 50
    #else
    private static let symbolSize: CGFloat = 22
    private static let margin: CGFloat = 20
    private static let bottomMargin: CGFloat = 10
    #endif

    var skipSeconds: Double = 30 {
        didSet {
            let seconds = Int(skipSeconds)
            backButton.configuration?.image = UIImage(systemName: "\(seconds).arrow.trianglehead.counterclockwise") ?? UIImage(systemName: "gobackward.\(seconds)") ?? UIImage(systemName: "gobackward")
            forwardButton.configuration?.image = UIImage(systemName: "\(seconds).arrow.trianglehead.clockwise") ?? UIImage(systemName: "goforward.\(seconds)") ?? UIImage(systemName: "goforward")
        }
    }

    override init(frame: CGRect) {
        playButton = Self.button(Symbol.play, size: Self.symbolSize * 1.5)
        backButton = Self.button("gobackward.10", size: Self.symbolSize)
        forwardButton = Self.button("goforward.10", size: Self.symbolSize)
        previousButton = Self.button("backward.end.fill", size: Self.symbolSize * 0.8)
        nextButton = Self.button("forward.end.fill", size: Self.symbolSize * 0.8)
        audioButton = Self.button("waveform", size: Self.symbolSize * 0.8, menu: true)
        subtitlesButton = Self.button("captions.bubble", size: Self.symbolSize * 0.8, menu: true)
        speedButton = Self.button("gauge.with.needle", size: Self.symbolSize * 0.8, menu: true)
        // The bed is a wide symbol: drawn smaller, it sits as large as the others.
        sleepButton = Self.button("bed.double", size: Self.symbolSize * 0.62, menu: true)
        channelsButton = Self.button("list.bullet.rectangle", size: Self.symbolSize * 0.8)
        guideButton = Self.button("calendar", size: Self.symbolSize * 0.8)
        promptButton = Self.pill(filled: true)
        cancelButton = Self.pill(filled: false)
        #if os(iOS)
        closeButton = Self.button("xmark", size: Self.symbolSize * 0.75)
        pictureInPictureButton = Self.button("pip.enter", size: Self.symbolSize * 0.8)
        #endif
        super.init(frame: frame)
        build()
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    // MARK: State

    func setTitle(_ title: String, subtitle: String?) {
        titleLabel.text = title
        subtitleLabel.text = subtitle
        subtitleLabel.isHidden = subtitle == nil
    }

    /// "1080p   E-AC-3 5.1   Original", or what the server is doing to the stream, as written (sentence case, never
    /// capitals).
    func setDetail(_ detail: String?) {
        detailLabel.text = detail
        detailLabel.isHidden = detail == nil
    }

    func setPlaying(_ playing: Bool) {
        playButton.configuration?.image = UIImage(systemName: playing ? Symbol.pause : Symbol.play)
    }

    /// Live TV: the timeline is the rewind window, so the left label is the time of day at the playhead and the right
    /// one says LIVE, or how far behind live the picture is.
    var isLive = false {
        didSet {
            #if os(tvOS)
            downToMenus.isEnabled = isLive
            #endif
        }
    }
    #if os(tvOS)
    /// Live TV: down from the circles goes to the buttons bottom-right, not the timeline.
    private let downToMenus = UIFocusGuide()
    #endif

    /// Kept while the language shown stays the same (it's read on every tick).
    private static var timeOfDayFormatter: DateFormatter?
    private static var timeOfDay: DateFormatter {
        let locale = Localization.locale
        if let kept = timeOfDayFormatter, kept.locale == locale { return kept }
        let formatter = DateFormatter()
        formatter.locale = locale
        formatter.timeStyle = .short
        timeOfDayFormatter = formatter
        return formatter
    }

    func setTime(position: Double, duration: Double, buffered: Double) {
        scrubber.duration = duration
        scrubber.position = position
        scrubber.buffered = buffered
        let shown = scrubber.isScrubbing ? scrubber.scrubPosition : position
        if isLive {
            let behind = max(0, duration - shown)
            elapsedLabel.text = Self.timeOfDay.string(from: Date().addingTimeInterval(-behind))
            remainingLabel.text = behind < LiveTransport.liveSlack ? t("Live") : t("−{time}", ["time": ScrubberView.clock(behind)])
            return
        }
        elapsedLabel.text = ScrubberView.clock(shown)
        remainingLabel.text = duration > 0 ? t("−{time}", ["time": ScrubberView.clock(max(0, duration - shown))]) : ""
    }

    /// Both buttons when either goes somewhere (Previous always does, starting the title again), the other dimmed.
    func setEpisodeNavigation(hasPrevious: Bool, hasNext: Bool) {
        let episode = hasPrevious || hasNext
        #if os(iOS)
        previousButton.superview?.superview?.isHidden = !episode // the glass circle around it too
        nextButton.superview?.superview?.isHidden = !episode
        #else
        previousButton.isHidden = !episode
        nextButton.isHidden = !episode
        #endif
        previousButton.isEnabled = hasPrevious
        nextButton.isEnabled = hasNext
        previousButton.alpha = hasPrevious ? 1 : 0.35
        nextButton.alpha = hasNext ? 1 : 0.35
    }

    /// The pill above the timeline: Skip Intro, Next Episode (counting down), with Cancel beside it while counting.
    /// `symbol` goes beside the title: forward.end.fill for a skip, the live symbol for Back to live. Counting down, the
    /// pill keeps the width of `widest` (its title at the countdown's start).
    func setPrompt(_ title: String?, symbol: String = "forward.fill", countingDown: Bool, widest: String? = nil, takesFocus: Bool = true) {
        promptTakesFocus = takesFocus
        if let title {
            UIView.performWithoutAnimation {
                // Counting down, the pill keeps its "in 10" width, so it doesn't twitch as the number changes or goes.
                if countingDown, promptWidthHold == nil {
                    promptButton.configuration?.title = widest ?? t(Words.nextEpisodeIn, ["seconds": 10])
                    promptButton.configuration?.image = UIImage(systemName: symbol)
                    let width = promptButton.systemLayoutSizeFitting(UIView.layoutFittingCompressedSize).width
                    promptWidthHold = promptButton.widthAnchor.constraint(greaterThanOrEqualToConstant: width)
                    promptWidthHold?.isActive = true
                }
                promptButton.configuration?.title = title
                promptButton.configuration?.image = UIImage(systemName: symbol)
                promptButton.layoutIfNeeded()
            }
        }
        promptCounting = countingDown && title != nil
        updatePromptCover()
        if title == nil || !countingDown {
            promptWidthHold?.isActive = false
            promptWidthHold = nil
        }
        let wasHidden = promptButton.alpha == 0
        if cancelButton.isHidden == countingDown { cancelButton.isHidden = !countingDown }
        promptButton.layer.maskedCorners = countingDown ? [.layerMinXMinYCorner, .layerMinXMaxYCorner]
            : [.layerMinXMinYCorner, .layerMinXMaxYCorner, .layerMaxXMinYCorner, .layerMaxXMaxYCorner]
        UIView.animate(withDuration: 0.2) {
            self.promptButton.alpha = title == nil ? 0 : 1
            self.promptCover.alpha = title == nil ? 0 : 1
            self.cancelButton.alpha = countingDown ? 1 : 0
        }
        promptButton.isUserInteractionEnabled = title != nil
        cancelButton.isUserInteractionEnabled = countingDown
        #if os(tvOS)
        if title != nil, wasHidden { setNeedsFocusUpdate() }
        #endif
    }

    /// The countdown's fill across the pill, 0...1 (nil: none). It moves on with each tick, linearly, so it runs smoothly
    /// while playing and holds when paused.
    func setPromptProgress(_ progress: Double?) {
        layoutPromptCover()
        guard let progress else {
            promptFill.layer.removeAllAnimations()
            promptFill.frame.size.width = 0
            return
        }
        let bounds = promptCover.bounds
        let target = CGRect(x: 0, y: 0, width: bounds.width * min(1, max(0, progress)), height: bounds.height)
        if promptFill.frame.width == 0 {
            promptFill.frame = target
            return
        }
        UIView.animate(withDuration: 0.3, delay: 0, options: [.curveLinear, .beginFromCurrentState, .allowUserInteraction]) {
            self.promptFill.frame = target
        }
    }

    /// Counting down, the pill rests grey with light words and fills with white, the words dark where the white has
    /// reached. A cover drawn over the button does it, so the button underneath only takes focus and presses (tvOS draws
    /// a focused button's own white platter over any colours given it). On a TV the focused half is a lighter grey then.
    private var promptCounting = false
    private let promptCover: UIView = {
        let view = UIView()
        view.clipsToBounds = true
        view.isUserInteractionEnabled = false
        view.isAccessibilityElement = false
        view.isHidden = true
        view.alpha = 0
        return view
    }()
    private let promptCoverLabel = PlayerControlsView.coverLabel()
    private let promptFill: UIView = {
        let view = UIView()
        view.clipsToBounds = true
        view.backgroundColor = UIColor(Color.screen)
        return view
    }()
    private let promptFillLabel = PlayerControlsView.coverLabel()

    private static func coverLabel() -> UIButton {
        let button = UIButton(configuration: .filled())
        button.isUserInteractionEnabled = false
        button.isAccessibilityElement = false
        return button
    }

    /// The cover's two copies of the pill's label: light on grey, and dark on the white fill.
    private func updatePromptCover() {
        promptCover.isHidden = !promptCounting
        guard promptCounting, let configuration = promptButton.configuration else { return }
        #if os(tvOS)
        let focused = promptButton.isFocused
        #else
        let focused = false
        #endif
        let grey = UIColor(Color.aisle).withAlphaComponent(0.85)
        let base = focused ? Self.blend(UIColor(Color.aisle), UIColor(Color.screen), 0.3) : grey
        promptCoverLabel.configuration = Self.cover(configuration, background: base, foreground: UIColor(Color.screen))
        promptFillLabel.configuration = Self.cover(configuration, background: UIColor(Color.screen), foreground: UIColor(Color.room))
        layoutPromptCover()
    }

    private func layoutPromptCover() {
        guard promptCounting else { return }
        // Just shown: lay it out first, or its copies are drawn at the size it had before.
        promptRow?.layoutIfNeeded()
        let bounds = promptCover.bounds
        promptCoverLabel.frame = bounds
        promptFillLabel.frame = bounds
        promptFill.frame.size.height = bounds.height
        promptCover.layer.cornerRadius = bounds.height / 2
        promptCover.layer.maskedCorners = promptButton.layer.maskedCorners
    }

    private static func cover(_ configuration: UIButton.Configuration, background: UIColor, foreground: UIColor) -> UIButton.Configuration {
        var copy = configuration
        copy.baseBackgroundColor = background
        copy.background.backgroundColor = background
        copy.baseForegroundColor = foreground
        copy.imageColorTransformer = UIConfigurationColorTransformer { _ in foreground }
        let font = configuration.titleTextAttributesTransformer
        copy.titleTextAttributesTransformer = UIConfigurationTextAttributesTransformer { attributes in
            var attributes = font?(attributes) ?? attributes
            attributes.foregroundColor = foreground
            return attributes
        }
        return copy
    }

    /// `a` taken `amount` of the way towards `b`, opaque.
    private static func blend(_ a: UIColor, _ b: UIColor, _ amount: CGFloat) -> UIColor {
        var (r1, g1, b1, a1): (CGFloat, CGFloat, CGFloat, CGFloat) = (0, 0, 0, 0)
        var (r2, g2, b2, a2): (CGFloat, CGFloat, CGFloat, CGFloat) = (0, 0, 0, 0)
        a.getRed(&r1, green: &g1, blue: &b1, alpha: &a1)
        b.getRed(&r2, green: &g2, blue: &b2, alpha: &a2)
        return UIColor(red: r1 + (r2 - r1) * amount, green: g1 + (g2 - g1) * amount, blue: b1 + (b2 - b1) * amount, alpha: 1)
    }

    var promptShown: Bool { promptButton.alpha > 0 }
    private var promptWidthHold: NSLayoutConstraint?


    /// Whether a showing pill takes focus (Select presses it). Not after Cancel, so Select pauses as usual.
    private(set) var promptTakesFocus = true
    var promptOffersFocus: Bool { promptShown && promptTakesFocus }

    /// Shows or fades everything but the Skip Intro / Next Episode pill, which shows whenever it's offered: fading this
    /// whole view took the pill with it, so Next Episode only appeared with the controls.
    func setChromeVisible(_ visible: Bool) {
        // The HUD (the skip and seek flash) fades by itself; hiding takes it too.
        for view in subviews where view !== promptRow && (view !== hud || !visible) {
            view.alpha = visible ? 1 : 0
        }
    }

    /// "1.25×" beside the speed button's gauge while it isn't playing at normal speed.
    func setSpeedBadge(_ text: String?) {
        guard speedButton.configuration?.title != text else { return }
        speedButton.configuration?.title = text
        speedButton.accessibilityValue = text
    }

    /// The bed filled while a sleep timer runs.
    func setSleepActive(_ active: Bool) {
        sleepButton.configuration?.image = UIImage(systemName: active ? "bed.double.fill" : "bed.double")
        sleepButton.accessibilityValue = active ? t("On") : nil
        sleepButton.setNeedsUpdateConfiguration()
    }

    /// A moment's "+15 s" in the middle of the picture after a skip.
    func flashSkip(_ seconds: Double) {
        hud.text = seconds > 0 ? t("+{seconds} s", ["seconds": Int(abs(seconds))]) : t("−{seconds} s", ["seconds": Int(abs(seconds))])
        hudTask?.cancel()
        hud.alpha = 1
        hudTask = Task { [weak self] in
            try? await Task.sleep(for: .milliseconds(700))
            guard !Task.isCancelled else { return }
            UIView.animate(withDuration: 0.3) { self?.hud.alpha = 0 }
        }
    }

    /// A message in the middle of the picture for a couple of seconds ("Subtitles re-synced").
    func flashMessage(_ message: String) {
        hud.text = message
        hudTask?.cancel()
        hud.alpha = 1
        hudTask = Task { [weak self] in
            try? await Task.sleep(for: .seconds(2.5))
            guard !Task.isCancelled else { return }
            UIView.animate(withDuration: 0.3) { self?.hud.alpha = 0 }
        }
    }

    // MARK: Build

    private func build() {
        for shade in [topShade, bottomShade] {
            shade.translatesAutoresizingMaskIntoConstraints = false
            addSubview(shade)
        }
        #if os(tvOS)
        titleLabel.font = .systemFont(ofSize: 40, weight: .bold)
        subtitleLabel.font = .systemFont(ofSize: 27, weight: .medium)
        detailLabel.font = .systemFont(ofSize: 20, weight: .semibold)
        elapsedLabel.font = .monospacedDigitSystemFont(ofSize: 23, weight: .medium)
        hud.font = .systemFont(ofSize: 48, weight: .semibold)
        #else
        titleLabel.font = .systemFont(ofSize: 17, weight: .bold)
        subtitleLabel.font = .systemFont(ofSize: 14, weight: .medium)
        detailLabel.font = .systemFont(ofSize: 12, weight: .semibold)
        elapsedLabel.font = .monospacedDigitSystemFont(ofSize: 13, weight: .medium)
        hud.font = .systemFont(ofSize: 28, weight: .semibold)
        #endif
        remainingLabel.font = elapsedLabel.font
        titleLabel.textColor = .white
        subtitleLabel.textColor = UIColor.white.withAlphaComponent(0.8)
        detailLabel.textColor = UIColor.white.withAlphaComponent(0.55)
        detailLabel.isHidden = true
        elapsedLabel.textColor = .white
        remainingLabel.textColor = UIColor.white.withAlphaComponent(0.7)
        remainingLabel.textAlignment = .right
        titleLabel.lineBreakMode = .byTruncatingTail
        for label in [titleLabel, subtitleLabel, detailLabel] {
            label.layer.shadowColor = UIColor.black.cgColor
            label.layer.shadowOpacity = 0.5
            label.layer.shadowRadius = 4
            label.layer.shadowOffset = CGSize(width: 0, height: 1)
        }
        hud.textColor = .white
        hud.alpha = 0
        hud.layer.shadowColor = UIColor.black.cgColor
        hud.layer.shadowOpacity = 0.6
        hud.layer.shadowRadius = 6
        hud.layer.shadowOffset = .zero

        let titles = UIStackView(arrangedSubviews: [titleLabel, subtitleLabel]) // the detail sits bottom-left
        titles.axis = .vertical
        titles.spacing = 3
        let prompts = UIStackView(arrangedSubviews: [promptButton, cancelButton])
        promptRow = prompts
        prompts.axis = .horizontal
        prompts.alignment = .fill
        prompts.spacing = 2
        promptButton.alpha = 0
        cancelButton.alpha = 0
        cancelButton.isHidden = true
        promptButton.clipsToBounds = true
        // The countdown's cover sits over the Next half (not inside it: the button redraws its own layers over anything
        // added to it), following its frame.
        promptCover.addSubview(promptCoverLabel)
        promptFill.addSubview(promptFillLabel)
        promptCover.addSubview(promptFill)
        prompts.addSubview(promptCover)
        promptCover.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            promptCover.leadingAnchor.constraint(equalTo: promptButton.leadingAnchor),
            promptCover.trailingAnchor.constraint(equalTo: promptButton.trailingAnchor),
            promptCover.topAnchor.constraint(equalTo: promptButton.topAnchor),
            promptCover.bottomAnchor.constraint(equalTo: promptButton.bottomAnchor),
        ])
        cancelButton.clipsToBounds = true
        promptButton.isUserInteractionEnabled = false
        cancelButton.isUserInteractionEnabled = false

        playButton.addAction(UIAction { [weak self] _ in self?.onPlayPause?() }, for: .primaryActionTriggered)
        backButton.addAction(UIAction { [weak self] _ in self?.onSkip?(-(self?.skipSeconds ?? 10)) }, for: .primaryActionTriggered)
        forwardButton.addAction(UIAction { [weak self] _ in self?.onSkip?(self?.skipSeconds ?? 10) }, for: .primaryActionTriggered)
        previousButton.addAction(UIAction { [weak self] _ in self?.onPrevious?() }, for: .primaryActionTriggered)
        nextButton.addAction(UIAction { [weak self] _ in self?.onNext?() }, for: .primaryActionTriggered)
        promptButton.addAction(UIAction { [weak self] _ in self?.onPrompt?() }, for: .primaryActionTriggered)
        (promptButton as? PillButton)?.onUpdate = { [weak self] in self?.updatePromptCover() }
        cancelButton.addAction(UIAction { [weak self] _ in self?.onCancel?() }, for: .primaryActionTriggered)
        channelsButton.addAction(UIAction { [weak self] _ in self?.onChannels?() }, for: .primaryActionTriggered)
        channelsButton.accessibilityLabel = t("Channels")
        channelsButton.isHidden = true
        guideButton.addAction(UIAction { [weak self] _ in self?.onGuide?() }, for: .primaryActionTriggered)
        guideButton.accessibilityLabel = t("Guide")
        guideButton.isHidden = true
        for button in [audioButton, subtitlesButton, speedButton, sleepButton] {
            button.showsMenuAsPrimaryAction = true
            button.isHidden = true
        }
        audioButton.accessibilityLabel = t("Audio")
        subtitlesButton.accessibilityLabel = t(Words.subtitles)
        speedButton.accessibilityLabel = t("Speed")
        sleepButton.accessibilityLabel = t("Sleep timer")
        // The speed's badge: small type beside the gauge.
        speedButton.configuration?.imagePadding = 6
        let badgeFont = UIFont.monospacedDigitSystemFont(ofSize: Self.symbolSize * 0.6, weight: .bold)
        speedButton.configuration?.titleTextAttributesTransformer = UIConfigurationTextAttributesTransformer { attributes in
            var attributes = attributes
            attributes.font = badgeFont
            return attributes
        }
        skipSeconds = 30
        #if os(tvOS)
        scrubber.onSwipeUp = { [weak self] in
            guard let self else { return }
            // Live: up to the buttons just above the timeline (the first of them); otherwise up to play.
            let menu = [channelsButton, guideButton, audioButton, subtitlesButton, speedButton, sleepButton].first { !$0.isHidden }
            focusNext = isLive ? menu ?? playButton : playButton
            setNeedsFocusUpdate()
            updateFocusIfNeeded()
        }
        #endif

        for view in [titles, prompts, hud] as [UIView] {
            view.translatesAutoresizingMaskIntoConstraints = false
            addSubview(view)
        }
        let margin = Self.margin
        let bottom = Self.bottomMargin
        NSLayoutConstraint.activate([
            topShade.topAnchor.constraint(equalTo: topAnchor),
            topShade.leadingAnchor.constraint(equalTo: leadingAnchor),
            topShade.trailingAnchor.constraint(equalTo: trailingAnchor),
            topShade.heightAnchor.constraint(equalTo: heightAnchor, multiplier: 0.32),
            bottomShade.bottomAnchor.constraint(equalTo: bottomAnchor),
            bottomShade.leadingAnchor.constraint(equalTo: leadingAnchor),
            bottomShade.trailingAnchor.constraint(equalTo: trailingAnchor),
            bottomShade.heightAnchor.constraint(equalTo: heightAnchor, multiplier: 0.5),
            hud.centerXAnchor.constraint(equalTo: centerXAnchor),
            hud.centerYAnchor.constraint(equalTo: centerYAnchor),
        ])

        #if os(tvOS)
        // The phone's layout at television scale: the title top-left; the transport as glass circles in the middle of
        // the picture; the detail bottom-left and the menus in a glass capsule bottom-right, above the timeline with
        // the times at its ends; the pill above the menus.
        for (button, size) in [(previousButton, 100.0), (backButton, 116.0), (playButton, 150.0), (forwardButton, 116.0), (nextButton, 100.0)] {
            NSLayoutConstraint.activate([button.widthAnchor.constraint(equalToConstant: size), button.heightAnchor.constraint(equalToConstant: size)])
        }
        let transport = UIStackView(arrangedSubviews: [previousButton, backButton, playButton, forwardButton, nextButton])
        transport.axis = .horizontal
        transport.alignment = .center
        transport.spacing = 36
        let menus = UIStackView(arrangedSubviews: [channelsButton, guideButton, audioButton, subtitlesButton, speedButton, sleepButton])
        menus.axis = .horizontal
        menus.alignment = .center
        menus.spacing = 8
        for view in [transport, menus, scrubber, elapsedLabel, remainingLabel, detailLabel] as [UIView] {
            view.translatesAutoresizingMaskIntoConstraints = false
            addSubview(view)
        }
        NSLayoutConstraint.activate([
            titles.topAnchor.constraint(equalTo: safeAreaLayoutGuide.topAnchor, constant: bottom),
            titles.leadingAnchor.constraint(equalTo: leadingAnchor, constant: margin),
            titles.trailingAnchor.constraint(lessThanOrEqualTo: trailingAnchor, constant: -margin),
            transport.centerXAnchor.constraint(equalTo: centerXAnchor),
            transport.centerYAnchor.constraint(equalTo: centerYAnchor),
            elapsedLabel.leadingAnchor.constraint(equalTo: leadingAnchor, constant: margin),
            elapsedLabel.bottomAnchor.constraint(equalTo: safeAreaLayoutGuide.bottomAnchor, constant: -bottom),
            elapsedLabel.widthAnchor.constraint(greaterThanOrEqualToConstant: 80),
            remainingLabel.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -margin),
            remainingLabel.centerYAnchor.constraint(equalTo: elapsedLabel.centerYAnchor),
            remainingLabel.widthAnchor.constraint(greaterThanOrEqualToConstant: 100),
            scrubber.leadingAnchor.constraint(equalTo: elapsedLabel.trailingAnchor, constant: 24),
            scrubber.trailingAnchor.constraint(equalTo: remainingLabel.leadingAnchor, constant: -24),
            scrubber.centerYAnchor.constraint(equalTo: elapsedLabel.centerYAnchor),
            menus.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -margin),
            menus.bottomAnchor.constraint(equalTo: elapsedLabel.topAnchor, constant: -28),
            detailLabel.leadingAnchor.constraint(equalTo: elapsedLabel.leadingAnchor),
            detailLabel.centerYAnchor.constraint(equalTo: menus.centerYAnchor),
            detailLabel.trailingAnchor.constraint(lessThanOrEqualTo: menus.leadingAnchor, constant: -24),
            prompts.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -margin),
            prompts.bottomAnchor.constraint(equalTo: menus.topAnchor, constant: -20),
        ])
        // Up from the menus: the circles are far to the left, outside the focus engine's upward search, so a guide
        // above the capsule points the way to the play button.
        let upFromMenus = UIFocusGuide()
        addLayoutGuide(upFromMenus)
        upFromMenus.preferredFocusEnvironments = [playButton]
        NSLayoutConstraint.activate([
            upFromMenus.leadingAnchor.constraint(equalTo: menus.leadingAnchor),
            upFromMenus.trailingAnchor.constraint(equalTo: menus.trailingAnchor),
            upFromMenus.bottomAnchor.constraint(equalTo: prompts.topAnchor),
            upFromMenus.heightAnchor.constraint(equalToConstant: 160),
        ])
        // Down from the circles, when live: the menus are far to the right, outside the downward search, which
        // would land on the timeline. A strip under the circles points the way (hidden buttons are passed over).
        addLayoutGuide(downToMenus)
        downToMenus.preferredFocusEnvironments = menus.arrangedSubviews
        downToMenus.isEnabled = isLive
        NSLayoutConstraint.activate([
            downToMenus.leadingAnchor.constraint(equalTo: transport.leadingAnchor),
            downToMenus.trailingAnchor.constraint(equalTo: transport.trailingAnchor),
            downToMenus.topAnchor.constraint(equalTo: transport.bottomAnchor, constant: 30),
            downToMenus.heightAnchor.constraint(equalToConstant: 40),
        ])
        #else
        // Top: close, the title, AirPlay and Picture in Picture. Middle: the transport in glass circles. Bottom: the
        // pill (right), the menus in a glass capsule (right), then elapsed, the timeline and remaining on one line.
        routePicker.prioritizesVideoDevices = true
        routePicker.tintColor = .white
        routePicker.activeTintColor = .white
        closeButton.addAction(UIAction { [weak self] _ in self?.onClose?() }, for: .primaryActionTriggered)
        pictureInPictureButton.addAction(UIAction { [weak self] _ in self?.onPictureInPicture?() }, for: .primaryActionTriggered)
        let close = GlassView(cornerRadius: 20, content: closeButton, size: 40)
        let route = GlassView(cornerRadius: 20, content: routePicker, size: 40)
        // A family guest's AirPlay receiver couldn't reach the tunnel this device plays through.
        route.isHidden = GuestMode.isActive
        let pip = GlassView(cornerRadius: 20, content: pictureInPictureButton, size: 40)
        pictureInPictureGlass = pip
        let top = UIStackView(arrangedSubviews: [close, titles, UIView(), route, pip])
        top.axis = .horizontal
        top.alignment = .center
        top.spacing = 10
        top.setCustomSpacing(14, after: close)
        let transport = UIStackView(arrangedSubviews: [
            GlassView(cornerRadius: 24, content: previousButton, size: 48),
            GlassView(cornerRadius: 28, content: backButton, size: 56),
            GlassView(cornerRadius: 38, content: playButton, size: 76),
            GlassView(cornerRadius: 28, content: forwardButton, size: 56),
            GlassView(cornerRadius: 24, content: nextButton, size: 48),
        ])
        transport.axis = .horizontal
        transport.alignment = .center
        transport.spacing = 18
        let menus = UIStackView(arrangedSubviews: [channelsButton, guideButton, audioButton, subtitlesButton, speedButton, sleepButton])
        menus.axis = .horizontal
        menus.alignment = .center
        menus.spacing = 0
        // The same slot for each, however wide its symbol, so they sit evenly; the speed's widens for its badge.
        for button in [channelsButton, guideButton, audioButton, subtitlesButton, sleepButton] {
            button.widthAnchor.constraint(equalToConstant: 44).isActive = true
        }
        speedButton.widthAnchor.constraint(greaterThanOrEqualToConstant: 44).isActive = true
        let menuBar = GlassView(cornerRadius: 22, content: menus, size: nil)
        for view in [top, transport, menuBar, scrubber, elapsedLabel, remainingLabel, detailLabel] as [UIView] {
            view.translatesAutoresizingMaskIntoConstraints = false
            addSubview(view)
        }
        NSLayoutConstraint.activate([
            top.topAnchor.constraint(equalTo: safeAreaLayoutGuide.topAnchor, constant: 8),
            top.leadingAnchor.constraint(equalTo: safeAreaLayoutGuide.leadingAnchor, constant: 16),
            top.trailingAnchor.constraint(equalTo: safeAreaLayoutGuide.trailingAnchor, constant: -16),
            transport.centerXAnchor.constraint(equalTo: centerXAnchor),
            transport.centerYAnchor.constraint(equalTo: centerYAnchor),
            elapsedLabel.leadingAnchor.constraint(equalTo: safeAreaLayoutGuide.leadingAnchor, constant: margin),
            elapsedLabel.bottomAnchor.constraint(equalTo: safeAreaLayoutGuide.bottomAnchor, constant: -bottom),
            elapsedLabel.widthAnchor.constraint(greaterThanOrEqualToConstant: 44),
            remainingLabel.trailingAnchor.constraint(equalTo: safeAreaLayoutGuide.trailingAnchor, constant: -margin),
            remainingLabel.centerYAnchor.constraint(equalTo: elapsedLabel.centerYAnchor),
            remainingLabel.widthAnchor.constraint(greaterThanOrEqualToConstant: 50),
            scrubber.leadingAnchor.constraint(equalTo: elapsedLabel.trailingAnchor, constant: 10),
            scrubber.trailingAnchor.constraint(equalTo: remainingLabel.leadingAnchor, constant: -10),
            scrubber.centerYAnchor.constraint(equalTo: elapsedLabel.centerYAnchor),
            menuBar.trailingAnchor.constraint(equalTo: safeAreaLayoutGuide.trailingAnchor, constant: -margin),
            menuBar.bottomAnchor.constraint(equalTo: elapsedLabel.topAnchor, constant: -14),
            menuBar.heightAnchor.constraint(equalToConstant: 44),
            detailLabel.leadingAnchor.constraint(equalTo: elapsedLabel.leadingAnchor),
            detailLabel.centerYAnchor.constraint(equalTo: menuBar.centerYAnchor),
            detailLabel.trailingAnchor.constraint(lessThanOrEqualTo: menuBar.leadingAnchor, constant: -12),
            prompts.trailingAnchor.constraint(equalTo: safeAreaLayoutGuide.trailingAnchor, constant: -margin),
            prompts.bottomAnchor.constraint(equalTo: menuBar.topAnchor, constant: -10),
        ])
        #endif
    }

    /// A menu is open (or has just closed) on one of the menu buttons.
    var onMenuVisibility: ((_ button: UIButton, _ visible: Bool) -> Void)?
    #if os(tvOS)
    /// Where focus goes next, once: the button whose menu just closed.
    var focusNext: UIView?
    #endif

    static func button(_ symbol: String, size: CGFloat, menu: Bool = false) -> UIButton {
        var configuration = UIButton.Configuration.plain()
        configuration.image = UIImage(systemName: symbol)
        configuration.preferredSymbolConfigurationForImage = UIImage.SymbolConfiguration(pointSize: size, weight: .semibold)
        configuration.baseForegroundColor = UIColor.white.withAlphaComponent(0.72) // the picture shows through the glyphs too
        #if os(tvOS)
        // Its own glass circle (the focus lift would be clipped inside a wrapper); focused, it goes white with a dark
        // glyph like every tvOS button.
        // The same glass as the phone: 22% black with a hairline, the glyph letting the picture through.
        configuration.background.backgroundColor = UIColor.black.withAlphaComponent(0.22)
        configuration.background.strokeColor = UIColor.white.withAlphaComponent(0.14)
        configuration.background.strokeWidth = 1
        configuration.cornerStyle = .capsule
        configuration.contentInsets = NSDirectionalEdgeInsets(top: 22, leading: 22, bottom: 22, trailing: 22)
        let button = menu ? MenuButton(configuration: configuration) : FocusButton(configuration: configuration)
        button.configurationUpdateHandler = { button in
            guard var updated = button.configuration else { return }
            let focused = button.isFocused
            let glyph = focused ? UIColor.black.withAlphaComponent(0.85) : UIColor.white.withAlphaComponent(0.72)
            updated.background.backgroundColor = focused ? UIColor.white.withAlphaComponent(0.95) : UIColor.black.withAlphaComponent(0.22)
            updated.baseForegroundColor = glyph
            // tvOS draws symbol images at full strength whatever the foreground colour's alpha; tint the image itself.
            updated.image = updated.image?.withTintColor(glyph, renderingMode: .alwaysOriginal)
            button.configuration = updated
        }
        #else
        configuration.contentInsets = NSDirectionalEdgeInsets(top: 10, leading: 11, bottom: 10, trailing: 11)
        let button = menu ? MenuButton(configuration: configuration) : FocusButton(configuration: configuration)
        #endif
        button.tintColor = configuration.baseForegroundColor
        return button
    }

    /// On Apple TV, a focused button redraws (white, dark glyph) and lifts a little.
    class FocusButton: UIButton {
        #if os(tvOS)
        override func didUpdateFocus(in context: UIFocusUpdateContext, with coordinator: UIFocusAnimationCoordinator) {
            super.didUpdateFocus(in: context, with: coordinator)
            setNeedsUpdateConfiguration()
            coordinator.addCoordinatedAnimations { [self] in
                transform = isFocused ? CGAffineTransform(scaleX: 1.12, y: 1.12) : .identity
            }
        }
        #endif
    }

    /// Says when its menu opens and closes, so the controls stay up meanwhile and focus comes straight back to it.
    final class MenuButton: FocusButton {
        var controls: PlayerControlsView? { superview?.superview as? PlayerControlsView ?? superview?.superview?.superview as? PlayerControlsView ?? superview?.superview?.superview?.superview as? PlayerControlsView }

        override func contextMenuInteraction(_ interaction: UIContextMenuInteraction, willDisplayMenuFor configuration: UIContextMenuConfiguration, animator: (any UIContextMenuInteractionAnimating)?) {
            super.contextMenuInteraction(interaction, willDisplayMenuFor: configuration, animator: animator)
            controls?.onMenuVisibility?(self, true)
        }

        override func contextMenuInteraction(_ interaction: UIContextMenuInteraction, willEndFor configuration: UIContextMenuConfiguration, animator: (any UIContextMenuInteractionAnimating)?) {
            super.contextMenuInteraction(interaction, willEndFor: configuration, animator: animator)
            controls?.onMenuVisibility?(self, false)
        }
    }

    /// The Skip Intro / Next Episode pill (filled, in the app's colours) and Cancel beside it; the player's other
    /// panels use them too.
    /// The X's resting grey: darker than a counting pill's.
    static let cancelFill = UIColor(Color.room).withAlphaComponent(0.9)

    static func pill(filled: Bool) -> UIButton {
        var configuration = UIButton.Configuration.filled()
        // Next Episode and its X are the two halves of one pill (`PillButton` rounds their outer ends).
        configuration.baseBackgroundColor = UIColor(Color.screen).withAlphaComponent(0.95)
        configuration.baseForegroundColor = UIColor(Color.room)
        configuration.cornerStyle = .fixed
        configuration.background.cornerRadius = 0
        configuration.imagePadding = 8
        #if os(tvOS)
        configuration.contentInsets = NSDirectionalEdgeInsets(top: 16, leading: 28, bottom: 16, trailing: 30)
        let font = UIFont.monospacedDigitSystemFont(ofSize: 26, weight: .semibold)
        #else
        configuration.contentInsets = NSDirectionalEdgeInsets(top: 8, leading: 13, bottom: 8, trailing: 14)
        configuration.imagePadding = 6
        configuration.preferredSymbolConfigurationForImage = UIImage.SymbolConfiguration(pointSize: 11, weight: .semibold)
        let font = UIFont.monospacedDigitSystemFont(ofSize: 14, weight: .semibold)
        #endif
        configuration.titleTextAttributesTransformer = UIConfigurationTextAttributesTransformer { attributes in
            var attributes = attributes
            attributes.font = font
            return attributes
        }
        guard !filled else { return PillButton.focusable(configuration) }
        // Cancel: a round X beside the pill, named Cancel for VoiceOver.
        #if os(tvOS)
        let side: CGFloat = 22
        #else
        let side: CGFloat = 10
        #endif
        configuration.image = UIImage(systemName: "xmark")
        configuration.preferredSymbolConfigurationForImage = UIImage.SymbolConfiguration(pointSize: side, weight: .bold)
        // The X half rests a darker grey, so it reads apart from the counting half beside it.
        configuration.baseBackgroundColor = Self.cancelFill
        configuration.baseForegroundColor = UIColor(Color.screen)
        configuration.contentInsets = NSDirectionalEdgeInsets(top: side, leading: side * 0.8, bottom: side, trailing: side * 1.2)
        let button = PillButton.focusable(configuration)
        button.restingBackground = Self.cancelFill
        button.layer.maskedCorners = [.layerMaxXMinYCorner, .layerMaxXMaxYCorner]
        button.accessibilityLabel = t(Words.cancel)
        return button
    }

    #if os(tvOS)
    /// The timeline takes focus first; a pill that's showing takes it over; a button whose menu just closed gets it back.
    override var preferredFocusEnvironments: [UIFocusEnvironment] {
        if let focusNext {
            self.focusNext = nil
            return [focusNext]
        }
        return promptOffersFocus ? [promptButton] : [scrubber]
    }
    #endif
}

/// Translucent dark glass with a hairline edge, behind controls.
final class GlassView: UIView {
    let contentView = UIView()

    /// Empty glass, laid out by whoever adds subviews to `contentView`.
    init(cornerRadius: CGFloat) {
        super.init(frame: .zero)
        setUp(cornerRadius: cornerRadius)
    }

    /// Glass around one view, square at `size` (nil: the view's own size).
    init(cornerRadius: CGFloat, content: UIView, size: CGFloat?) {
        super.init(frame: .zero)
        setUp(cornerRadius: cornerRadius)
        content.translatesAutoresizingMaskIntoConstraints = false
        contentView.addSubview(content)
        var constraints = [
            content.centerXAnchor.constraint(equalTo: centerXAnchor),
            content.centerYAnchor.constraint(equalTo: centerYAnchor),
        ]
        if let size {
            constraints += [widthAnchor.constraint(equalToConstant: size), heightAnchor.constraint(equalToConstant: size)]
        } else {
            constraints += [
                content.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 4),
                content.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -4),
                content.topAnchor.constraint(equalTo: topAnchor),
                content.bottomAnchor.constraint(equalTo: bottomAnchor),
            ]
        }
        NSLayoutConstraint.activate(constraints)
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    private func setUp(cornerRadius: CGFloat) {
        // Clear tinted glass: the picture shows through, the controls just need an edge.
        #if os(iOS)
        backgroundColor = UIColor.black.withAlphaComponent(0.22)
        #else
        backgroundColor = UIColor.black.withAlphaComponent(0.3)
        #endif
        contentView.translatesAutoresizingMaskIntoConstraints = false
        addSubview(contentView)
        NSLayoutConstraint.activate([
            contentView.leadingAnchor.constraint(equalTo: leadingAnchor), contentView.trailingAnchor.constraint(equalTo: trailingAnchor),
            contentView.topAnchor.constraint(equalTo: topAnchor), contentView.bottomAnchor.constraint(equalTo: bottomAnchor),
        ])
        layer.cornerRadius = cornerRadius
        layer.cornerCurve = .continuous
        layer.borderWidth = 1 / UIScreen.main.scale
        layer.borderColor = UIColor.white.withAlphaComponent(0.14).cgColor
        clipsToBounds = true
    }
}

/// A vertical black gradient, for keeping the title and times readable over bright pictures.
final class GradientView: UIView {
    override class var layerClass: AnyClass { CAGradientLayer.self }

    init(top: CGFloat, bottom: CGFloat) {
        super.init(frame: .zero)
        isUserInteractionEnabled = false
        let gradient = layer as! CAGradientLayer
        gradient.colors = [UIColor.black.withAlphaComponent(top).cgColor, UIColor.black.withAlphaComponent(bottom).cgColor]
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }
}

/// A button whose rounded ends (`layer.maskedCorners`) stay half its height however wide it grows.
final class PillButton: UIButton {
    /// On Apple TV each half shows whether it has focus, as the system's buttons do: white with dark text when focused,
    /// grey with light text when not. On a phone, Next Episode is white and its X grey.
    /// Told after each configuration update (focus coming or going), so a cover over the button can follow it.
    var onUpdate: (() -> Void)?
    /// Its grey when not focused on a TV.
    var restingBackground = UIColor(Color.aisle).withAlphaComponent(0.85)

    static func focusable(_ configuration: UIButton.Configuration) -> PillButton {
        let button = PillButton(configuration: configuration)
        #if os(tvOS)
        button.configurationUpdateHandler = { button in
            defer { (button as? PillButton)?.onUpdate?() }
            guard var configuration = button.configuration else { return }
            let resting = (button as? PillButton)?.restingBackground ?? UIColor(Color.aisle).withAlphaComponent(0.85)
            configuration.baseBackgroundColor = button.isFocused ? UIColor(Color.screen) : resting
            configuration.baseForegroundColor = button.isFocused ? UIColor(Color.room) : UIColor(Color.screen)
            button.configuration = configuration
        }
        #endif
        return button
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        layer.cornerRadius = bounds.height / 2
    }

    #if os(iOS)
    /// Taps land up to 8 points above and below the compact pill, so it's easy to hit.
    override func point(inside point: CGPoint, with event: UIEvent?) -> Bool {
        bounds.insetBy(dx: 0, dy: -8).contains(point)
    }
    #endif
}
#endif
