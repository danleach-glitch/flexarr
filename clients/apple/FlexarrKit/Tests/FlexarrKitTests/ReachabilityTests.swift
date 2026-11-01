import Foundation
import Testing
@testable import FlexarrKit

/// Never answers, as a server behind a VPN with nothing on the other end doesn't.
private final class SilentProtocol: URLProtocol, @unchecked Sendable {
    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {}
    override func stopLoading() {}
}

private final class Seen: @unchecked Sendable {
    private let lock = NSLock()
    private var urls: [URL] = []
    func add(_ url: URL) { lock.withLock { urls.append(url) } }
    func contains(_ url: URL) -> Bool { lock.withLock { urls.contains(url) } }
}

/// Hosts with no stub can't be connected to (see `StubProtocol`).
private func client(home: StubProtocol.Stub?, remote: StubProtocol.Stub? = nil, hasRemote: Bool = true,
                    protocolClass: AnyClass = StubProtocol.self) -> FlexarrClient {
    let homeHost = "home-\(UUID().uuidString.lowercased()).local"
    let remoteHost = "remote-\(UUID().uuidString.lowercased()).ts.net"
    if let home { StubProtocol.register(host: homeHost, home) }
    if let remote { StubProtocol.register(host: remoteHost, remote) }
    let configuration = URLSessionConfiguration.ephemeral
    configuration.protocolClasses = [protocolClass]
    let addresses = ServerAddresses(home: URL(string: "http://\(homeHost):8383")!,
                                    remote: hasRemote ? URL(string: "https://\(remoteHost):8443")! : nil)
    return FlexarrClient(addresses: addresses, apiKey: "secret", session: URLSession(configuration: configuration))
}

private let ok = StubProtocol.Stub(status: 200, body: Data("{}".utf8))

struct ReachabilityTests {
    @Test func reachableWhenHomeAnswers() async {
        let client = client(home: ok)
        #expect(await client.isReachable(within: .seconds(2)))
        #expect(!client.addresses.isAway)
    }

    @Test func anyAnswerCounts() async {
        // Refused is still there: the sign-in is another matter.
        #expect(await client(home: .init(status: 401, body: Data())).isReachable(within: .seconds(2)))
    }

    @Test func askedWithoutTheSignIn() async {
        let client = client(home: ok, hasRemote: false)
        #expect(await client.isReachable(within: .seconds(2)))
        let host = client.addresses.home.host ?? ""
        let probe = StubProtocol.recorded(host: host).last
        #expect(probe?.url?.path == "/api/v1/system/status")
        #expect(probe?.value(forHTTPHeaderField: "X-Api-Key") == nil)
    }

    @Test func aWebPageIsNotTheServer() async {
        // A hotel's sign-in page at the home address.
        let portal = StubProtocol.Stub(status: 200, body: Data("<html>".utf8), contentType: "text/html")
        #expect(!(await client(home: portal, hasRemote: false).isReachable(within: .seconds(2))))
    }

    @Test func homeGetsAHeadStart() async {
        // Both answer: home, the address in use, wins, and the app stays at home.
        let client = client(home: ok, remote: ok)
        #expect(await client.isReachable(within: .seconds(4)))
        #expect(!client.addresses.isAway)
    }

    @Test func aSuccessfulRequestIsReported() async throws {
        let before = Date()
        let (client, _) = stubbedClient(.init(status: 200, body: Data("[]".utf8)))
        _ = try await client.libraries()
        #expect((FlexarrClient.lastAnsweredAt ?? .distantPast) >= before)
    }

    @Test func awayAddressAnswersAndIsUsedFromThen() async {
        let client = client(home: nil, remote: ok)
        #expect(await client.isReachable(within: .seconds(2)))
        #expect(client.addresses.isAway)
    }

    @Test func nothingConnectsAndItSaysSoAtOnce() async {
        let started = Date()
        #expect(!(await client(home: nil, remote: nil).isReachable(within: .seconds(4))))
        #expect(Date().timeIntervalSince(started) < 2)
    }

    @Test func givesUpAfterTheLimitWhenNothingAnswers() async {
        let started = Date()
        #expect(!(await client(home: nil, hasRemote: false, protocolClass: SilentProtocol.self).isReachable(within: .milliseconds(300))))
        #expect(Date().timeIntervalSince(started) < 2)
    }

    @Test func aRequestThatCantConnectIsReported() async {
        let seen = Seen()
        let previous = FlexarrClient.onCouldNotConnect
        FlexarrClient.onCouldNotConnect = { url in
            seen.add(url)
            previous?(url)
        }
        let unreachable = client(home: nil, hasRemote: false)
        let refusing = client(home: .init(status: 503, body: Data()), hasRemote: false)

        _ = try? await unreachable.home()
        _ = try? await refusing.home()

        #expect(seen.contains(unreachable.addresses.home))
        // The server answered, with an error: that's not being offline.
        #expect(!seen.contains(refusing.addresses.home))
    }

    @Test func profileListIsKeptAndForgottenOnSignOut() throws {
        let name = "test.\(UUID().uuidString)"
        let store = CredentialStore(service: name, suiteName: name)
        #expect(store.savedProfiles.isEmpty)
        let list = [Profile(id: "dan", name: "Dan", color: "#a9c2ec", isMain: true, isCurrent: true),
                    Profile(id: "kid", name: "Ada", color: "#f0c", isKids: true)]
        store.saveProfiles(list)
        #expect(store.savedProfiles == list)
        store.clear()
        #expect(store.savedProfiles.isEmpty)
    }
}
