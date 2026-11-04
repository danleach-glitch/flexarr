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
    /// Nil until known, and from servers without Trakt (before 0.57) or on kids profiles.
    @State private var trakt: TraktStatus?

    var body: some View {
        SettingsPage(title: t(Words.settings), summary: t("Flexarr for Apple TV {version}", ["version": Bundle.main.shortVersion])) {
            if let profile = model.currentProfile {
                SettingsGroupLabel(t("Watching as"))
                if model.offersProfilePicker {
                    SettingsRow(title: profile.name, profile: profile, value: t("Switch"), help: t("Switch to another profile. Each has its own Continue watching, watch history and playback settings.")) {
                        choosingProfile = true
                    }
                } else {
                    SettingsFact(t("Profile"), profile.name, profile: profile)
                }
            }

            SettingsLinkRow(title: t("Playback"), symbol: "play.rectangle", value: playsFilesDirectly ? t("Files play directly") : t("Server streams"),
                            help: t("Auto-play, skipping intros and credits, subtitle size, and how this Apple TV plays files.")) {
                PlaybackSettingsView()
            }
            .padding(.top, model.currentProfile == nil ? 0 : 36)
            SettingsLinkRow(title: t(Words.libraries), symbol: "rectangle.stack", value: t("Open on {view}", ["view": libraryOpensOn.label]),
                            help: t("What each library opens on: its highlights (Browse), everything, or its genres. Music has its own.")) {
                LibrarySettingsView()
            }
            if let first = TabBarEntry.ordered(model: model, stored: tabBarOrder).first {
                SettingsLinkRow(title: t("Sidebar"), symbol: "sidebar.left", value: t("{name} first", ["name": first.title]),
                                help: t("The order of your libraries and Live TV in the sidebar, between Home and Discover.")) {
                    TabBarSettingsView()
                }
            }
            SettingsLinkRow(title: t("Home screen"), symbol: "rectangle.topthird.inset.filled", value: topShelf.label,
                            help: t("What the Apple TV's Top Shelf shows when Flexarr is in the top row.")) {
                HomeScreenSettingsView()
            }
            // Once the server offers more than English (from 0.61).
            if model.languages.count > 1 {
                SettingsLinkRow(title: t("Language"), symbol: "globe", value: LanguageSettingsView.current(model),
                                help: t("The language Flexarr is in for this profile, on every device.")) {
                    LanguageSettingsView()
                }
            }
            if model.profiles.count > 1 {
                SettingsLinkRow(title: t("Profiles"), symbol: "person.2", value: asksWhoIsWatching ? t("Ask who's watching") : t("Last profile"),
                                help: t("Whether Flexarr asks who's watching each time it opens on this Apple TV.")) {
                    ProfileSettingsView()
                }
            }
            if let trakt, trakt.available {
                SettingsLinkRow(title: "Trakt", symbol: "arrow.triangle.2.circlepath",
                                value: trakt.state == .connected ? trakt.username ?? t("On") : trakt.state == .needsSignIn ? t("Signed out") : t("Off"),
                                help: t("Keep what this profile watches, and its watchlist, in step with Trakt.")) {
                    TraktSettingsView()
                }
            }
            SettingsLinkRow(title: t("Server"), symbol: "server.rack", value: connection,
                            help: t("The server this Apple TV uses: its address, how it's reached and what it's running.")) {
                ServerSettingsView()
            }

            SettingsRow(title: t("Sign out"), symbol: "rectangle.portrait.and.arrow.right",
                        help: t("Disconnect this Apple TV from the server. You'll need to connect it again to watch."), destructive: true) {
                confirmSignOut = true
            }
            .padding(.top, 36)
        }
        .onAppear {
            playsFilesDirectly = PlaybackController.usesEngine
            topShelf = TopShelfStyle.current
            asksWhoIsWatching = AppModel.asksWhoIsWatching
        }
        .confirmationDialog(t("Sign out of this server?"), isPresented: $confirmSignOut, titleVisibility: .visible) {
            Button(t("Sign out"), role: .destructive) { model.signOut() }
            Button(t(Words.cancel), role: .cancel) {}
        } message: {
            Text(t("This Apple TV will need to connect again to use the server."))
        }
        .fullScreenCover(isPresented: $choosingProfile) { ProfilePickerView(marksCurrent: true) }
        .task(id: model.currentProfile?.id) { trakt = try? await model.client?.traktStatus() }
    }

    private var connection: String? {
        guard model.credentials?.remoteAddress != nil else { return model.credentials?.address.host() }
        return model.client?.addresses.isAway == true ? t("Away") : t("At home")
    }
}

extension TopShelfStyle {
    var label: String {
        switch self {
        case .wide: t("Wide artwork")
        case .posters: t("Posters")
        }
    }
}
