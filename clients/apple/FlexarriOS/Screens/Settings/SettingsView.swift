import FlexarrKit
import SwiftUI
import UserNotifications

/// Settings as a short list of pages, the way the system's own Settings app works: each row
/// says where it's set now, and the details live a tap away.
struct SettingsView: View {
    @Environment(AppModel.self) private var model
    @Environment(DownloadStore.self) private var downloads
    @Environment(PushNotifications.self) private var notifications
    @Environment(PairingPrompts.self) private var pairing
    @State private var confirmSignOut = false
    @State private var choosingProfile = false
    /// Locking to kids profiles: the admin PIN is being asked for.
    @State private var askingParentPin = false
    /// A lock is on its way to the server, so another tap doesn't send a second.
    @State private var locking = false
    @State private var lockError: String?
    @State private var siriDeviceName: String?
    /// iOS lets Flexarr show notifications, so if they're off it's this server that isn't sending them.
    @State private var notificationsAllowed = false
    @State private var turningOnNotifications = false
    @State private var notificationProblem: String?

    var body: some View {
        List {
            if let current = model.currentProfile {
                Section {
                    HStack(spacing: 14) {
                        ProfileAvatar(profile: current, size: 44)
                        Text(current.name).font(.body.weight(.semibold))
                        Spacer()
                        if model.offersProfilePicker {
                            Button("Switch") { choosingProfile = true }
                                .font(.subheadline.weight(.semibold))
                        }
                    }
                    .padding(.vertical, 4)
                    .listRowBackground(Color.seat)
                    if model.offersKidsLock {
                        Button {
                            Task { await lockToKids(pin: nil) }
                        } label: {
                            Label("Child lock", systemImage: "lock")
                        }
                        .disabled(locking)
                        .listRowBackground(Color.seat)
                    }
                } header: {
                    Text("Watching as")
                } footer: {
                    if let lockError { Text(lockError).foregroundStyle(Color.danger) }
                }
            }

            if notificationsAllowed && !notifications.isOn && !notifications.turnedOff {
                Section {
                    Button {
                        Task { await turnOnNotifications() }
                    } label: {
                        Label("Turn on notifications", systemImage: "bell.badge.fill")
                    }
                    .disabled(turningOnNotifications)
                    .listRowBackground(Color.seat)
                } footer: {
                    Text("This \(ThisDevice.name) isn't getting notifications from this server, so you won't hear when something new is ready.")
                }
            }

            // The main profile only: the server refuses anyone else the list (and servers before 0.56 keep none).
            if pairing.listKept == true {
                WaitingDevicesSection()
            }

            Section {
                SettingsLink("Playback", icon: "play.fill") { PlaybackSettingsView() }
                SettingsLink("Downloads", icon: "arrow.down", value: downloads.bytesUsed == 0 ? nil : ByteCountFormatter.string(fromByteCount: downloads.bytesUsed, countStyle: .file)) {
                    DownloadSettingsView()
                }
                if model.podcastStatus?.available == true {
                    SettingsLink("Podcasts", icon: "mic.fill") { PodcastSettingsView() }
                }
                // Servers before 0.54 keep no order.
                if model.libraryOrder != nil, LibraryMenuEntry.ordered(model: model).count > 1 {
                    SettingsLink("Library order", icon: "list.bullet", value: model.libraryOrder?.isEmpty == true ? "By name" : "Your own") {
                        LibraryOrderSettingsView()
                    }
                }
                SettingsLink("Notifications", icon: "bell.fill", value: notifications.isOn ? "On" : "Off") { NotificationSettingsView() }
                SettingsLink("Siri", icon: "waveform", value: siriDeviceName ?? "This \(ThisDevice.name)") { SiriSettingsView() }
            }

            Section {
                SettingsLink("Server", icon: "server.rack", value: connection) { ServerSettingsView() }
            }

            Section {
                Link(destination: ReviewPrompt.writeReviewURL) {
                    LabeledContent {
                        Image(systemName: "arrow.up.forward").font(.footnote.weight(.semibold))
                    } label: {
                        Label { Text("Rate Flexarr") } icon: { SettingsIcon("star.fill") }
                    }
                }
                .foregroundStyle(.primary)
                .listRowBackground(Color.seat)
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
        .sheet(isPresented: $askingParentPin) {
            ZStack {
                RoomBackground(ambient: nil)
                PinEntry(profile: model.profiles.first(where: \.isMain), title: "Enter the admin PIN", error: $lockError,
                         onCancel: { askingParentPin = false; lockError = nil }) { pin in
                    await lockToKids(pin: pin)
                }
            }
        }
        .refreshable { await catchUpOnPairing() }
        .onAppear { siriDeviceName = AppModel.siriDevice == nil ? nil : AppModel.siriDeviceName }
        .task { await catchUpOnPairing() }
        .task { notificationsAllowed = [.authorized, .provisional].contains(await notifications.authorization()) }
        .alert("Couldn't turn on notifications", isPresented: Binding(get: { notificationProblem != nil }, set: { if !$0 { notificationProblem = nil } })) {
            Button("OK", role: .cancel) {}
        } message: {
            Text(notificationProblem ?? "")
        }
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

    /// Without a PIN first: the main profile, unlocked here, needs none; otherwise the server asks for it.
    private func lockToKids(pin: String?) async {
        guard !locking else { return }
        locking = true
        defer { locking = false }
        lockError = nil
        do {
            try await model.lockToKids(pin: pin)
            askingParentPin = false
        } catch FlexarrClientError.parentPinNeeded(let message) {
            if pin == nil { askingParentPin = true } else { lockError = message ?? "That PIN isn’t right." }
        } catch {
            lockError = ConnectionError(error).message
        }
    }

    private func turnOnNotifications() async {
        guard let credentials = model.credentials else { return }
        turningOnNotifications = true
        defer { turningOnNotifications = false }
        do {
            _ = try await notifications.turnOn(credentials)
        } catch {
            notificationProblem = ConnectionError(error).message
        }
    }

    /// Who's waiting to connect now.
    private func catchUpOnPairing() async {
        pairing.use(model.client)
        await pairing.catchUp()
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
                    SettingsIcon(icon)
                }
            }
        }
        .listRowBackground(Color.seat)
    }
}

/// A Settings row's symbol, in its tile.
struct SettingsIcon: View {
    var name: String

    init(_ name: String) {
        self.name = name
    }

    var body: some View {
        Image(systemName: name)
            .font(.system(size: 14, weight: .semibold))
            .foregroundStyle(Color.screen)
            .frame(width: 29, height: 29)
            .background(Color.aisle, in: .rect(cornerRadius: 7))
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
