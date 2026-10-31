import FlexarrKit
import SwiftUI

/// Profiles on this Apple TV: whether it stays with whoever watched last, or asks each time it opens, as a shared
/// living-room screen often should.
struct ProfileSettingsView: View {
    @Environment(AppModel.self) private var model
    @State private var asksWhoIsWatching = AppModel.asksWhoIsWatching
    /// Locking to kids profiles: the admin PIN is being asked for.
    @State private var askingPin = false
    /// A lock is on its way to the server, so another press doesn't send a second.
    @State private var locking = false
    @State private var error: String?

    var body: some View {
        SettingsPage(title: "Profiles") {
            SettingsGroupLabel("When Flexarr opens")
            SettingsRow(title: "Stay with the last profile", symbol: "person.crop.circle.badge.checkmark",
                        help: "Flexarr opens as whoever watched last on this Apple TV.",
                        accessory: asksWhoIsWatching ? .none : .checkmark) { choose(false) }
            SettingsRow(title: "Ask who's watching", symbol: "person.2",
                        help: "Flexarr opens on Who's watching? each time it starts, so everyone's history stays their own. Profiles with a PIN ask for it every time.",
                        accessory: asksWhoIsWatching ? .checkmark : .none) { choose(true) }
            if model.offersKidsLock {
                SettingsGroupLabel("Kids")
                SettingsRow(title: "Child lock", symbol: "lock", value: locking ? "Locking" : nil,
                            help: error ?? "Only kids profiles can be chosen on this Apple TV until the admin PIN unlocks it.",
                            accessory: .none) { Task { await lock(pin: nil) } }
            }
        }
        .fullScreenCover(isPresented: $askingPin) {
            PinPad(profile: model.profiles.first(where: \.isMain), title: "Enter the admin PIN", error: $error,
                   onCancel: { askingPin = false; error = nil }) { pin in
                await lock(pin: pin)
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .background(RoomBackground(ambient: nil))
        }
    }

    /// Without a PIN first: the main profile, unlocked here, needs none; otherwise the server asks for it.
    private func lock(pin: String?) async {
        // Not disabled while it's busy: tvOS doesn't reliably move focus back onto a button that was.
        guard !locking else { return }
        locking = true
        defer { locking = false }
        error = nil
        do {
            try await model.lockToKids(pin: pin)
            askingPin = false
        } catch FlexarrClientError.parentPinNeeded(let message) {
            if pin == nil { askingPin = true } else { error = message ?? "That PIN isn’t right." }
        } catch {
            self.error = ConnectionError(error).message
        }
    }

    private func choose(_ asks: Bool) {
        asksWhoIsWatching = asks
        AppModel.asksWhoIsWatching = asks
    }
}
