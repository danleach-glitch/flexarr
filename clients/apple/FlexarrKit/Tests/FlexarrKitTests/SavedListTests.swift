import Foundation
import Testing
@testable import FlexarrKit

struct SavedListTests {
    private struct Saved: Codable, Equatable {
        let id: String
        let size: Int
    }

    private func file(_ json: String) throws -> URL {
        let folder = FileManager.default.temporaryDirectory.appending(path: "savedlist-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let url = folder.appending(path: "downloads.json")
        try Data(json.utf8).write(to: url)
        return url
    }

    @Test func anEntryThatCantBeRead_IsDropped_AndTheRestKept_WithTheFileCopiedAside() throws {
        let url = try file(#"[{"id":"a","size":1},{"id":"b"},{"id":"c","size":3}]"#)

        let loaded = SavedList.load(Saved.self, from: url)

        #expect(loaded.items == [Saved(id: "a", size: 1), Saved(id: "c", size: 3)])
        #expect(loaded.unreadable == 1)
        let kept = try #require(loaded.keptAt)
        #expect(try Data(contentsOf: kept) == Data(contentsOf: url))
    }

    @Test func aFileThatIsntAListAtAll_IsKeptAside() throws {
        let url = try file(#"{"not":"a list"}"#)

        let loaded = SavedList.load(Saved.self, from: url)

        #expect(loaded.items.isEmpty)
        #expect(loaded.unreadable == 1)
        #expect(loaded.keptAt != nil)
    }

    @Test func aGoodListOrNoFile_KeepsNothingAside() throws {
        let url = try file(#"[{"id":"a","size":1}]"#)

        #expect(SavedList.load(Saved.self, from: url).keptAt == nil)
        #expect(SavedList.load(Saved.self, from: url.appending(path: "missing")).items.isEmpty)
    }
}
