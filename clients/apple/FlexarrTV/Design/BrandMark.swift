import SwiftUI

/// The Flexarr mark: three stacked bars (the web favicon's geometry, in a 64-unit square).
struct BrandMark: View {
    var size: CGFloat = 96

    var body: some View {
        Canvas { context, canvas in
            let unit = canvas.width / 64
            let bars: [(x: CGFloat, y: CGFloat, width: CGFloat, color: Color, tilt: Double)] = [
                (14, 15, 36, .screen, -5), (14, 28.5, 30, .silver, 0), (14, 42, 23, Color(red: 0.91, green: 0.63, blue: 0.75), 0),
            ]
            for bar in bars {
                var layer = context
                layer.translateBy(x: 14 * unit, y: 20 * unit)
                layer.rotate(by: .degrees(bar.tilt))
                layer.translateBy(x: -14 * unit, y: -20 * unit)
                let rect = CGRect(x: bar.x * unit, y: bar.y * unit, width: bar.width * unit, height: 10 * unit)
                layer.fill(Path(roundedRect: rect, cornerRadius: 5 * unit), with: .color(bar.color))
            }
        }
        .frame(width: size, height: size)
        .accessibilityHidden(true)
    }
}
