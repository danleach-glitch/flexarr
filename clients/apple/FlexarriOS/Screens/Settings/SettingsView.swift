import FlexarrKit
import SwiftUI

/// Settings as a short list of pages, the way the system's own Settings app works: each row
/// says where it's set now, and the details live a tap away.
struct SettingsView: View {
    @Environment(AppModel.self) private var model
    @Environment(DownloadStore.self) private var downloads
    @Environment(PushNotifications.self) private var notifications
    @State private var confirmSignOut = false
    @State private var choosingProfile = false
    @State private var siriDeviceName: String?

    var body: some View {
        List {
            if let current = model.currentProfile {
                Section("Watching as") {
                    HStack(spacing: 14) {
                        ProfileAvatar(profile: current, size: 44)
                        Text(current.name).font(.body.weight(.semibold))
                        Spacer()
                        if model.profiles.count > 1 {
                            Button("Switch") { choosingProfile = true }
                                .font(.subheadline.weight(.semibold))
                        }
                    }
                    .padding(.vertical, 4)
                    .listRowBackground(Color.seat)
                }
            }

            Section {
                SettingsLink("Playback", icon: "play.fill") { PlaybackSettingsView() }
                SettingsLink("Downloads", icon: "arrow.down", value: downloads.bytesUsed == 0 ? nil : ByteCountFormatter.string(fromByteCount: downloads.bytesUsed, countStyle: .file)) {
                    DownloadSettingsView()
                }
                if model.podcastStatus?.available == true {
                    SettingsLink("Podcasts", icon: "mic.fill") { PodcastSettingsView() }
                }
                SettingsLink("Notifications", icon: "bell.fill", value: notifications.isOn ? "On" : "Off") { NotificationSettingsView() }
                SettingsLink("Siri", icon: "waveform", value: siriDeviceName ?? "This \(ThisDevice.name)") { SiriSettingsView() }
            }

            Section {
                SettingsLink("Server", icon: "server.rack", value: connection) { ServerSettingsView() }
            }

            Section {
                Button(role: .destructive) { confirmSignOut = true } label: {
                    Label("Sign out", systemImage: "rectangle.portrait.and.arrow.right")
                        .foregroundStyle(Color.danger)
                }
                .listRowBackground(Color.seat)
            } footer: {
                Text("Flexarr for \(ThisDevice.name) \(Bundle.main.shortVersion)")
            }
        }
        .settingsPage("Settings")
        .onAppear { siriDeviceName = AppModel.siriDevice == nil ? nil : AppModel.siriDeviceName }
        .confirmationDialog("Sign out of this server?", isPresented: $confirmSignOut, titleVisibility: .visible) {
            Button("Sign out", role: .destructive) { model.signOut() }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("This \(ThisDevice.name) will need to connect again to use the server, and its downloads will be deleted.")
        }
        .sheet(isPresented: $choosingProfile) {
            ProfilePickerView(marksCurrent: true)
                .presentationDetents([.medium, .large])
                .presentationBackground(Color.room)
        }
    }

    private var connection: String? {
        guard model.credentials?.remoteAddress != nil else { return model.credentials?.address.host() }
        return model.client?.addresses.isAway == true ? "Away" : "At home"
    }
}

/// A row on the Settings list: an icon in a tile, the page's name and, on the right, how it's set now.
struct SettingsLink<Destination: View>: View {
    var title: String
    var icon: String
    var value: String?
    @ViewBuilder var destination: () -> Destination

    init(_ title: String, icon: String, value: String? = nil, @ViewBuilder destination: @escaping () -> Destination) {
        self.title = title
        self.icon = icon
        self.value = value
        self.destination = destination
    }

    var body: some View {
        NavigationLink(destination: destination) {
            LabeledContent {
                if let value { Text(value).lineLimit(1) }
            } label: {
                Label {
                    Text(title)
                } icon: {
                    Image(systemName: icon)
                        .font(.system(size: 14, weight: .semibold))
                        .foregroundStyle(Color.screen)
                        .frame(width: 29, height: 29)
                        .background(Color.aisle, in: .rect(cornerRadius: 7))
                }
            }
        }
        .listRowBackground(Color.seat)
    }
}

extension View {
    /// The room behind a Settings page, as every page of Settings has it.
    func settingsPage(_ title: String) -> some View {
        scrollContentBackground(.hidden)
            .background(RoomBackground(ambient: nil))
            .navigationTitle(title)
    }

    /// A Settings switch, in the app's silver.
    func settingsRow() -> some View {
        tint(.silver).listRowBackground(Color.seat)
    }
}
