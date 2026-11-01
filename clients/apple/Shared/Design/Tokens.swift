// Generated from design/tokens.json by `make tokens`. Don't edit; change the JSON.

import SwiftUI

extension Color {
    /// Base background: a dim, plum-tinted screening room.
    static let room = Color(.sRGB, red: 27 / 255, green: 23 / 255, blue: 32 / 255)
    /// Raised surfaces: panels, menus, inputs.
    static let seat = Color(.sRGB, red: 37 / 255, green: 32 / 255, blue: 44 / 255)
    /// A surface on a surface: a menu over a panel.
    static let seatRaised = Color(.sRGB, red: 46 / 255, green: 40 / 255, blue: 54 / 255)
    /// Hairlines and inactive controls.
    static let aisle = Color(.sRGB, red: 58 / 255, green: 51 / 255, blue: 67 / 255)
    /// Primary text: warm projected white.
    static let screen = Color(.sRGB, red: 242 / 255, green: 238 / 255, blue: 231 / 255)
    /// Secondary text.
    static let usher = Color(.sRGB, red: 163 / 255, green: 154 / 255, blue: 174 / 255)
    /// Fallback accent when a title has no ambient colour.
    static let silver = Color(.sRGB, red: 169 / 255, green: 194 / 255, blue: 236 / 255)
    /// Connected, on, set up, downloaded, in the library.
    static let go = Color(.sRGB, red: 127 / 255, green: 209 / 255, blue: 160 / 255)
    /// Destructive actions and error messages, never decoration.
    static let danger = Color(.sRGB, red: 240 / 255, green: 138 / 255, blue: 126 / 255)
    /// The live and recording dot on Live TV, nothing else.
    static let record = Color(.sRGB, red: 229 / 255, green: 72 / 255, blue: 77 / 255)
    /// Inside text fields and menus, below the panel.
    static let well = Color(.sRGB, red: 20 / 255, green: 17 / 255, blue: 24 / 255)
}

/// Corner radii, in points.
enum Radius {
    /// Posters, stills, covers.
    static let art: CGFloat = 4
    /// Buttons and inputs.
    static let control: CGFloat = 10
    /// Panels, sheets, cards of text.
    static let panel: CGFloat = 14
}

enum MainButton {
    /// The main button's fill: the ambient colour mixed with screen, so a dark or dull title colour still reads.
    static let ambientMix: Double = 0.68
}
