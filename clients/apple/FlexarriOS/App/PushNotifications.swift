import FlexarrKit
import Foundation
import UIKit
import UserNotifications

private let log = FlexarrLog("notifications")

/// Notifications from the server (see docs/notifications.md): what's new, sealed with a key only this phone and its
/// server know, and opened by the notification extension. The phone hears about one profile, whichever it was on when
/// they were turned on, so it registers as that profile (with that profile's token) whoever is watching now.
@MainActor
@Observable
final class PushNotifications {
    nonisolated static let videoCategory = "flexarr.video"
    nonisolated static let podcastCategory = "flexarr.podcast"
    nonisolated static let recordingCategory = "flexarr.recording"
    /// "Living room wants to connect": opening it asks to allow the device (see `PairingPrompts`). Allow opens the app
    /// to allow it there, since that can take the admin PIN; Don't allow answers without opening it.
    nonisolated static let pairingCategory = "flexarr.pairing"
    /// "Someone's guessing your PIN": opening it goes to Settings, with the devices waiting to connect.
    nonisolated static let pinCategory = "flexarr.pin"
    nonisolated static let playAction = "flexarr.play"
    nonisolated static let allowPairingAction = "flexarr.pairing.allow"
    nonisolated static let declinePairingAction = "flexarr.pairing.decline"
    nonisolated static let downloadAction = "flexarr.download"
    nonisolated static let playOnTvAction = "flexarr.play-on-tv"

    /// The main switch, as last set here.
    private(set) var isOn = UserDefaults.standard.bool(forKey: Keys.on) {
        didSet { UserDefaults.standard.set(isOn, forKey: Keys.on) }
    }
    /// They've been turned on here and not turned off since. Signing out (or connecting to another server) leaves it
    /// set, so the next sign-in turns them back on.
    private(set) var wanted = UserDefaults.standard.bool(forKey: Keys.wanted) {
        didSet { UserDefaults.standard.set(wanted, forKey: Keys.wanted) }
    }
    /// They were turned off here on purpose, so Settings doesn't offer them again.
    private(set) var turnedOff = UserDefaults.standard.bool(forKey: Keys.turnedOff) {
        didSet { UserDefaults.standard.set(turnedOff, forKey: Keys.turnedOff) }
    }
    /// Who they're for, as the server last said.
    private(set) var profileId = UserDefaults.standard.string(forKey: Keys.profileId) {
        didSet { UserDefaults.standard.set(profileId, forKey: Keys.profileId) }
    }
    private(set) var profileName = UserDefaults.standard.string(forKey: Keys.profileName) {
        didSet { UserDefaults.standard.set(profileName, forKey: Keys.profileName) }
    }
    /// What this phone hears about, kept here so a new device token can be sent with them.
    private(set) var settings: NotificationSettings = UserDefaults.standard.data(forKey: Keys.settings)
        .flatMap { try? JSONDecoder().decode(NotificationSettings.self, from: $0) } ?? NotificationSettings() {
        didSet { UserDefaults.standard.set(try? JSONEncoder().encode(settings), forKey: Keys.settings) }
    }
    /// The token of the profile they're for ("" for none, which the server takes as the main profile).
    @ObservationIgnored private var profileToken = UserDefaults.standard.string(forKey: Keys.profileToken) {
        didSet { UserDefaults.standard.set(profileToken, forKey: Keys.profileToken) }
    }

    /// The APNs token this launch, once Apple has given it.
    @ObservationIgnored private var deviceToken: String?
    @ObservationIgnored private var tokenWaiters: [CheckedContinuation<String?, Never>] = []
    /// The token the server has from this launch (tokens can change, so each launch sends it once).
    @ObservationIgnored private var sentToken: String?

    private enum Keys {
        static let on = "notifications.on"
        static let wanted = "notifications.wanted"
        static let turnedOff = "notifications.turnedOff"
        static let profileId = "notifications.profile"
        static let profileName = "notifications.profileName"
        static let profileToken = "notifications.profileToken"
        static let settings = "notifications.settings"
    }

    /// Where the extension finds the key: the app group (see `NotificationKey`).
    private static var keyGroup: String? { Bundle.main.object(forInfoDictionaryKey: "FlexarrAppGroup") as? String }

    /// Apple's sandbox for development builds (and ones signed for development, as `make ios-install` does), the
    /// real thing for TestFlight and the App Store, which carry no provisioning profile.
    static var environment: String {
        #if DEBUG
        "sandbox"
        #else
        guard let url = Bundle.main.url(forResource: "embedded", withExtension: "mobileprovision"),
              let data = try? Data(contentsOf: url), let text = String(data: data, encoding: .isoLatin1) else { return "production" }
        return text.range(of: #"aps-environment</key>\s*<string>development"#, options: .regularExpression) == nil ? "production" : "sandbox"
        #endif
    }

    static func registerCategories() {
        let play = UNNotificationAction(identifier: playAction, title: "Play", options: [.foreground],
                                        icon: UNNotificationActionIcon(systemImageName: "play.fill"))
        // Both run without opening the app: the download is asked for and fetched in the background.
        let download = UNNotificationAction(identifier: downloadAction, title: "Download", options: [],
                                            icon: UNNotificationActionIcon(systemImageName: "arrow.down"))
        let playOnTv = UNNotificationAction(identifier: playOnTvAction, title: "Play on Apple TV", options: [],
                                            icon: UNNotificationActionIcon(systemImageName: "appletv.fill"))
        // The phone has to be unlocked to answer, and the code to check is in the notification. Allowing opens the app:
        // from 0.56 it takes the admin PIN when the main profile is locked here, and only the app can ask.
        let allow = UNNotificationAction(identifier: allowPairingAction, title: "Allow", options: [.foreground],
                                         icon: UNNotificationActionIcon(systemImageName: "checkmark"))
        let decline = UNNotificationAction(identifier: declinePairingAction, title: "Don't allow",
                                           options: [.destructive, .authenticationRequired],
                                           icon: UNNotificationActionIcon(systemImageName: "xmark"))
        UNUserNotificationCenter.current().setNotificationCategories([
            UNNotificationCategory(identifier: videoCategory, actions: [play, download, playOnTv], intentIdentifiers: []),
            UNNotificationCategory(identifier: podcastCategory, actions: [play, download], intentIdentifiers: []),
            UNNotificationCategory(identifier: recordingCategory, actions: [play], intentIdentifiers: []),
            UNNotificationCategory(identifier: pairingCategory, actions: [allow, decline], intentIdentifiers: []),
        ])
    }

    /// Takes away the "wants to connect" notification for a device that's stopped waiting.
    static func removePairingNotifications(for code: String) {
        Task { await removePairingNotifications { $0 == code } }
    }

    /// Takes away the "wants to connect" notifications for any device but those still waiting.
    static func removePairingNotifications(except waiting: Set<String>) {
        Task { await removePairingNotifications { !waiting.contains($0) } }
    }

    /// Takes away the "wants to connect" notifications whose code `gone` picks, by the time it returns.
    static func removePairingNotifications(where gone: @Sendable (String) -> Bool) async {
        let center = UNUserNotificationCenter.current()
        let ids = await center.deliveredNotifications().compactMap { delivered -> String? in
            guard let code = delivered.request.content.userInfo["pairing"] as? String, gone(code) else { return nil }
            return delivered.request.identifier
        }
        if !ids.isEmpty { center.removeDeliveredNotifications(withIdentifiers: ids) }
    }

    func authorization() async -> UNAuthorizationStatus {
        await UNUserNotificationCenter.current().notificationSettings().authorizationStatus
    }

    // MARK: Turning on and off

    /// Notifications are off for Flexarr in the Settings app.
    struct Refused: Error {}

    /// Asks (the first time), then registers this phone for the profile `credentials` act as.
    func turnOn(_ credentials: ServerCredentials) async throws -> NotificationDevice {
        switch await authorization() {
        case .denied: throw Refused()
        case .notDetermined:
            let granted = (try? await UNUserNotificationCenter.current().requestAuthorization(options: [.alert, .sound, .badge])) ?? false
            guard granted else { throw Refused() }
        default: break
        }
        return try await register(as: credentials, settings: settings, useCurrentProfile: true)
    }

    /// Stops them on the server (best effort) and forgets them here.
    func turnOff(_ credentials: ServerCredentials?) async {
        if let credentials { try? await credentials.client.stopNotifications() }
        forget()
        wanted = false
        turnedOff = true
    }

    /// A sign-in with them off, though they were on before it (this phone signed out and back in, say): on again for
    /// the profile it's on, if iOS still allows them. Quietly; Settings says so if it didn't work.
    func resume(_ credentials: ServerCredentials) async {
        guard !isOn, wanted, [.authorized, .provisional].contains(await authorization()) else { return }
        do {
            _ = try await register(as: credentials, settings: settings, useCurrentProfile: true)
        } catch {
            log.warning("Couldn't turn notifications back on", error: error)
        }
    }

    /// Notifications for another profile from now on (the one `credentials` act as).
    func move(to credentials: ServerCredentials) async throws -> NotificationDevice {
        try await register(as: credentials, settings: settings, useCurrentProfile: true)
    }

    /// Changes what this phone hears about, for the same profile. Shown at once; put back if the server refuses.
    func update(_ settings: NotificationSettings, signedIn credentials: ServerCredentials) async throws -> NotificationDevice {
        let before = self.settings
        self.settings = settings
        do {
            return try await register(as: forProfile(credentials), settings: settings)
        } catch {
            self.settings = before
            throw error
        }
    }

    /// How the server has this phone, with the switch here having the last word: registered again if the server
    /// forgot it (Apple said the token went stale, say), stopped if they were turned off here while out of reach.
    func load(_ credentials: ServerCredentials) async throws -> NotificationDevice? {
        let device = try await credentials.client.notificationDevice()
        switch (device, isOn) {
        case (let device?, true):
            remember(device)
            return device
        case (nil, true):
            return try await register(as: forProfile(credentials), settings: settings)
        case (.some, false):
            try? await credentials.client.stopNotifications()
            return nil
        case (nil, false):
            return nil
        }
    }

    func sendTest(_ credentials: ServerCredentials) async throws {
        try await credentials.client.sendTestNotification()
    }

    /// Signing out: the server stops them (best effort, before the sign-in itself goes). They're forgotten here as the
    /// app goes back to connecting.
    func signedOut(_ client: FlexarrClient) async {
        try? await client.stopNotifications()
    }

    /// Forgets them here, key and all (the server does too when the phone is signed out). `wanted` stays, for the next
    /// sign-in.
    func forget() {
        isOn = false
        profileId = nil
        profileName = nil
        profileToken = nil
        sentToken = nil
        NotificationKey.delete(accessGroup: Self.keyGroup)
    }

    // MARK: The device token

    /// Launch and coming to the front: asks Apple for the token again (it can change), which is then sent on.
    func refresh() {
        guard isOn else { return }
        UIApplication.shared.registerForRemoteNotifications()
    }

    /// Apple's answer, through the app delegate. A token the server doesn't have yet is sent, as the profile they're for.
    func received(token: Data) {
        let hex = token.map { String(format: "%02x", $0) }.joined()
        deviceToken = hex
        let waiting = tokenWaiters
        tokenWaiters = []
        waiting.forEach { $0.resume(returning: hex) }
        guard isOn, hex != sentToken, let credentials = CredentialStore().load() else { return }
        Task {
            do {
                _ = try await register(as: forProfile(credentials), settings: settings)
            } catch {
                log.warning("Couldn't send the new device token", error: error)
            }
        }
    }

    func failedToRegister(_ error: Error) {
        log.warning("No device token", error: error)
        let waiting = tokenWaiters
        tokenWaiters = []
        waiting.forEach { $0.resume(returning: nil) }
    }

    /// This launch's token, asked for if need be. Nil if Apple doesn't answer in a while (no network, say).
    private func token() async -> String? {
        if let deviceToken { return deviceToken }
        UIApplication.shared.registerForRemoteNotifications()
        Task {
            try? await Task.sleep(for: .seconds(15))
            let waiting = tokenWaiters
            tokenWaiters = []
            waiting.forEach { $0.resume(returning: nil) }
        }
        return await withCheckedContinuation { tokenWaiters.append($0) }
    }

    // MARK: Registering

    /// No token from Apple.
    struct NoToken: LocalizedError {
        var errorDescription: String? { "Couldn't reach Apple's notification service. Check this \(ThisDevice.name) is online and try again." }
    }

    private func register(as credentials: ServerCredentials, settings: NotificationSettings,
                          useCurrentProfile: Bool = false) async throws -> NotificationDevice {
        guard let token = await token() else { throw NoToken() }
        let registration = NotificationRegistration(token: token, environment: Self.environment,
                                                    key: NotificationKey.current(accessGroup: Self.keyGroup),
                                                    timeZone: TimeZone.current.identifier, settings: settings,
                                                    useCurrentProfile: useCurrentProfile)
        let device = try await credentials.client.registerForNotifications(registration)
        sentToken = token
        profileToken = credentials.profileToken ?? ""
        isOn = true
        wanted = true
        turnedOff = false
        remember(device)
        return device
    }

    private func remember(_ device: NotificationDevice) {
        profileId = device.profileId
        profileName = device.profileName
        settings = device.settings
    }

    /// The sign-in, as the profile notifications are for (rather than whoever is watching now).
    private func forProfile(_ credentials: ServerCredentials) -> ServerCredentials {
        guard let profileToken else { return credentials }
        var profile = credentials
        profile.profileToken = profileToken.isEmpty ? nil : profileToken
        return profile
    }
}
