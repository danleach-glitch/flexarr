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

    /// An NSImage decodes as it's first drawn; drawing it once off the main thread here keeps that out of scrolling.
    func preparedForDisplay() async -> NSImage {
        guard let cg = platformCGImage else { return self }
        return NSImage(cgImage: cg, size: size)
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
