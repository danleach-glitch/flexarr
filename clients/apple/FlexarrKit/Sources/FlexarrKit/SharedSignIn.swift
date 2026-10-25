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

    /// Saves (or, with nil, removes) the sign-in the extension uses.
    public static func save(_ value: Value?) {
        SecItemDelete(query() as CFDictionary)
        guard let value, let data = try? JSONEncoder().encode(value) else { return }
        var add = query()
        add[kSecValueData as String] = data
        add[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlock
        SecItemAdd(add as CFDictionary, nil)
    }

    public static func load() -> Value? {
        var read = query()
        read[kSecReturnData as String] = true
        read[kSecMatchLimit as String] = kSecMatchLimitOne
        var result: AnyObject?
        guard SecItemCopyMatching(read as CFDictionary, &result) == errSecSuccess, let data = result as? Data else { return nil }
        return try? JSONDecoder().decode(Value.self, from: data)
    }

    private static func query() -> [String: Any] {
        var query: [String: Any] = [kSecClass as String: kSecClassGenericPassword,
                                    kSecAttrService as String: service,
                                    kSecAttrAccount as String: "current"]
        // "$(AppIdentifierPrefix)…" left unexpanded means an unsigned build: the app's own Keychain will do.
        if let group = Bundle.main.object(forInfoDictionaryKey: "FlexarrKeychainGroup") as? String, !group.contains("$(") {
            query[kSecAttrAccessGroup as String] = group
        }
        return query
    }
}
