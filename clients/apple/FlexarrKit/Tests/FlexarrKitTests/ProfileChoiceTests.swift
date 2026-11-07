import Foundation
import Testing
@testable import FlexarrKit

/// Hosts the choose-profile hook has heard from (the hook is static, so each test looks for its own host).
private final class Heard: @unchecked Sendable {
    private let lock = NSLock()
    private var hosts: Set<String> = []
    func add(_ url: URL) { lock.withLock { _ = hosts.insert(url.host ?? "") } }
    func contains(_ host: String) -> Bool { lock.withLock { hosts.contains(host) } }
}

private let heard: Heard = {
    let heard = Heard()
    FlexarrClient.onChooseProfile = { heard.add($0) }
    return heard
}()

struct ProfileChoiceTests {
    @Test func everyRequestSaysTheAppAsksWhoIsWatching() async throws {
        let (client, host) = stubbedClient(.init(status: 200, body: Data(#"{"profileId":"main","method":"device","isAdmin":false}"#.utf8)))
        _ = try await client.me()
        let request = try #require(StubProtocol.recorded(host: host).first)
        #expect(request.value(forHTTPHeaderField: "X-Flexarr-Profile-Choice") == "1")
        #expect(client.downloadFileRequest("x").value(forHTTPHeaderField: "X-Flexarr-Profile-Choice") == "1")
    }

    @Test func meSaysWhenSomeoneMustChoose() async throws {
        let (asks, _) = stubbedClient(.init(status: 200, body: Data(#"{"profileId":"main","method":"device","isAdmin":false,"chooseProfile":true}"#.utf8)))
        #expect(try await asks.me().chooseProfile == true)
        // Servers before 0.63 say nothing.
        let (old, _) = stubbedClient(.init(status: 200, body: Data(#"{"profileId":"main","method":"device","isAdmin":false}"#.utf8)))
        #expect(try await old.me().chooseProfile == nil)
    }

    @Test func aRefusalUntilSomeoneChoosesIsHeard() async {
        _ = heard
        let refused = #"{"title":"Choose who’s watching first.","status":403,"chooseProfile":true}"#
        let (client, host) = stubbedClient(.init(status: 403, body: Data(refused.utf8)))
        await #expect(throws: FlexarrClientError.http(status: 403, message: "Choose who’s watching first.")) {
            try await client.home()
        }
        #expect(heard.contains(host))

        let (other, otherHost) = stubbedClient(.init(status: 403, body: Data(#"{"title":"Not allowed.","status":403}"#.utf8)))
        await #expect(throws: FlexarrClientError.http(status: 403, message: "Not allowed.")) {
            try await other.home()
        }
        #expect(!heard.contains(otherHost))
    }
}
