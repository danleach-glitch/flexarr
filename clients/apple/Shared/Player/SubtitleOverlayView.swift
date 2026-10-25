#if canImport(AetherEngine)
import AetherEngine
import FlexarrKit
import UIKit

/// Subtitles the engine decodes, drawn over the video: text cues at the bottom (styled like Apple's own captions), and
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
    private let textLabel = UILabel()

    override init(frame: CGRect) {
        super.init(frame: frame)
        isUserInteractionEnabled = false
        textLabel.numberOfLines = 0
        textLabel.textAlignment = .center
        textLabel.textColor = .white
        textLabel.layer.shadowColor = UIColor.black.cgColor
        textLabel.layer.shadowOpacity = 0.9
        textLabel.layer.shadowRadius = 2
        textLabel.layer.shadowOffset = CGSize(width: 0, height: 1)
        addSubview(textLabel)
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

    /// Text at the bottom of the picture, at the profile's size: Apple's captions are about a twentieth of the picture's
    /// height on a television, and "Standard" matches that.
    private func layoutText(in video: CGRect) {
        guard !textCues.isEmpty else {
            textLabel.isHidden = true
            return
        }
        let base = max(14, video.height * 0.048)
        let points = base * CGFloat(subtitleSize.percent) / 100
        let text = NSMutableAttributedString()
        for (i, cue) in textCues.enumerated() {
            if i > 0 { text.append(NSAttributedString(string: "\n")) }
            text.append(Self.attributed(cue, size: points))
        }
        textLabel.attributedText = text
        textLabel.isHidden = false
        let width = video.width * 0.84
        let size = textLabel.sizeThatFits(CGSize(width: width, height: video.height))
        let bottom = video.maxY - max(video.height * 0.06, safeAreaInsets.bottom + 8)
        textLabel.frame = CGRect(x: video.midX - size.width / 2, y: bottom - size.height, width: size.width, height: size.height)
    }

    private static func attributed(_ cue: SubtitleCue, size: CGFloat) -> NSAttributedString {
        let paragraph = NSMutableParagraphStyle()
        paragraph.alignment = .center
        let plain: [NSAttributedString.Key: Any] = [
            .font: UIFont.systemFont(ofSize: size, weight: .medium), .foregroundColor: UIColor.white, .paragraphStyle: paragraph,
        ]
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
                if !traits.isEmpty, let descriptor = UIFont.systemFont(ofSize: size, weight: .medium).fontDescriptor.withSymbolicTraits(traits) {
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
#endif
