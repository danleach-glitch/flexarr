// Makes the Mac app's icon set from the iPhone app's 1024-pixel artwork, in the shape macOS uses: the artwork in a
// rounded square 824 pixels across, centred on a 1024 canvas, with a soft shadow below.
// Usage: swift Tools/make-app-icon.swift   (from clients/macos; rerun when the iPhone icon changes)
import AppKit

let source = "../apple/FlexarriOS/Assets.xcassets/AppIcon.appiconset/AppIcon.png"
let output = "FlexarrServer/Assets.xcassets/AppIcon.appiconset"
guard let artwork = NSImage(contentsOfFile: source)?.cgImage(forProposedRect: nil, context: nil, hints: nil) else {
    fatalError("Can't read \(source)")
}

func icon(pixels: Int) -> Data {
    let scale = CGFloat(pixels) / 1024
    let context = CGContext(data: nil, width: pixels, height: pixels, bitsPerComponent: 8, bytesPerRow: 0,
                            space: CGColorSpace(name: CGColorSpace.sRGB)!,
                            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
    context.scaleBy(x: scale, y: scale)
    let body = CGRect(x: 100, y: 100, width: 824, height: 824)
    let shape = CGPath(roundedRect: body, cornerWidth: 185, cornerHeight: 185, transform: nil)
    context.saveGState()
    context.setShadow(offset: CGSize(width: 0, height: -10), blur: 28, color: CGColor(gray: 0, alpha: 0.35))
    context.addPath(shape)
    context.setFillColor(CGColor(gray: 0.1, alpha: 1))
    context.fillPath()
    context.restoreGState()
    context.addPath(shape)
    context.clip()
    context.interpolationQuality = .high
    context.draw(artwork, in: body)
    let rep = NSBitmapImageRep(cgImage: context.makeImage()!)
    return rep.representation(using: .png, properties: [:])!
}

var images: [[String: String]] = []
for points in [16, 32, 128, 256, 512] {
    for scale in [1, 2] {
        let name = "icon_\(points)x\(points)\(scale == 2 ? "@2x" : "").png"
        try! icon(pixels: points * scale).write(to: URL(fileURLWithPath: "\(output)/\(name)"))
        images.append(["filename": name, "idiom": "mac", "scale": "\(scale)x", "size": "\(points)x\(points)"])
    }
}
let contents: [String: Any] = ["images": images, "info": ["author": "xcode", "version": 1]]
let json = try! JSONSerialization.data(withJSONObject: contents, options: [.prettyPrinted, .sortedKeys])
try! json.write(to: URL(fileURLWithPath: "\(output)/Contents.json"))
print("Wrote \(images.count) icons to \(output)")
