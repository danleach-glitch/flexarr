import FlexarrKit
import SwiftUI

/// The Mac app: the player (docs/mac-player.md). Flexarr Server is a different app, in clients/macos.
@main
struct FlexarrMacApp: App {
    init() {
        Localization.shared.startWithLastShown()
    }

    var body: some Scene {
        WindowGroup {
            Text(verbatim: "Flexarr")
                .frame(minWidth: 960, minHeight: 600)
                .preferredColorScheme(.dark)
        }
    }
}
