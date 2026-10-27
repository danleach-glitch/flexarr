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
    /// Downloaded, wherever it's shown: a calm green that reads on the room, with the room's own colour for a glyph on it.
    static let downloaded = Color(rgb: RGB(hex: "#6FCF97")!)
    /// In the library, on Discover's posters: a warm marquee gold, with the room's own colour for a glyph on it. Not
    /// the green, which is downloaded's.
    static let inLibrary = Color(rgb: RGB(hex: "#E8B84A")!)

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

/// The room background with an optional ambient wash bleeding in from the top-left,
/// like bias lighting behind a TV.
struct RoomBackground: View {
    var ambient: Color?
    var intensity: Double = 0.35
    #if os(tvOS)
    var reach: CGFloat = 1500
    #else
    var reach: CGFloat = 700
    #endif

    var body: some View {
        ZStack {
            Color.room
            if let ambient {
                RadialGradient(colors: [ambient.opacity(intensity), .clear], center: .topLeading, startRadius: 0, endRadius: reach)
                    .transition(.opacity)
            }
        }
        .ignoresSafeArea()
    }
}
