import CryptoKit
import Foundation

/// A family invite: what a guest's app needs to join its host's tailnet (with Tailscale built in) and sign in to
/// Flexarr there, carried in the link `https://flexarr.io/join#<this, as base64url JSON>` (or `flexarr://join#…`),
/// or sealed for a TV that showed a code. See docs/remote-guests.md.
public struct GuestInvite: Codable, Sendable, Equatable {
    /// Format version (1).
    public let v: Int
    /// The host's name, for "Join Dan's Flexarr?".
    public let n: String
    /// The server's id, checked once connected.
    public let s: String
    /// The server's tailnet address and Flexarr's port ("100.101.102.103:8383").
    public let a: String
    /// The one-use Tailscale auth key.
    public let k: String
    /// The one-time Flexarr token, swapped for a sign-in (`FlexarrClient.redeemInvite`).
    public let t: String
    /// What this device is called in the host's list.
    public let g: String
    /// The profile the guest watches as.
    public let p: String
    /// When it stops working.
    public let e: Date?

    public var ownerName: String { n.trimmingCharacters(in: .whitespaces).isEmpty ? "Someone" : n }
    public var profileName: String { p }
    public var deviceName: String { g }
    public var tailnetTarget: String { a }
    public var authKey: String { k }
    public var token: String { t }
    public var serverId: String { s }

    /// The node's name on the host's tailnet: "flexarr-guest-grans-ipad".
    public var hostname: String { Self.hostname(for: g) }

    public static func hostname(for deviceName: String) -> String {
        let allowed = Set("abcdefghijklmnopqrstuvwxyz0123456789-")
        var slug = ""
        for character in deviceName.lowercased().folding(options: .diacriticInsensitive, locale: nil) {
            if allowed.contains(character) {
                slug.append(character)
            } else if character == " " || character == "_" || character == ".", slug.last != "-" {
                slug.append("-")
            }
        }
        slug = String(slug.trimmingCharacters(in: CharacterSet(charactersIn: "-")).prefix(26))
        return slug.isEmpty ? "flexarr-guest" : "flexarr-guest-\(slug)"
    }

    /// The apps offer joining someone's Flexarr (the Join button, invite links): once servers have family invites
    /// switched on. The app's Info.plist key `FlexarrGuestInvites` (build setting FLEXARR_GUEST_INVITES) says YES,
    /// or it's a debug build, or `-FlexarrGuestInvites YES` was given at launch.
    public static var guestInvitesOffered: Bool {
        #if DEBUG
        return true
        #else
        return guestInvitesOffered(info: Bundle.main.object(forInfoDictionaryKey: "FlexarrGuestInvites"),
                                   asked: UserDefaults.standard.bool(forKey: "FlexarrGuestInvites"))
        #endif
    }

    /// `info` as Info.plist gives it: a Bool, or the build setting's "YES" or "NO".
    static func guestInvitesOffered(info: Any?, asked: Bool) -> Bool {
        if asked { return true }
        if let on = info as? Bool { return on }
        if let text = info as? String { return ["yes", "true", "1"].contains(text.lowercased()) }
        return false
    }

    public func isExpired(now: Date = Date()) -> Bool {
        e.map { $0 <= now } ?? false
    }

    /// The invite in `text`: a link (https://flexarr.io/join#…, flexarr://join#…), or the part after the # by itself
    /// (what Paste invite finds). Nil when it isn't a whole, well-formed invite.
    public static func parse(_ text: String) -> GuestInvite? {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        let payload: Substring
        if let hash = trimmed.firstIndex(of: "#") {
            let before = trimmed[..<hash].lowercased()
            guard before.hasSuffix("flexarr.io/join") || before.hasPrefix("flexarr://join") else { return nil }
            payload = trimmed[trimmed.index(after: hash)...]
        } else {
            payload = Substring(trimmed)
        }
        return decode(String(payload))
    }

    /// The invite from its base64url JSON (what's after the # in a link, or what a TV opens).
    public static func decode(_ payload: String) -> GuestInvite? {
        var base64 = payload.replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/")
        base64 += String(repeating: "=", count: (4 - base64.count % 4) % 4)
        guard payload.count < 4096, let data = Data(base64Encoded: base64) else { return nil }
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .custom { decoder in
            let text = try decoder.singleValueContainer().decode(String.self)
            guard let date = FlexarrDate.parse(text) else {
                throw DecodingError.dataCorrupted(.init(codingPath: decoder.codingPath, debugDescription: "Not a date: \(text)"))
            }
            return date
        }
        guard let invite = try? decoder.decode(GuestInvite.self, from: data), invite.isWellFormed else { return nil }
        return invite
    }

    /// Version 1, a Tailscale key and token, and a tailnet address (100.64.0.0/10) with a port: an invite can never
    /// point the app anywhere but a host's tailnet.
    var isWellFormed: Bool {
        guard v == 1, k.hasPrefix("tskey-"), !t.isEmpty, t.count <= 128,
              let colon = a.lastIndex(of: ":"), let port = Int(a[a.index(after: colon)...]), (1...65535).contains(port) else { return false }
        return FlexarrClient.isTailscaleAddress(String(a[..<colon]))
    }
}

/// What redeeming an invite gives this device: its own sign-in, locked to the invite's profile.
public struct RedeemedInvite: Decodable, Sendable, Equatable {
    public let apiKey: String
    public let profileId: String
    public let serverId: String
    public let name: String
}

extension FlexarrClient {
    /// Swaps an invite's one-time token for this device's sign-in. Sent through the tunnel (the server only accepts it
    /// from a guest device on its tailnet), with no sign-in of its own. 410: used, cancelled or expired; 503: the
    /// host's invites are paused.
    public func redeemInvite(token: String) async throws -> RedeemedInvite {
        let data = try await send("POST", "invites/redeem", body: ["token": token])
        return try decode(RedeemedInvite.self, from: data)
    }
}

/// Inviting a guest's TV by the code it shows: the TV's side of the meeting at Flexarr's relay
/// (relay/api/pair.js). The TV leaves its code and public key there, its host's server leaves an invite sealed to
/// that key, and the TV collects and opens it.
public struct GuestTvCode: Sendable {
    /// Letters and digits that can't be mistaken for each other when read out over the phone.
    public static let alphabet = Array("ABCDEFGHJKMNPQRSTUVWXYZ23456789")
    public static let relay = URL(string: "https://push.flexarr.io/api/pair")!
    static let info = Data("flexarr-tv-invite".utf8)

    /// "ABCDEFGH", as the relay keeps it.
    public let code: String
    /// Proves to the relay that this TV is the one collecting.
    public let secret: String
    public let privateKey: P256.KeyAgreement.PrivateKey

    public init(code: String? = nil, secret: String? = nil, privateKey: P256.KeyAgreement.PrivateKey = .init()) {
        self.code = code ?? String((0..<8).map { _ in Self.alphabet.randomElement()! })
        self.secret = secret ?? Data((0..<32).map { _ in UInt8.random(in: 0...255) }).base64EncodedString()
            .replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_").replacingOccurrences(of: "=", with: "")
        self.privateKey = privateKey
    }

    /// "ABCD-EFGH", as the TV shows it.
    public var display: String { "\(code.prefix(4))-\(code.suffix(4))" }

    /// The public key as the relay and server take it: X9.63 uncompressed (65 bytes), base64.
    public var publicKey: String { privateKey.publicKey.x963Representation.base64EncodedString() }

    public enum Answer: Sendable, Equatable {
        case waiting
        case invite(GuestInvite)
        /// The code ran out (ten minutes) or was never there: show a new one.
        case expired
    }

    public enum Failure: Error, Equatable {
        /// Another TV is showing this code: make another.
        case codeTaken
        /// The invite couldn't be opened or isn't a well-formed one.
        case badInvite
        case relay(status: Int)
    }

    /// Leaves the code and public key at the relay for ten minutes.
    public func register(session: URLSession = .shared, relay: URL = Self.relay) async throws {
        var request = URLRequest(url: relay)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try JSONEncoder().encode(["code": code, "publicKey": publicKey, "secret": secret])
        let (_, response) = try await session.data(for: request)
        let status = (response as? HTTPURLResponse)?.statusCode ?? 0
        if status == 409 { throw Failure.codeTaken }
        guard status == 201 else { throw Failure.relay(status: status) }
    }

    /// Asks the relay whether the invite has arrived.
    public func poll(session: URLSession = .shared, relay: URL = Self.relay) async throws -> Answer {
        var url = URLComponents(url: relay, resolvingAgainstBaseURL: false)!
        url.queryItems = [URLQueryItem(name: "code", value: code), URLQueryItem(name: "secret", value: secret)]
        let (data, response) = try await session.data(from: url.url!)
        switch (response as? HTTPURLResponse)?.statusCode ?? 0 {
        case 204: return .waiting
        case 404: return .expired
        case 200:
            struct Sealed: Decodable { let serverKey: String; let sealed: String }
            let sealed = try JSONDecoder().decode(Sealed.self, from: data)
            guard let server = Data(base64Encoded: sealed.serverKey), let box = Data(base64Encoded: sealed.sealed) else { throw Failure.badInvite }
            let payload = try open(serverKey: server, sealed: box)
            guard let text = String(data: payload, encoding: .utf8), let invite = GuestInvite.decode(text) else { throw Failure.badInvite }
            return .invite(invite)
        case let status: throw Failure.relay(status: status)
        }
    }

    /// Opens what the server sealed to this TV's key: P-256 ECDH, HKDF-SHA256 (salt: this TV's key then the
    /// server's, info "flexarr-tv-invite"), AES-256-GCM as nonce, ciphertext, tag.
    public func open(serverKey: Data, sealed: Data) throws -> Data {
        let server = try P256.KeyAgreement.PublicKey(x963Representation: serverKey)
        let shared = try privateKey.sharedSecretFromKeyAgreement(with: server)
        let key = shared.hkdfDerivedSymmetricKey(using: SHA256.self, salt: privateKey.publicKey.x963Representation + serverKey,
                                                 sharedInfo: Self.info, outputByteCount: 32)
        return try AES.GCM.open(AES.GCM.SealedBox(combined: sealed), using: key)
    }
}
