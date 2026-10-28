import Foundation
import Security
import Synchronization

/// Where sign-ins and keys are kept in the Keychain: on this device only, so a backup restored to another phone
/// (or Quick Start) doesn't bring them along. Two phones sharing one device token and notification key would take the
/// command stream from each other and read each other's notifications; the new phone signs in for itself instead.
enum KeychainPlacement {
    static var accessible: CFString { kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly }

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
