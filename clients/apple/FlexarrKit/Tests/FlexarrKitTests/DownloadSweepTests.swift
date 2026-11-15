import Testing
@testable import FlexarrKit

/// Files left in the downloads folder by a download deleted as it finished go at the next launch; nothing else does.
struct DownloadSweepTests {
    @Test func onlyFilesNothingHoldsGo() {
        let known = DownloadSweep.known(fileNames: ["d1.mp4", nil, "book-b1", "cover-a1.jpg", ""])
        let names = ["d1.mp4", "d2.mp4", "book-b1", "book-b2", "cover-a1.jpg", "d2.jpg", "downloads.json", "collections.json",
                     "downloads.unreadable-2026-11-15T10-00-00Z.json", ".DS_Store"]
        #expect(DownloadSweep.unknown(names, known: known) == ["book-b2", "d2.jpg", "d2.mp4"])
    }
}
