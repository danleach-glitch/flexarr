import FlexarrKit
import SwiftUI

// The tokens themselves are in Tokens.swift (generated from design/tokens.json). The room is a dim plum, never pure black, and the only
// loud colour on screen is each title's own ambient colour.
extension Color {
    init(rgb: RGB) {
        self.init(.sRGB, red: rgb.red, green: rgb.green, blue: rgb.blue)
    }
}

extension ItemSummary {
    /// Ambient colour lifted so it reads as a glow on the room background.
    var glowColor: Color { Color(rgb: ambient.glow) }
}

extension Font {
    /// Marquee display type: Big Shoulders Display (Shared/Fonts, OFL), tall and condensed like a cinema marquee, for
    /// titles, row headings and Top 10 numerals. Bold, extra bold (`.heavy`) and black are bundled; lighter weights
    /// take the bold and heavier ones the black.
    static func marquee(_ size: CGFloat, weight: Font.Weight = .heavy) -> Font {
        .custom(MarqueeFace(weight).postScriptName, fixedSize: size)
    }
}

/// The bundled Big Shoulders Display files (listed in each app's UIAppFonts).
enum MarqueeFace: String, CaseIterable {
    case bold = "BigShouldersDisplay-Bold"
    case extraBold = "BigShouldersDisplay-ExtraBold"
    case black = "BigShouldersDisplay-Black"

    init(_ weight: Font.Weight) {
        switch weight {
        case .black: self = .black
        case .heavy: self = .extraBold
        default: self = .bold
        }
    }

    var postScriptName: String { rawValue }
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
