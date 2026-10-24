// Renders the brand assets for Flexarr: the tvOS layered app icon and top shelf, and the iPhone/iPad app icon.
// usage (from clients/apple): swift Tools/make-icons.swift FlexarrTV/Assets.xcassets FlexarriOS/Assets.xcassets
import AppKit
import Foundation

let root = URL(fileURLWithPath: CommandLine.arguments[1])
let brand = root.appending(path: "App Icon & Top Shelf Image.brandassets")
let fm = FileManager.default
try? fm.removeItem(at: brand)

func color(_ hex: UInt32, _ alpha: CGFloat = 1) -> NSColor {
    NSColor(srgbRed: CGFloat((hex >> 16) & 0xFF) / 255, green: CGFloat((hex >> 8) & 0xFF) / 255, blue: CGFloat(hex & 0xFF) / 255, alpha: alpha)
}
let room = color(0x1B1720), screen = color(0xF2EEE7), silver = color(0xA9C2EC), plum = color(0x4A3560), rose = color(0xE8A0BF)

func png(width: Int, height: Int, jpeg: Bool = false, draw: (CGContext, CGSize) -> Void) -> Data {
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: width, pixelsHigh: height, bitsPerSample: 8,
                               samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB,
                               bytesPerRow: 0, bitsPerPixel: 0)!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
    let ctx = NSGraphicsContext.current!.cgContext
    ctx.clear(CGRect(x: 0, y: 0, width: width, height: height))
    draw(ctx, CGSize(width: width, height: height))
    NSGraphicsContext.restoreGraphicsState()
    return jpeg ? rep.representation(using: .jpeg, properties: [.compressionFactor: 0.88])! : rep.representation(using: .png, properties: [:])!
}

/// The dim plum room with ambient light washing in from the lower left, like bias lighting.
func background(_ ctx: CGContext, _ size: CGSize, opaque: Bool = true) {
    ctx.setFillColor(room.cgColor)
    ctx.fill(CGRect(origin: .zero, size: size))
    let space = CGColorSpaceCreateDeviceRGB()
    let glow = CGGradient(colorsSpace: space, colors: [silver.withAlphaComponent(0.42).cgColor, plum.withAlphaComponent(0.25).cgColor, room.withAlphaComponent(0).cgColor] as CFArray, locations: [0, 0.45, 1])!
    ctx.drawRadialGradient(glow, startCenter: CGPoint(x: size.width * 0.18, y: size.height * 0.05), startRadius: 0,
                           endCenter: CGPoint(x: size.width * 0.18, y: size.height * 0.05), endRadius: max(size.width, size.height) * 0.9, options: [])
    let warm = CGGradient(colorsSpace: space, colors: [color(0x8E5A9E, 0.28).cgColor, room.withAlphaComponent(0).cgColor] as CFArray, locations: [0, 1])!
    ctx.drawRadialGradient(warm, startCenter: CGPoint(x: size.width * 0.95, y: size.height), startRadius: 0,
                           endCenter: CGPoint(x: size.width * 0.95, y: size.height), endRadius: size.width * 0.6, options: [])
}

/// The marquee wordmark: tall, condensed, heavy.
/// The Flexarr mark, three stacked bars (the web favicon's geometry, a 64-unit square), drawn in `square`.
func mark(_ ctx: CGContext, in square: CGRect) {
    ctx.saveGState()
    // The favicon's coordinates run top-down; flip into Core Graphics' bottom-up space and scale 64 units to the square.
    ctx.translateBy(x: square.minX, y: square.maxY)
    ctx.scaleBy(x: square.width / 64, y: -square.height / 64)
    let bars: [(x: CGFloat, y: CGFloat, width: CGFloat, color: NSColor, tilt: CGFloat)] = [
        (14, 15, 36, screen, -5), (14, 28.5, 30, silver, 0), (14, 42, 23, rose, 0),
    ]
    for bar in bars {
        ctx.saveGState()
        ctx.translateBy(x: 14, y: 20)
        ctx.rotate(by: bar.tilt * .pi / 180)
        ctx.translateBy(x: -14, y: -20)
        ctx.setFillColor(bar.color.cgColor)
        ctx.addPath(CGPath(roundedRect: CGRect(x: bar.x, y: bar.y, width: bar.width, height: 10), cornerWidth: 5, cornerHeight: 5, transform: nil))
        ctx.fillPath()
        ctx.restoreGState()
    }
    ctx.restoreGState()
}

/// The mark centred on the canvas at `fraction` of its height; with `withName`, the wordmark beside it (Top Shelf only:
/// the app icon is the mark alone).
func logo(_ ctx: CGContext, _ size: CGSize, height fraction: CGFloat, withName: Bool) {
    let side = size.height * fraction
    if !withName {
        // The bars span x 14–50 of their 64-unit square, so the square is nudged to centre what's visible.
        mark(ctx, in: CGRect(x: (size.width - side) / 2 - side * 0.0, y: (size.height - side) / 2 + side * 0.02, width: side, height: side))
        return
    }
    let fontSize = side * 0.62
    let font = NSFont.systemFont(ofSize: fontSize, weight: .black, width: .condensed)
    let name = NSAttributedString(string: "flexarr", attributes: [.font: font, .foregroundColor: screen, .kern: -fontSize * 0.01])
    let nameSize = name.size()
    let markWidth = side * 36 / 64
    let gap = side * 0.12
    let left = (size.width - markWidth - gap - nameSize.width) / 2
    mark(ctx, in: CGRect(x: left - side * 14 / 64, y: (size.height - side) / 2 + side * 0.02, width: side, height: side))
    name.draw(at: CGPoint(x: left + markWidth + gap, y: (size.height - nameSize.height) / 2))
}

func writeImageSet(_ dir: URL, idiom: String = "tv", images: [(scale: String, data: Data)]) throws {
    try fm.createDirectory(at: dir, withIntermediateDirectories: true)
    var entries: [[String: String]] = []
    for image in images {
        let isJPEG = image.data.starts(with: [0xFF, 0xD8])
        let name = "image@\(image.scale).\(isJPEG ? "jpg" : "png")"
        try image.data.write(to: dir.appending(path: name))
        entries.append(["idiom": idiom, "filename": name, "scale": image.scale])
    }
    let json: [String: Any] = ["images": entries, "info": ["version": 1, "author": "xcode"]]
    try JSONSerialization.data(withJSONObject: json, options: [.prettyPrinted, .sortedKeys]).write(to: dir.appending(path: "Contents.json"))
}

func writeContents(_ dir: URL, _ json: [String: Any]) throws {
    try fm.createDirectory(at: dir, withIntermediateDirectories: true)
    try JSONSerialization.data(withJSONObject: json, options: [.prettyPrinted, .sortedKeys]).write(to: dir.appending(path: "Contents.json"))
}

let info: [String: Any] = ["version": 1, "author": "xcode"]

/// A two-layer image stack (back: room and glow, front: wordmark) for parallax.
func imageStack(_ name: String, width: Int, height: Int, scales: [Int]) throws {
    let stack = brand.appending(path: "\(name).imagestack")
    try writeContents(stack, ["layers": [["filename": "Front.imagestacklayer"], ["filename": "Back.imagestacklayer"]], "info": info])
    for (layer, draw) in [("Front", { (c: CGContext, s: CGSize) in logo(c, s, height: 0.78, withName: false) }),
                          ("Back", { (c: CGContext, s: CGSize) in background(c, s) })] {
        let layerDir = stack.appending(path: "\(layer).imagestacklayer")
        try writeContents(layerDir, ["info": info])
        try writeImageSet(layerDir.appending(path: "Content.imageset"),
                          images: scales.map { ("\($0)x", png(width: width * $0, height: height * $0, jpeg: layer == "Back", draw: draw)) })
    }
}

try imageStack("App Icon", width: 400, height: 240, scales: [1, 2])
try imageStack("App Icon - App Store", width: 1280, height: 768, scales: [1])

func topShelf(_ name: String, width: Int, height: Int) throws {
    try writeImageSet(brand.appending(path: "\(name).imageset"), images: [1, 2].map { scale in
        ("\(scale)x", png(width: width * scale, height: height * scale, jpeg: true) { ctx, size in
            background(ctx, size)
            logo(ctx, size, height: 0.42, withName: true)
        })
    })
}
try topShelf("Top Shelf Image", width: 1920, height: 720)
try topShelf("Top Shelf Image Wide", width: 2320, height: 720)

try writeContents(brand, [
    "assets": [
        ["filename": "App Icon - App Store.imagestack", "idiom": "tv", "role": "primary-app-icon", "size": "1280x768"],
        ["filename": "App Icon.imagestack", "idiom": "tv", "role": "primary-app-icon", "size": "400x240"],
        ["filename": "Top Shelf Image Wide.imageset", "idiom": "tv", "role": "top-shelf-image-wide", "size": "2320x720"],
        ["filename": "Top Shelf Image.imageset", "idiom": "tv", "role": "top-shelf-image", "size": "1920x720"],
    ],
    "info": info,
])
try writeContents(root, ["info": info])
print("wrote \(brand.path)")

// iPhone and iPad: one 1024 icon, the TV icon's room and mark squared up (iOS rounds the corners), and the room colour
// for the launch screen.
if CommandLine.arguments.count > 2 {
    let ios = URL(fileURLWithPath: CommandLine.arguments[2])
    let icon = ios.appending(path: "AppIcon.appiconset")
    try? fm.removeItem(at: icon)
    try fm.createDirectory(at: icon, withIntermediateDirectories: true)
    try png(width: 1024, height: 1024) { ctx, size in
        background(ctx, size)
        logo(ctx, size, height: 0.72, withName: false)
    }.write(to: icon.appending(path: "AppIcon.png"))
    try writeContents(icon, ["images": [["filename": "AppIcon.png", "idiom": "universal", "platform": "ios", "size": "1024x1024"]], "info": info])
    try writeContents(ios.appending(path: "LaunchBackground.colorset"), ["colors": [["idiom": "universal", "color": ["color-space": "srgb",
        "components": ["red": "0x1B", "green": "0x17", "blue": "0x20", "alpha": "1.000"]]]], "info": info])
    try writeContents(ios, ["info": info])
    print("wrote \(icon.path)")
}
