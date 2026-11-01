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
    ///
    /// On iPhone and iPad it grows and shrinks with Dynamic Type, relative to the text style nearest its size; `scales:
    /// false` keeps it fixed, for type inside artwork or another box of fixed size (a poster's placeholder title, a
    /// profile's initial). The TV has no Dynamic Type.
    static func marquee(_ size: CGFloat, weight: Font.Weight = .heavy, scales: Bool = true) -> Font {
        let name = MarqueeFace(weight).postScriptName
        #if os(iOS)
        if scales { return .custom(name, size: size, relativeTo: marqueeStyle(size)) }
        #endif
        return .custom(name, fixedSize: size)
    }

    /// The text style a marquee size scales with: big headings grow less than small ones, as Apple's own do.
    private static func marqueeStyle(_ size: CGFloat) -> Font.TextStyle {
        switch size {
        case 34...: .largeTitle
        case 28..<34: .title
        case 22..<28: .title2
        case 20..<22: .title3
        default: .headline
        }
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

/// A soft pool of the room behind a title or logo, so it reads over a bright backdrop without a drop shadow.
struct TitleScrim: ViewModifier {
    var reach: CGFloat

    func body(content: Content) -> some View {
        content.background {
            RadialGradient(colors: [Color.room.opacity(0.5), .clear], center: .center, startRadius: 0, endRadius: reach)
                .padding(-reach / 3)
                .allowsHitTesting(false)
        }
    }
}
