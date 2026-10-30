import FlexarrKit
import SwiftUI

/// Settings as a short list of pages, as on iPhone and in the Apple TV's own Settings: each row says how it's set
/// now, the details are a press away, and Sign out is on its own at the end.
struct SettingsView: View {
    @Environment(AppModel.self) private var model
    @State private var confirmSignOut = false
    @State private var choosingProfile = false
    // Read again whenever Settings comes back to this page, so the rows say what the pages changed.
    @State private var playsFilesDirectly = PlaybackController.usesEngine
    @State private var topShelf = TopShelfStyle.current
    @State private var asksWhoIsWatching = AppModel.asksWhoIsWatching
    @AppStorage(LibraryOpening.storageKey) private var libraryOpensOn = LibraryOpening.browse
    @AppStorage(TabBarEntry.storageKey) private var tabBarOrder = ""

    var body: some View {
        SettingsPage(title: "Settings", summary: "Flexarr for Apple TV \(Bundle.main.shortVersion)") {
            if let profile = model.currentProfile {
                SettingsGroupLabel("Watching as")
                if model.offersProfilePicker {
                    SettingsRow(title: profile.name, profile: profile, value: "Switch", help: "Switch to another profile. Each has its own Continue Watching, watch history and playback settings.") {
                        choosingProfile = true
                    }
                } else {
                    SettingsFact("Profile", profile.name, profile: profile)
                }
            }

            SettingsLinkRow(title: "Playback", symbol: "play.rectangle", value: playsFilesDirectly ? "Files play directly" : "Server streams",
                            help: "Auto-play, skipping intros and credits, subtitle size, and how this Apple TV plays files.") {
                PlaybackSettingsView()
            }
            .padding(.top, model.currentProfile == nil ? 0 : 36)
            SettingsLinkRow(title: "Libraries", symbol: "rectangle.stack", value: libraryOpensOn == .browse ? "Open on Browse" : "Open on All titles",
                            help: "Whether a library opens on its highlights (Browse) or on every title, A to Z.") {
                LibrarySettingsView()
            }
            if let first = TabBarEntry.ordered(model: model, stored: tabBarOrder).first {
                SettingsLinkRow(title: "Sidebar", symbol: "sidebar.left", value: "\(first.title) first",
                                help: "The order of your libraries and Live TV in the sidebar, between Home and Discover.") {
                    TabBarSettingsView()
                }
            }
            SettingsLinkRow(title: "Home screen", symbol: "rectangle.topthird.inset.filled", value: topShelf.label,
                            help: "What the Apple TV's Top Shelf shows when Flexarr is in the top row.") {
                HomeScreenSettingsView()
            }
            if model.profiles.count > 1 {
                SettingsLinkRow(title: "Profiles", symbol: "person.2", value: asksWhoIsWatching ? "Ask who's watching" : "Last profile",
                                help: "Whether Flexarr asks who's watching each time it opens on this Apple TV.") {
                    ProfileSettingsView()
                }
            }
            SettingsLinkRow(title: "Server", symbol: "server.rack", value: connection,
                            help: "The server this Apple TV uses: its address, how it's reached and what it's running.") {
                ServerSettingsView()
            }

            SettingsRow(title: "Sign out", symbol: "rectangle.portrait.and.arrow.right",
                        help: "Disconnect this Apple TV from the server. You'll need to connect it again to watch.", destructive: true) {
                confirmSignOut = true
            }
            .padding(.top, 36)
        }
        .onAppear {
            playsFilesDirectly = PlaybackController.usesEngine
            topShelf = TopShelfStyle.current
            asksWhoIsWatching = AppModel.asksWhoIsWatching
        }
        .confirmationDialog("Sign out of this server?", isPresented: $confirmSignOut, titleVisibility: .visible) {
            Button("Sign out", role: .destructive) { model.signOut() }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("This Apple TV will need to connect again to use the server.")
        }
        .fullScreenCover(isPresented: $choosingProfile) { ProfilePickerView(marksCurrent: true) }
    }

    private var connection: String? {
        guard model.credentials?.remoteAddress != nil else { return model.credentials?.address.host() }
        return model.client?.addresses.isAway == true ? "Away" : "At home"
    }
}

extension TopShelfStyle {
    var label: String {
        switch self {
        case .wide: "Wide artwork"
        case .posters: "Posters"
        }
    }
}
