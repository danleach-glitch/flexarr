import Foundation
import Testing
@testable import FlexarrKit

/// The app's own links carry its key; any other link only opens what it names.
struct OwnLinksTests {
    private let key = String(repeating: "ab", count: 32)

    private func url(_ text: String) throws -> URL { try #require(URL(string: text)) }

    @Test func aSignedLinkIsKnownAsTheAppsOwn() throws {
        let signed = OwnLinks.signed(try url("flexarr://play/abc-123?t=600"), key: key)
        #expect(OwnLinks.isSigned(signed, key: key))
        #expect(OwnLinks.unsigned(signed) == (try url("flexarr://play/abc-123?t=600")))
        // Signing again replaces the key rather than adding a second.
        #expect(OwnLinks.signed(signed, key: key).absoluteString.components(separatedBy: "k=").count == 2)
    }

    @Test func aLinkWithoutTheKey_OrWithAnother_IsFromOutside() throws {
        #expect(!OwnLinks.isSigned(try url("flexarr://play/abc"), key: key))
        #expect(!OwnLinks.isSigned(try url("flexarr://play/abc?k=\(String(repeating: "cd", count: 32))"), key: key))
        #expect(!OwnLinks.isSigned(try url("flexarr://play/abc?k=ab"), key: key))
        #expect(!OwnLinks.isSigned(try url("flexarr://play/abc?k="), key: ""))
    }

    @Test func fromOutside_PlayOpensThePage_KeepingOnlyTheProfile() throws {
        #expect(OwnLinks.fromOutside(try url("flexarr://play/abc?t=600&s=en&shuffle=1")) == (try url("flexarr://item/abc")))
        #expect(OwnLinks.fromOutside(try url("flexarr://play/abc?t=0&profile=Sam")) == (try url("flexarr://item/abc?profile=Sam")))
    }

    @Test func fromOutside_AChannelIsDropped_AndPagesOpenAsTheyAre() throws {
        #expect(OwnLinks.fromOutside(try url("flexarr://live/bbc-one")) == nil)
        #expect(OwnLinks.fromOutside(try url("flexarr://item/abc?profile=Sam")) == (try url("flexarr://item/abc?profile=Sam")))
        #expect(OwnLinks.fromOutside(try url("flexarr://watchlist")) == (try url("flexarr://watchlist")))
        // A key someone copied doesn't come along to be mistaken later.
        #expect(OwnLinks.fromOutside(try url("flexarr://item/abc?k=\(key)")) == (try url("flexarr://item/abc")))
        #expect(OwnLinks.fromOutside(try url("https://flexarr.io/join#abc")) == (try url("https://flexarr.io/join#abc")))
    }
}
