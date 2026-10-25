import Foundation
import Security

/// A saved server connection: the addresses and chosen profile live in UserDefaults, the API key in the Keychain.
public struct ServerCredentials: Sendable, Equatable {
    /// The address this device was connected with, usually on the home network.
    public var address: URL
    public var apiKey: String
    /// Who's watching on this device; nil until someone chooses (the server then acts as the main profile).
    public var profileToken: String?
    /// The server's address for away from home (its Tailscale address), learnt from the server; nil until it has one.
    public var remoteAddress: URL?

    public init(address: URL, apiKey: String, profileToken: String? = nil, remoteAddress: URL? = nil) {
        self.address = address
        self.apiKey = apiKey
        self.profileToken = profileToken
        self.remoteAddress = remoteAddress == address ? nil : remoteAddress
    }

    public var client: FlexarrClient { client() }

    /// A client for this sign-in; `onUnauthorized` hears about every 401 it gets. Clients for the same sign-in share
    /// which address is answering (see `ServerAddresses`).
    public func client(onUnauthorized: (@Sendable () -> Void)? = nil) -> FlexarrClient {
        FlexarrClient(addresses: ServerAddresses.shared(home: address, remote: remoteAddress),
                      apiKey: apiKey.isEmpty ? nil : apiKey, profileToken: profileToken, onUnauthorized: onUnauthorized)
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
        return ServerCredentials(address: url, apiKey: readKey(account: text) ?? "",
                                 profileToken: defaults.string(forKey: "\(addressKey).profile"),
                                 remoteAddress: defaults.string(forKey: "\(addressKey).remote").flatMap(URL.init(string:)))
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
        saveProfile(credentials.profileToken)
        saveRemoteAddress(credentials.remoteAddress)
    }

    /// Remembers the server's address for away from home (nil forgets it).
    public func saveRemoteAddress(_ url: URL?) {
        defaults.set(url?.absoluteString, forKey: "\(addressKey).remote")
    }

    /// Remembers who's watching. The token only works alongside the API key, so it needn't be in the Keychain.
    public func saveProfile(_ token: String?) {
        defaults.set(token, forKey: "\(addressKey).profile")
    }

    public func clear() {
        if let text = defaults.string(forKey: addressKey) { deleteKey(account: text) }
        defaults.removeObject(forKey: addressKey)
        defaults.removeObject(forKey: "\(addressKey).profile")
        defaults.removeObject(forKey: "\(addressKey).remote")
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

    /// Replaces the key in place, so a failed write leaves the old one there rather than none at all.
    private func writeKey(_ key: String, account: String) throws {
        let attributes: [String: Any] = [kSecValueData as String: Data(key.utf8),
                                         kSecAttrAccessible as String: kSecAttrAccessibleAfterFirstUnlock]
        var status = SecItemUpdate(baseQuery(account: account) as CFDictionary, attributes as CFDictionary)
        if status == errSecItemNotFound {
            let query = baseQuery(account: account).merging(attributes) { _, new in new }
            status = SecItemAdd(query as CFDictionary, nil)
        }
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
