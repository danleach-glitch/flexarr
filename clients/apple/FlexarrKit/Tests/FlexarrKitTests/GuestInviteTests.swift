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
        #expect(GuestInvite.parse(payload(["a": "192.168.1.50:8383"])) == nil) // not a tailnet address
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
        store.saveRemoteAddress(FlexarrClient.Away(URL(string: "https://mini.tail.ts.net:8443")!))
        let loaded = try #require(store.load())
        #expect(loaded.guest == tunnel)
        #expect(loaded.remoteAddress == nil)
        #expect(loaded.apiKey == "guest-key")
    }

    @Test func theRedeemAnswerSaysWhetherItsAHouseholdDevice() throws {
        let household = #"{"apiKey":"k","profileId":"claire","serverId":"s","name":"Claire’s iPad","household":true}"#
        #expect(try JSONDecoder().decode(RedeemedInvite.self, from: Data(household.utf8)).household == true)
        // Servers before 0.67 never say: a guest.
        let before = #"{"apiKey":"k","profileId":"gran","serverId":"s","name":"Gran’s iPad"}"#
        #expect(try JSONDecoder().decode(RedeemedInvite.self, from: Data(before.utf8)).household == nil)
    }

    @Test func aHouseholdDeviceAwayUsesTheTunnelWithoutAGuestsLimits() {
        let tunnel = GuestTunnelInfo(target: "100.101.102.103:8383", hostname: "flexarr-guest-claire", ownerName: "Dan", household: true)
        let away = ServerCredentials(address: URL(string: "http://127.0.0.1:52000")!, apiKey: "key",
                                     remoteAddress: URL(string: "https://mini.tail.ts.net:8443"), guest: tunnel, serverId: "abc")
        #expect(away.usesTunnel)
        #expect(!away.isGuest)
        #expect(away.isHouseholdAway)
        // Still only ever the tunnel: no away address, no home id to ask.
        #expect(away.remoteAddress == nil)
        #expect(away.serverId == nil)

        let guest = ServerCredentials(address: URL(string: "http://127.0.0.1:52000")!, apiKey: "key",
                                      guest: GuestTunnelInfo(target: "100.101.102.103:8383", hostname: "flexarr-guest-gran", ownerName: "Dan"))
        #expect(guest.usesTunnel && guest.isGuest && !guest.isHouseholdAway)

        let home = ServerCredentials(address: URL(string: "http://192.168.1.2:8383")!, apiKey: "key")
        #expect(!home.usesTunnel && !home.isGuest && !home.isHouseholdAway)
    }

    @Test func meDecidesWhetherATunnelSignInIsAGuests() {
        let tunnel = GuestTunnelInfo(target: "100.101.102.103:8383", hostname: "flexarr-guest-claire", ownerName: "Dan")
        let saved = ServerCredentials(address: URL(string: "http://127.0.0.1:52000")!, apiKey: "key", guest: tunnel)
        // Joined before the app kept it (or the redeem answer was lost): /me says it's no guest.
        let household = saved.matching(serverSaysGuest: false)
        #expect(household.isHouseholdAway)
        #expect(household.guest?.isSameTunnel(as: tunnel) == true)
        #expect(household.matching(serverSaysGuest: false) == household)
        #expect(household.matching(serverSaysGuest: true).isGuest)
        // Nothing said, or a sign-in at home: as it was.
        #expect(saved.matching(serverSaysGuest: nil) == saved)
        #expect(saved.matching(serverSaysGuest: true) == saved)
        let home = ServerCredentials(address: URL(string: "http://192.168.1.2:8383")!, apiKey: "key")
        #expect(home.matching(serverSaysGuest: false) == home)
    }

    @Test func aSignInIsTheSameOneAfterTheServerSaysWhoseItIs() {
        let tunnel = GuestTunnelInfo(target: "100.101.102.103:8383", hostname: "flexarr-guest-claire", ownerName: "Dan")
        let saved = ServerCredentials(address: URL(string: "http://127.0.0.1:52000")!, apiKey: "key", guest: tunnel)
        // Corrected to a household device's by /me: a 401 from a client made before still counts.
        let corrected = saved.matching(serverSaysGuest: false)
        #expect(corrected != saved)
        #expect(corrected.isSameSignIn(as: saved))
        // The tunnel's port moved: the same sign-in.
        var moved = corrected
        moved.address = URL(string: "http://127.0.0.1:52001")!
        #expect(moved.isSameSignIn(as: saved))
        // Another key, another profile, another tunnel, or none: not.
        #expect(!saved.isSameSignIn(as: ServerCredentials(address: saved.address, apiKey: "other", guest: tunnel)))
        #expect(!saved.isSameSignIn(as: ServerCredentials(address: saved.address, apiKey: "key", profileToken: "p", guest: tunnel)))
        #expect(!saved.isSameSignIn(as: ServerCredentials(address: saved.address, apiKey: "key",
                                                          guest: GuestTunnelInfo(target: "100.1.1.1:8383", hostname: "x", ownerName: "Dan"))))
        let home = ServerCredentials(address: URL(string: "http://192.168.1.2:8383")!, apiKey: "key")
        #expect(!saved.isSameSignIn(as: home))
        #expect(!home.isSameSignIn(as: saved))
        #expect(home.isSameSignIn(as: home))
        #expect(!home.isSameSignIn(as: nil))
    }

    @Test func aHouseholdDeviceIsSavedAsOneAndOlderSignInsReadAsGuests() throws {
        let suite = "household-test-\(UUID().uuidString)"
        let service = "io.github.flexarr.test.\(UUID().uuidString)"
        let store = CredentialStore(service: service, suiteName: suite)
        defer { store.clear(); UserDefaults().removePersistentDomain(forName: suite) }
        let tunnel = GuestTunnelInfo(target: "100.101.102.103:8383", hostname: "flexarr-guest-claire", ownerName: "Dan", household: true)
        do {
            try store.save(ServerCredentials(address: URL(string: "http://127.0.0.1:52000")!, apiKey: "key", guest: tunnel))
        } catch {
            return // No Keychain for command-line tests on every machine.
        }
        #expect(store.load()?.isHouseholdAway == true)

        // What the version before saved: no household at all.
        let defaults = try #require(UserDefaults(suiteName: suite))
        let older = #"{"target":"100.101.102.103:8383","hostname":"flexarr-guest-gran","ownerName":"Dan"}"#
        defaults.set(Data(older.utf8), forKey: "\(service).address.guest")
        let loaded = try #require(store.load())
        #expect(loaded.isGuest)
        #expect(loaded.guest?.household == nil)
    }

    @Test func anUnconfirmedAwayAddressIsKeptWhereOlderVersionsDoNotLook() throws {
        let suite = "away-test-\(UUID().uuidString)"
        let service = "io.github.flexarr.test.\(UUID().uuidString)"
        let store = CredentialStore(service: service, suiteName: suite)
        defer { store.clear(); UserDefaults().removePersistentDomain(forName: suite) }
        let away = URL(string: "https://mini.tail.ts.net:8443")!
        do {
            try store.save(ServerCredentials(address: URL(string: "http://192.168.1.2:8383")!, apiKey: "key", serverId: "abc"))
        } catch {
            return // No Keychain for command-line tests on every machine.
        }
        let defaults = try #require(UserDefaults(suiteName: suite))

        store.saveRemoteAddress(FlexarrClient.Away(away, confirmed: false))
        #expect(defaults.string(forKey: "\(service).address.remote") == nil, "what older versions send the sign-in to")
        #expect(store.load()?.away == FlexarrClient.Away(away, confirmed: false))

        store.saveRemoteAddress(FlexarrClient.Away(away))
        #expect(defaults.string(forKey: "\(service).address.remotePending") == nil)
        #expect(store.load()?.away == FlexarrClient.Away(away))

        store.saveRemoteAddress(nil)
        #expect(store.load()?.away == nil)
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

    @Test func aTvPollsWithItsSecretInAHeaderNotTheAddress() async throws {
        let host = "relay-\(UUID().uuidString.lowercased()).local"
        StubProtocol.register(host: host, StubProtocol.Stub(status: 204, body: Data()))
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [StubProtocol.self]
        let tv = GuestTvCode()

        let answer = try await tv.poll(session: URLSession(configuration: configuration), relay: URL(string: "https://\(host)/api/pair")!)

        guard case .waiting = answer else { Issue.record("expected waiting"); return }
        let request = try #require(StubProtocol.recorded(host: host).first)
        let query = URLComponents(url: try #require(request.url), resolvingAgainstBaseURL: false)?.queryItems ?? []
        #expect(query.map(\.name) == ["code"])
        #expect(request.value(forHTTPHeaderField: "X-Pair-Secret") == tv.secret)
    }

    @Test func aTvCodeIsEightLettersThatCantBeMisread() {
        let tv = GuestTvCode()
        #expect(tv.code.count == 8)
        #expect(tv.code.allSatisfy { GuestTvCode.alphabet.contains($0) })
        #expect(tv.display == "\(tv.code.prefix(4))-\(tv.code.suffix(4))")
        #expect(tv.secret.count >= 32)
        #expect(Data(base64Encoded: tv.publicKey)?.count == 65)
    }

    /// The vectors in docs/remote-guests.md ("The code proves the key"), which relay/test/pair.test.js checks too.
    @Test func aTvCodeIsMadeFromItsPublicKey() {
        #expect(GuestTvCode.code(forPublicKey: "BAabxf2q9hMu/dxG+nGp5nYWsRDth260mBqx6U/8TU9miOPz86o/f4xuMSzrZ/YVxS1EHcdBY/JxFPKujcWATRA=") == "P5GSPMQP")
        #expect(GuestTvCode.code(forPublicKey: "BJeO6XNfhIDBbIdQxtPpYlj3Cjk7nlUzjg+xJFmk20ChvzfI1jdWs2T5CgsWu1MmBW4iXHxrMfK9/MTDb9Nb1mM=") == "VFAZC453")
        #expect(GuestTvCode.code(forPublicKey: "BBikfwYmLGipShyfrqxMWA/kv+NWBDrgrZotgMESONlNQfZ0um1pjfF8J+1iarUDMOzmd5+F9czYFE5xxV+pIvo=") == "7EAR565J")

        let tv = GuestTvCode()
        #expect(tv.code == GuestTvCode.code(forPublicKey: tv.publicKey))
        #expect(GuestTvCode().code != tv.code)
    }
}
