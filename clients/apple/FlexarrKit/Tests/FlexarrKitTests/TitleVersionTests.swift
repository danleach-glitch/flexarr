import Foundation
import Testing
@testable import FlexarrKit

struct TitleVersionTests {
    private func version(_ label: String, seconds: Double? = 7200, edition: String? = nil) -> TitleVersion {
        TitleVersion(fileId: label, label: label, edition: edition, durationSeconds: seconds, size: 1024)
    }

    @Test func aLengthTheyShare_IsLeftOut() {
        let all = [version("4K"), version("1080p", seconds: 7180)]
        #expect(all[0].name(among: all) == "4K")
    }

    @Test func aCutAndALength_ShowWhereTheyDiffer() {
        let all = [version("4K", seconds: 10800, edition: "Director's Cut"), version("1080p")]
        #expect(all[0].name(among: all) == "4K, Director's Cut, 3h")
        #expect(all[1].name(among: all) == "1080p, 2h")
    }

    @Test func anUnknownLength_IsIgnored() {
        let all = [version("4K"), version("1080p", seconds: nil)]
        #expect(all[1].name(among: all) == "1080p")
    }

    @Test func theFacts_NameEachVersionOnce_InPlaceOfTheResolution() throws {
        let item = try Fixture.decode(ItemDetail.self, "item-movie").item
        let facts = item.facts(scores: false, versions: [version("4K"), version("1080p"), version("1080p", seconds: 10800)])
        #expect(facts.suffix(2) == ["4K", "1080p"])
    }
}
