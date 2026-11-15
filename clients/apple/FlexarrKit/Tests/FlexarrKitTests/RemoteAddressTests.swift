import Foundation
import Testing
@testable import FlexarrKit

/// Which address for away from home the app keeps, so its sign-in only ever goes to its own server.
struct RemoteAddressTests {
    private let home = URL(string: "http://192.168.1.50:8383")!
    private let tailscale = URL(string: "https://mini.tail1234.ts.net:8443")!

    private func status(_ remoteUrl: String?, serverId: String? = "home-id") -> SystemStatus {
        SystemStatus(appName: "Flexarr", version: "0.55.0", osDescription: "macOS", runtimeVersion: ".NET 10",
                     startTime: Date(), remoteUrl: remoteUrl.flatMap(URL.init(string:)), serverId: serverId)
    }

    /// Where the app asked, without the sign-in, which server is there, and what it refused.
    private final class Record: @unchecked Sendable {
        private let lock = NSLock()
        private var askedAt: [URL] = []
        private var refused: [String] = []
        var asked: [URL] { lock.withLock { askedAt } }
        var refusals: [String] { lock.withLock { refused } }
        func ask(_ url: URL) { lock.withLock { askedAt.append(url) } }
        func refuse(_ reason: String) { lock.withLock { refused.append(reason) } }
    }

    private struct NotAsked: Error {}

    private func remote(_ status: SystemStatus, address: URL? = nil, kept: URL? = nil, keptConfirmed: Bool = true,
                        knownId: String? = nil, _ record: Record,
                        idThere: @escaping @Sendable () throws -> String?) async -> URL? {
        await away(status, address: address, kept: kept, keptConfirmed: keptConfirmed, knownId: knownId, record, idThere: idThere)?.url
    }

    private func away(_ status: SystemStatus, address: URL? = nil, kept: URL? = nil, keptConfirmed: Bool = true,
                      knownId: String? = nil, _ record: Record,
                      idThere: @escaping @Sendable () throws -> String?) async -> FlexarrClient.Away? {
        await FlexarrClient.remoteAddress(for: status, address: address ?? home,
                                          kept: kept.map { FlexarrClient.Away($0, confirmed: keptConfirmed) }, knownId: knownId,
                                          idAt: { url in record.ask(url); return try idThere() },
                                          refused: { record.refuse($0) })
    }

    @Test func theSameServerThereIsKept() async {
        let record = Record()
        #expect(await remote(status("https://mini.tail1234.ts.net:8443"), record) { "home-id" } == tailscale)
        #expect(record.asked == [tailscale])
        #expect(record.refusals.isEmpty)
    }

    @Test func aDifferentServerThereOrOneThatGivesNoIdIsNot() async {
        let record = Record()
        #expect(await remote(status(tailscale.absoluteString), record) { "someone-else" } == nil)
        #expect(await remote(status(tailscale.absoluteString), record) { nil } == nil)
        #expect(record.refusals.count == 2)
    }

    @Test func aNewAddressHasToGiveTheIdKeptAtSignIn_NotOneTheSameAnswerBrings() async {
        // An answer tampered with on the home network: someone's own Tailscale host, and the id that host gives.
        let theirs = URL(string: "https://theirs.tail9999.ts.net")!
        let record = Record()
        #expect(await remote(status(theirs.absoluteString, serverId: "their-id"), knownId: "home-id", record) { "their-id" } == nil)
        // Or with the id taken out, as a server before 0.55 would answer: still asked, and still not this server.
        #expect(await remote(status(theirs.absoluteString, serverId: nil), knownId: "home-id", record) { "their-id" } == nil)
        #expect(await remote(status(theirs.absoluteString, serverId: nil), knownId: "home-id", record) { nil } == nil)
        #expect(record.asked == [theirs, theirs, theirs])
        #expect(record.refusals.count == 3)
        // The server's own new address gives the id kept, whatever the answer said.
        #expect(await remote(status(tailscale.absoluteString, serverId: nil), knownId: "home-id", record) { "home-id" } == tailscale)
        #expect(await remote(status(tailscale.absoluteString), knownId: "home-id", record) { "home-id" } == tailscale)
        // While nothing answers there, what was kept stays; and an address already kept isn't asked again.
        #expect(await remote(status(theirs.absoluteString, serverId: "their-id"), kept: tailscale, knownId: "home-id", record) {
            throw FlexarrClientError.unreachable
        } == tailscale)
        #expect(await remote(status(tailscale.absoluteString, serverId: "their-id"), kept: tailscale, knownId: "home-id", record) {
            throw NotAsked()
        } == tailscale)
    }

    @Test func whileNothingAnswersThereWhatWasKeptStaysUntilNextTime() async {
        let record = Record()
        // The server's address has changed since: the old one, confirmed, stays rather than an unchecked new one.
        let old = URL(string: "https://old.tail1234.ts.net")!
        #expect(await away(status(tailscale.absoluteString), kept: old, record) { throw FlexarrClientError.unreachable }
                == FlexarrClient.Away(old))
        #expect(record.refusals.isEmpty)
    }

    @Test func signingInWithTailscaleOffKeepsTheAddressUnconfirmed() async {
        let record = Record()
        // Signing in at home with Tailscale off: kept, but to be asked before it's sent the sign-in.
        #expect(await away(status(tailscale.absoluteString), record) { throw FlexarrClientError.unreachable }
                == FlexarrClient.Away(tailscale, confirmed: false))
        // Next time, still nothing there: still unconfirmed, and asked again.
        #expect(await away(status(tailscale.absoluteString), kept: tailscale, keptConfirmed: false, knownId: "home-id", record) {
            throw FlexarrClientError.unreachable
        } == FlexarrClient.Away(tailscale, confirmed: false))
        // Tailscale on: confirmed.
        #expect(await away(status(tailscale.absoluteString), kept: tailscale, keptConfirmed: false, knownId: "home-id", record) {
            "home-id"
        } == FlexarrClient.Away(tailscale))
        #expect(record.asked == [tailscale, tailscale, tailscale])
        // Or someone else answers there: dropped.
        #expect(await away(status(tailscale.absoluteString), kept: tailscale, keptConfirmed: false, knownId: "home-id", record) {
            "their-id"
        } == nil)
        #expect(record.refusals.count == 1)
    }

    @Test func anUnconfirmedAddressIsReplacedByTheServersNewOne() async {
        let record = Record()
        let old = URL(string: "https://old.tail1234.ts.net")!
        #expect(await away(status(tailscale.absoluteString), kept: old, keptConfirmed: false, knownId: "home-id", record) {
            throw FlexarrClientError.unreachable
        } == FlexarrClient.Away(tailscale, confirmed: false))
    }

    @Test func anUnconfirmedAwayAddressSaysWhoItIsBeforeTheSignIn() async {
        let remote = tailscale
        let addresses = ServerAddresses(home: home, remote: remote, serverId: "home-id", remoteConfirmed: false)
        #expect(addresses.asksProof(of: remote))
        #expect(addresses.needsProof(remote))
        #expect(!(await addresses.prove(remote) { _, _ in false }))
        #expect(addresses.needsProof(remote))
        // Just missed, so asked again only on purpose (see `remoteMissLasts`).
        #expect(await addresses.prove(remote, fresh: true) { url, id in url == remote && id == "home-id" })
        #expect(!addresses.needsProof(remote, now: Date() + 3600), "a Tailscale address stays proven")
        #expect(addresses.remoteHasProven)
        // Confirmed, or a server with no id: never asked.
        #expect(!ServerAddresses(home: home, remote: remote, serverId: "home-id").asksProof(of: remote))
        #expect(!ServerAddresses(home: home, remote: remote, remoteConfirmed: false).asksProof(of: remote))
    }

    @Test func anAddressAlreadyKeptIsNotAskedAgain() async {
        let record = Record()
        #expect(await remote(status(tailscale.absoluteString), kept: tailscale, record) { throw NotAsked() } == tailscale)
        #expect(record.asked.isEmpty)
    }

    @Test func aServerBefore055HasNoIdSoItsTailscaleAddressIsTakenWithoutAsking() async {
        let record = Record()
        #expect(await remote(status(tailscale.absoluteString, serverId: nil), record) { throw NotAsked() } == tailscale)
        #expect(await remote(status("100.101.2.3", serverId: nil), record) { throw NotAsked() }
                == URL(string: "http://100.101.2.3:8383"))
        #expect(record.asked.isEmpty)
    }

    @Test func onlyATailscaleAddressIsEverAskedWhateverTheServersId() async {
        let record = Record()
        for url in ["https://flexarr.example.com", "http://mini.tail1234.ts.net:8443", "http://192.168.1.20:8383"] {
            #expect(await remote(status(url), record) { "home-id" } == nil, "\(url)")
            #expect(await remote(status(url, serverId: nil), record) { nil } == nil, "\(url)")
        }
        #expect(record.asked.isEmpty)
        #expect(record.refusals.count == 6)
    }

    @Test func noneOrTheAddressAlreadyInUseIsNone() async {
        let record = Record()
        #expect(await remote(status(nil), record) { throw NotAsked() } == nil)
        #expect(await remote(status("not an address"), record) { throw NotAsked() } == nil)
        let inUse = URL(string: "http://100.101.2.3:8383")!
        #expect(await remote(status("100.101.2.3"), address: inUse, record) { throw NotAsked() } == nil)
        #expect(record.asked.isEmpty)
        #expect(record.refusals.isEmpty)
    }

    @Test func tailscaleAddressesAreThe100_64Block() {
        #expect(FlexarrClient.isTailscaleAddress("100.64.0.1"))
        #expect(FlexarrClient.isTailscaleAddress("100.127.255.254"))
        #expect(!FlexarrClient.isTailscaleAddress("100.128.0.1"))
        #expect(!FlexarrClient.isTailscaleAddress("100.63.255.255"))
        #expect(!FlexarrClient.isTailscaleAddress("10.0.0.1"))
        #expect(!FlexarrClient.isTailscaleAddress("100.64.0"))
        #expect(!FlexarrClient.isTailscaleAddress("100.64.0.1.example.com"))
        #expect(FlexarrClient.isAcceptableRemote(URL(string: "https://MINI.tail1234.TS.NET.:8443")!))
    }

    @Test func theServerThereIsAskedWithoutTheSignIn() async throws {
        let (client, host) = stubbedClient(.init(status: 200, body: Data(#"{"open":false,"codeRequired":true,"serverId":"abc123"}"#.utf8)))
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [StubProtocol.self]
        let session = URLSession(configuration: configuration)

        #expect(try await FlexarrClient.serverId(at: client.baseURL, session: session) == "abc123")
        let request = try #require(StubProtocol.recorded(host: host).first)
        #expect(request.url?.path == "/api/v1/setup")
        #expect(request.value(forHTTPHeaderField: "X-Api-Key") == nil)
        #expect(request.timeoutInterval == 5)
    }

    @Test func noIdOrNoServerThereSaySo() async throws {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [StubProtocol.self]
        let session = URLSession(configuration: configuration)
        let (old, _) = stubbedClient(.init(status: 200, body: Data(#"{"open":false}"#.utf8)))
        #expect(try await FlexarrClient.serverId(at: old.baseURL, session: session) == nil)
        let (web, _) = stubbedClient(.init(status: 200, body: Data("<html></html>".utf8), contentType: "text/html"))
        #expect(try await FlexarrClient.serverId(at: web.baseURL, session: session) == nil)
        // No stub for the host: it can't be connected to.
        let nowhere = URL(string: "http://nowhere-\(UUID().uuidString.lowercased()).local:8383")!
        await #expect(throws: FlexarrClientError.unreachable) {
            try await FlexarrClient.serverId(at: nowhere, session: session)
        }
    }
}
