#if canImport(AetherEngine)
import AetherEngine
import CoreText
import FlexarrKit
import MediaAccessibility
import UIKit

/// Subtitles the engine decodes, drawn over the video: text cues where the profile put them (in the caption style
/// chosen in the system's Accessibility settings, as Apple's own player draws them), or where a styled (ASS) cue asks
/// to be, and picture subtitles (PGS, DVB) placed as the disc authored them, the cue's canvas mapped width-aligned and
/// centre-anchored onto the video's rectangle (the picture fitted into this view).
final class SubtitleOverlayView: UIView {
    /// The picture's size in pixels, for placing cues over it.
    var videoSize: CGSize = .zero {
        didSet { if videoSize != oldValue { setNeedsLayout() } }
    }

    var subtitleSize: SubtitleSize = .standard {
        didSet { if subtitleSize != oldValue { setNeedsLayout() } }
    }

    var placement = SubtitlePlacement() {
        didSet { if placement != oldValue { setNeedsLayout() } }
    }

    private var images: [SubtitleImage] = []
    private var imageViews: [UIImageView] = []
    /// Text cues in groups drawn together: the ones the profile places, then each one placed by its own ASS tags.
    private var textGroups: [(placement: SubtitleTextPlacement?, cues: [SubtitleCue])] = []
    /// A window behind each group's text (the caption style's window colour), and the text in it.
    private var captionWindows: [UIView] = []
    private var textLabels: [UILabel] = []
    private var style = CaptionStyle.current

    override init(frame: CGRect) {
        super.init(frame: frame)
        isUserInteractionEnabled = false
        applyStyle()
        NotificationCenter.default.addObserver(self, selector: #selector(styleChanged),
                                               name: Notification.Name(kMACaptionAppearanceSettingsChangedNotification as String), object: nil)
    }

    @objc private func styleChanged() {
        style = .current
        applyStyle()
        setNeedsLayout()
    }

    /// The windows' colour and the text's edge, from the caption style.
    private func applyStyle() {
        for (window, label) in zip(captionWindows, textLabels) {
            applyStyle(window: window, label: label)
        }
    }

    private func applyStyle(window: UIView, label: UILabel) {
        window.backgroundColor = style.window
        window.layer.cornerRadius = style.window == nil ? 0 : 4
        let layer = label.layer
        layer.shadowColor = UIColor.black.cgColor
        switch style.edge {
        case .none:
            layer.shadowOpacity = 0
        case .raised:
            layer.shadowOpacity = 0.9; layer.shadowRadius = 0; layer.shadowOffset = CGSize(width: 0, height: -1.5)
        case .depressed:
            layer.shadowOpacity = 0.9; layer.shadowRadius = 0; layer.shadowOffset = CGSize(width: 0, height: 1.5)
        case .uniform:
            layer.shadowOpacity = 1; layer.shadowRadius = 1.5; layer.shadowOffset = .zero
        default: // a drop shadow, Apple's default
            layer.shadowOpacity = 0.9; layer.shadowRadius = 2; layer.shadowOffset = CGSize(width: 0, height: 1)
        }
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func show(_ cues: [SubtitleCue]) {
        images = cues.compactMap { cue in
            if case .image(let image) = cue.body { return image }
            return nil
        }
        let text = cues.filter { if case .image = $0.body { false } else { true } }
        let placed = text.filter { Self.ownPlacement($0) != nil }
        textGroups = (text.count > placed.count ? [(nil, text.filter { Self.ownPlacement($0) == nil })] : [])
            + placed.map { (Self.ownPlacement($0), [$0]) }
        while captionWindows.count < textGroups.count {
            let window = UIView()
            let label = UILabel()
            label.numberOfLines = 0
            window.addSubview(label)
            addSubview(window)
            applyStyle(window: window, label: label)
            captionWindows.append(window)
            textLabels.append(label)
        }
        while imageViews.count < images.count {
            let view = UIImageView()
            view.contentMode = .scaleToFill
            addSubview(view)
            imageViews.append(view)
        }
        setNeedsLayout()
    }

    private var videoRect: CGRect {
        guard videoSize.width > 0, videoSize.height > 0, bounds.width > 0, bounds.height > 0 else { return bounds }
        let scale = min(bounds.width / videoSize.width, bounds.height / videoSize.height)
        let size = CGSize(width: videoSize.width * scale, height: videoSize.height * scale)
        return CGRect(x: (bounds.width - size.width) / 2, y: (bounds.height - size.height) / 2, width: size.width, height: size.height)
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        let video = videoRect
        for (i, view) in imageViews.enumerated() {
            guard i < images.count else {
                view.isHidden = true
                continue
            }
            let image = images[i]
            let canvas = image.canvasSize == .zero ? CGSize(width: image.position.maxX, height: image.position.maxY) : image.canvasSize
            let scale = canvas.width > 0 ? video.width / canvas.width : 1
            let top = video.minY + (video.height - canvas.height * scale) / 2
            view.image = UIImage(cgImage: image.cgImage)
            view.frame = CGRect(x: video.minX + image.position.minX * scale, y: top + image.position.minY * scale,
                                width: image.position.width * scale, height: image.position.height * scale)
            view.isHidden = false
        }
        layoutText(in: video)
    }

    /// A styled cue's own place, from its ASS tags; nil when it has none worth following. Bottom-centre alignment
    /// without a position is where every line goes anyway, so the profile's position applies to it too.
    private static func ownPlacement(_ cue: SubtitleCue) -> SubtitleTextPlacement? {
        guard let placement = cue.placement, placement.position != nil || (placement.alignment ?? 2) != 2 else { return nil }
        return placement
    }

    /// Text where the profile put it, or where a styled cue asks. Apple's captions are about 4.3% of the picture's
    /// height at the system's standard size; the caption style's text size multiplies that, and the profile's size
    /// multiplies it again.
    private func layoutText(in video: CGRect) {
        let base = max(14, video.height * 0.043)
        let points = base * style.scale * CGFloat(subtitleSize.percent) / 100
        let padding = style.window == nil ? 0 : points * 0.3
        for (i, window) in captionWindows.enumerated() {
            guard i < textGroups.count else {
                window.isHidden = true
                continue
            }
            let group = textGroups[i]
            let label = textLabels[i]
            let text = NSMutableAttributedString()
            for (j, cue) in group.cues.enumerated() {
                if j > 0 { text.append(NSAttributedString(string: "\n")) }
                text.append(Self.attributed(cue, size: points, style: style))
            }
            let column = ((group.placement?.alignment ?? 2) - 1) % 3 // 0 left, 1 centre, 2 right
            label.textAlignment = column == 0 ? .left : column == 2 ? .right : .center
            label.attributedText = text
            window.isHidden = false
            let size = label.sizeThatFits(CGSize(width: video.width * 0.84 - padding * 2, height: video.height))
            let boxed = CGSize(width: size.width + padding * 2, height: size.height + padding * 2)
            let origin = group.placement.map { Self.origin(of: boxed, placedBy: $0, in: video) }
                ?? origin(of: boxed, in: video)
            window.frame = CGRect(origin: origin, size: boxed)
            label.frame = CGRect(x: padding, y: padding, width: size.width, height: size.height)
        }
    }

    /// Where the profile's subtitles go: lifted above the picture's foot (clear of the screen's edge), hung from its
    /// top, or in the black bar below a wide picture when that's wanted and the text fits there.
    private func origin(of box: CGSize, in video: CGRect) -> CGPoint {
        let x = video.midX - box.width / 2
        let bar = bounds.maxY - video.maxY - safeAreaInsets.bottom
        if placement.position == .top {
            return CGPoint(x: x, y: video.minY + max(video.height * 0.06, safeAreaInsets.top + 8))
        }
        if placement.inBlackBars, bar >= box.height + 12 {
            return CGPoint(x: x, y: video.maxY + 6)
        }
        // At the bottom, where Apple's own player puts captions; raised, as far up as the web player puts them.
        let lift = placement.position == .bottom || placement.position == .unknown ? 0.06 : placement.position.lift ?? 0.06
        return CGPoint(x: x, y: video.maxY - max(video.height * lift, safeAreaInsets.bottom + 8) - box.height)
    }

    /// Where a styled cue asks to be: anchored by its alignment at its \pos point, or else against the picture's edge
    /// that alignment names, kept on the picture.
    private static func origin(of box: CGSize, placedBy placement: SubtitleTextPlacement, in video: CGRect) -> CGPoint {
        let alignment = min(9, max(1, placement.alignment ?? 2))
        let column = CGFloat((alignment - 1) % 3) / 2 // 0, 0.5, 1: the share of the box left of its anchor
        let row: CGFloat = alignment >= 7 ? 0 : alignment >= 4 ? 0.5 : 1 // the share above it
        let anchor: CGPoint
        if let position = placement.position {
            anchor = CGPoint(x: video.minX + position.x * video.width, y: video.minY + position.y * video.height)
        } else {
            let margin = CGSize(width: video.width * 0.04, height: video.height * 0.06)
            anchor = CGPoint(x: video.minX + margin.width + column * (video.width - margin.width * 2),
                             y: video.minY + margin.height + row * (video.height - margin.height * 2))
        }
        let x = min(max(anchor.x - column * box.width, video.minX), video.maxX - box.width)
        let y = min(max(anchor.y - row * box.height, video.minY), video.maxY - box.height)
        return CGPoint(x: x, y: y)
    }

    private static func attributed(_ cue: SubtitleCue, size: CGFloat, style: CaptionStyle) -> NSAttributedString {
        let paragraph = NSMutableParagraphStyle()
        paragraph.alignment = .center
        var plain: [NSAttributedString.Key: Any] = [
            .font: style.font(ofSize: size), .foregroundColor: style.text, .paragraphStyle: paragraph,
        ]
        if let background = style.background { plain[.backgroundColor] = background }
        switch cue.body {
        case .text(let string):
            return NSAttributedString(string: string, attributes: plain)
        case .richText(let runs):
            let text = NSMutableAttributedString()
            for run in runs {
                var attributes = plain
                var traits: UIFontDescriptor.SymbolicTraits = []
                if run.isBold { traits.insert(.traitBold) }
                if run.isItalic { traits.insert(.traitItalic) }
                if !traits.isEmpty, let descriptor = style.font(ofSize: size).fontDescriptor.withSymbolicTraits(traits) {
                    attributes[.font] = UIFont(descriptor: descriptor, size: size)
                }
                if let color = run.color {
                    attributes[.foregroundColor] = UIColor(red: CGFloat(color.r) / 255, green: CGFloat(color.g) / 255, blue: CGFloat(color.b) / 255, alpha: 1)
                }
                if run.isUnderlined { attributes[.underlineStyle] = NSUnderlineStyle.single.rawValue }
                if run.isStruckThrough { attributes[.strikethroughStyle] = NSUnderlineStyle.single.rawValue }
                text.append(NSAttributedString(string: run.text, attributes: attributes))
            }
            return text
        case .image:
            return NSAttributedString()
        }
    }
}

/// The caption style the viewer chose in the system's Accessibility settings (Subtitles and Captioning): font, text
/// size, colours and opacities, and the text's edge. Apple's own player draws captions this way; public API only.
struct CaptionStyle {
    var fontDescriptor: CTFontDescriptor?
    /// The style's text size as a multiplier of the standard size.
    var scale: CGFloat
    var text: UIColor
    /// Behind the text itself, if the style has one.
    var background: UIColor?
    /// Behind the whole caption, if the style has one.
    var window: UIColor?
    var edge: MACaptionAppearanceTextEdgeStyle

    static var current: CaptionStyle {
        let domain = MACaptionAppearanceDomain.user
        func color(_ copy: (MACaptionAppearanceDomain, UnsafeMutablePointer<MACaptionAppearanceBehavior>?) -> Unmanaged<CGColor>,
                   _ opacity: (MACaptionAppearanceDomain, UnsafeMutablePointer<MACaptionAppearanceBehavior>?) -> CGFloat) -> UIColor? {
            let alpha = opacity(domain, nil)
            guard alpha > 0.01 else { return nil }
            return UIColor(cgColor: copy(domain, nil).takeRetainedValue()).withAlphaComponent(alpha)
        }
        let scale = MACaptionAppearanceGetRelativeCharacterSize(domain, nil)
        return CaptionStyle(
            fontDescriptor: MACaptionAppearanceCopyFontDescriptorForStyle(domain, nil, .default).takeRetainedValue(),
            scale: scale > 0 ? scale : 1,
            text: color(MACaptionAppearanceCopyForegroundColor, MACaptionAppearanceGetForegroundOpacity) ?? .white,
            background: color(MACaptionAppearanceCopyBackgroundColor, MACaptionAppearanceGetBackgroundOpacity),
            window: color(MACaptionAppearanceCopyWindowColor, MACaptionAppearanceGetWindowOpacity),
            edge: MACaptionAppearanceGetTextEdgeStyle(domain, nil)
        )
    }

    func font(ofSize size: CGFloat) -> UIFont {
        guard let fontDescriptor else { return .systemFont(ofSize: size, weight: .medium) }
        return CTFontCreateWithFontDescriptor(fontDescriptor, size, nil) as UIFont
    }
}
#endif
