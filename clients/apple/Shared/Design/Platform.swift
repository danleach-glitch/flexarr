import SwiftUI
#if canImport(UIKit)
import UIKit

/// UIKit's image, view and colour on the iPhone and Apple TV; AppKit's on the Mac. Code that works the same with
/// either uses these names, so it's shared rather than written twice.
typealias PlatformImage = UIImage
typealias PlatformView = UIView
typealias PlatformColor = UIColor
typealias PlatformFont = UIFont

extension Image {
    init(platformImage image: PlatformImage) { self.init(uiImage: image) }
}

extension UIImage {
    /// Its pixels, as Core Graphics draws them.
    var platformCGImage: CGImage? { cgImage }
    /// Decoded ahead of drawing, so scrolling doesn't stall on it.
    func preparedForDisplay() async -> UIImage { await byPreparingForDisplay() ?? self }
    /// Its size in pixels.
    var pixelSize: CGSize { CGSize(width: size.width * scale, height: size.height * scale) }
}
#else
import AppKit

typealias PlatformImage = NSImage
typealias PlatformView = NSView
typealias PlatformColor = NSColor
typealias PlatformFont = NSFont

extension Image {
    init(platformImage image: PlatformImage) { self.init(nsImage: image) }
}

extension NSImage: @retroactive @unchecked Sendable {}

extension NSImage {
    var platformCGImage: CGImage? {
        var rect = CGRect(origin: .zero, size: size)
        return cgImage(forProposedRect: &rect, context: nil, hints: nil)
    }

    /// An NSImage decodes as it's first drawn: drawn here into a bitmap, off the main thread, so scrolling never waits
    /// for a JPEG. Artwork bigger than a 5K screen's width is drawn at that size.
    func preparedForDisplay() async -> NSImage {
        guard let cg = platformCGImage else { return self }
        let scale = min(1, 2880 / CGFloat(max(cg.width, 1)))
        let width = Int(CGFloat(cg.width) * scale), height = Int(CGFloat(cg.height) * scale)
        guard let context = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0,
                                      space: cg.colorSpace ?? CGColorSpace(name: CGColorSpace.sRGB)!,
                                      bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return self }
        context.interpolationQuality = .high
        context.draw(cg, in: CGRect(x: 0, y: 0, width: width, height: height))
        guard let decoded = context.makeImage() else { return self }
        return NSImage(cgImage: decoded, size: size)
    }

    var pixelSize: CGSize {
        guard let rep = representations.first, rep.pixelsWide > 0 else { return size }
        return CGSize(width: rep.pixelsWide, height: rep.pixelsHigh)
    }

    /// An SF Symbol by name, as UIImage(systemName:) gives one.
    convenience init?(systemName: String) {
        self.init(systemSymbolName: systemName, accessibilityDescription: nil)
    }
}
#endif

extension View {
    /// A list page on the Mac (Settings, Downloads, Coming soon's list): one centred column of readable width with
    /// the page margin either side, rows tall enough to breathe, icons spaced from their words. The phones' lists
    /// already have all of that; an iPad at regular width takes the same column width.
    /// `title` heads the column, and `parent` before it ("Settings › Server"): the Mac's window shows no title.
    @ViewBuilder
    func macReadable(title: String? = nil, parent: String? = nil) -> some View {
        #if os(macOS)
        VStack(alignment: .leading, spacing: 8) {
            if let title {
                HStack(alignment: .firstTextBaseline, spacing: 10) {
                    if let parent {
                        Text(parent).foregroundStyle(Color.usher)
                        Image(systemName: "chevron.forward").font(.system(size: 18, weight: .bold)).foregroundStyle(Color.usher)
                    }
                    Text(title).foregroundStyle(Color.screen)
                }
                .font(.marquee(34, weight: .bold, scales: false))
                .lineLimit(1)
                .padding(.top, 8)
                .accessibilityAddTraits(.isHeader)
            }
            self
                .environment(\.defaultMinListRowHeight, 46)
                .labelStyle(SpacedLabelStyle())
        }
        .frame(maxWidth: 760)
        .padding(.horizontal, Layout.edge)
        .frame(maxWidth: .infinity)
        // No band for the toolbar over a list page: back floats over the page.
        .toolbarBackgroundVisibility(.hidden, for: .windowToolbar)
        #elseif os(iOS)
        modifier(ReadableColumn())
        #else
        self
        #endif
    }
}

#if os(iOS)
/// An iPad's list page at regular width (landscape, the sidebar closed): rows no wider than the Mac's 760-point column,
/// centred, rather than a label at the far left and its value at the far right. The margins are the scroll view's, so
/// the page still scrolls from anywhere across it. An iPhone, or an iPad's narrow split, is as it was.
private struct ReadableColumn: ViewModifier {
    @Environment(\.horizontalSizeClass) private var sizeClass
    @State private var width: CGFloat = 0

    static let maxWidth: CGFloat = 760

    func body(content: Content) -> some View {
        content
            .contentMargins(.horizontal, sizeClass == .regular ? Self.margin(for: width) : 0, for: .scrollContent)
            .onGeometryChange(for: CGFloat.self) { $0.size.width } action: { width = $0 }
    }

    /// Either side of the column: whatever's left over once it's 760 wide.
    static func margin(for width: CGFloat) -> CGFloat {
        max(0, (width - maxWidth) / 2)
    }
}
#endif

#if os(macOS)
/// An icon then its words, with room between them.
struct SpacedLabelStyle: LabelStyle {
    func makeBody(configuration: Configuration) -> some View {
        HStack(spacing: 14) {
            configuration.icon
            configuration.title
        }
    }
}
#endif
