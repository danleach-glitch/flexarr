import Foundation
import Testing
@testable import FlexarrKit

struct LibraryOrderTests {
    @Test func olderServersHaveNoOrder_NewerOnesSayIt() throws {
        let older = ##"{"id":"owner","name":"Dan","color":"#a9c2ec","hasPin":false,"isMain":true,"isCurrent":true,"hiddenLibraryIds":[],"image":null}"##
        let newer = ##"{"id":"owner","name":"Dan","color":"#a9c2ec","hasPin":false,"isMain":true,"isCurrent":true,"hiddenLibraryIds":[],"image":null,"libraryOrder":["b","live-tv"]}"##
        #expect(try FlexarrClient.decoder.decode(Profile.self, from: Data(older.utf8)).libraryOrder == nil)
        #expect(try FlexarrClient.decoder.decode(Profile.self, from: Data(newer.utf8)).libraryOrder == ["b", "live-tv"])
    }

    @Test func entriesThisAppDoesntShowKeepTheirPlaces() {
        #expect(LibraryOrder.reordered(saved: ["a", "live-tv", "b", "c"], shown: ["c", "a", "b"]) == ["c", "live-tv", "a", "b"])
        #expect(LibraryOrder.reordered(saved: ["live-tv", "a"], shown: ["new", "a"]) == ["live-tv", "new", "a"])
        #expect(LibraryOrder.reordered(saved: [], shown: ["b", "a"]) == ["b", "a"])
    }

    @Test func sortingPutsUnmentionedEntriesLast_InTheirOwnOrder() {
        #expect(LibraryOrder.sorted(["a", "b", "live-tv", "c"], by: ["live-tv", "C"], id: { $0 }) == ["live-tv", "c", "a", "b"])
    }
}
