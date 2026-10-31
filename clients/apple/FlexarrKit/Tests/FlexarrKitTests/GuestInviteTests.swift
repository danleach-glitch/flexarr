import CryptoKit
import Foundation
import Testing
@testable import FlexarrKit

/// Family invites: reading an invite link, keeping a guest's sign-in, and a TV opening its sealed invite.
struct GuestInviteTests {
    @Test func joiningIsOfferedOnlyOnceTheBuildSaysSo() {
        #expect(GuestInvite.guestInvitesOffered(info: "YES", asked: false))
        #expect(GuestInvite.guestInvitesOffered(info: true, asked: false))
        #expect(!GuestInvite.guestInvitesOffered(info: "NO", asked: false))
        #expect(!GuestInvite.guestInvitesOffered(info: nil, asked: false))
        #expect(GuestInvite.guestInvitesOffered(info: "NO", asked: true))
    }

    private func payload(_ fields: [String: Any]) -> String {
        var base: [String: Any] = ["v": 1, "n": "Dan", "s": "server-1", "a": "100.101.102.103:8383", "k": "tskey-auth-abc",
                                   "t": "token-123", "g": "Gran’s iPad", "p": "Gran", "e": "2099-01-01T00:00:00Z"]
        base.merge(fields) { _, new in new }
        let data = try! JSONSerialization.data(withJSONObject: base)
        return data.base64EncodedString().replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
    }

    @Test func readsALinkTheAppsLinkOrThePartAfterTheHash() throws {
        let encoded = payload([:])
        for text in ["https://flexarr.io/join#\(encoded)", "flexarr://join#\(encoded)", "  \(encoded)\n"] {
            let invite = try #require(GuestInvite.parse(text))
            #expect(invite.ownerName == "Dan")
            #expect(invite.profileName == "Gran")
            #expect(invite.tailnetTarget == "100.101.102.103:8383")
            #expect(invite.token == "token-123")
            #expect(!invite.isExpired())
        }
    }

    @Test func refusesAnythingThatCouldPointElsewhere() {
        #expect(GuestInvite.parse("https://evil.example/join#\(payload([:]))") == nil)
        #expect(GuestInvite.parse(payload(["a": "192.168.1.106:8383"])) == nil) // not a tailnet address
        #expect(GuestInvite.parse(payload(["a": "100.101.102.103"])) == nil) // no port
        #expect(GuestInvite.parse(payload(["k": "not-a-key"])) == nil)
        #expect(GuestInvite.parse(payload(["v": 2])) == nil)
        #expect(GuestInvite.parse(payload(["t": ""])) == nil)
        #expect(GuestInvite.parse("not an invite") == nil)
    }

    @Test func knowsWhenItHasRunOut() throws {
        let invite = try #require(GuestInvite.parse(payload(["e": "2026-11-03T12:00:00.1234567Z"])))
        #expect(!invite.isExpired(now: Date(timeIntervalSince1970: 1_793_000_000)))
        #expect(invite.isExpired(now: Date(timeIntervalSince1970: 1_800_000_000)))
    }

    @Test func namesTheDeviceOnTheTailnet() {
        #expect(GuestInvite.hostname(for: "Gran’s iPad") == "flexarr-guest-grans-ipad")
        #expect(GuestInvite.hostname(for: "Café TV") == "flexarr-guest-cafe-tv")
        #expect(GuestInvite.hostname(for: "😀") == "flexarr-guest")
        #expect(GuestInvite.hostname(for: String(repeating: "a", count: 80)).count <= 40)
    }

    @Test func aGuestSignInKeepsItsTunnelAndNeverAnAwayAddress() throws {
        let suite = "guest-test-\(UUID().uuidString)"
        let store = CredentialStore(service: "io.github.flexarr.test.\(UUID().uuidString)", suiteName: suite)
        defer { store.clear(); UserDefaults().removePersistentDomain(forName: suite) }
        let tunnel = GuestTunnelInfo(target: "100.101.102.103:8383", hostname: "flexarr-guest-gran", ownerName: "Dan")
        let credentials = ServerCredentials(address: URL(string: "http://127.0.0.1:52000")!, apiKey: "guest-key",
                                            remoteAddress: URL(string: "https://mini.tail.ts.net:8443"), guest: tunnel)
        #expect(credentials.remoteAddress == nil)
        #expect(credentials.isGuest)

        do {
            try store.save(credentials)
        } catch {
            // The Keychain isn't there for command-line tests on every machine; the rest still holds.
            return
        }
        store.saveRemoteAddress(URL(string: "https://mini.tail.ts.net:8443"))
        let loaded = try #require(store.load())
        #expect(loaded.guest == tunnel)
        #expect(loaded.remoteAddress == nil)
        #expect(loaded.apiKey == "guest-key")
    }

    @Test func aTvOpensTheSharedVectorAsTheServerSealsIt() throws {
        let url = try #require(Bundle.module.url(forResource: "tv-invite", withExtension: "json", subdirectory: "Fixtures"))
        let vector = try #require(JSONSerialization.jsonObject(with: Data(contentsOf: url)) as? [String: String])
        let privateKey = try P256.KeyAgreement.PrivateKey(rawRepresentation: try #require(Data(base64Encoded: vector["tvPrivateKey"]!)))
        let tv = GuestTvCode(code: "ABCDEFGH", secret: String(repeating: "x", count: 43), privateKey: privateKey)
        #expect(tv.publicKey == vector["tvPublicKey"])

        let opened = try tv.open(serverKey: try #require(Data(base64Encoded: vector["serverKey"]!)),
                                 sealed: try #require(Data(base64Encoded: vector["sealed"]!)))

        #expect(String(data: opened, encoding: .utf8) == vector["plaintext"])
    }

    @Test func aTvCodeIsEightLettersThatCantBeMisread() {
        let tv = GuestTvCode()
        #expect(tv.code.count == 8)
        #expect(tv.code.allSatisfy { GuestTvCode.alphabet.contains($0) })
        #expect(tv.display == "\(tv.code.prefix(4))-\(tv.code.suffix(4))")
        #expect(tv.secret.count >= 32)
        #expect(Data(base64Encoded: tv.publicKey)?.count == 65)
    }
}
