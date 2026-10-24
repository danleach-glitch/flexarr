import Foundation
import Security

/// A saved server connection: the address lives in UserDefaults, the API key in the Keychain.
public struct ServerCredentials: Sendable, Equatable {
    public var address: URL
    public var apiKey: String

    public init(address: URL, apiKey: String) {
        self.address = address
        self.apiKey = apiKey
    }

    public var client: FlexarrClient {
        FlexarrClient(baseURL: address, apiKey: apiKey.isEmpty ? nil : apiKey)
    }
}

public struct CredentialStore: Sendable {
    private let service: String
    private let addressKey: String
    private let suiteName: String?

    public init(service: String = "io.github.flexarr.server", suiteName: String? = nil) {
        self.service = service
        self.addressKey = "\(service).address"
        self.suiteName = suiteName
    }

    private var defaults: UserDefaults {
        suiteName.flatMap(UserDefaults.init(suiteName:)) ?? .standard
    }

    public func load() -> ServerCredentials? {
        guard let text = defaults.string(forKey: addressKey), let url = URL(string: text) else { return nil }
        return ServerCredentials(address: url, apiKey: readKey(account: text) ?? "")
    }

    /// The last address used, kept after sign-out so the connect screen can prefill it.
    public var lastAddress: String? {
        defaults.string(forKey: "\(addressKey).last")
    }

    public func save(_ credentials: ServerCredentials) throws {
        let account = credentials.address.absoluteString
        try writeKey(credentials.apiKey, account: account)
        defaults.set(account, forKey: addressKey)
        defaults.set(account, forKey: "\(addressKey).last")
    }

    public func clear() {
        if let text = defaults.string(forKey: addressKey) { deleteKey(account: text) }
        defaults.removeObject(forKey: addressKey)
    }

    // MARK: Keychain

    private func baseQuery(account: String) -> [String: Any] {
        [kSecClass as String: kSecClassGenericPassword,
         kSecAttrService as String: service,
         kSecAttrAccount as String: account]
    }

    private func readKey(account: String) -> String? {
        var query = baseQuery(account: account)
        query[kSecReturnData as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne
        var result: AnyObject?
        guard SecItemCopyMatching(query as CFDictionary, &result) == errSecSuccess, let data = result as? Data else { return nil }
        return String(data: data, encoding: .utf8)
    }

    private func writeKey(_ key: String, account: String) throws {
        deleteKey(account: account)
        var query = baseQuery(account: account)
        query[kSecValueData as String] = Data(key.utf8)
        query[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlock
        let status = SecItemAdd(query as CFDictionary, nil)
        guard status == errSecSuccess else { throw KeychainError(status: status) }
    }

    private func deleteKey(account: String) {
        SecItemDelete(baseQuery(account: account) as CFDictionary)
    }
}

public struct KeychainError: Error, LocalizedError {
    public let status: OSStatus
    public var errorDescription: String? { "Couldn't save the API key to the Keychain (error \(status))." }
}
