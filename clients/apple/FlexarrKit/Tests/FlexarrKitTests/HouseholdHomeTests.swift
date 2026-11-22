import Foundation
import Testing
@testable import FlexarrKit

/// A household device that joined by invite uses the server's home address while it answers, and the built-in tunnel
/// when it doesn't (docs/remote-guests.md, "Household devices at home"). A family guest never has a home address.
/// One at a time: some set `FlexarrClient.onTunnelNeeded`, which every household client here would call.
@Suite(.serialized)
struct HouseholdHomeTests {
    private static let id = "7d1e0b44-household"
    private static let target = "100.101.102.103:8383"
    private static let tunnelAddress = URL(string: "http://127.0.0.1:59871")!
    private static let household = GuestTunnelInfo(target: target, hostname: "flexarr-guest-claire", ownerName: "Dan", household: true)
    private static let guest = GuestTunnelInfo(target: target, hostname: "flexarr-guest-gran", ownerName: "Dan")

    private static let status = StubProtocol.Stub(status: 200, body: Data("""
    {"appName":"Flexarr","version":"0.67.0","osDescription":"macOS","runtimeVersion":".NET 10","startTime":"2026-11-20T12:00:00Z"}
    """.utf8))

    private static func setup(_ id: String) -> StubProtocol.Stub {
        .init(status: 200, body: Data(#"{"open":false,"codeRequired":true,"serverId":"\#(id)"}"#.utf8))
    }

    @Test func aHouseholdSignInKeepsItsHomeAddressAndServerId_AGuestNever() {
        let home = URL(string: "http://192.168.1.106:8383")!
        let household = ServerCredentials(address: Self.tunnelAddress, apiKey: "k", guest: Self.household, serverId: Self.id,
                                          homeAddress: home)
        #expect(household.homeAddress == home)
        #expect(household.serverId == Self.id)
        #expect(household.hasHomeRoute)

        let guest = ServerCredentials(address: Self.tunnelAddress, apiKey: "k", guest: Self.guest, serverId: Self.id, homeAddress: home)
        #expect(guest.homeAddress == nil)
        #expect(guest.serverId == nil)
        #expect(!guest.hasHomeRoute)

        // The server saying it's a guest's after all takes the home address away.
        let corrected = household.matching(serverSaysGuest: true)
        #expect(corrected.isGuest)
        #expect(corrected.homeAddress == nil)
        #expect(corrected.serverId == nil)
    }

    @Test func homeAndTheTunnelArePaired() {
        let home = URL(string: "http://192.168.77.\(Int.random(in: 2...250)):8383")!
        let credentials = ServerCredentials(address: Self.tunnelAddress, apiKey: "k", guest: Self.household, serverId: Self.id,
                                            homeAddress: home)
        let addresses = credentials.addresses
        #expect(addresses.home == home)
        #expect(addresses.remote == Self.tunnelAddress)
        #expect(addresses.tunnel == Self.tunnelAddress)
        #expect(addresses.hasHomeRoute)
        #expect(addresses.asksProof(of: home), "home says who it is before it's sent the sign-in")
        #expect(!addresses.asksProof(of: Self.tunnelAddress))

        // Without the server's id, home can't be asked: the tunnel only.
        let unproven = ServerCredentials(address: Self.tunnelAddress, apiKey: "k", guest: Self.household, homeAddress: home)
        #expect(!unproven.hasHomeRoute)
        #expect(unproven.addresses.home == Self.tunnelAddress)
        #expect(unproven.addresses.remote == nil)
    }

    @Test func theRouteSaysWhetherItsAway() {
        let home = URL(string: "http://192.168.1.106:8383")!
        let addresses = ServerAddresses(home: home, remote: Self.tunnelAddress, serverId: Self.id, guest: Self.target)
        #expect(!addresses.onTunnel)
        #expect(!addresses.awayFromHome)
        #expect(addresses.linkKey == "\(Self.id)|home")
        addresses.reached(Self.tunnelAddress)
        #expect(addresses.onTunnel)
        #expect(addresses.awayFromHome)
        #expect(addresses.linkKey == "\(Self.target)|guest")

        // A guest is always through its tunnel.
        let guest = ServerAddresses(home: Self.tunnelAddress, remote: nil, guest: Self.target)
        #expect(guest.onTunnel)
        #expect(guest.awayFromHome)
        #expect(!guest.hasHomeRoute)
    }

    @Test func theTunnelsPortMovingKeepsTheRoute() {
        let home = URL(string: "http://192.168.200.\(Int.random(in: 2...250)):8383")!
        let first = ServerAddresses.shared(home: home, remote: URL(string: "http://127.0.0.1:59001")!, serverId: Self.id, guest: Self.target)
        first.reached(URL(string: "http://127.0.0.1:59001")!)
        let moved = ServerAddresses.shared(home: home, remote: URL(string: "http://127.0.0.1:59002")!, serverId: Self.id, guest: Self.target)
        #expect(moved !== first)
        #expect(moved.current == URL(string: "http://127.0.0.1:59002")!)
        #expect(moved.onTunnel)
    }

    /// A client for a household pairing whose home is `homeHost` (stubbed) and whose tunnel isn't up.
    private func client(onUnauthorized: (@Sendable () -> Void)? = nil) -> (client: FlexarrClient, home: String) {
        let homeHost = "home-\(UUID().uuidString.lowercased()).local"
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [StubProtocol.self]
        let addresses = ServerAddresses(home: URL(string: "http://\(homeHost):8383")!, remote: Self.tunnelAddress, serverId: Self.id,
                                        guest: Self.target)
        let client = FlexarrClient(addresses: addresses, apiKey: "household-key", session: URLSession(configuration: configuration),
                                   onUnauthorized: onUnauthorized)
        return (client, homeHost)
    }

    private func signedIn(_ host: String) -> [URLRequest] {
        StubProtocol.recorded(host: host).filter { $0.value(forHTTPHeaderField: "X-Api-Key") != nil }
    }

    @Test func homeProvesItselfBeforeItsSentTheKey_AndNeverGetsTheTunnelsToken() async throws {
        let (client, home) = client()
        StubProtocol.register(host: home, Self.status)
        StubProtocol.register(host: home, path: "/api/v1/setup", Self.setup(Self.id))

        _ = try await client.systemStatus()

        let requests = StubProtocol.recorded(host: home)
        #expect(requests.map { $0.url?.path } == ["/api/v1/setup", "/api/v1/system/status"])
        #expect(requests[0].value(forHTTPHeaderField: "X-Api-Key") == nil)
        #expect(requests[1].value(forHTTPHeaderField: "X-Api-Key") == "household-key")
        #expect(requests.allSatisfy { $0.value(forHTTPHeaderField: GuestTunnelToken.header) == nil })
    }

    @Test func somethingElseAtTheHomeAddressGetsNothing_NorDoesATunnelThatIsntUp() async {
        let (client, home) = client()
        StubProtocol.register(host: home, Self.status)
        StubProtocol.register(host: home, path: "/api/v1/setup", Self.setup("someone-else"))

        await #expect(throws: (any Error).self) { _ = try await client.systemStatus() }

        #expect(signedIn(home).isEmpty)
        #expect(signedIn("127.0.0.1").allSatisfy { $0.url?.port != Self.tunnelAddress.port })
    }

    @Test func aRefusalAtHomeIsNeverASignOut() async {
        final class Flag: @unchecked Sendable {
            private let lock = NSLock()
            private var raised = false
            var isRaised: Bool { lock.withLock { raised } }
            func raise() { lock.withLock { raised = true } }
        }
        let refused = Flag()
        let (client, home) = client { refused.raise() }
        StubProtocol.register(host: home, .init(status: 401, body: Data()))
        StubProtocol.register(host: home, path: "/api/v1/setup", Self.setup(Self.id))

        await #expect(throws: (any Error).self) { _ = try await client.systemStatus() }

        #expect(!refused.isRaised, "a server that only takes the key through the tunnel hasn't signed this device out")
        #expect(client.addresses.onTunnel)
        #expect(client.addresses.alternate(to: Self.tunnelAddress) == nil, "home isn't tried again")
        #expect(!(await client.homeAnswers()))
    }

    /// Records each time a request asks for the tunnel, and whether the tunnel was the route then.
    private final class TunnelAsks: @unchecked Sendable {
        private let lock = NSLock()
        private var asks: [Bool] = []
        var recorded: [Bool] { lock.withLock { asks } }
        func ask(onTunnel: Bool) { lock.withLock { asks.append(onTunnel) } }
    }

    @Test func homeIsNeverRacedAgainstTheTunnel_ASlowAnswerIsWaitedFor() async throws {
        let (client, home) = client()
        let asks = TunnelAsks()
        let addresses = client.addresses
        FlexarrClient.onTunnelNeeded = { asks.ask(onTunnel: addresses.onTunnel); return false }
        defer { FlexarrClient.onTunnelNeeded = nil }
        #expect(!addresses.racesFrom(addresses.home))
        #expect(addresses.racesFrom(Self.tunnelAddress), "from the tunnel, home is still asked alongside")
        var slow = Self.status
        slow.delay = 3.2 // longer than `ServerAddresses.alternateDelay`
        StubProtocol.register(host: home, slow)
        StubProtocol.register(host: home, path: "/api/v1/setup", Self.setup(Self.id))

        let status = try await client.systemStatus()

        #expect(status.appName == "Flexarr")
        #expect(asks.recorded.isEmpty, "a slow home never starts the tunnel")
        #expect(!addresses.onTunnel)
    }

    @Test func homeThatCantConnectMovesToTheTunnel_WhichOnlyThenStarts() async {
        let (client, _) = client() // nothing answers at home
        let asks = TunnelAsks()
        let addresses = client.addresses
        FlexarrClient.onTunnelNeeded = { asks.ask(onTunnel: addresses.onTunnel); return false }
        defer { FlexarrClient.onTunnelNeeded = nil }

        await #expect(throws: (any Error).self) { _ = try await client.systemStatus() }

        #expect(asks.recorded == [true], "the tunnel starts once it's the route, and only then")
        #expect(addresses.onTunnel)
    }

    @Test func aRefusalAtHomeOfTheEventStreamIsNeverASignOut() async {
        final class Flag: @unchecked Sendable {
            private let lock = NSLock()
            private var raised = false
            var isRaised: Bool { lock.withLock { raised } }
            func raise() { lock.withLock { raised = true } }
        }
        let refused = Flag()
        let (client, home) = client { refused.raise() }
        StubProtocol.register(host: home, .init(status: 401, body: Data()))
        StubProtocol.register(host: home, path: "/api/v1/setup", Self.setup(Self.id))

        var failure: Error?
        do {
            for try await _ in client.deviceEvents() {}
        } catch {
            failure = error
        }

        #expect((failure as? URLError)?.code == .cannotConnectToHost)
        #expect(!refused.isRaised, "the stream turned away at home hasn't signed this device out")
        #expect(client.addresses.onTunnel)
    }

    @Test func anotherNetworkAsksHomeAgain_AndDropsAQuestionPutOnTheLastOne() async {
        let home = URL(string: "http://192.168.1.106:8383")!
        let addresses = ServerAddresses(home: home, remote: Self.tunnelAddress, serverId: Self.id, guest: Self.target)
        #expect(await addresses.proveHome { _, _ in true })
        #expect(!addresses.needsProof(home))

        addresses.networkChanged()
        #expect(addresses.needsProof(home), "whatever is at home on this network says who it is first")

        // A question still out when the network changes: its answer doesn't count.
        let asked = Task { await addresses.proveHome(fresh: true) { _, _ in
            try? await Task.sleep(for: .milliseconds(300))
            return true
        } }
        try? await Task.sleep(for: .milliseconds(50))
        addresses.networkChanged()
        #expect(!(await asked.value))
        try? await Task.sleep(for: .milliseconds(400))
        #expect(addresses.needsProof(home))
    }

    @Test func theTunnelsPortMovingDoesntCarryHomesProofOver() async {
        let home = URL(string: "http://192.168.201.\(Int.random(in: 2...250)):8383")!
        let first = ServerAddresses.shared(home: home, remote: URL(string: "http://127.0.0.1:59011")!, serverId: Self.id, guest: Self.target)
        first.reached(home) // whatever an earlier test left remembered
        #expect(await first.proveHome { _, _ in true })
        let moved = ServerAddresses.shared(home: home, remote: URL(string: "http://127.0.0.1:59012")!, serverId: Self.id, guest: Self.target)
        #expect(moved.current == home)
        #expect(moved.needsProof(home))
    }

    @Test func aRefusalAtHomeLastsUntilAnotherNetwork() {
        let home = URL(string: "http://192.168.1.106:8383")!
        let addresses = ServerAddresses(home: home, remote: Self.tunnelAddress, serverId: Self.id, guest: Self.target)
        addresses.refusedAtHome()
        #expect(addresses.onTunnel)
        #expect(addresses.alternate(to: Self.tunnelAddress) == nil)
        #expect(!addresses.shouldCheckHome())

        addresses.networkChanged()
        #expect(addresses.alternate(to: Self.tunnelAddress) == home)
        #expect(addresses.shouldCheckHome(), "asked again at once on the new network")
    }

    @Test func homeProvingItselfLateStillBringsTheRequestsBack() async throws {
        let (client, home) = client()
        client.addresses.reached(Self.tunnelAddress)
        var slow = Self.setup(Self.id)
        slow.delay = 0.6
        StubProtocol.register(host: home, path: "/api/v1/setup", slow)

        #expect(!(await client.homeAnswers(within: .milliseconds(100), fresh: true)), "not within the limit")
        #expect(client.addresses.onTunnel)
        try await Task.sleep(for: .milliseconds(900))
        #expect(!client.addresses.onTunnel, "home, once it has said who it is")
    }

    @Test func atHomeOnlyOnceHomeHasSaidWhoItIs() async {
        let home = URL(string: "http://192.168.1.106:8383")!
        let addresses = ServerAddresses(home: home, remote: Self.tunnelAddress, serverId: Self.id, guest: Self.target)
        #expect(!addresses.onTunnel)
        #expect(!addresses.homeProven, "home in use, but not yet proven")
        #expect(await addresses.proveHome { _, _ in true })
        #expect(addresses.homeProven)
        addresses.networkChanged()
        #expect(!addresses.homeProven)
        #expect(await addresses.proveHome { _, _ in true })
        addresses.reached(Self.tunnelAddress)
        #expect(!addresses.homeProven)
    }

    @Test func aHouseholdSignInIsSavedWhereOlderVersionsDoNotLook() throws {
        let suite = "household-home-test-\(UUID().uuidString)"
        let service = "io.github.flexarr.test.\(UUID().uuidString)"
        let store = CredentialStore(service: service, suiteName: suite)
        defer { store.clear(); UserDefaults().removePersistentDomain(forName: suite) }
        let home = URL(string: "http://192.168.1.106:8383")!
        do {
            try store.save(ServerCredentials(address: Self.tunnelAddress, apiKey: "k", guest: Self.household, serverId: Self.id,
                                             homeAddress: home))
        } catch {
            return // No Keychain for command-line tests on every machine.
        }
        let defaults = try #require(UserDefaults(suiteName: suite))
        #expect(defaults.string(forKey: "\(service).address.serverId") == nil)
        #expect(defaults.string(forKey: "\(service).address.remote") == nil)
        let loaded = try #require(store.load())
        #expect(loaded.homeAddress == home)
        #expect(loaded.serverId == Self.id)
        #expect(loaded.hasHomeRoute)

        // What the version before saved: the tunnel and nothing else.
        defaults.removeObject(forKey: "\(service).address.householdHome")
        defaults.removeObject(forKey: "\(service).address.householdServerId")
        let older = try #require(store.load())
        #expect(older.isHouseholdAway)
        #expect(older.homeAddress == nil)
        #expect(!older.hasHomeRoute)
        #expect(older.addresses.home == Self.tunnelAddress)

        // A version from before signed out (leaving them) and joined another server: they aren't that one's.
        try store.save(ServerCredentials(address: Self.tunnelAddress, apiKey: "k", guest: Self.household, serverId: Self.id,
                                         homeAddress: home))
        let elsewhere = GuestTunnelInfo(target: "100.90.80.70:8383", hostname: "flexarr-guest-claire", ownerName: "Sam", household: true)
        defaults.set(try JSONEncoder().encode(elsewhere), forKey: "\(service).address.guest")
        let stale = try #require(store.load())
        #expect(stale.homeAddress == nil)
        #expect(stale.serverId == nil)
        #expect(!stale.hasHomeRoute)

        // A guest's sign-in never keeps them.
        try store.save(ServerCredentials(address: Self.tunnelAddress, apiKey: "k", guest: Self.guest, serverId: Self.id, homeAddress: home))
        #expect(defaults.string(forKey: "\(service).address.householdHome") == nil)
        #expect(store.load()?.homeAddress == nil)
    }

    @Test func meAndTheRedeemAnswerCarryTheHomeAddress() throws {
        let redeemed = #"{"apiKey":"k","profileId":"claire","serverId":"s","name":"iPad","household":true,"homeUrl":"http://192.168.1.106:8383"}"#
        #expect(try JSONDecoder().decode(RedeemedInvite.self, from: Data(redeemed.utf8)).homeAddress == URL(string: "http://192.168.1.106:8383"))
        let before = #"{"apiKey":"k","profileId":"claire","serverId":"s","name":"iPad","household":true}"#
        #expect(try JSONDecoder().decode(RedeemedInvite.self, from: Data(before.utf8)).homeAddress == nil)
        let me = #"{"profileId":"p","method":"device","isAdmin":false,"homeUrl":null}"#
        #expect(try JSONDecoder().decode(SignInInfo.self, from: Data(me.utf8)).homeAddress == nil)
    }

    @Test func aHomeAddressTheAppCantUseIsLeftOut_AndNeverSpoilsTheAnswer() throws {
        for bad in ["", "not a url", "ftp://192.168.1.106:8383", "http://localhost:8383", "http://127.0.0.1:59871",
                    "http://[::1]:8383", "http://0.0.0.0:8383", "192.168.1.106:8383"] {
            let me = #"{"profileId":"p","method":"device","isAdmin":true,"homeUrl":"\#(bad)"}"#
            let decoded = try JSONDecoder().decode(SignInInfo.self, from: Data(me.utf8))
            #expect(decoded.isAdmin, "the rest of /me still reads")
            #expect(decoded.homeAddress == nil, "\(bad)")
        }
        #expect(FlexarrClient.householdHome("https://flexarr.home.arpa/") == URL(string: "https://flexarr.home.arpa"))
        #expect(FlexarrClient.householdHome("http://192.168.1.106:8383") == URL(string: "http://192.168.1.106:8383"))
        let redeemed = #"{"apiKey":"k","profileId":"p","serverId":"s","name":"iPad","homeUrl":"http://localhost:8383"}"#
        #expect(try JSONDecoder().decode(RedeemedInvite.self, from: Data(redeemed.utf8)).homeAddress == nil)
    }
}
