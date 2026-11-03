import Testing
@testable import FlexarrKit

/// Android core's ServerSupportTest checks the same cases.
@Suite struct ServerSupportTests {
    @Test func olderThanTheMinimumIsTooOld() {
        #expect(ServerSupport.isTooOld("0.49.9"))
        #expect(ServerSupport.isTooOld("0.23.0"))
        #expect(ServerSupport.isTooOld("0.49.2+d4c892c6190c"))
        #expect(ServerSupport.isTooOld("0.49"))
    }

    @Test func theMinimumAndNewerAreFine() {
        #expect(!ServerSupport.isTooOld("0.50.0"))
        #expect(!ServerSupport.isTooOld("0.50.1"))
        #expect(!ServerSupport.isTooOld("0.59.0+d4c892c6190c"))
        #expect(!ServerSupport.isTooOld("1.0.0"))
        #expect(!ServerSupport.isTooOld("v0.51.0"))
    }

    @Test func devBuildsCountByTheirNumbers() {
        #expect(!ServerSupport.isTooOld("0.60.0-dev.1a2b3c4"))
        #expect(!ServerSupport.isTooOld("0.50.0-dev.1a2b3c4+abc"))
        #expect(ServerSupport.isTooOld("0.49.0-dev.1a2b3c4"))
    }

    @Test func versionsThatDontReadAreNeverTooOld() {
        #expect(!ServerSupport.isTooOld(nil))
        #expect(!ServerSupport.isTooOld(""))
        #expect(!ServerSupport.isTooOld("dev"))
        #expect(!ServerSupport.isTooOld("0"))
        #expect(!ServerSupport.isTooOld("0.x.1"))
        #expect(!ServerSupport.isTooOld("0..1"))
        #expect(!ServerSupport.isTooOld("0.1.2.3"))
    }

    @Test func theMinimumReads() {
        #expect(ServerSupport.numbers(of: ServerSupport.minimumVersion) == [0, 50, 0])
    }
}
