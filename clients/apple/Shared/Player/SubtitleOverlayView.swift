#if canImport(AetherEngine)
import AetherEngine
import CoreText
import FlexarrKit
import MediaAccessibility
import UIKit

/// Subtitles the engine decodes, drawn over the video: text cues at the bottom (in the caption style chosen in the
/// system's Accessibility settings, as Apple's own player draws them), and
/// picture subtitles (PGS, DVB) placed as the disc authored them, the cue's canvas mapped width-aligned and
/// centre-anchored onto the video's rectangle (the picture fitted into this view).
final class SubtitleOverlayView: UIView {
    /// The picture's size in pixels, for placing cues over it.
    var videoSize: CGSize = .zero {
        didSet { if videoSize != oldValue { setNeedsLayout() } }
    }

    var subtitleSize: SubtitleSize = .standard {
        didSet { if subtitleSize != oldValue { setNeedsLayout() } }
    }

    private var images: [SubtitleImage] = []
    private var imageViews: [UIImageView] = []
    private var textCues: [SubtitleCue] = []
    /// The window behind the text (the caption style's window colour), and the text in it.
    private let captionWindow = UIView()
    private let textLabel = UILabel()
    private var style = CaptionStyle.current

    override init(frame: CGRect) {
        super.init(frame: frame)
        isUserInteractionEnabled = false
        textLabel.numberOfLines = 0
        textLabel.textAlignment = .center
        captionWindow.isHidden = true
        captionWindow.addSubview(textLabel)
        addSubview(captionWindow)
        applyStyle()
        NotificationCenter.default.addObserver(self, selector: #selector(styleChanged),
                                               name: Notification.Name(kMACaptionAppearanceSettingsChangedNotification as String), object: nil)
    }

    @objc private func styleChanged() {
        style = .current
        applyStyle()
        setNeedsLayout()
    }

    /// The window's colour and the text's edge, from the caption style.
    private func applyStyle() {
        captionWindow.backgroundColor = style.window
        captionWindow.layer.cornerRadius = style.window == nil ? 0 : 4
        let layer = textLabel.layer
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
        textCues = cues.filter { if case .image = $0.body { false } else { true } }
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

    /// Text at the bottom of the picture. Apple's captions are about 4.3% of the picture's height at the system's
    /// standard size; the caption style's text size multiplies that, and the profile's size multiplies it again.
    private func layoutText(in video: CGRect) {
        guard !textCues.isEmpty else {
            captionWindow.isHidden = true
            return
        }
        let base = max(14, video.height * 0.043)
        let points = base * style.scale * CGFloat(subtitleSize.percent) / 100
        let text = NSMutableAttributedString()
        for (i, cue) in textCues.enumerated() {
            if i > 0 { text.append(NSAttributedString(string: "\n")) }
            text.append(Self.attributed(cue, size: points, style: style))
        }
        textLabel.attributedText = text
        captionWindow.isHidden = false
        let padding = style.window == nil ? 0 : points * 0.3
        let width = video.width * 0.84 - padding * 2
        let size = textLabel.sizeThatFits(CGSize(width: width, height: video.height))
        let bottom = video.maxY - max(video.height * 0.06, safeAreaInsets.bottom + 8)
        let boxed = CGSize(width: size.width + padding * 2, height: size.height + padding * 2)
        captionWindow.frame = CGRect(x: video.midX - boxed.width / 2, y: bottom - boxed.height, width: boxed.width, height: boxed.height)
        textLabel.frame = CGRect(x: padding, y: padding, width: size.width, height: size.height)
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
