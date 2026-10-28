import UIKit

/// The colours of a cover, for the visualiser: its few strongest hues, each lifted to a level that glows on a dark room.
/// A grey or black-and-white cover has none to speak of, so the track's own glow colour stands in. The web app picks
/// them the same way (frontend/src/lib/palette.ts).
enum CoverPalette {
    struct RGB: Equatable, Sendable {
        var r: Double
        var g: Double
        var b: Double
    }

    /// Up to four colours from `image`, filled out with neighbouring hues to `count`.
    static func colours(of image: UIImage?, fallback: RGB, count: Int = 4) -> [RGB] {
        let read = image.map { strongest(in: pixels(of: $0), count: count) } ?? []
        return fill(read.isEmpty ? [glow(fallback)] : read, to: count)
    }

    static func strongest(in pixels: [UInt8], count: Int) -> [RGB] {
        var bins = Array(repeating: (weight: 0.0, r: 0.0, g: 0.0, b: 0.0), count: 12)
        var i = 0
        while i + 3 < pixels.count {
            defer { i += 4 }
            guard pixels[i + 3] >= 128 else { continue }
            let rgb = RGB(r: Double(pixels[i]), g: Double(pixels[i + 1]), b: Double(pixels[i + 2]))
            let (h, s, l) = hsl(rgb)
            // Near-black, near-white and grey say nothing about the cover's colour.
            guard s >= 0.18, l >= 0.08, l <= 0.94 else { continue }
            let weight = s * (1 - abs(l - 0.5))
            let bin = Int(h / 30) % 12
            bins[bin].weight += weight
            bins[bin].r += rgb.r * weight
            bins[bin].g += rgb.g * weight
            bins[bin].b += rgb.b * weight
        }
        let total = bins.reduce(0) { $0 + $1.weight }
        guard total > 0 else { return [] }
        return bins
            .filter { $0.weight / total > 0.04 }
            .sorted { $0.weight > $1.weight }
            .prefix(count)
            .map { glow(RGB(r: $0.r / $0.weight, g: $0.g / $0.weight, b: $0.b / $0.weight)) }
    }

    /// Brings a colour into the range that reads as light in a dark room: not muddy, not white.
    static func glow(_ rgb: RGB) -> RGB {
        let (h, s, l) = hsl(rgb)
        return fromHSL(h, min(0.9, max(0.5, s)), min(0.66, max(0.42, l)))
    }

    private static func fill(_ colours: [RGB], to count: Int) -> [RGB] {
        var out = colours
        var i = 0
        while out.count < count {
            let (h, s, l) = hsl(colours[i % colours.count])
            let turn = Double(i % 2 == 0 ? 1 : -1) * (28 + 14 * Double(i / 2))
            out.append(glow(fromHSL((h + turn + 360).truncatingRemainder(dividingBy: 360), s, l)))
            i += 1
        }
        return out
    }

    /// The image drawn into 32 × 32 RGBA pixels.
    private static func pixels(of image: UIImage) -> [UInt8] {
        guard let cg = image.cgImage else { return [] }
        let side = 32
        var data = [UInt8](repeating: 0, count: side * side * 4)
        let drawn = data.withUnsafeMutableBytes { bytes -> Bool in
            guard let context = CGContext(data: bytes.baseAddress, width: side, height: side, bitsPerComponent: 8, bytesPerRow: side * 4,
                                          space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)
            else { return false }
            context.interpolationQuality = .medium
            context.draw(cg, in: CGRect(x: 0, y: 0, width: side, height: side))
            return true
        }
        return drawn ? data : []
    }

    private static func hsl(_ c: RGB) -> (Double, Double, Double) {
        let r = c.r / 255, g = c.g / 255, b = c.b / 255
        let high = max(r, g, b), low = min(r, g, b)
        let l = (high + low) / 2
        guard high != low else { return (0, 0, l) }
        let d = high - low
        let s = l > 0.5 ? d / (2 - high - low) : d / (high + low)
        let h: Double = high == r ? (g - b) / d + (g < b ? 6 : 0) : high == g ? (b - r) / d + 2 : (r - g) / d + 4
        return (h * 60, s, l)
    }

    private static func fromHSL(_ h: Double, _ s: Double, _ l: Double) -> RGB {
        let a = s * min(l, 1 - l)
        func f(_ n: Double) -> Double {
            let k = (n + h / 30).truncatingRemainder(dividingBy: 12)
            return (l - a * max(-1, min(k - 3, min(9 - k, 1)))) * 255
        }
        return RGB(r: f(0), g: f(8), b: f(4))
    }
}
