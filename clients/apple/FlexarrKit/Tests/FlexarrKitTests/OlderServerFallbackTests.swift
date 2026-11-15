import Foundation
import Testing
@testable import FlexarrKit

/// Calls that fall back for servers before their endpoint: those before 0.59 answer a path they don't know with the
/// web app's page (an unexpected response), later ones with 404, and both read as "not there".
struct OlderServerFallbackTests {
    private static let webPage = StubProtocol.Stub(status: 200, body: Data("<!doctype html><html></html>".utf8), contentType: "text/html")

    @Test func recentSearchesWaitingDevicesAndLanguagesAreEmptyFromAnOlderServer() async throws {
        for stub in [Self.webPage, .init(status: 404, body: Data())] {
            let (client, _) = stubbedClient(stub)
            #expect(try await client.recentSearches().queries.isEmpty)
            #expect(try await client.waitingPairingsIfKept() == nil)
            #expect(try await client.languages().isEmpty)
        }
    }
}
