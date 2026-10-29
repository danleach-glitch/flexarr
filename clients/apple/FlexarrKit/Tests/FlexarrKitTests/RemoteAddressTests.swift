import Foundation
import Testing
@testable import FlexarrKit

/// Which address for away from home the app keeps, so its sign-in only ever goes to its own server.
struct RemoteAddressTests {
    private let home = URL(string: "http://192.168.1.106:8383")!
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

    private func remote(_ status: SystemStatus, address: URL? = nil, kept: URL? = nil, _ record: Record,
                        idThere: @escaping @Sendable () throws -> String?) async -> URL? {
        await FlexarrClient.remoteAddress(for: status, address: address ?? home, kept: kept,
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

    @Test func whileNothingAnswersThereWhatWasKeptStaysUntilNextTime() async {
        let record = Record()
        // Signing in: nothing yet.
        #expect(await remote(status(tailscale.absoluteString), record) { throw FlexarrClientError.unreachable } == nil)
        // The server's address has changed since: the old one stays rather than an unchecked new one.
        let old = URL(string: "https://old.tail1234.ts.net")!
        #expect(await remote(status(tailscale.absoluteString), kept: old, record) { throw FlexarrClientError.unreachable } == old)
        #expect(record.refusals.isEmpty)
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
