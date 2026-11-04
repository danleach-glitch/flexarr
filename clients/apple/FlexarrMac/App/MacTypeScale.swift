import SwiftUI

// The Mac's type scale (docs/mac-player.md, "The Mac's page layout"). The screens shared with the iPad name iOS text
// styles, which macOS sets two sizes smaller (body 13 pt, subheadline 11, footnote and caption 10): read as written,
// every page is small and cramped. These stand in for them in the Mac app, close to the iPad's sizes, so the shared
// screens read as intended. They're this module's, so they're the ones the screens pick up here; the phones and the
// TV keep the system's. Weights and designs still apply on top (`.subheadline.weight(.semibold)`).

extension Font {
    static var largeTitle: Font { .system(size: 34, weight: .bold) }
    static var title: Font { .system(size: 28, weight: .bold) }
    static var title2: Font { .system(size: 22, weight: .bold) }
    static var title3: Font { .system(size: 19, weight: .semibold) }
    static var headline: Font { .system(size: 16, weight: .semibold) }
    static var body: Font { .system(size: 15) }
    static var callout: Font { .system(size: 14) }
    static var subheadline: Font { .system(size: 14) }
    static var footnote: Font { .system(size: 13) }
    static var caption: Font { .system(size: 12) }
    static var caption2: Font { .system(size: 11) }
}
