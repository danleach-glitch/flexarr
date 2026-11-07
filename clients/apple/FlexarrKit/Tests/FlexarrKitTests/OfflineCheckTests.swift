import Foundation
import Testing
@testable import FlexarrKit

/// Downloads a profile may no longer play (audit sec-64): which come off the device after the server's check.
struct OfflineCheckTests {
    private let film = "0199a000-0000-7000-8000-000000000001"
    private let episode = "0199a000-0000-7000-8000-000000000002"
    private let book = "0199a000-0000-7000-8000-000000000003"
    private let deleted = "0199a000-0000-7000-8000-000000000004"

    @Test func hiddenAndGoneComeOff_AllowedAndMissingStay() {
        let check = OfflineCheck(allowed: [film], hidden: [book], missing: [episode], gone: [deleted])
        #expect(check.toRemove(asked: [film, episode, book, deleted]) == [book, deleted])
    }

    @Test func aServerThatKnowsNoneOfThemTakesNothingOff() {
        // Every one gone: most likely not the server they came from.
        let check = OfflineCheck(gone: [film, episode])
        #expect(check.toRemove(asked: [film, episode]).isEmpty)
        // Hidden ones still come off whatever else is gone.
        #expect(OfflineCheck(hidden: [film], gone: [episode]).toRemove(asked: [film, episode]) == [film, episode])
    }

    @Test func onlyWhatWasAskedAbout_WhateverTheCase() {
        let check = OfflineCheck(allowed: [film], hidden: [book.uppercased(), deleted])
        #expect(check.toRemove(asked: [film, book]) == [book])
        // A server that lists an id both ways is taken at its kindest.
        #expect(OfflineCheck(allowed: [film], hidden: [film]).toRemove(asked: [film]).isEmpty)
        #expect(OfflineCheck(hidden: [episode], missing: [episode]).toRemove(asked: [episode]).isEmpty)
    }

    @Test func theClientAsksWithTheIdsAndReadsTheAnswer() async throws {
        let body = Data(#"{"allowed":["\#(film)"],"hidden":["\#(book)"],"missing":[],"gone":[]}"#.utf8)
        let (client, host) = stubbedClient(.init(status: 200, body: body))
        let answer = try await client.offlineCheck(itemIds: [film, book])
        #expect(answer == OfflineCheck(allowed: [film], hidden: [book]))
        let request = try #require(StubProtocol.recorded(host: host).last)
        #expect(request.httpMethod == "POST")
        #expect(request.url?.path == "/api/v1/offline/check")
        let sent = try JSONSerialization.jsonObject(with: try #require(request.httpBody)) as? [String: [String]]
        #expect(sent?["itemIds"] == [film, book])
    }

    @Test func aServerBeforeTheCheckThrows() async {
        let (older, _) = stubbedClient(.init(status: 404, body: Data()))
        await #expect(throws: (any Error).self) { try await older.offlineCheck(itemIds: [film]) }
        // The web app's page, for a server whose fallback answers an unknown path.
        let (page, _) = stubbedClient(.init(status: 200, body: Data("<html></html>".utf8), contentType: "text/html"))
        await #expect(throws: (any Error).self) { try await page.offlineCheck(itemIds: [film]) }
    }
}
