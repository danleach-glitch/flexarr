import CryptoKit
import Foundation
import Security

/// What a phone is told about. Upcoming (each morning, what's airing that day) is off until asked for.
public struct NotificationSettings: Codable, Sendable, Hashable {
    public var films: Bool
    public var shows: Bool
    public var podcasts: Bool
    public var recordings: Bool
    /// When something this profile requested is ready.
    public var requests: Bool
    public var upcoming: Bool
    /// The hour of the morning list, in the phone's time zone.
    public var upcomingHour: Int

    public init(films: Bool = true, shows: Bool = true, podcasts: Bool = true, recordings: Bool = true,
                requests: Bool = true, upcoming: Bool = false, upcomingHour: Int = 9) {
        self.films = films
        self.shows = shows
        self.podcasts = podcasts
        self.recordings = recordings
        self.requests = requests
        self.upcoming = upcoming
        self.upcomingHour = upcomingHour
    }
}

/// This phone as the server has it registered for notifications (no key, no token).
public struct NotificationDevice: Codable, Sendable, Hashable {
    /// The one profile this phone hears about.
    public let profileId: String
    public let profileName: String?
    public let films: Bool
    public let shows: Bool
    public let podcasts: Bool
    public let recordings: Bool
    public let requests: Bool
    public let upcoming: Bool
    public let upcomingHour: Int
    public let timeZone: String?
    /// False when the server has notifications turned off: nothing will arrive, however it's set here.
    public let relay: Bool

    public var settings: NotificationSettings {
        NotificationSettings(films: films, shows: shows, podcasts: podcasts, recordings: recordings, requests: requests,
                             upcoming: upcoming, upcomingHour: upcomingHour)
    }
}

/// What `PUT /notifications/device` takes: where to reach the phone, the key to seal its notifications with, and
/// what it wants to hear about.
public struct NotificationRegistration: Encodable, Sendable, Hashable {
    /// The APNs device token, in hex.
    public var token: String
    /// "sandbox" for development builds, "production" otherwise.
    public var environment: String
    /// The 32-byte key, in base64.
    public var key: String
    public var timeZone: String
    public var settings: NotificationSettings
    /// Notifications for the profile the phone is on now. Otherwise a phone already registered keeps its profile,
    /// whoever's watching: the phone's sign-in follows the profile last chosen on it.
    public var useCurrentProfile: Bool

    public init(token: String, environment: String, key: SymmetricKey, timeZone: String, settings: NotificationSettings,
                useCurrentProfile: Bool = false) {
        self.token = token
        self.environment = environment
        self.key = key.withUnsafeBytes { Data($0) }.base64EncodedString()
        self.timeZone = timeZone
        self.settings = settings
        self.useCurrentProfile = useCurrentProfile
    }

    private enum CodingKeys: String, CodingKey {
        case token, environment, key, timeZone, films, shows, podcasts, recordings, requests, upcoming, upcomingHour, useCurrentProfile
    }

    // The settings sit beside the rest, not in an object of their own.
    public func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode(token, forKey: .token)
        try container.encode(environment, forKey: .environment)
        try container.encode(key, forKey: .key)
        try container.encode(timeZone, forKey: .timeZone)
        try container.encode(settings.films, forKey: .films)
        try container.encode(settings.shows, forKey: .shows)
        try container.encode(settings.podcasts, forKey: .podcasts)
        try container.encode(settings.recordings, forKey: .recordings)
        try container.encode(settings.requests, forKey: .requests)
        try container.encode(settings.upcoming, forKey: .upcoming)
        try container.encode(settings.upcomingHour, forKey: .upcomingHour)
        try container.encode(useCurrentProfile, forKey: .useCurrentProfile)
    }
}

extension FlexarrClient {
    /// How this phone is registered for notifications; nil when it isn't (or the server is from before them).
    public func notificationDevice() async throws -> NotificationDevice? {
        do {
            return try await get("notifications/device")
        } catch FlexarrClientError.http(status: 404, _) {
            return nil
        }
    }

    /// Registers this phone (or updates it), for the profile the client acts as.
    public func registerForNotifications(_ registration: NotificationRegistration) async throws -> NotificationDevice {
        let data = try await send("PUT", "notifications/device", body: registration)
        return try decode(NotificationDevice.self, from: data)
    }

    /// Stops this phone's notifications.
    public func stopNotifications() async throws {
        _ = try await send("DELETE", "notifications/device")
    }

    /// Sends a test notification now. The relay or Apple refusing it comes back as the server's reason.
    public func sendTestNotification() async throws {
        _ = try await send("POST", "notifications/device/test")
    }
}

/// A notification as the server sealed it, opened on the phone by its notification extension.
public struct PushPayload: Decodable, Sendable, Hashable {
    public let title: String
    public let body: String
    /// Decides the buttons (`flexarr.video`, `flexarr.podcast`, …).
    public let category: String?
    /// Groups a show's notifications together.
    public let thread: String?
    /// Artwork, a public URL.
    public let image: URL?
    /// What tapping opens and what Play plays (`item`, `play`, `podcast`, `episode`).
    public let info: [String: String]

    private enum CodingKeys: String, CodingKey {
        case title = "t", body = "b", category = "c", thread = "g", image = "i", info = "u"
    }

    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        title = try container.decodeIfPresent(String.self, forKey: .title) ?? ""
        body = try container.decodeIfPresent(String.self, forKey: .body) ?? ""
        category = try container.decodeIfPresent(String.self, forKey: .category)
        thread = try container.decodeIfPresent(String.self, forKey: .thread)
        image = (try? container.decodeIfPresent(String.self, forKey: .image)).flatMap(URL.init(string:))
        info = (try? container.decodeIfPresent([String: String].self, forKey: .info)) ?? [:]
    }

    /// Opens what the relay carried: base64 of nonce, ciphertext and tag (`AES.GCM.SealedBox.combined`).
    public static func open(_ sealed: String, key: SymmetricKey) throws -> PushPayload {
        guard let data = Data(base64Encoded: sealed) else { throw FlexarrClientError.invalidResponse }
        let plain = try AES.GCM.open(AES.GCM.SealedBox(combined: data), using: key)
        return try JSONDecoder().decode(PushPayload.self, from: plain)
    }
}

/// The key this phone's notifications are sealed with, made once and kept in the Keychain where the notification
/// extension can read it too: the app group, used as a Keychain access group. A build without the app group keeps
/// it in the app's own Keychain, which the extension can't see (notifications then say only "Something new is ready").
public enum NotificationKey {
    private static let service = "io.github.flexarr.notifications"
    private static let account = "key"

    public static func load(accessGroup: String?) -> SymmetricKey? {
        for group in groups(accessGroup) {
            var query = query(group)
            query[kSecReturnData as String] = true
            query[kSecMatchLimit as String] = kSecMatchLimitOne
            var result: AnyObject?
            if SecItemCopyMatching(query as CFDictionary, &result) == errSecSuccess, let data = result as? Data, data.count == 32 {
                KeychainPlacement.keepOnThisDevice(query)
                return SymmetricKey(data: data)
            }
        }
        return nil
    }

    /// The saved key, or a new one saved now.
    public static func current(accessGroup: String?) -> SymmetricKey {
        if let key = load(accessGroup: accessGroup) { return key }
        let key = SymmetricKey(size: .bits256)
        let data = key.withUnsafeBytes { Data($0) }
        for group in groups(accessGroup) {
            var add = query(group)
            add[kSecValueData as String] = data
            // The extension decrypts on a locked phone.
            add[kSecAttrAccessible as String] = KeychainPlacement.accessible
            if SecItemAdd(add as CFDictionary, nil) == errSecSuccess { break }
        }
        return key
    }

    public static func delete(accessGroup: String?) {
        for group in groups(accessGroup) { SecItemDelete(query(group) as CFDictionary) }
    }

    /// The shared group first, then (nil) the app's own Keychain.
    private static func groups(_ accessGroup: String?) -> [String?] {
        guard let accessGroup, !accessGroup.isEmpty, !accessGroup.contains("$(") else { return [nil] }
        return [accessGroup, nil]
    }

    private static func query(_ group: String?) -> [String: Any] {
        var query: [String: Any] = [kSecClass as String: kSecClassGenericPassword,
                                    kSecAttrService as String: service,
                                    kSecAttrAccount as String: account]
        if let group { query[kSecAttrAccessGroup as String] = group }
        return query
    }
}
