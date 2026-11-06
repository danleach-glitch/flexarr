import Foundation
import Testing
@testable import FlexarrKit

struct SavedListTests {
    private struct Saved: Codable, Equatable, Sendable {
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

    // MARK: Writing

    private func emptyFile() throws -> URL {
        let folder = FileManager.default.temporaryDirectory.appending(path: "savedlist-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        return folder.appending(path: "downloads.json")
    }

    /// Waits (up to a few seconds) for something done on another thread.
    private func eventually(_ condition: () -> Bool) async -> Bool {
        for _ in 0..<300 {
            if condition() { return true }
            try? await Task.sleep(for: .milliseconds(10))
        }
        return condition()
    }

    @Test func aRunOfChanges_IsWrittenOnce_WithTheNewest() async throws {
        let url = try emptyFile()
        let writer = SavedList.Writer<Saved>(to: url, atMostEvery: 60)
        // The first change after a quiet spell goes straight to the file, without being asked.
        writer.save([Saved(id: "a", size: 0)])
        #expect(await eventually { writer.writes == 1 })
        #expect(SavedList.load(Saved.self, from: url).items == [Saved(id: "a", size: 0)])

        // The 500 that follow wait their turn: nothing more is written until the time is up, or it's asked for.
        for size in 1...500 { writer.save([Saved(id: "a", size: size)]) }
        try await Task.sleep(for: .milliseconds(100))
        #expect(writer.writes == 1)
        #expect(SavedList.load(Saved.self, from: url).items == [Saved(id: "a", size: 0)])

        writer.flush()
        #expect(writer.writes == 2)
        #expect(SavedList.load(Saved.self, from: url).items == [Saved(id: "a", size: 500)])
    }

    @Test func aChangeThatCantWait_IsWrittenRightAway_WithoutWaitingForTheTime() async throws {
        let url = try emptyFile()
        let writer = SavedList.Writer<Saved>(to: url, atMostEvery: 60)
        writer.save([Saved(id: "a", size: 1)])
        #expect(await eventually { writer.writes == 1 })
        // One that can wait, then one that can't: the newest is written, once.
        writer.save([Saved(id: "a", size: 2)])
        writer.save([Saved(id: "a", size: 2), Saved(id: "b", size: 3)], rightAway: true)

        #expect(await eventually { writer.writes == 2 })
        #expect(SavedList.load(Saved.self, from: url).items == [Saved(id: "a", size: 2), Saved(id: "b", size: 3)])
        // Nothing is left waiting: asking for it to be written writes nothing more.
        writer.flush()
        #expect(writer.writes == 2)
    }

    @Test func flushing_WritesBeforeItReturns_AndNothingWhenNothingIsWaiting() throws {
        let url = try emptyFile()
        let writer = SavedList.Writer<Saved>(to: url, atMostEvery: 60)

        writer.flush()
        #expect(writer.writes == 0)
        #expect(!FileManager.default.fileExists(atPath: url.path(percentEncoded: false)))

        writer.save([Saved(id: "a", size: 1)])
        writer.flush()
        writer.save([Saved(id: "a", size: 1), Saved(id: "b", size: 2)])
        writer.flush()
        writer.flush()
        #expect(writer.writes == 2)
        #expect(SavedList.load(Saved.self, from: url).items == [Saved(id: "a", size: 1), Saved(id: "b", size: 2)])
    }

    @Test func changesAfterTheTimeIsUp_AreWrittenByThemselves() async throws {
        let url = try emptyFile()
        let writer = SavedList.Writer<Saved>(to: url, atMostEvery: 0.2)

        writer.save([Saved(id: "a", size: 1)])
        #expect(await eventually { writer.writes == 1 })
        writer.save([Saved(id: "a", size: 2)])
        writer.save([Saved(id: "a", size: 3)])

        #expect(await eventually { writer.writes == 2 })
        #expect(SavedList.load(Saved.self, from: url).items == [Saved(id: "a", size: 3)])
    }

    @Test func whateverWasSavedLast_IsWhatsInTheFile_HoweverTheWritesFellOut() async throws {
        let url = try emptyFile()
        let writer = SavedList.Writer<Saved>(to: url, atMostEvery: 0.01)
        // Ones that can wait and ones that can't, with writes landing in between (their time up, or asked for): an
        // older list never ends up over a newer one, and the last is there once it's been asked for.
        for size in 1...1000 {
            writer.save([Saved(id: "a", size: size)], rightAway: size.isMultiple(of: 7))
            if size.isMultiple(of: 100) { try await Task.sleep(for: .milliseconds(15)) }
            if size.isMultiple(of: 250) { writer.flush() }
        }
        writer.flush()
        #expect(SavedList.load(Saved.self, from: url).items == [Saved(id: "a", size: 1000)])
        let written = writer.writes
        #expect(written <= 1000)

        // Writes still on their way for lists already written find nothing waiting, and write nothing.
        try await Task.sleep(for: .milliseconds(100))
        #expect(writer.writes == written)
        #expect(SavedList.load(Saved.self, from: url).items == [Saved(id: "a", size: 1000)])
    }

    @Test func aListIsWrittenWithTheEncoderGiven_SoItsDatesReadBack() throws {
        struct Dated: Codable, Equatable, Sendable {
            let at: Date
        }
        let url = try emptyFile()
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        let writer = SavedList.Writer<Dated>(to: url, encoder: encoder)
        let at = Date(timeIntervalSince1970: 1_800_000_000)

        writer.save([Dated(at: at)])
        writer.flush()

        #expect(SavedList.load(Dated.self, from: url).items == [Dated(at: at)])
    }
}
