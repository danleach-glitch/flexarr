import FlexarrKit
import SwiftUI

/// Profiles on this Apple TV: whether it stays with whoever watched last, or asks each time it opens, as a shared
/// living-room screen often should.
struct ProfileSettingsView: View {
    @State private var asksWhoIsWatching = AppModel.asksWhoIsWatching

    var body: some View {
        SettingsPage(title: "Profiles") {
            SettingsGroupLabel("When Flexarr opens")
            SettingsRow(title: "Stay with the last profile", symbol: "person.crop.circle.badge.checkmark",
                        help: "Flexarr opens as whoever watched last on this Apple TV.",
                        accessory: asksWhoIsWatching ? .none : .checkmark) { choose(false) }
            SettingsRow(title: "Ask who's watching", symbol: "person.2",
                        help: "Flexarr opens on Who's watching? each time it starts, so everyone's history stays their own. Profiles with a PIN ask for it every time.",
                        accessory: asksWhoIsWatching ? .checkmark : .none) { choose(true) }
        }
    }

    private func choose(_ asks: Bool) {
        asksWhoIsWatching = asks
        AppModel.asksWhoIsWatching = asks
    }
}
