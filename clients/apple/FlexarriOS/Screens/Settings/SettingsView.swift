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
    /// Nil until known, and from servers without Trakt (before 0.57) or on kids profiles.
    @State private var trakt: TraktStatus?

    var body: some View {
        List {
            if let current = model.currentProfile {
                Section {
                    HStack(spacing: 14) {
                        ProfileAvatar(profile: current, size: 44)
                        Text(current.name).font(.body.weight(.semibold))
                        Spacer()
                        // Choosing someone else takes the server.
                        if model.offersProfilePicker, !model.isOffline {
                            Button(t("Switch")) { choosingProfile = true }
                                .font(.subheadline.weight(.semibold))
                        }
                    }
                    .padding(.vertical, 4)
                    .listRowBackground(Color.seat)
                    if model.offersKidsLock, !model.isOffline {
                        Button {
                            Task { await lockToKids(pin: nil) }
                        } label: {
                            Label(t("Child lock"), systemImage: "lock")
                        }
                        .disabled(locking)
                        .listRowBackground(Color.seat)
                    }
                } header: {
                    #if !os(macOS)
                    Text(t("Watching as"))
                    #endif
                } footer: {
                    if let lockError { Text(lockError).foregroundStyle(Color.danger) }
                }
            }

            if notificationsAllowed && !notifications.isOn && !notifications.turnedOff {
                Section {
                    Button {
                        Task { await turnOnNotifications() }
                    } label: {
                        Label(t("Turn on notifications"), systemImage: "bell.badge.fill")
                    }
                    .disabled(turningOnNotifications)
                    .listRowBackground(Color.seat)
                } footer: {
                    Text(t("This {device} isn’t getting notifications from this server, so you won’t hear when something new is ready.", ["device": ThisDevice.name]))
                }
            }

            // The main profile only: the server refuses anyone else the list (and servers before 0.56 keep none).
            if pairing.listKept == true {
                WaitingDevicesSection()
            }

            Section {
                SettingsLink(t("Playback"), icon: "play.fill") { PlaybackSettingsView() }
                SettingsLink(t(Words.downloads), icon: "arrow.down", value: downloads.bytesUsed == 0 ? nil : formatBytes(downloads.bytesUsed)) {
                    DownloadSettingsView()
                }
                if model.podcastStatus?.available == true || model.bookListening != nil {
                    SettingsLink(t("Podcasts & audiobooks"), icon: "headphones") { ListeningSettingsView() }
                }
                // Servers before 0.54 keep no order.
                if model.libraryOrder != nil, LibraryMenuEntry.ordered(model: model).count > 1 {
                    SettingsLink(t("Library order"), icon: "list.bullet", value: model.libraryOrder?.isEmpty == true ? t("By name") : t("Your own")) {
                        LibraryOrderSettingsView()
                    }
                }
                // Once the server offers more than English (from 0.61).
                if model.languages.count > 1 {
                    SettingsLink(t("Language"), icon: "globe", value: LanguageSettingsView.current(model)) { LanguageSettingsView() }
                }
                SettingsLink(t("Notifications"), icon: "bell.fill", value: notifications.isOn ? t("On") : t("Off")) { NotificationSettingsView() }
                SettingsLink("Siri", icon: "waveform", value: siriDeviceName ?? t("This {device}", ["device": ThisDevice.name])) { SiriSettingsView() }
                if let trakt, trakt.available {
                    SettingsLink("Trakt", icon: "arrow.triangle.2.circlepath", value: trakt.state == .connected ? trakt.username ?? t("On") : trakt.state == .needsSignIn ? t("Signed out") : t("Off")) {
                        TraktSettingsView()
                    }
                }
            }

            Section {
                SettingsLink(t("Server"), icon: "server.rack", value: connection) { ServerSettingsView() }
            }

            Section {
                Link(destination: ReviewPrompt.writeReviewURL) {
                    LabeledContent {
                        Image(systemName: "arrow.up.forward").font(.footnote.weight(.semibold))
                    } label: {
                        Label { Text(t("Rate Flexarr")) } icon: { SettingsIcon("star.fill") }
                    }
                }
                .foregroundStyle(.primary)
                .listRowBackground(Color.seat)
            }

            Section {
                Button(role: .destructive) { confirmSignOut = true } label: {
                    Label(t("Sign out"), systemImage: "rectangle.portrait.and.arrow.right")
                        .foregroundStyle(Color.danger)
                }
                .listRowBackground(Color.seat)
            } footer: {
                Text(t("Flexarr for {device} {version}", ["device": ThisDevice.name, "version": Bundle.main.shortVersion]))
            }
        }
        .settingsPage(t(Words.settings))
        .sheet(isPresented: $askingParentPin) {
            ZStack {
                RoomBackground(ambient: nil)
                PinEntry(profile: nil, title: t("Enter the admin PIN"), error: $lockError,
                         onCancel: { askingParentPin = false; lockError = nil }) { pin in
                    await lockToKids(pin: pin)
                }
            }
        }
        .refreshable { await catchUpOnPairing() }
        .onAppear { siriDeviceName = AppModel.siriDevice == nil ? nil : AppModel.siriDeviceName }
        .task { await catchUpOnPairing() }
        .task(id: model.currentProfile?.id) { trakt = try? await model.client?.traktStatus() }
        .task { notificationsAllowed = [.authorized, .provisional].contains(await notifications.authorization()) }
        .alert(t("Couldn’t turn on notifications"), isPresented: Binding(get: { notificationProblem != nil }, set: { if !$0 { notificationProblem = nil } })) {
            Button(t("OK"), role: .cancel) {}
        } message: {
            Text(notificationProblem ?? "")
        }
        .alert(t("Sign out of this server?"), isPresented: $confirmSignOut) {
            Button(t("Sign out"), role: .destructive) { model.signOut() }
            Button(t(Words.cancel), role: .cancel) {}
        } message: {
            Text(t("This {device} will need to connect again to use the server, and its downloads will be deleted.", ["device": ThisDevice.name]))
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
            if pin == nil { askingParentPin = true } else { lockError = message ?? t("That PIN isn’t right.") }
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
        if model.isOffline { return t("Offline") }
        if model.credentials?.isGuest == true { return model.serverTitle }
        // The server's name when it has given one; with an address for away from home, which of the two is in use.
        guard model.credentials?.remoteAddress != nil else { return model.serverTitle }
        return model.client?.addresses.isAway == true ? t("Away") : t("At home")
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
            .background(Color.aisle, in: .rect(cornerRadius: Radius.control))
    }
}

extension View {
    /// The room behind a Settings page, as every page of Settings has it.
    func settingsPage(_ title: String) -> some View {
        scrollContentBackground(.hidden)
            .macReadable(title: title, parent: title == t(Words.settings) ? nil : t(Words.settings))
            .modifier(SettingsBack(isSubpage: title != t(Words.settings)))
            .background(RoomBackground(ambient: nil))
            .navigationTitle(title)
    }

    /// A Settings switch, in the app's silver.
    func settingsRow() -> some View {
        tint(.silver).listRowBackground(Color.seat)
    }
}

/// A page inside Settings gets the Mac's back button (FlexarrMac's capsule); Settings itself, a sidebar place, has none.
private struct SettingsBack: ViewModifier {
    let isSubpage: Bool

    func body(content: Content) -> some View {
        if isSubpage { content.macBackButton() } else { content }
    }
}
