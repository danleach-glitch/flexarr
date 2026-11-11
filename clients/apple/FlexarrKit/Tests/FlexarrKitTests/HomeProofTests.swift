import Foundation
import Testing
@testable import FlexarrKit

/// The home address says who it is before it's sent the sign-in, so on someone else's network whatever answers at
/// the same address gets nothing (see `ServerAddresses`).
struct HomeProofTests {
    private static let id = "0f3c1a52-home"

    private static func setup(_ id: String?) -> StubProtocol.Stub {
        let field = id.map { #","serverId":"\#($0)""# } ?? ""
        return .init(status: 200, body: Data(#"{"open":false,"codeRequired":true\#(field)}"#.utf8))
    }

    private static let status = StubProtocol.Stub(status: 200, body: Data("""
    {"appName":"Flexarr","version":"0.62.0","osDescription":"macOS","runtimeVersion":".NET 10","startTime":"2026-10-25T12:00:00Z"}
    """.utf8))

    /// A signed-in client that knows its server's id. `home`: what the home address answers everywhere but
    /// `/setup` (nil: nothing connects there); `whoIsHome`: the id it gives there (nil: none).
    private func client(home: StubProtocol.Stub?, whoIsHome: String? = HomeProofTests.id, remote: StubProtocol.Stub? = nil,
                        hasRemote: Bool = false, onUnauthorized: (@Sendable () -> Void)? = nil)
        -> (client: FlexarrClient, home: String, remote: String) {
        let homeHost = "home-\(UUID().uuidString.lowercased()).local"
        let remoteHost = "remote-\(UUID().uuidString.lowercased()).ts.net"
        if let home {
            StubProtocol.register(host: homeHost, home)
            StubProtocol.register(host: homeHost, path: "/api/v1/setup", Self.setup(whoIsHome))
        }
        if let remote { StubProtocol.register(host: remoteHost, remote) }
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [StubProtocol.self]
        let addresses = ServerAddresses(home: URL(string: "http://\(homeHost):8383")!,
                                        remote: hasRemote || remote != nil ? URL(string: "https://\(remoteHost):8443")! : nil,
                                        serverId: Self.id)
        let client = FlexarrClient(addresses: addresses, apiKey: "secret", profileToken: "profile",
                                   session: URLSession(configuration: configuration), onUnauthorized: onUnauthorized)
        return (client, homeHost, remoteHost)
    }

    /// The requests `host` was sent that carried the sign-in.
    private func signedIn(_ host: String) -> [URLRequest] {
        StubProtocol.recorded(host: host).filter {
            $0.value(forHTTPHeaderField: "X-Api-Key") != nil || $0.value(forHTTPHeaderField: "X-Flexarr-Profile") != nil
        }
    }

    private final class Flag: @unchecked Sendable {
        private let lock = NSLock()
        private var raised = false
        var isRaised: Bool { lock.withLock { raised } }
        func raise() { lock.withLock { raised = true } }
    }

    private final class Counter: @unchecked Sendable {
        private let lock = NSLock()
        private var value = 0
        var count: Int { lock.withLock { value } }
        func add() { lock.withLock { value += 1 } }
    }

    @Test func homeIsAskedWhoItIsOnceAndThenSentTheSignIn() async throws {
        let (client, home, _) = client(home: Self.status)

        _ = try await client.systemStatus()
        _ = try await client.systemStatus()

        let requests = StubProtocol.recorded(host: home)
        #expect(requests.map { $0.url?.path } == ["/api/v1/setup", "/api/v1/system/status", "/api/v1/system/status"])
        #expect(requests[0].value(forHTTPHeaderField: "X-Api-Key") == nil, "asked without the sign-in")
        #expect(requests[0].value(forHTTPHeaderField: "X-Flexarr-Profile") == nil)
        #expect(requests[1].value(forHTTPHeaderField: "X-Api-Key") == "secret")
    }

    @Test func requestsStartingTogetherAskOnce() async throws {
        let (client, home, _) = client(home: Self.status)

        try await withThrowingTaskGroup(of: Void.self) { group in
            for _ in 0..<6 { group.addTask { _ = try await client.systemStatus() } }
            try await group.waitForAll()
        }

        #expect(StubProtocol.recorded(host: home).filter { $0.url?.path == "/api/v1/setup" }.count == 1)
        #expect(signedIn(home).count == 6)
    }

    @Test func anotherServerAtTheHomeAddressIsSentNothingAndCannotSignTheAppOut() async {
        // Someone else's network: whatever is at the home address refuses everything, as a server that had signed
        // this device out would.
        let refused = Flag()
        let (client, home, _) = client(home: .init(status: 401, body: Data()), whoIsHome: "someone-else") { refused.raise() }

        do {
            _ = try await client.systemStatus()
            Issue.record("expected the home address to count as not there")
        } catch {
            #expect(FlexarrClient.couldNotConnect(error), "not there, rather than signed out: \(error)")
        }
        await #expect(throws: (any Error).self) { try await client.setWatched("item-1", watched: true) }

        #expect(signedIn(home).isEmpty)
        #expect(!refused.isRaised)
    }

    @Test func somethingThatGivesNoIdIsNotTheServerEither() async {
        let (client, home, _) = client(home: Self.status, whoIsHome: nil)
        await #expect(throws: (any Error).self) { try await client.systemStatus() }
        #expect(signedIn(home).isEmpty)

        // Not Flexarr at all: no `/setup` to ask.
        let (other, otherHome, _) = self.client(home: nil)
        StubProtocol.register(host: otherHome, .init(status: 404, body: Data()))
        await #expect(throws: (any Error).self) { try await other.systemStatus() }
        #expect(signedIn(otherHome).isEmpty)
    }

    @Test func withAnotherServerAtHomeTheRemoteAddressIsUsed() async throws {
        let (client, home, remote) = client(home: .init(status: 401, body: Data()), whoIsHome: "someone-else", remote: Self.status)

        _ = try await client.systemStatus()
        try await client.setWatched("item-1", watched: true)

        #expect(client.addresses.isAway)
        #expect(signedIn(home).isEmpty)
        #expect(signedIn(remote).count == 2)
    }

    @Test func aWriteIsNotSentToAnotherServerAtHome() async throws {
        // A write goes to the address in use first: home, until something has answered elsewhere.
        let (client, home, remote) = client(home: .init(status: 204, body: Data()), whoIsHome: "someone-else",
                                            remote: .init(status: 204, body: Data()))

        try await client.setWatched("item-1", watched: true)

        #expect(signedIn(home).isEmpty)
        #expect(signedIn(remote).count == 1)
    }

    @Test func awayWithTheRemoteAddressNotAnsweringAnotherServerAtHomeIsStillSentNothing() async {
        // On the remote address, which stops answering: a GET, then a write, turn to the home address, where
        // something else answers. The same turn a GET takes when the remote address is slow.
        let refused = Flag()
        let (client, home, remote) = client(home: .init(status: 401, body: Data()), whoIsHome: "someone-else", hasRemote: true) { refused.raise() }
        client.addresses.reached(client.addresses.remote!)

        await #expect(throws: (any Error).self) { try await client.systemStatus() }
        await #expect(throws: (any Error).self) { try await client.setWatched("item-1", watched: true) }

        #expect(signedIn(home).isEmpty)
        #expect(!refused.isRaised)
        #expect(client.addresses.isAway)
        #expect(signedIn(remote).count == 2, "each was tried at the address in use first")
    }

    @Test func awayWithTheRemoteAddressNotAnsweringTheServerAtHomeIsAskedThenUsed() async throws {
        // Home again with Tailscale off, the app still on its remote address: home says who it is, then gets the request.
        let (client, home, _) = client(home: Self.status, hasRemote: true)
        client.addresses.reached(client.addresses.remote!)

        _ = try await client.systemStatus()

        #expect(!client.addresses.isAway)
        #expect(StubProtocol.recorded(host: home).map { $0.url?.path } == ["/api/v1/setup", "/api/v1/system/status"])
        #expect(signedIn(home).count == 1)
    }

    @Test func oneRequestGivingUpDoesNotTakeTheAnswerFromTheRest() async {
        let home = URL(string: "http://192.168.1.20:8383")!
        let addresses = ServerAddresses(home: home, remote: nil, serverId: Self.id)
        let asked = Flag()
        let (gate, open) = AsyncStream<Void>.makeStream()
        // Home takes its time to say who it is; counts how often it's asked.
        let times = Counter()
        let ask: @Sendable (URL, String) async -> Bool = { _, id in
            times.add()
            asked.raise()
            for await _ in gate { break }
            return id == Self.id
        }
        let first = Task { await addresses.proveHome(by: ask) }
        while !asked.isRaised { await Task.yield() }
        let second = Task { await addresses.proveHome(by: ask) }

        // The first to ask is cancelled while home hasn't answered: it stops waiting, and isn't told home is there.
        first.cancel()
        #expect(!(await first.value))
        #expect(addresses.needsProof(home), "nothing is trusted until home has answered")

        open.yield()
        #expect(await second.value)
        #expect(times.count == 1)
        #expect(!addresses.needsProof(home))
    }

    @Test func reachableMeansThisServerAnswers() async {
        let (ours, home, _) = client(home: Self.status)
        #expect(await ours.isReachable(within: .seconds(2)))
        #expect(StubProtocol.recorded(host: home).map { $0.url?.path } == ["/api/v1/setup"])
        #expect(signedIn(home).isEmpty)

        let (theirs, _, _) = client(home: .init(status: 401, body: Data()), whoIsHome: "someone-else")
        #expect(!(await theirs.isReachable(within: .seconds(2))))

        let (away, _, _) = client(home: .init(status: 401, body: Data()), whoIsHome: "someone-else", remote: Self.status)
        #expect(await away.isReachable(within: .seconds(4)))
        #expect(away.addresses.isAway)
    }

    @Test func whileAwayHomeIsCheckedWithoutTheSignIn() async {
        // Something at the home address that answers everything with a 200 doesn't call the app over.
        let (client, home, remote) = client(home: Self.status, whoIsHome: "someone-else", remote: Self.status)
        client.addresses.reached(client.addresses.remote!)

        await client.checkHome()

        #expect(client.addresses.isAway)
        #expect(signedIn(home).isEmpty)
        #expect(StubProtocol.recorded(host: remote).isEmpty)

        // Back home: the server says who it is, and the app goes back to it.
        let (back, backHome, _) = self.client(home: Self.status, remote: Self.status)
        back.addresses.reached(back.addresses.remote!)
        await back.checkHome()
        #expect(!back.addresses.isAway)
        #expect(signedIn(backHome).isEmpty)
    }

    @Test func theCommandStreamWaitsForHomeToSayWhoItIsToo() async {
        let (client, home, _) = client(home: .init(status: 401, body: Data()), whoIsHome: "someone-else")
        do {
            for try await _ in client.deviceEvents() {}
            Issue.record("expected the stream not to open")
        } catch {
            #expect(FlexarrClient.couldNotConnect(error))
        }
        #expect(signedIn(home).isEmpty)
    }

    @Test func aDownloadTransferWaitsForHomeToSayWhoItIs() async {
        // Someone else's network: the transfer (which runs apart from the client, sign-in and all) isn't made.
        let (theirs, theirHome, _) = client(home: .init(status: 200, body: Data()), whoIsHome: "someone-else")
        #expect(!(await theirs.maySendTransfer(to: theirs.downloadFileRequest("d1").url)))
        #expect(signedIn(theirHome).isEmpty)

        // Home, and it's this server: it may go.
        let (ours, ourHome, _) = client(home: Self.status)
        #expect(await ours.maySendTransfer(to: ours.downloadFileRequest("d1").url))
        #expect(signedIn(ourHome).isEmpty, "only asked who it is")
    }

    @Test func aDownloadTransferGoesOnlyToTheAddressInUse() async {
        let (client, _, remote) = client(home: Self.status, remote: Self.status)
        let atHome = client.downloadFileRequest("d1").url
        client.addresses.reached(client.addresses.remote!)
        let away = client.downloadFileRequest("d1").url
        #expect(away?.host() == remote)
        #expect(await client.maySendTransfer(to: away))
        #expect(!(await client.maySendTransfer(to: atHome)), "made for home while the app is away")
        #expect(!(await client.maySendTransfer(to: nil)))
    }

    @Test func whereATransferGotToIsUsedOnlyAtTheSameAddress() {
        let home = URL(string: "http://192.168.1.20:8383/api/v1/offline/d1/file")!
        #expect(FlexarrClient.resumes(home, at: URL(string: "http://192.168.1.20:8383/api/v1/offline/d1/file")!))
        #expect(FlexarrClient.resumes(URL(string: "HTTP://192.168.1.20:8383/x")!, at: home))
        #expect(!FlexarrClient.resumes(home, at: URL(string: "https://mini.tail1234.ts.net:8443/api/v1/offline/d1/file")!))
        #expect(!FlexarrClient.resumes(home, at: URL(string: "http://192.168.1.21:8383/api/v1/offline/d1/file")!))
        #expect(!FlexarrClient.resumes(home, at: URL(string: "http://192.168.1.20:8384/api/v1/offline/d1/file")!))
        #expect(FlexarrClient.resumes(URL(string: "https://mini.ts.net/a")!, at: URL(string: "https://mini.ts.net:443/b")!))
        #expect(!FlexarrClient.resumes(nil, at: home), "not known where it went")
    }

    @Test func trustLapsesOnceHomeHasBeenQuietAWhile() async {
        let home = URL(string: "http://192.168.1.20:8383")!
        let addresses = ServerAddresses(home: home, remote: URL(string: "https://mini.tail1234.ts.net:8443")!, serverId: Self.id)
        #expect(addresses.needsProof(home))
        #expect(!addresses.needsProof(addresses.remote!), "the remote address was checked when it was kept")

        #expect(await addresses.proveHome { _, id in id == Self.id })
        let proven = Date()
        #expect(!addresses.needsProof(home, now: proven + 30))
        #expect(addresses.needsProof(home, now: proven + ServerAddresses.proofLasts + 1), "the device may be somewhere else by now")

        // Each answer from home keeps it trusted from then.
        addresses.answered(from: home, now: proven + 30)
        #expect(!addresses.needsProof(home, now: proven + 80))
        #expect(addresses.needsProof(home, now: proven + 30 + ServerAddresses.proofLasts + 1))
        // An answer after it lapsed doesn't bring the trust back: home is asked again.
        addresses.answered(from: home, now: proven + 200)
        #expect(addresses.needsProof(home, now: proven + 201))

        // Leaving home for the remote address ends it too.
        #expect(await addresses.proveHome(fresh: true) { _, _ in true })
        #expect(!addresses.needsProof(home))
        addresses.reached(addresses.remote!)
        #expect(addresses.needsProof(home))

        // Asked again and it's something else: not trusted, straight away.
        #expect(await addresses.proveHome { _, _ in true })
        #expect(!(await addresses.proveHome(fresh: true) { _, _ in false }))
        #expect(addresses.needsProof(home))
    }

    @Test func homeIsOnlyAskedWhenTheIdIsKnownAndItIsPlainHTTP() async {
        let home = URL(string: "http://192.168.1.20:8383")!
        // A server before 0.55 has no id: home is trusted as it always was.
        let older = ServerAddresses(home: home, remote: nil)
        #expect(!older.asksProof(of: home))
        #expect(await older.proveHome { _, _ in false })
        #expect(ServerAddresses(home: home, remote: nil, serverId: "").serverId == nil)
        // https says who it is by its certificate.
        let secure = URL(string: "https://flexarr.example.com")!
        #expect(!ServerAddresses(home: secure, remote: nil, serverId: Self.id).asksProof(of: secure))
    }

    @Test func theIdIsKeptWithTheSignInAndSharedAddressesAreOnePerServer() throws {
        let home = URL(string: "http://shared-\(UUID().uuidString.lowercased()).local:8383")!
        #expect(ServerAddresses.shared(home: home, remote: nil, serverId: "a") === ServerAddresses.shared(home: home, remote: nil, serverId: "a"))
        #expect(ServerAddresses.shared(home: home, remote: nil, serverId: "a") !== ServerAddresses.shared(home: home, remote: nil))
        #expect(ServerCredentials(address: home, apiKey: "key", serverId: "a").client.addresses.serverId == "a")
        // A guest's address is its own tunnel on this device: nothing to ask.
        let guest = GuestTunnelInfo(target: "100.101.102.103:8383", hostname: "guest", ownerName: "Dan")
        #expect(ServerCredentials(address: home, apiKey: "key", guest: guest, serverId: "a").serverId == nil)

        let suite = "io.github.flexarr.test.\(UUID().uuidString)"
        let store = CredentialStore(service: suite, suiteName: suite)
        defer { store.clear(); UserDefaults().removePersistentDomain(forName: suite) }
        do {
            try store.save(ServerCredentials(address: home, apiKey: "key", serverId: "a"))
        } catch {
            return // No Keychain for command-line tests on every machine.
        }
        #expect(store.load()?.serverId == "a")
        store.saveServerId("b")
        #expect(store.load()?.serverId == "b")
        // A sign-in from before the app kept it has none, and signing out forgets it.
        try store.save(ServerCredentials(address: home, apiKey: "key"))
        #expect(store.load()?.serverId == nil)
        store.saveServerId("b")
        store.clear()
        #expect(UserDefaults(suiteName: suite)?.string(forKey: "\(suite).address.serverId") == nil)
    }
}
