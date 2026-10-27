import FlexarrKit
import SwiftUI
import TVServices

/// How Flexarr shows on the Apple TV's Home screen: the Top Shelf's Continue Watching.
struct HomeScreenSettingsView: View {
    @State private var topShelf = TopShelfStyle.current

    var body: some View {
        SettingsPage(title: "Home screen") {
            SettingsGroupLabel("Top Shelf")
            ForEach(TopShelfStyle.allCases, id: \.self) { style in
                SettingsRow(title: style.label, symbol: style == .wide ? "rectangle.inset.filled" : "rectangle.portrait.inset.filled",
                            help: style.help, accessory: topShelf == style ? .checkmark : .none) {
                    topShelf = style
                    TopShelfStyle.current = style
                    TVTopShelfContentProvider.topShelfContentDidChange()
                }
            }
        }
    }
}

private extension TopShelfStyle {
    var help: String {
        switch self {
        case .wide: "Continue Watching on the Top Shelf as wide artwork, with each title's logo on it."
        case .posters: "Continue Watching on the Top Shelf as posters, which fit more titles in."
        }
    }
}
