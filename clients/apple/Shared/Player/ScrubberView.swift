import FlexarrKit
import UIKit

/// The timeline: what's played, what's buffered, the intro and credits shaded, and a knob. Dragging (or, on Apple TV,
/// swiping on the remote while it's focused) scrubs with a preview thumbnail; letting go (or pressing) seeks there.
final class ScrubberView: UIView {
    var duration: Double = 0 { didSet { setNeedsLayout() } }
    var position: Double = 0 { didSet { if !isScrubbing { setNeedsLayout() } } }
    var buffered: Double = 0 { didSet { setNeedsLayout() } }
    var markers: [Marker] = [] { didSet { setNeedsLayout() } }
    var previews: TrickplayPreviews? {
        didSet { previews?.onLoaded = { [weak self] in self?.updateThumbnail() } }
    }
    /// Scrubbing began, moved (the position under the knob), and ended (nil: cancelled).
    var onScrubBegan: (() -> Void)?
    var onScrubMoved: ((Double) -> Void)?
    var onScrubEnded: ((Double?) -> Void)?

    private(set) var isScrubbing = false
    private(set) var scrubPosition: Double = 0

    private let track = CALayer()
    private let bufferedLayer = CALayer()
    private let played = CALayer()
    private var markerLayers: [CALayer] = []
    private let knob = CALayer()
    private let thumbnail = UIImageView()
    private let thumbnailTime = UILabel()
    private let thumbnailBox = UIView()

    #if os(tvOS)
    private let trackHeight: CGFloat = 10
    private let knobSize: CGFloat = 26
    private let thumbnailWidth: CGFloat = 360
    #else
    private let trackHeight: CGFloat = 5
    private let knobSize: CGFloat = 16
    private let thumbnailWidth: CGFloat = 160
    #endif

    override init(frame: CGRect) {
        super.init(frame: frame)
        isOpaque = false
        track.backgroundColor = UIColor.white.withAlphaComponent(0.25).cgColor
        bufferedLayer.backgroundColor = UIColor.white.withAlphaComponent(0.25).cgColor
        played.backgroundColor = UIColor.white.cgColor
        knob.backgroundColor = UIColor.white.cgColor
        knob.shadowColor = UIColor.black.cgColor
        knob.shadowOpacity = 0.5
        knob.shadowRadius = 3
        knob.shadowOffset = .zero
        for layer in [track, bufferedLayer, played, knob] {
            layer.cornerRadius = layer === knob ? knobSize / 2 : trackHeight / 2
            self.layer.addSublayer(layer)
        }
        thumbnailBox.backgroundColor = UIColor.black.withAlphaComponent(0.75)
        thumbnailBox.layer.cornerRadius = 12
        thumbnailBox.layer.cornerCurve = .continuous
        thumbnailBox.layer.borderWidth = 1
        thumbnailBox.layer.borderColor = UIColor.white.withAlphaComponent(0.25).cgColor
        thumbnailBox.clipsToBounds = true
        thumbnailBox.alpha = 0
        thumbnail.contentMode = .scaleAspectFill
        thumbnail.clipsToBounds = true
        thumbnailTime.textColor = .white
        thumbnailTime.textAlignment = .center
        thumbnailTime.font = .monospacedDigitSystemFont(ofSize: thumbnailWidth / 10, weight: .semibold)
        thumbnailBox.addSubview(thumbnail)
        thumbnailBox.addSubview(thumbnailTime)
        addSubview(thumbnailBox)

        #if os(tvOS)
        let pan = UIPanGestureRecognizer(target: self, action: #selector(panned))
        pan.allowedTouchTypes = [NSNumber(value: UITouch.TouchType.indirect.rawValue)]
        addGestureRecognizer(pan)
        #else
        let pan = UIPanGestureRecognizer(target: self, action: #selector(panned))
        addGestureRecognizer(pan)
        let tap = UITapGestureRecognizer(target: self, action: #selector(tapped))
        addGestureRecognizer(tap)
        #endif
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override var intrinsicContentSize: CGSize { CGSize(width: UIView.noIntrinsicMetric, height: knobSize + 8) }

    // MARK: Focus (Apple TV)

    #if os(tvOS)
    override var canBecomeFocused: Bool { true }

    override func didUpdateFocus(in context: UIFocusUpdateContext, with coordinator: UIFocusAnimationCoordinator) {
        super.didUpdateFocus(in: context, with: coordinator)
        coordinator.addCoordinatedAnimations { [self] in
            let focused = isFocused
            knob.transform = focused ? CATransform3DMakeScale(1.4, 1.4, 1) : CATransform3DIdentity
            knob.shadowColor = (focused ? UIColor.white : UIColor.black).cgColor
            knob.shadowRadius = focused ? 14 : 3
            knob.shadowOpacity = focused ? 0.9 : 0.5
            played.backgroundColor = (focused ? UIColor.white : UIColor.white.withAlphaComponent(0.85)).cgColor
            if !focused, isScrubbing { endScrub(cancelled: true) }
        }
    }

    /// Left and right on the remote while focused: the controller turns them into skips. A pan scrubs.
    @objc private func panned(_ pan: UIPanGestureRecognizer) {
        guard duration > 0 else { return }
        switch pan.state {
        case .began:
            beginScrub()
            pan.setTranslation(.zero, in: self)
        case .changed:
            // The whole track is the whole title, like Apple's player: a long swipe crosses it.
            let delta = pan.translation(in: self).x
            pan.setTranslation(.zero, in: self)
            moveScrub(to: scrubPosition + Double(delta / max(1, bounds.width)) * duration * 1.2)
        case .ended, .cancelled, .failed:
            break // Stays put until Select commits it (or the scrub is cancelled by leaving).
        default:
            break
        }
    }

    /// Select while scrubbing: seek there. Otherwise nothing (the screen handles presses).
    func commitScrubIfNeeded() -> Bool {
        guard isScrubbing else { return false }
        endScrub(cancelled: false)
        return true
    }

    func cancelScrubIfNeeded() -> Bool {
        guard isScrubbing else { return false }
        endScrub(cancelled: true)
        return true
    }
    #else
    @objc private func panned(_ pan: UIPanGestureRecognizer) {
        guard duration > 0 else { return }
        let x = pan.location(in: self).x
        switch pan.state {
        case .began:
            beginScrub()
            moveScrub(to: Double(x / max(1, bounds.width)) * duration)
        case .changed:
            moveScrub(to: Double(x / max(1, bounds.width)) * duration)
        case .ended:
            endScrub(cancelled: false)
        case .cancelled, .failed:
            endScrub(cancelled: true)
        default:
            break
        }
    }

    @objc private func tapped(_ tap: UITapGestureRecognizer) {
        guard duration > 0 else { return }
        beginScrub()
        moveScrub(to: Double(tap.location(in: self).x / max(1, bounds.width)) * duration)
        endScrub(cancelled: false)
    }
    #endif

    // MARK: Scrubbing

    private func beginScrub() {
        isScrubbing = true
        scrubPosition = position
        onScrubBegan?()
        UIView.animate(withDuration: 0.15) { self.thumbnailBox.alpha = 1 }
    }

    private func moveScrub(to seconds: Double) {
        scrubPosition = max(0, min(seconds, duration))
        onScrubMoved?(scrubPosition)
        setNeedsLayout()
        updateThumbnail()
    }

    private func endScrub(cancelled: Bool) {
        guard isScrubbing else { return }
        isScrubbing = false
        UIView.animate(withDuration: 0.15) { self.thumbnailBox.alpha = 0 }
        if !cancelled { position = scrubPosition }
        setNeedsLayout()
        onScrubEnded?(cancelled ? nil : scrubPosition)
    }

    private func updateThumbnail() {
        guard isScrubbing else { return }
        thumbnailTime.text = Self.clock(scrubPosition)
        thumbnail.image = previews?.image(at: scrubPosition)
        setNeedsLayout()
    }

    // MARK: Layout

    override func layoutSubviews() {
        super.layoutSubviews()
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        let width = bounds.width
        let midY = bounds.midY
        let fraction = { (seconds: Double) -> CGFloat in self.duration > 0 ? CGFloat(max(0, min(seconds / self.duration, 1))) : 0 }
        let shown = isScrubbing ? scrubPosition : position
        track.frame = CGRect(x: 0, y: midY - trackHeight / 2, width: width, height: trackHeight)
        bufferedLayer.frame = CGRect(x: 0, y: midY - trackHeight / 2, width: width * fraction(buffered), height: trackHeight)
        played.frame = CGRect(x: 0, y: midY - trackHeight / 2, width: width * fraction(shown), height: trackHeight)
        while markerLayers.count < markers.count {
            let layer = CALayer()
            layer.backgroundColor = UIColor.white.withAlphaComponent(0.35).cgColor
            layer.cornerRadius = trackHeight / 2
            self.layer.insertSublayer(layer, above: bufferedLayer)
            markerLayers.append(layer)
        }
        for (i, layer) in markerLayers.enumerated() {
            guard i < markers.count else { layer.isHidden = true; continue }
            let marker = markers[i]
            layer.isHidden = false
            layer.frame = CGRect(x: width * fraction(marker.start), y: midY - trackHeight / 2, width: width * (fraction(marker.end) - fraction(marker.start)), height: trackHeight)
        }
        let knobX = width * fraction(shown)
        knob.bounds = CGRect(x: 0, y: 0, width: knobSize, height: knobSize)
        knob.position = CGPoint(x: knobX, y: midY)

        let hasImage = thumbnail.image != nil && previews?.isAvailable == true
        let imageHeight = hasImage ? thumbnailWidth / (previews?.aspectRatio ?? 16 / 9) : 0
        let timeHeight = thumbnailTime.font.lineHeight + 8
        let boxWidth = hasImage ? thumbnailWidth : thumbnailTime.font.lineHeight * 4
        let boxHeight = imageHeight + timeHeight
        let boxX = max(0, min(knobX - boxWidth / 2, width - boxWidth))
        thumbnailBox.frame = CGRect(x: boxX, y: midY - knobSize - 12 - boxHeight, width: boxWidth, height: boxHeight)
        thumbnail.frame = CGRect(x: 0, y: 0, width: boxWidth, height: imageHeight)
        thumbnailTime.frame = CGRect(x: 0, y: imageHeight, width: boxWidth, height: timeHeight)
        CATransaction.commit()
    }

    override func point(inside point: CGPoint, with event: UIEvent?) -> Bool {
        // A comfortable band around the thin track.
        bounds.insetBy(dx: 0, dy: -12).contains(point)
    }

    /// "1:23:45" or "23:45".
    static func clock(_ seconds: Double) -> String {
        guard seconds.isFinite else { return "--:--" }
        let total = Int(seconds.rounded())
        let hours = total / 3600, minutes = (total / 60) % 60, secs = total % 60
        return hours > 0 ? String(format: "%d:%02d:%02d", hours, minutes, secs) : String(format: "%d:%02d", minutes, secs)
    }
}
