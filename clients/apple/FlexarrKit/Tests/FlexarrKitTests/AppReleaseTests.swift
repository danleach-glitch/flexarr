import Foundation
import Testing
@testable import FlexarrKit

struct AppReleaseTests {
    private func release(_ tag: String, assets: [String]) throws -> AppRelease {
        let json = #"{"tag_name":"\#(tag)","assets":[\#(assets.map { #"{"name":"\#($0)"}"# }.joined(separator: ","))]}"#
        return try JSONDecoder().decode(AppRelease.self, from: Data(json.utf8))
    }

    @Test func aNewerReleaseWithTheDiskImageCounts() throws {
        let latest = try release("v0.62.0", assets: ["flexarr-0.62.0-osx-arm64.tar.gz", "Flexarr.dmg"])
        #expect(latest.version == "0.62.0")
        #expect(latest.isNewer(than: "0.61.0"))
        #expect(latest.isNewer(than: "0.61.0-dev.1a2b3c4"))
        #expect(!latest.isNewer(than: "0.62.0"))
        #expect(!latest.isNewer(than: "0.63.0-dev.9f8e7d6"))
    }

    /// A release without the Mac app (a server-only fix, or its disk image still uploading) isn't offered.
    @Test func aReleaseWithoutTheDiskImageDoesNot() throws {
        let latest = try release("v0.62.1", assets: ["SHA256SUMS"])
        #expect(!latest.hasMacDownload)
        #expect(!latest.isNewer(than: "0.61.0"))
    }

    @Test func versionsThatDontReadNeverCount() throws {
        #expect(try !release("nightly", assets: ["Flexarr.dmg"]).isNewer(than: "0.61.0"))
        #expect(try !release("v0.62.0", assets: ["Flexarr.dmg"]).isNewer(than: "unknown"))
    }
}
