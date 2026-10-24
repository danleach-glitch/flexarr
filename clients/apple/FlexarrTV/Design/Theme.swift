import FlexarrKit
import SwiftUI

// Tokens from docs/design.md. The room is a dim plum, never pure black, and the only
// loud colour on screen is each title's own ambient colour.
extension Color {
    static let room = Color(rgb: RGB(hex: "#1B1720")!)
    static let seat = Color(rgb: RGB(hex: "#25202C")!)
    static let aisle = Color(rgb: RGB(hex: "#3A3343")!)
    static let screen = Color(rgb: RGB(hex: "#F2EEE7")!)
    static let usher = Color(rgb: RGB(hex: "#A39AAE")!)
    static let silver = Color(rgb: .silver)
    /// Errors, as the web app's `--danger`: a soft coral that reads on the room without shouting.
    static let danger = Color(rgb: RGB(hex: "#F08A7E")!)

    init(rgb: RGB) {
        self.init(.sRGB, red: rgb.red, green: rgb.green, blue: rgb.blue)
    }
}

extension ItemSummary {
    /// Ambient colour lifted so it reads as a glow on the room background.
    var glowColor: Color { Color(rgb: ambient.glow) }
}

extension Font {
    /// Marquee display type: tall, condensed and heavy, standing in for Big Shoulders Display.
    static func marquee(_ size: CGFloat, weight: Font.Weight = .heavy) -> Font {
        .system(size: size, weight: weight).width(.condensed)
    }
}

enum Layout {
    /// The shared left edge for heroes, rows and grids (tvOS title-safe inset).
    static let edge: CGFloat = 80
    static let posterWidth: CGFloat = 250
    static let wideWidth: CGFloat = 500
    static let rowSpacing: CGFloat = 44
    /// Six posters exactly fill the 1920 - 2 * 80 content width.
    static let gridSpacing: CGFloat = (1920 - 2 * edge - 6 * posterWidth) / 5
}

/// The room background with an optional ambient wash bleeding in from the top-left,
/// like bias lighting behind a TV.
struct RoomBackground: View {
    var ambient: Color?
    var intensity: Double = 0.35

    var body: some View {
        ZStack {
            Color.room
            if let ambient {
                RadialGradient(colors: [ambient.opacity(intensity), .clear], center: .topLeading, startRadius: 0, endRadius: 1500)
                    .transition(.opacity)
            }
        }
        .ignoresSafeArea()
    }
}

/// "2016   1h 56m   TV-MA": facts separated by space, never middle dots.
struct FactsLine: View {
    let facts: [String]

    var body: some View {
        HStack(spacing: 22) {
            ForEach(Array(facts.enumerated()), id: \.offset) { _, fact in
                Text(fact)
            }
        }
        .font(.system(size: 26, weight: .semibold))
        .foregroundStyle(Color.screen.opacity(0.85))
        .monospacedDigit()
    }
}

/// Thin progress bar for resume points.
struct ProgressBar: View {
    let value: Double
    var tint: Color = .screen

    var body: some View {
        GeometryReader { proxy in
            ZStack(alignment: .leading) {
                Capsule().fill(Color.black.opacity(0.45))
                Capsule().fill(tint).frame(width: max(8, proxy.size.width * value))
            }
        }
        .frame(height: 7)
    }
}

/// Section heading for rows: marquee type, left-aligned on the shared edge.
struct RowHeading: View {
    let title: String

    var body: some View {
        Text(title)
            .font(.marquee(40, weight: .bold))
            .foregroundStyle(Color.screen)
            .padding(.leading, Layout.edge)
    }
}
