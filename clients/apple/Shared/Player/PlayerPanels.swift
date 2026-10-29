import UIKit

/// Nudging a file's sound earlier or later when it's out of step with the picture: a glass panel over the bottom of
/// the picture, opened from the audio menu. Each press moves it 10 ms; holding a button (a long press on the Apple TV
/// remote) keeps moving it in 50 ms jumps until let go, up to two seconds either way. The value is the household's,
/// kept by the server.
final class AudioSyncPanel: UIView {
    /// The offset was nudged (or reset): seconds the sound plays later, earlier when negative.
    var onChange: ((Double) -> Void)?
    var onDone: (() -> Void)?

    static let step = 0.01
    static let jump = 0.05
    static let limit = 2.0

    private(set) var seconds: Double = 0
    private let titleLabel = UILabel()
    private let valueLabel = UILabel()
    private let captionLabel = UILabel()
    let earlierButton: UIButton
    let laterButton: UIButton
    private let resetButton = PlayerControlsView.pill(filled: false)
    private let doneButton = PlayerControlsView.pill(filled: true)
    /// The 50 ms jumps while a button is held, and when the last hold ended: the release that ends a hold isn't a press.
    private var repeatTimer: Timer?
    private var heldUntil = Date.distantPast

    override init(frame: CGRect) {
        #if os(tvOS)
        let symbolSize: CGFloat = 32
        #else
        let symbolSize: CGFloat = 20
        #endif
        earlierButton = PlayerControlsView.button("minus", size: symbolSize)
        laterButton = PlayerControlsView.button("plus", size: symbolSize)
        super.init(frame: frame)
        build()
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func configure(seconds: Double) {
        self.seconds = seconds
        showValue()
    }

    /// "+0.15 s", "−0.30 s", "0.00 s".
    static func label(_ seconds: Double) -> String {
        let amount = String(format: "%.2f s", abs(seconds))
        return seconds > 0.0005 ? "+" + amount : seconds < -0.0005 ? "−" + amount : amount
    }

    private func showValue() {
        valueLabel.text = Self.label(seconds)
        captionLabel.text = seconds > 0.0005 ? "Sound later" : seconds < -0.0005 ? "Sound earlier" : "As the file is"
    }

    private func nudge(_ change: Double?) {
        let next = change.map { ((seconds + $0) * 1000).rounded() / 1000 } ?? 0
        let clamped = max(-Self.limit, min(Self.limit, next))
        guard clamped != seconds else { return }
        seconds = clamped
        showValue()
        onChange?(clamped)
    }

    @objc private func held(_ hold: UILongPressGestureRecognizer) {
        let direction: Double = hold.view === earlierButton ? -1 : 1
        switch hold.state {
        case .began:
            repeatTimer?.invalidate()
            nudge(direction * Self.jump)
            repeatTimer = Timer.scheduledTimer(withTimeInterval: 0.25, repeats: true) { [weak self] timer in
                MainActor.assumeIsolated {
                    // Put away (Menu, a tap on the picture) with the button still down.
                    guard let self, !self.isHidden, self.window != nil else { return timer.invalidate() }
                    self.nudge(direction * Self.jump)
                }
            }
        case .ended, .cancelled, .failed:
            guard repeatTimer != nil else { return }
            repeatTimer?.invalidate()
            repeatTimer = nil
            heldUntil = Date().addingTimeInterval(0.3)
        default:
            break
        }
    }

    private func build() {
        #if os(tvOS)
        backgroundColor = UIColor.black.withAlphaComponent(0.55)
        layer.cornerRadius = 36
        titleLabel.font = .systemFont(ofSize: 26, weight: .semibold)
        valueLabel.font = .monospacedDigitSystemFont(ofSize: 44, weight: .bold)
        captionLabel.font = .systemFont(ofSize: 22, weight: .medium)
        let spacing: CGFloat = 24
        let insets = NSDirectionalEdgeInsets(top: 28, leading: 40, bottom: 28, trailing: 40)
        #else
        backgroundColor = UIColor.black.withAlphaComponent(0.6)
        layer.cornerRadius = 22
        titleLabel.font = .systemFont(ofSize: 13, weight: .semibold)
        valueLabel.font = .monospacedDigitSystemFont(ofSize: 24, weight: .bold)
        captionLabel.font = .systemFont(ofSize: 12, weight: .medium)
        let spacing: CGFloat = 10
        let insets = NSDirectionalEdgeInsets(top: 14, leading: 16, bottom: 14, trailing: 16)
        #endif
        layer.cornerCurve = .continuous
        layer.borderWidth = 1 / UIScreen.main.scale
        layer.borderColor = UIColor.white.withAlphaComponent(0.14).cgColor
        titleLabel.text = "Audio sync"
        titleLabel.textColor = UIColor.white.withAlphaComponent(0.7)
        valueLabel.textColor = .white
        valueLabel.textAlignment = .center
        captionLabel.textColor = UIColor.white.withAlphaComponent(0.6)
        for label in [titleLabel, valueLabel, captionLabel] { label.textAlignment = .center }

        earlierButton.accessibilityLabel = "Sound earlier"
        laterButton.accessibilityLabel = "Sound later"
        resetButton.configuration?.title = "Reset"
        doneButton.configuration?.title = "Done"
        for (button, direction) in [(earlierButton, -1.0), (laterButton, 1.0)] {
            button.addAction(UIAction { [weak self] _ in
                guard let self, Date() > heldUntil else { return }
                nudge(direction * Self.step)
            }, for: .primaryActionTriggered)
            let hold = UILongPressGestureRecognizer(target: self, action: #selector(held(_:)))
            hold.minimumPressDuration = 0.4
            #if os(tvOS)
            hold.allowedPressTypes = [NSNumber(value: UIPress.PressType.select.rawValue)]
            #endif
            button.addGestureRecognizer(hold)
        }
        resetButton.addAction(UIAction { [weak self] _ in self?.nudge(nil) }, for: .primaryActionTriggered)
        doneButton.addAction(UIAction { [weak self] _ in self?.onDone?() }, for: .primaryActionTriggered)

        let value = UIStackView(arrangedSubviews: [valueLabel, captionLabel])
        value.axis = .vertical
        value.spacing = 2
        #if os(tvOS)
        value.widthAnchor.constraint(greaterThanOrEqualToConstant: 220).isActive = true
        #else
        value.widthAnchor.constraint(greaterThanOrEqualToConstant: 110).isActive = true
        #endif
        let nudges = UIStackView(arrangedSubviews: [earlierButton, value, laterButton])
        nudges.axis = .horizontal
        nudges.alignment = .center
        nudges.spacing = spacing
        let actions = UIStackView(arrangedSubviews: [resetButton, doneButton])
        actions.axis = .horizontal
        actions.alignment = .center
        actions.spacing = spacing / 2
        let stack = UIStackView(arrangedSubviews: [titleLabel, nudges, actions])
        stack.axis = .vertical
        stack.alignment = .center
        stack.spacing = spacing
        stack.translatesAutoresizingMaskIntoConstraints = false
        addSubview(stack)
        NSLayoutConstraint.activate([
            stack.topAnchor.constraint(equalTo: topAnchor, constant: insets.top),
            stack.bottomAnchor.constraint(equalTo: bottomAnchor, constant: -insets.bottom),
            stack.leadingAnchor.constraint(equalTo: leadingAnchor, constant: insets.leading),
            stack.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -insets.trailing),
        ])
    }

    #if os(tvOS)
    override var preferredFocusEnvironments: [UIFocusEnvironment] { [laterButton] }
    #endif
}

/// The sleep timer running out over a video: the picture dims and "Still watching?" offers 15 minutes more (any
/// button on the remote, a tap or a shake on the phone) or turning the timer off. Once it has run out the dim stays
/// over the paused picture until someone touches something.
final class SleepPromptView: UIView {
    var onMore: (() -> Void)?
    var onCancel: (() -> Void)?

    private let dim = UIView()
    private let titleLabel = UILabel()
    private let detailLabel = UILabel()
    let moreButton = PlayerControlsView.pill(filled: true)
    private let cancelButton = PlayerControlsView.pill(filled: false)
    private let stack: UIStackView

    override init(frame: CGRect) {
        let buttons = UIStackView(arrangedSubviews: [cancelButton, moreButton])
        buttons.axis = .horizontal
        buttons.alignment = .center
        stack = UIStackView(arrangedSubviews: [titleLabel, detailLabel, buttons])
        super.init(frame: frame)
        #if os(tvOS)
        titleLabel.font = .systemFont(ofSize: 52, weight: .bold)
        detailLabel.font = .monospacedDigitSystemFont(ofSize: 28, weight: .medium)
        buttons.spacing = 24
        stack.spacing = 18
        stack.setCustomSpacing(44, after: detailLabel)
        #else
        titleLabel.font = .systemFont(ofSize: 26, weight: .bold)
        detailLabel.font = .monospacedDigitSystemFont(ofSize: 15, weight: .medium)
        buttons.spacing = 12
        stack.spacing = 8
        stack.setCustomSpacing(22, after: detailLabel)
        #endif
        dim.backgroundColor = .black
        dim.alpha = 0
        dim.isUserInteractionEnabled = false
        titleLabel.text = "Still watching?"
        titleLabel.textColor = .white
        detailLabel.textColor = UIColor.white.withAlphaComponent(0.75)
        moreButton.configuration?.title = "+15 min"
        moreButton.configuration?.image = UIImage(systemName: "bed.double")
        cancelButton.configuration?.title = "Cancel timer"
        moreButton.addAction(UIAction { [weak self] _ in self?.onMore?() }, for: .primaryActionTriggered)
        cancelButton.addAction(UIAction { [weak self] _ in self?.onCancel?() }, for: .primaryActionTriggered)
        stack.axis = .vertical
        stack.alignment = .center
        stack.alpha = 0
        for view in [dim, stack] as [UIView] {
            view.translatesAutoresizingMaskIntoConstraints = false
            addSubview(view)
        }
        NSLayoutConstraint.activate([
            dim.leadingAnchor.constraint(equalTo: leadingAnchor), dim.trailingAnchor.constraint(equalTo: trailingAnchor),
            dim.topAnchor.constraint(equalTo: topAnchor), dim.bottomAnchor.constraint(equalTo: bottomAnchor),
            stack.centerXAnchor.constraint(equalTo: centerXAnchor),
            stack.centerYAnchor.constraint(equalTo: centerYAnchor),
            stack.leadingAnchor.constraint(greaterThanOrEqualTo: leadingAnchor, constant: 20),
        ])
        isHidden = true
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    /// Only the buttons take touches: a tap anywhere else is the player's (which gives 15 minutes more).
    override func hitTest(_ point: CGPoint, with event: UIEvent?) -> UIView? {
        let hit = super.hitTest(point, with: event)
        var view = hit
        while let current = view, current !== self {
            if current is UIControl { return hit }
            view = current.superview
        }
        return nil
    }

    /// The last minute: the picture dims gently and the question comes up. `detail`: "Pausing in 0:42".
    func ask(detail: String) {
        detailLabel.text = detail
        guard stack.alpha == 0 || isHidden else { return }
        isHidden = false
        setButtonsEnabled(true)
        UIView.animate(withDuration: 3) { self.dim.alpha = 0.55 }
        UIView.animate(withDuration: 0.4) { self.stack.alpha = 1 }
        titleLabel.text = "Still watching?"
    }

    /// Run out: the picture is paused under the dim, and only a line says why.
    func sleep() {
        isHidden = false
        setButtonsEnabled(false)
        titleLabel.text = "Paused by the sleep timer"
        detailLabel.text = nil
        UIView.animate(withDuration: 1) {
            self.dim.alpha = 0.7
            self.stack.alpha = 0.6
        }
    }

    func dismiss() {
        guard !isHidden else { return }
        setButtonsEnabled(false)
        UIView.animate(withDuration: 0.4) {
            self.dim.alpha = 0
            self.stack.alpha = 0
        } completion: { _ in
            // Asked again meanwhile: stay.
            if self.stack.alpha == 0 { self.isHidden = true }
        }
    }

    var isAsking: Bool { !isHidden && moreButton.isEnabled }

    private func setButtonsEnabled(_ enabled: Bool) {
        for button in [moreButton, cancelButton] {
            button.isEnabled = enabled
            button.superview?.isHidden = !enabled
        }
    }

    #if os(tvOS)
    override var preferredFocusEnvironments: [UIFocusEnvironment] { [moreButton] }
    #endif
}
