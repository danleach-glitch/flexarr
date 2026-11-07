import Testing
@testable import FlexarrKit

/// A long library grid fetches again only the titles near where it's looking after a watch, and everything when
/// that stretch has changed (audit perf-71).
struct NearbyRefreshTests {
    private struct Title: Identifiable, Equatable {
        let id: String
        var watched = false
    }

    private func titles(_ count: Int) -> [Title] { (0..<count).map { Title(id: "t\($0)") } }

    @Test func aShortListIsFetchedWhole() {
        #expect(NearbyRefresh.window(around: 10, loaded: 240) == nil)
        #expect(NearbyRefresh.window(around: 10, loaded: 60) == nil)
    }

    @Test func aLongListFetchesAroundTheTitleShown() {
        #expect(NearbyRefresh.window(around: 2000, loaded: 4000) == 1880..<2120)
        // Near either end it keeps its size, inside the list.
        #expect(NearbyRefresh.window(around: 5, loaded: 4000) == 0..<240)
        #expect(NearbyRefresh.window(around: 3990, loaded: 4000) == 3760..<4000)
        #expect(NearbyRefresh.window(around: 9000, loaded: 4000) == 3760..<4000)
        #expect(NearbyRefresh.window(around: 2000, loaded: 4000)!.count <= 500)
    }

    @Test func theSameTitlesArePatchedInPlace() {
        let items = titles(1000)
        var fetched = Array(items[400..<640])
        fetched[3].watched = true
        let patched = NearbyRefresh.patch(items, window: 400..<640, fetched: fetched, fetchedTotal: 1000, total: 1000)
        #expect(patched?.count == 1000)
        #expect(patched?[403].watched == true)
        #expect(patched?[402].watched == false)
        #expect(patched?.map(\.id) == items.map(\.id))
    }

    @Test func aChangedStretchOrSizeFetchesEverything() {
        let items = titles(1000)
        var moved = Array(items[400..<640])
        moved.swapAt(0, 1)
        #expect(NearbyRefresh.patch(items, window: 400..<640, fetched: moved, fetchedTotal: 1000, total: 1000) == nil)
        let same = Array(items[400..<640])
        #expect(NearbyRefresh.patch(items, window: 400..<640, fetched: same, fetchedTotal: 1001, total: 1000) == nil)
        #expect(NearbyRefresh.patch(items, window: 400..<640, fetched: Array(same.dropLast()), fetchedTotal: 1000, total: 1000) == nil)
        #expect(NearbyRefresh.patch(items, window: 900..<1140, fetched: same, fetchedTotal: 1000, total: 1000) == nil)
    }
}
