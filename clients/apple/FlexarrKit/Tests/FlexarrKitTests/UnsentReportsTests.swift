import Foundation
import Testing
@testable import FlexarrKit

/// Reports of watching done offline, kept through signing out until they reach the server and profile they're for.
struct UnsentReportsTests {
    private let home = "http://192.168.1.10:8383"
    private let at = Date(timeIntervalSince1970: 1_800_000_000)

    private func report(_ item: String, server: String? = nil, profile: String = "p", at offset: TimeInterval = 0,
                        position: Double = 100) -> UnsentReport {
        UnsentReport(server: server ?? home, profileId: profile, itemId: item, position: position, finished: false,
                     playedAt: at.addingTimeInterval(offset))
    }

    @Test func aServerIsKnownByItsHomeAddressHoweverItsWritten() {
        #expect(UnsentReports.serverKey(URL(string: "HTTP://Mini.Local:8383/")!) == "http://mini.local:8383")
        #expect(UnsentReports.serverKey(URL(string: "http://mini.local:8383")!) == "http://mini.local:8383")
        #expect(UnsentReports.serverKey(URL(string: "https://example.com/flexarr/")!) == "https://example.com/flexarr")
    }

    @Test func onlyTheServerAndProfileItWasWatchedAsSendIt() {
        let kept = [report("a"), report("b", profile: "kid"), report("c", server: "http://other:8383")]

        #expect(UnsentReports.due(in: kept, server: home, profileId: "p").map(\.itemId) == ["a"])
        #expect(UnsentReports.due(in: kept, server: home, profileId: "kid").map(\.itemId) == ["b"])
        // The main profile has the same id on every server: another server's play isn't this one's.
        #expect(UnsentReports.due(in: kept, server: "http://other:8383", profileId: "p").map(\.itemId) == ["c"])
        // Not known yet (signed in, the profile not chosen): nothing goes.
        #expect(UnsentReports.due(in: kept, server: home, profileId: nil).isEmpty)
        #expect(UnsentReports.due(in: kept, server: nil, profileId: "p").isEmpty)
    }

    @Test func aTitleKeepsOnlyItsNewestReport() {
        let kept = UnsentReports.adding([report("a", at: 10, position: 200)], to: [report("a", position: 100), report("b")])
        #expect(kept.count == 2)
        #expect(kept.first { $0.itemId == "a" }?.position == 200)

        let older = UnsentReports.adding([report("a", at: -10, position: 50)], to: kept)
        #expect(older.first { $0.itemId == "a" }?.position == 200)

        // Another profile's play of the same title is its own.
        #expect(UnsentReports.adding([report("a", profile: "kid")], to: kept).count == 3)
    }

    @Test func sendingRemovesOnlyWhatWasSent() {
        let sent = report("a")
        #expect(UnsentReports.removing(sent, from: [sent, report("b")]).map(\.itemId) == ["b"])
        // Watched again while it was on its way: the newer place is still to send.
        let newer = report("a", at: 30, position: 400)
        #expect(UnsentReports.removing(sent, from: [newer]) == [newer])
    }

    @Test func aTitleTheServerHasntIsTriedAFewTimesThenDropped() {
        var count = 0
        var tries = 1
        while let next = UnsentReports.notFoundAgain(count) {
            count = next
            tries += 1
        }
        #expect(tries == UnsentReports.notFoundLimit)
        #expect(UnsentReports.notFoundAgain(0) == 1)
        #expect(UnsentReports.notFoundAgain(UnsentReports.notFoundLimit - 1) == nil)
    }

    @Test func aKeptListReadsBack() throws {
        let kept = [UnsentReport(server: home, profileId: "p", itemId: "a", position: 12.5, finished: true, playedAt: at, isBook: true, notFound: 1)]
        let data = try JSONEncoder().encode(kept)
        #expect(try JSONDecoder().decode([UnsentReport].self, from: data) == kept)
    }
}
