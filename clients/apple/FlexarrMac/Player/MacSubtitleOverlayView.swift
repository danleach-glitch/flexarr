#if canImport(AetherEngine)
import AetherEngine
import AppKit
import CoreText
import FlexarrKit
import MediaAccessibility

/// The Mac's subtitles over the engine's picture: the same placing as the iPhone's (Shared/Player/SubtitleOverlayView),
/// in AppKit. Text cues where the profile put them, in the caption style from System Settings › Accessibility ›
/// Captions, or where a styled (ASS) cue asks; picture subtitles (PGS, DVB) where the disc put them.
final class SubtitleOverlayView: NSView {
    var videoSize: CGSize = .zero {
        didSet { if videoSize != oldValue { needsLayout = true } }
    }

    var subtitleSize: SubtitleSize = .standard {
        didSet { if subtitleSize != oldValue { needsLayout = true } }
    }

    var placement = SubtitlePlacement() {
        didSet { if placement != oldValue { needsLayout = true } }
    }

    private var images: [SubtitleImage] = []
    private var imageViews: [NSImageView] = []
    private var textGroups: [(placement: SubtitleTextPlacement?, cues: [SubtitleCue])] = []
    private var captionWindows: [NSView] = []
    private var textLabels: [NSTextField] = []
    private var style = CaptionStyle.current

    // Top-left origin, as the iPhone's geometry is written.
    override var isFlipped: Bool { true }

    override init(frame: NSRect) {
        super.init(frame: frame)
        wantsLayer = true
        NotificationCenter.default.addObserver(self, selector: #selector(styleChanged),
                                               name: Notification.Name(kMACaptionAppearanceSettingsChangedNotification as String), object: nil)
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) is not used") }

    /// Clicks go through to the player underneath.
    override func hitTest(_ point: NSPoint) -> NSView? { nil }

    @objc private func styleChanged() {
        style = .current
        for (window, label) in zip(captionWindows, textLabels) { applyStyle(window: window, label: label) }
        needsLayout = true
    }

    private func applyStyle(window: NSView, label: NSTextField) {
        window.wantsLayer = true
        window.layer?.backgroundColor = style.window?.cgColor
        window.layer?.cornerRadius = style.window == nil ? 0 : 4
        let shadow = NSShadow()
        shadow.shadowColor = NSColor.black.withAlphaComponent(0.9)
        switch style.edge {
        case .none:
            label.shadow = nil
            return
        case .raised:
            shadow.shadowBlurRadius = 0; shadow.shadowOffset = CGSize(width: 0, height: 1.5)
        case .depressed:
            shadow.shadowBlurRadius = 0; shadow.shadowOffset = CGSize(width: 0, height: -1.5)
        case .uniform:
            shadow.shadowColor = .black; shadow.shadowBlurRadius = 1.5; shadow.shadowOffset = .zero
        default:
            shadow.shadowBlurRadius = 2; shadow.shadowOffset = CGSize(width: 0, height: -1)
        }
        label.shadow = shadow
    }

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
            let window = NSView()
            let label = NSTextField(labelWithString: "")
            label.maximumNumberOfLines = 0
            label.lineBreakMode = .byWordWrapping
            label.drawsBackground = false
            window.addSubview(label)
            addSubview(window)
            applyStyle(window: window, label: label)
            captionWindows.append(window)
            textLabels.append(label)
        }
        while imageViews.count < images.count {
            let view = NSImageView()
            view.imageScaling = .scaleAxesIndependently
            addSubview(view)
            imageViews.append(view)
        }
        needsLayout = true
    }

    private var videoRect: CGRect {
        guard videoSize.width > 0, videoSize.height > 0, bounds.width > 0, bounds.height > 0 else { return bounds }
        let scale = min(bounds.width / videoSize.width, bounds.height / videoSize.height)
        let size = CGSize(width: videoSize.width * scale, height: videoSize.height * scale)
        return CGRect(x: (bounds.width - size.width) / 2, y: (bounds.height - size.height) / 2, width: size.width, height: size.height)
    }

    override func layout() {
        super.layout()
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
            view.image = NSImage(cgImage: image.cgImage, size: image.position.size)
            view.frame = CGRect(x: video.minX + image.position.minX * scale, y: top + image.position.minY * scale,
                                width: image.position.width * scale, height: image.position.height * scale)
            view.isHidden = false
        }
        layoutText(in: video)
    }

    private static func ownPlacement(_ cue: SubtitleCue) -> SubtitleTextPlacement? {
        guard let placement = cue.placement, placement.position != nil || (placement.alignment ?? 2) != 2 else { return nil }
        return placement
    }

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
            let column = ((group.placement?.alignment ?? 2) - 1) % 3
            let alignment: NSTextAlignment = column == 0 ? .left : column == 2 ? .right : .center
            let text = NSMutableAttributedString()
            for (j, cue) in group.cues.enumerated() {
                if j > 0 { text.append(NSAttributedString(string: "\n")) }
                text.append(Self.attributed(cue, size: points, style: style, alignment: alignment))
            }
            label.attributedStringValue = text
            window.isHidden = false
            let width = video.width * 0.84 - padding * 2
            label.preferredMaxLayoutWidth = width
            let size = label.sizeThatFits(CGSize(width: width, height: video.height))
            let boxed = CGSize(width: size.width + padding * 2, height: size.height + padding * 2)
            let origin = group.placement.map { Self.origin(of: boxed, placedBy: $0, in: video) } ?? origin(of: boxed, in: video)
            window.frame = CGRect(origin: origin, size: boxed)
            label.frame = CGRect(x: padding, y: padding, width: size.width, height: size.height)
        }
    }

    private func origin(of box: CGSize, in video: CGRect) -> CGPoint {
        let x = video.midX - box.width / 2
        let bar = bounds.maxY - video.maxY
        if placement.position == .top {
            return CGPoint(x: x, y: video.minY + max(video.height * 0.06, 8))
        }
        if placement.inBlackBars, bar >= box.height + 12 {
            return CGPoint(x: x, y: video.maxY + 6)
        }
        let lift = placement.position == .bottom || placement.position == .unknown ? 0.06 : placement.position.lift ?? 0.06
        return CGPoint(x: x, y: video.maxY - max(video.height * lift, 8) - box.height)
    }

    private static func origin(of box: CGSize, placedBy placement: SubtitleTextPlacement, in video: CGRect) -> CGPoint {
        let alignment = min(9, max(1, placement.alignment ?? 2))
        let column = CGFloat((alignment - 1) % 3) / 2
        let row: CGFloat = alignment >= 7 ? 0 : alignment >= 4 ? 0.5 : 1
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

    private static func attributed(_ cue: SubtitleCue, size: CGFloat, style: CaptionStyle, alignment: NSTextAlignment) -> NSAttributedString {
        let paragraph = NSMutableParagraphStyle()
        paragraph.alignment = alignment
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
                var traits: NSFontDescriptor.SymbolicTraits = []
                if run.isBold { traits.insert(.bold) }
                if run.isItalic { traits.insert(.italic) }
                if !traits.isEmpty {
                    let descriptor = style.font(ofSize: size).fontDescriptor.withSymbolicTraits(traits)
                    attributes[.font] = NSFont(descriptor: descriptor, size: size)
                }
                if let color = run.color {
                    attributes[.foregroundColor] = NSColor(red: CGFloat(color.r) / 255, green: CGFloat(color.g) / 255, blue: CGFloat(color.b) / 255, alpha: 1)
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
#endif
