import Foundation
import Testing
@testable import FlexarrKit

struct TraktTests {
    @Test func statusWhileACodeWaits() throws {
        let json = """
        {"available":true,"state":"disconnected","username":null,"connectedAt":null,"lastSyncAt":null,"lastError":null,
         "watchlistFull":false,"importedAt":null,"importing":false,"queued":0,
         "pending":{"state":"Waiting","userCode":"ABCD1234","verificationUrl":"https://trakt.tv/activate","qrUrl":"https://trakt.tv/activate",
                    "expiresAt":"2026-10-31T12:10:00.000Z","error":null}}
        """
        let status = try FlexarrClient.decoder.decode(TraktStatus.self, from: Data(json.utf8))
        #expect(status.available)
        #expect(status.state == .disconnected)
        #expect(status.pending?.state == .waiting)
        #expect(status.pending?.userCode == "ABCD1234")
        #expect(status.pending?.state.problem == nil)
    }

    @Test func newerStatesReadAsUnknown() throws {
        let json = """
        {"available":true,"state":"paused","watchlistFull":true,"importing":false,"queued":3}
        """
        let status = try FlexarrClient.decoder.decode(TraktStatus.self, from: Data(json.utf8))
        #expect(status.state == .unknown)
        #expect(status.watchlistFull)
        #expect(status.pending == nil)
    }

    @Test func previewSaysWhatChanges() {
        let preview = TraktImportPreview(traktFilms: 1, traktEpisodes: 340, inLibrary: 200, newToFlexarr: 41, notOnServer: 0, unmatched: 2, toTrakt: 1)
        #expect(preview.summary == "Trakt has 1 movie and 340 episodes")
        #expect(preview.details == "200 are here, 41 not yet watched in Flexarr. 1 play from Flexarr goes to Trakt.")
    }

    @Test func previewWithNothingHere() {
        let preview = TraktImportPreview(traktFilms: 214, traktEpisodes: 40, inLibrary: 0, newToFlexarr: 0, notOnServer: 254, unmatched: 0, toTrakt: 0)
        #expect(preview.details == "None are here yet. 254 will show as watched if they arrive.")
    }

    @Test func listsSayWhose() throws {
        let json = """
        [{"id":"dan/55","name":"Sci-fi night","itemCount":12,"liked":false,"selected":true},
         {"id":"sam/77","name":"Best of 2024","owner":"Sam","itemCount":1,"liked":true,"selected":false}]
        """
        let lists = try FlexarrClient.decoder.decode([TraktList].self, from: Data(json.utf8))
        #expect(lists.map(\.detail) == ["12 titles", "Sam, 1 title"])
    }

    @Test func problemsUseTheServersWords() {
        #expect(TraktWords.problem(FlexarrClientError.http(status: 409, message: "The import is already running.")) == "The import is already running.")
        #expect(TraktWords.problem(FlexarrClientError.http(status: 500, message: nil)) == TraktWords.failed)
    }
}
