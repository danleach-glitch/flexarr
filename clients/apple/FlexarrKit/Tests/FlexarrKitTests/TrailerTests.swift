import Testing
@testable import FlexarrKit

struct TrailerTests {
    @Test func onlyRealVideoIdsMakeATrailer() {
        #expect(YouTubeTrailer(id: "dQw4w9WgXcQ")?.id == "dQw4w9WgXcQ")
        #expect(YouTubeTrailer(id: "a-b_c-d_e-f") != nil)
        #expect(YouTubeTrailer(id: nil) == nil)
        #expect(YouTubeTrailer(id: "") == nil)
        #expect(YouTubeTrailer(id: "dQw4w9WgXc") == nil)
        #expect(YouTubeTrailer(id: "dQw4w9WgXcQQ") == nil)
        #expect(YouTubeTrailer(id: "dQw4w9\"><sc") == nil)
        #expect(YouTubeTrailer(id: "dQw4w9WgXcé") == nil)
    }

    @Test func addresses() throws {
        let trailer = try #require(YouTubeTrailer(id: "dQw4w9WgXcQ"))
        #expect(trailer.embedURL.absoluteString == "https://www.youtube-nocookie.com/embed/dQw4w9WgXcQ?autoplay=1&rel=0&playsinline=1")
        #expect(trailer.appURL.absoluteString == "youtube://watch/dQw4w9WgXcQ")
        #expect(trailer.embedPage.contains("src=\"https://www.youtube-nocookie.com/embed/dQw4w9WgXcQ?"))
    }
}
