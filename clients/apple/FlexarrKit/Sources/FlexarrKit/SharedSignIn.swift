import Foundation
import Security

/// The signed-in server and profile, shared with the Apple TV app's Top Shelf extension through a Keychain access
/// group (named in each target's Info.plist as `FlexarrKeychainGroup`). Absent when signed out.
public enum SharedSignIn {
    public struct Value: Codable, Sendable, Equatable {
        public let address: URL
        public let apiKey: String?
        public let profileToken: String?

        public init(address: URL, apiKey: String?, profileToken: String?) {
            self.address = address
            self.apiKey = apiKey
            self.profileToken = profileToken
        }
    }

    private static let service = "io.github.flexarr.shared-sign-in"

    /// The sign-in the Top Shelf may use: the app's own, with the profile last chosen. None for a family guest, and
    /// none on an Apple TV that asks who's watching each time it starts when `watching` (the profile whose titles the
    /// Top Shelf would show) is behind a PIN, or isn't known: the Home screen would show, and Play would start, what
    /// "Who's watching?" keeps for whoever has the PIN. A profile without one can be chosen by anyone at the TV anyway.
    /// The same while nobody has been chosen here yet in a household with `severalProfiles`: "Who's watching?" is up
    /// then too, and the server would answer as the main profile.
    public static func forTopShelf(_ credentials: ServerCredentials?, asksWhoIsWatching: Bool, watching: Profile?,
                                   severalProfiles: Bool = false) -> Value? {
        guard let credentials, !credentials.isGuest else { return nil }
        let nobodyChosen = severalProfiles && credentials.profileToken == nil
        if asksWhoIsWatching || nobodyChosen, watching?.needsPinToChoose != false { return nil }
        return Value(address: credentials.address, apiKey: credentials.apiKey, profileToken: credentials.profileToken)
    }

    /// Saves (or, with nil, removes) the sign-in the extension uses.
    public static func save(_ value: Value?) {
        guard let value, let data = try? JSONEncoder().encode(value) else {
            SecItemDelete(query() as CFDictionary)
            return
        }
        write(data, account: "current")
    }

    public static func load() -> Value? {
        read(account: "current").flatMap { try? JSONDecoder().decode(Value.self, from: $0) }
    }

    /// Also how the Top Shelf extension learns the app's other choices (see `TopShelfStyle`).
    static func write(_ data: Data, account: String) {
        SecItemDelete(query(account: account) as CFDictionary)
        var add = query(account: account)
        add[kSecValueData as String] = data
        add[kSecAttrAccessible as String] = KeychainPlacement.accessible
        SecItemAdd(add as CFDictionary, nil)
    }

    static func read(account: String) -> Data? {
        var read = query(account: account)
        read[kSecReturnData as String] = true
        read[kSecMatchLimit as String] = kSecMatchLimitOne
        var result: AnyObject?
        guard SecItemCopyMatching(read as CFDictionary, &result) == errSecSuccess else { return nil }
        KeychainPlacement.keepOnThisDevice(read)
        return result as? Data
    }

    private static func query(account: String = "current") -> [String: Any] {
        var query: [String: Any] = [kSecClass as String: kSecClassGenericPassword,
                                    kSecAttrService as String: service,
                                    kSecAttrAccount as String: account]
        // "$(AppIdentifierPrefix)…" left unexpanded means an unsigned build: the app's own Keychain will do.
        if let group = Bundle.main.object(forInfoDictionaryKey: "FlexarrKeychainGroup") as? String, !group.contains("$(") {
            query[kSecAttrAccessGroup as String] = group
        }
        return KeychainPlacement.placed(query)
    }
}
