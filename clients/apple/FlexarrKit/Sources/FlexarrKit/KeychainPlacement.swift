import Foundation
import Security
import Synchronization

/// Where sign-ins and keys are kept in the Keychain: on this device only, so a backup restored to another phone
/// (or Quick Start) doesn't bring them along. Two phones sharing one device token and notification key would take the
/// command stream from each other and read each other's notifications; the new phone signs in for itself instead.
public enum KeychainPlacement {
    static var accessible: CFString { kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly }

    /// `query` in the right Keychain. On a Mac that's the data-protection Keychain, the one iOS has, where
    /// `accessible` and access groups mean what they do on the iPhone; the Mac's older file Keychain would ask the
    /// user's login password to read an item. A build without a team (ad hoc, `swift test`) isn't allowed in it, and
    /// keeps the file Keychain.
    public static func placed(_ query: [String: Any]) -> [String: Any] {
        #if os(macOS)
        guard usesDataProtectionKeychain else { return query }
        var placed = query
        placed[kSecUseDataProtectionKeychain as String] = true
        return placed
        #else
        return query
        #endif
    }

    #if os(macOS)
    /// Whether this build may use the data-protection Keychain: it needs the app's identifier (a signed build), and
    /// says "missing entitlement" otherwise.
    public static let usesDataProtectionKeychain: Bool = {
        // Reading answers "not found" either way; only saving says whether it's allowed.
        let probe: [String: Any] = [kSecClass as String: kSecClassGenericPassword,
                                    kSecAttrService as String: "io.github.flexarr.keychain-probe",
                                    kSecAttrAccount as String: "probe",
                                    kSecUseDataProtectionKeychain as String: true]
        var add = probe
        add[kSecValueData as String] = Data()
        let status = SecItemAdd(add as CFDictionary, nil)
        SecItemDelete(probe as CFDictionary)
        return status == errSecSuccess || status == errSecDuplicateItem
    }()
    #endif

    private static let moved = Mutex<Set<String>>([])

    /// Items saved before this were kept the old way (they went with backups): moved over once, the first time each
    /// is read. Their contents are untouched.
    static func keepOnThisDevice(_ query: [String: Any]) {
        let key = [query[kSecAttrService as String], query[kSecAttrAccount as String], query[kSecAttrAccessGroup as String]]
            .map { "\($0 ?? "")" }.joined(separator: "|")
        guard moved.withLock({ $0.insert(key).inserted }) else { return }
        var match = query
        match.removeValue(forKey: kSecReturnData as String)
        match.removeValue(forKey: kSecMatchLimit as String)
        SecItemUpdate(match as CFDictionary, [kSecAttrAccessible as String: accessible] as CFDictionary)
    }
}
