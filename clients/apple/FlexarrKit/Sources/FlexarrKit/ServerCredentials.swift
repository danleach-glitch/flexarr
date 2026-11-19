import Foundation
import Security

/// A saved server connection: the addresses live in UserDefaults, the API key and the chosen profile's token in the
/// Keychain.
public struct ServerCredentials: Sendable, Equatable {
    /// The address this device was connected with, usually on the home network.
    public var address: URL
    public var apiKey: String
    /// Who's watching on this device; nil until someone chooses (the server then acts as the main profile).
    public var profileToken: String?
    /// The server's address for away from home (its Tailscale address), learnt from the server; nil until it has one.
    public var remoteAddress: URL?
    /// False while `remoteAddress` hasn't given the server's id to this device: the server gave it while Tailscale
    /// wasn't connected here. It's then asked before it's sent this sign-in (see `ServerAddresses.remoteConfirmed`).
    public var remoteConfirmed: Bool
    /// A sign-in through the built-in tunnel (a family guest's, or a household device's away from home): how to start
    /// the tunnel into the host's tailnet. `address` is then the tunnel's own address on this device (127.0.0.1), and
    /// there's never a home or away address: the device's network might have something else at the host's home
    /// address, which mustn't be sent this sign-in.
    public var guest: GuestTunnelInfo?
    /// The server's own id (`SystemStatus.serverId`), kept from the first time it gives it, so the home address can be
    /// asked who it is before it's sent this sign-in (see `ServerAddresses`). Nil for servers before 0.55, until the
    /// server has said, and for a guest (whose address is its own tunnel).
    public var serverId: String?

    /// Reaches the server through the built-in tunnel: no AirPlay (a receiver can't reach a tunnel inside this device),
    /// downloads only while the app is open, no Top Shelf, and never a home or away address. A guest or a household
    /// device away from home.
    public var usesTunnel: Bool { guest != nil }

    /// A family guest: the tunnel, with a guest's limits (kept to its profile, no child lock, Leave rather than Sign
    /// out, no commands from the host's devices). Not a household device away from home (`isHouseholdAway`).
    public var isGuest: Bool { guest != nil && guest?.household != true }

    /// A household device away from home: the tunnel, but signed in to its own household profile like a device at home.
    public var isHouseholdAway: Bool { guest?.household == true }

    /// This sign-in as the server's `/me` says it is (`SignInInfo.isGuest`): a tunnel sign-in it calls a guest's is a
    /// guest's, and one it doesn't is a household device's (which covers a device whose redeem answer was lost, or that
    /// joined before the app kept it). Unchanged when it isn't a tunnel sign-in or the server doesn't say.
    public func matching(serverSaysGuest: Bool?) -> ServerCredentials {
        guard var tunnel = guest, let says = serverSaysGuest, says == isHouseholdAway else { return self }
        tunnel.household = !says
        var matched = self
        matched.guest = tunnel
        return matched
    }

    public init(address: URL, apiKey: String, profileToken: String? = nil, remoteAddress: URL? = nil, guest: GuestTunnelInfo? = nil,
                serverId: String? = nil, remoteConfirmed: Bool = true) {
        self.address = address
        self.apiKey = apiKey
        self.profileToken = profileToken
        self.guest = guest
        self.remoteAddress = guest != nil || remoteAddress == address ? nil : remoteAddress
        self.serverId = guest != nil ? nil : serverId
        self.remoteConfirmed = remoteConfirmed
    }

    /// The away address with whether it's confirmed, as `FlexarrClient.remoteAddress` takes and gives it.
    public var away: FlexarrClient.Away? {
        get { remoteAddress.map { FlexarrClient.Away($0, confirmed: remoteConfirmed) } }
        set {
            remoteAddress = usesTunnel || newValue?.url == address ? nil : newValue?.url
            remoteConfirmed = newValue?.confirmed ?? true
        }
    }

    public var client: FlexarrClient { client() }

    /// A client for this sign-in; `onUnauthorized` hears about every 401 it gets. Clients for the same sign-in share
    /// which address is answering (see `ServerAddresses`).
    public func client(onUnauthorized: (@Sendable () -> Void)? = nil) -> FlexarrClient {
        FlexarrClient(addresses: ServerAddresses.shared(home: address, remote: remoteAddress, serverId: usesTunnel ? nil : serverId,
                                                        remoteConfirmed: remoteConfirmed, guest: guest?.target),
                      apiKey: apiKey.isEmpty ? nil : apiKey, profileToken: profileToken, onUnauthorized: onUnauthorized)
    }
}

/// Where a tunnel sign-in's tunnel leads: the host's server on its tailnet, and this device's name there.
public struct GuestTunnelInfo: Codable, Sendable, Equatable {
    /// The server's tailnet address and Flexarr's port ("100.101.102.103:8383").
    public var target: String
    /// This device's name on the host's tailnet.
    public var hostname: String
    /// The host's name, for "Connected to Dan's Flexarr".
    public var ownerName: String
    /// The server's name, from the invite (absent from invites before 0.61 and from guests who joined before).
    public var serverName: String?
    /// A household device away from home (`RedeemedInvite.household`), not a guest. Nil for guests and for sign-ins
    /// saved before the apps knew of household devices (they're guests until `/me` says otherwise).
    public var household: Bool?

    public init(target: String, hostname: String, ownerName: String, serverName: String? = nil, household: Bool? = nil) {
        self.target = target
        self.hostname = hostname
        self.ownerName = ownerName
        self.serverName = serverName
        self.household = household
    }

    /// The same tunnel (where it leads, and as which node), whether or not it's a household device's.
    public func isSameTunnel(as other: GuestTunnelInfo?) -> Bool {
        other.map { $0.target == target && $0.hostname == hostname } ?? false
    }

    /// What to call the host's server: its name, or "Dan's Flexarr".
    public var serverNaming: ServerNaming { ServerNaming(name: serverName, owner: ownerName) }
    public var serverTitle: String { serverNaming.title }

    /// The server's name when there is one, else "Dan's Flexarr" (or "this Flexarr" with no owner either).
    public static func title(serverName: String?, ownerName: String?) -> String {
        ServerNaming(name: serverName, owner: ownerName).title
    }
}

/// What a sentence calls a server: the name an admin chose, else "Dan’s Flexarr", else "this Flexarr". A sentence takes
/// `values` and has all three wordings whole, `{kind, select, named {Leave {server}?} owner {Leave {owner}’s Flexarr?}
/// other {Leave this Flexarr?}}`, so a translator is never handed a piece of one.
public struct ServerNaming: Sendable, Equatable {
    public let name: String?
    public let owner: String?

    public init(name: String?, owner: String?) {
        self.name = name?.trimmingCharacters(in: .whitespaces).nilIfBlank
        self.owner = owner?.trimmingCharacters(in: .whitespaces).nilIfBlank
    }

    public var kind: String { name != nil ? "named" : owner != nil ? "owner" : "other" }

    /// For `t(…)`: `kind`, `server` and `owner`.
    public var values: [String: Any] { ["kind": kind, "server": name ?? "", "owner": owner ?? ""] }

    /// On its own, as a value: "Smith House", "Dan’s Flexarr" or "this Flexarr".
    public var title: String {
        t("{kind, select, named {{server}} owner {{owner}’s Flexarr} other {this Flexarr}}", values)
    }
}

private extension String {
    var nilIfBlank: String? { isEmpty ? nil : self }
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
        let confirmed = defaults.string(forKey: "\(addressKey).remote").flatMap(URL.init(string:))
        let pending = defaults.string(forKey: "\(addressKey).remotePending").flatMap(URL.init(string:))
        return ServerCredentials(address: url, apiKey: readKey(account: text) ?? "",
                                 profileToken: profileToken(account: text),
                                 remoteAddress: confirmed ?? pending,
                                 guest: defaults.data(forKey: "\(addressKey).guest").flatMap { try? JSONDecoder().decode(GuestTunnelInfo.self, from: $0) },
                                 serverId: defaults.string(forKey: "\(addressKey).serverId"),
                                 remoteConfirmed: confirmed != nil || pending == nil)
    }

    /// The last address used, kept after sign-out so the connect screen can prefill it.
    public var lastAddress: String? {
        defaults.string(forKey: "\(addressKey).last")
    }

    public func save(_ credentials: ServerCredentials) throws {
        let account = credentials.address.absoluteString
        // Another server's name (and profile) mustn't stay behind for this one.
        if let previous = defaults.string(forKey: addressKey), previous != account {
            defaults.removeObject(forKey: "\(addressKey).name")
            deleteKey(account: Self.profileAccount(previous))
        }
        try writeKey(credentials.apiKey, account: account)
        defaults.set(account, forKey: addressKey)
        // A guest's address is its own tunnel on this device: no use to offer as the server to connect to again.
        if credentials.guest == nil { defaults.set(account, forKey: "\(addressKey).last") }
        saveProfile(credentials.profileToken)
        saveRemoteAddress(credentials.away)
        saveServerId(credentials.usesTunnel ? nil : credentials.serverId)
        defaults.set(credentials.guest.flatMap { try? JSONEncoder().encode($0) }, forKey: "\(addressKey).guest")
    }

    /// Remembers the server's address for away from home and whether it's confirmed (nil forgets it). A guest never
    /// has one. One not yet confirmed is kept under a key of its own (`remotePending`): versions of the app from
    /// before it could be read `remote` as an address to send the sign-in to without asking, so they never see it.
    public func saveRemoteAddress(_ away: FlexarrClient.Away?) {
        guard away == nil || defaults.data(forKey: "\(addressKey).guest") == nil else { return }
        let confirmed = away?.confirmed != false
        defaults.set(confirmed ? away?.url.absoluteString : nil, forKey: "\(addressKey).remote")
        defaults.set(confirmed ? nil : away?.url.absoluteString, forKey: "\(addressKey).remotePending")
    }

    /// Remembers the server's id (nil forgets it), which the home address is asked for before it's sent the sign-in.
    public func saveServerId(_ id: String?) {
        defaults.set(id?.isEmpty == false ? id : nil, forKey: "\(addressKey).serverId")
    }

    /// Remembers who's watching, in the Keychain beside the API key (on this device only, so not in backups).
    public func saveProfile(_ token: String?) {
        // Earlier versions kept it in UserDefaults, which go with backups.
        defaults.removeObject(forKey: "\(addressKey).profile")
        guard let account = defaults.string(forKey: addressKey) else { return }
        if let token, !token.isEmpty {
            try? writeKey(token, account: Self.profileAccount(account))
        } else {
            deleteKey(account: Self.profileAccount(account))
        }
    }

    /// The chosen profile's token for the sign-in at `account`: from the Keychain, or once, moved there from where
    /// earlier versions kept it.
    private func profileToken(account: String) -> String? {
        if let kept = readKey(account: Self.profileAccount(account)) { return kept }
        guard let old = defaults.string(forKey: "\(addressKey).profile") else { return nil }
        guard (try? writeKey(old, account: Self.profileAccount(account))) != nil else { return old }
        defaults.removeObject(forKey: "\(addressKey).profile")
        return old
    }

    /// The Keychain account the profile token goes under, beside the API key's (the address).
    private static func profileAccount(_ account: String) -> String { "\(account)#profile" }

    /// The profile list as the server last gave it (names, colours, which is watching, which are kids), so the app
    /// still knows who's watching while it can't reach the server. `Profile` is saved as it is, so its new fields must
    /// stay optional, or a list saved by the version before can't be read and offline mode starts with none.
    public func saveProfiles(_ profiles: [Profile]) {
        defaults.set(try? JSONEncoder().encode(profiles), forKey: "\(addressKey).profiles")
    }

    /// The profile list last saved for this sign-in; empty if there's none.
    public var savedProfiles: [Profile] {
        defaults.data(forKey: "\(addressKey).profiles").flatMap { try? JSONDecoder().decode([Profile].self, from: $0) } ?? []
    }

    /// Whether this device was locked to kids profiles when the server last said, so offline it stays a child's.
    public func saveKidsLock(_ locked: Bool) {
        defaults.set(locked, forKey: "\(addressKey).kidsLock")
    }

    public var savedKidsLock: Bool { defaults.bool(forKey: "\(addressKey).kidsLock") }

    /// Remembers what the server is called, so Settings can say so while it can't be reached (nil forgets it).
    public func saveServerName(_ name: String?) {
        defaults.set(name?.trimmingCharacters(in: .whitespaces).isEmpty == false ? name : nil, forKey: "\(addressKey).name")
    }

    /// The server's name as it last gave it; nil before it has said (or for servers before 0.61).
    public var savedServerName: String? { defaults.string(forKey: "\(addressKey).name") }

    public func clear() {
        if let text = defaults.string(forKey: addressKey) {
            deleteKey(account: text)
            deleteKey(account: Self.profileAccount(text))
        }
        defaults.removeObject(forKey: "\(addressKey).profiles")
        defaults.removeObject(forKey: "\(addressKey).kidsLock")
        defaults.removeObject(forKey: addressKey)
        defaults.removeObject(forKey: "\(addressKey).profile")
        defaults.removeObject(forKey: "\(addressKey).remote")
        defaults.removeObject(forKey: "\(addressKey).remotePending")
        defaults.removeObject(forKey: "\(addressKey).serverId")
        defaults.removeObject(forKey: "\(addressKey).guest")
        defaults.removeObject(forKey: "\(addressKey).name")
    }

    // MARK: Keychain

    private func baseQuery(account: String) -> [String: Any] {
        KeychainPlacement.placed([kSecClass as String: kSecClassGenericPassword,
                                  kSecAttrService as String: service,
                                  kSecAttrAccount as String: account])
    }

    private func readKey(account: String) -> String? {
        var query = baseQuery(account: account)
        query[kSecReturnData as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne
        var result: AnyObject?
        guard SecItemCopyMatching(query as CFDictionary, &result) == errSecSuccess, let data = result as? Data else { return nil }
        KeychainPlacement.keepOnThisDevice(query)
        return String(data: data, encoding: .utf8)
    }

    /// Replaces the key in place, so a failed write leaves the old one there rather than none at all.
    private func writeKey(_ key: String, account: String) throws {
        let attributes: [String: Any] = [kSecValueData as String: Data(key.utf8),
                                         kSecAttrAccessible as String: KeychainPlacement.accessible]
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
    public var errorDescription: String? { t("Couldn’t save the API key to the Keychain (error {status}).", ["status": String(status)]) }
}
