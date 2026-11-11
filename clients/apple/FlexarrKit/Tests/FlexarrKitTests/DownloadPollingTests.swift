import Foundation
import Testing
@testable import FlexarrKit

/// A whole show waiting on the server: the first few are asked after every look, the rest a couple at a time.
struct DownloadPollingTests {
    private let now = Date(timeIntervalSince1970: 1_000_000)
    private var ids: [String] { (1...10).map { "e\($0)" } }

    @Test func theFirstFewAreAskedEveryLook() {
        let asked = Dictionary(uniqueKeysWithValues: ids.map { ($0, now) })
        #expect(DownloadPolling.due(ids, lastAsked: asked, now: now) == ["e1", "e2", "e3"])
        #expect(DownloadPolling.due(["e1"], lastAsked: [:], now: now) == ["e1"])
        #expect(DownloadPolling.due([], lastAsked: [:], now: now).isEmpty)
    }

    @Test func theRestAreAskedACoupleAtATimeLongestAgoFirst() {
        var asked = Dictionary(uniqueKeysWithValues: ids.map { ($0, now) })
        asked["e9"] = now - 100
        asked["e5"] = now - 60
        asked["e7"] = now - 40
        #expect(DownloadPolling.due(ids, lastAsked: asked, now: now) == ["e1", "e2", "e3", "e9", "e5"])
    }

    @Test func noneOfTheRestIsAskedTooOften() {
        var asked = Dictionary(uniqueKeysWithValues: ids.map { ($0, now) })
        asked["e6"] = now - DownloadPolling.othersEvery + 1
        #expect(DownloadPolling.due(ids, lastAsked: asked, now: now) == ["e1", "e2", "e3"])
    }

    @Test func oneNeverAskedCountsAsJustAnswered() {
        // Answered when it was asked for: not all asked at once on the first look.
        #expect(DownloadPolling.due(ids, lastAsked: [:], now: now) == ["e1", "e2", "e3"])
    }
}
