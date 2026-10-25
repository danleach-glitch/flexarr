import AVKit
import FlexarrKit
import SwiftUI
import UIKit

/// Our controls over the video: the title, previous and next episode, skip back and forward, play/pause, the
/// timeline, and menus for audio, subtitles and speed; the Skip Intro / Next Episode pill above the timeline. The
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

    let scrubber = ScrubberView()
    let playButton: UIButton
    let backButton: UIButton
    let forwardButton: UIButton
    let previousButton: UIButton
    let nextButton: UIButton
    let audioButton: UIButton
    let subtitlesButton: UIButton
    let speedButton: UIButton
    let promptButton: UIButton
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
    private static let symbolSize: CGFloat = 34
    private static let margin: CGFloat = 80
    private static let bottomMargin: CGFloat = 56
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
        playButton = Self.button("play.fill", size: Self.symbolSize * 1.5)
        backButton = Self.button("gobackward.10", size: Self.symbolSize)
        forwardButton = Self.button("goforward.10", size: Self.symbolSize)
        previousButton = Self.button("backward.end.fill", size: Self.symbolSize * 0.8)
        nextButton = Self.button("forward.end.fill", size: Self.symbolSize * 0.8)
        audioButton = Self.button("waveform", size: Self.symbolSize * 0.8)
        subtitlesButton = Self.button("captions.bubble", size: Self.symbolSize * 0.8)
        speedButton = Self.button("gauge.with.needle", size: Self.symbolSize * 0.8)
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

    /// "1080p · E-AC-3 5.1", or what the server is doing to the stream.
    func setDetail(_ detail: String?) {
        detailLabel.text = detail?.uppercased()
        detailLabel.isHidden = detail == nil
    }

    func setPlaying(_ playing: Bool) {
        playButton.configuration?.image = UIImage(systemName: playing ? "pause.fill" : "play.fill")
    }

    func setTime(position: Double, duration: Double, buffered: Double) {
        scrubber.duration = duration
        scrubber.position = position
        scrubber.buffered = buffered
        let shown = scrubber.isScrubbing ? scrubber.scrubPosition : position
        elapsedLabel.text = ScrubberView.clock(shown)
        remainingLabel.text = duration > 0 ? "−" + ScrubberView.clock(max(0, duration - shown)) : ""
    }

    /// A film has neither button; an episode has both, with the one that goes nowhere dimmed.
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
    func setPrompt(_ title: String?, countingDown: Bool) {
        if let title {
            UIView.performWithoutAnimation {
                promptButton.configuration?.title = title
                promptButton.configuration?.image = UIImage(systemName: title.hasPrefix("Skip") ? "forward.end.fill" : "forward.fill")
                promptButton.layoutIfNeeded()
            }
        }
        let wasHidden = promptButton.alpha == 0
        UIView.animate(withDuration: 0.2) {
            self.promptButton.alpha = title == nil ? 0 : 1
            self.cancelButton.alpha = countingDown ? 1 : 0
        }
        promptButton.isUserInteractionEnabled = title != nil
        cancelButton.isUserInteractionEnabled = countingDown
        #if os(tvOS)
        if title != nil, wasHidden { setNeedsFocusUpdate() }
        #endif
    }

    var promptShown: Bool { promptButton.alpha > 0 }

    /// A moment's "+15 s" in the middle of the picture after a skip.
    func flashSkip(_ seconds: Double) {
        hud.text = (seconds > 0 ? "+" : "−") + "\(Int(abs(seconds))) s"
        hudTask?.cancel()
        hud.alpha = 1
        hudTask = Task { [weak self] in
            try? await Task.sleep(for: .milliseconds(700))
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
        detailLabel.font = .systemFont(ofSize: 11, weight: .semibold)
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

        #if os(tvOS)
        let titles = UIStackView(arrangedSubviews: [titleLabel, subtitleLabel, detailLabel])
        titles.setCustomSpacing(8, after: subtitleLabel)
        #else
        let titles = UIStackView(arrangedSubviews: [titleLabel, subtitleLabel]) // the detail sits bottom-left on a phone
        #endif
        titles.axis = .vertical
        titles.spacing = 3
        let prompts = UIStackView(arrangedSubviews: [cancelButton, promptButton])
        prompts.axis = .horizontal
        prompts.alignment = .center
        prompts.spacing = 10
        promptButton.alpha = 0
        cancelButton.alpha = 0
        promptButton.isUserInteractionEnabled = false
        cancelButton.isUserInteractionEnabled = false

        playButton.addAction(UIAction { [weak self] _ in self?.onPlayPause?() }, for: .primaryActionTriggered)
        backButton.addAction(UIAction { [weak self] _ in self?.onSkip?(-(self?.skipSeconds ?? 10)) }, for: .primaryActionTriggered)
        forwardButton.addAction(UIAction { [weak self] _ in self?.onSkip?(self?.skipSeconds ?? 10) }, for: .primaryActionTriggered)
        previousButton.addAction(UIAction { [weak self] _ in self?.onPrevious?() }, for: .primaryActionTriggered)
        nextButton.addAction(UIAction { [weak self] _ in self?.onNext?() }, for: .primaryActionTriggered)
        promptButton.addAction(UIAction { [weak self] _ in self?.onPrompt?() }, for: .primaryActionTriggered)
        cancelButton.addAction(UIAction { [weak self] _ in self?.onCancel?() }, for: .primaryActionTriggered)
        for button in [audioButton, subtitlesButton, speedButton] {
            button.showsMenuAsPrimaryAction = true
            button.isHidden = true
        }
        skipSeconds = 30

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
        // Top: the title. Bottom: one glass bar holding the buttons (transport left, menus right), the timeline and
        // the times; the pill floats above it on the right.
        let bar = GlassView(cornerRadius: 30)
        let transport = UIStackView(arrangedSubviews: [previousButton, backButton, playButton, forwardButton, nextButton])
        transport.axis = .horizontal
        transport.alignment = .center
        transport.spacing = 6
        let menus = UIStackView(arrangedSubviews: [audioButton, subtitlesButton, speedButton])
        menus.axis = .horizontal
        menus.alignment = .center
        menus.spacing = 6
        for view in [transport, menus, scrubber, elapsedLabel, remainingLabel] as [UIView] {
            view.translatesAutoresizingMaskIntoConstraints = false
            bar.contentView.addSubview(view)
        }
        bar.translatesAutoresizingMaskIntoConstraints = false
        addSubview(bar)
        let inset: CGFloat = 32
        NSLayoutConstraint.activate([
            titles.topAnchor.constraint(equalTo: safeAreaLayoutGuide.topAnchor, constant: bottom),
            titles.leadingAnchor.constraint(equalTo: leadingAnchor, constant: margin),
            titles.trailingAnchor.constraint(lessThanOrEqualTo: trailingAnchor, constant: -margin),
            bar.leadingAnchor.constraint(equalTo: leadingAnchor, constant: margin),
            bar.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -margin),
            bar.bottomAnchor.constraint(equalTo: safeAreaLayoutGuide.bottomAnchor, constant: -bottom),
            transport.leadingAnchor.constraint(equalTo: bar.leadingAnchor, constant: inset - 16),
            transport.topAnchor.constraint(equalTo: bar.topAnchor, constant: 18),
            menus.trailingAnchor.constraint(equalTo: bar.trailingAnchor, constant: -inset + 16),
            menus.centerYAnchor.constraint(equalTo: transport.centerYAnchor),
            scrubber.leadingAnchor.constraint(equalTo: bar.leadingAnchor, constant: inset),
            scrubber.trailingAnchor.constraint(equalTo: bar.trailingAnchor, constant: -inset),
            scrubber.topAnchor.constraint(equalTo: transport.bottomAnchor, constant: 14),
            elapsedLabel.leadingAnchor.constraint(equalTo: scrubber.leadingAnchor),
            elapsedLabel.topAnchor.constraint(equalTo: scrubber.bottomAnchor, constant: 6),
            elapsedLabel.bottomAnchor.constraint(equalTo: bar.bottomAnchor, constant: -22),
            remainingLabel.trailingAnchor.constraint(equalTo: scrubber.trailingAnchor),
            remainingLabel.centerYAnchor.constraint(equalTo: elapsedLabel.centerYAnchor),
            prompts.trailingAnchor.constraint(equalTo: bar.trailingAnchor),
            prompts.bottomAnchor.constraint(equalTo: bar.topAnchor, constant: -20),
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
        let menus = UIStackView(arrangedSubviews: [audioButton, subtitlesButton, speedButton])
        menus.axis = .horizontal
        menus.alignment = .center
        menus.spacing = 0
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

    private static func button(_ symbol: String, size: CGFloat) -> UIButton {
        var configuration = UIButton.Configuration.plain()
        configuration.image = UIImage(systemName: symbol)
        configuration.preferredSymbolConfigurationForImage = UIImage.SymbolConfiguration(pointSize: size, weight: .semibold)
        #if os(tvOS)
        configuration.baseForegroundColor = .white
        configuration.contentInsets = NSDirectionalEdgeInsets(top: 12, leading: 18, bottom: 12, trailing: 18)
        #else
        configuration.baseForegroundColor = UIColor.white.withAlphaComponent(0.72) // the picture shows through the glyphs too
        configuration.contentInsets = NSDirectionalEdgeInsets(top: 10, leading: 11, bottom: 10, trailing: 11)
        #endif
        let button = UIButton(configuration: configuration)
        button.tintColor = configuration.baseForegroundColor
        return button
    }

    /// The Skip Intro / Next Episode pill (filled, in the app's colours) and Cancel beside it.
    private static func pill(filled: Bool) -> UIButton {
        var configuration = UIButton.Configuration.filled()
        configuration.baseBackgroundColor = filled ? UIColor(Color.screen).withAlphaComponent(0.95) : UIColor.black.withAlphaComponent(0.55)
        configuration.baseForegroundColor = filled ? UIColor(Color.room) : .white
        configuration.cornerStyle = .capsule
        configuration.imagePadding = 8
        #if os(tvOS)
        configuration.contentInsets = NSDirectionalEdgeInsets(top: 16, leading: 28, bottom: 16, trailing: 30)
        let font = UIFont.systemFont(ofSize: 26, weight: .semibold)
        #else
        configuration.contentInsets = NSDirectionalEdgeInsets(top: 12, leading: 18, bottom: 12, trailing: 20)
        let font = UIFont.systemFont(ofSize: 16, weight: .semibold)
        #endif
        configuration.titleTextAttributesTransformer = UIConfigurationTextAttributesTransformer { attributes in
            var attributes = attributes
            attributes.font = font
            return attributes
        }
        if !filled { configuration.title = "Cancel" }
        return UIButton(configuration: configuration)
    }

    #if os(tvOS)
    /// The timeline takes focus first; a pill that's showing takes it over.
    override var preferredFocusEnvironments: [UIFocusEnvironment] {
        promptShown ? [promptButton] : [scrubber]
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
