import Testing
@testable import FlexarrKit

struct SeasonFetchesTests {
    /// `start`, as a call `#expect` can take.
    private func go(_ fetches: inout SeasonFetches, _ id: String, have: Bool, force: Bool) -> Bool {
        fetches.start(id, have: have, force: force)
    }

    @Test func theKeyedEffectWaitsForTheLoadsFetch() {
        var fetches = SeasonFetches()
        // The load picks the season and fetches it, forced.
        #expect(go(&fetches, "s1", have: false, force: true))
        // The effect keyed on the selected season fires for the same pick: no second request.
        #expect(!go(&fetches, "s1", have: false, force: false))
        fetches.finish("s1")
        #expect(fetches.inFlight.isEmpty)
    }

    @Test func aSeasonAlreadyHadIsntFetchedUnlessForced() {
        var fetches = SeasonFetches()
        #expect(!go(&fetches, "s1", have: true, force: false))
        #expect(go(&fetches, "s1", have: true, force: true))
    }

    @Test func anotherSeasonStillGoes() {
        var fetches = SeasonFetches()
        #expect(go(&fetches, "s1", have: false, force: true))
        #expect(go(&fetches, "s2", have: false, force: false))
        #expect(fetches.inFlight == ["s1", "s2"])
    }

    @Test func aReloadGoesEvenWhileOneIsOut() {
        var fetches = SeasonFetches()
        #expect(go(&fetches, "s1", have: false, force: false))
        #expect(go(&fetches, "s1", have: false, force: true))
        fetches.finish("s1")
        #expect(!go(&fetches, "s1", have: true, force: false))
    }
}
