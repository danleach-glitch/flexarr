import Foundation
import Testing
@testable import FlexarrKit

/// Artwork is kept once, under the server's home address, whichever address it was fetched from.
struct KeptArtworkTests {
    /// A server of this test's own, so what another test pairs can't change what this one sees.
    private func server() -> (home: URL, remote: URL) {
        let name = UUID().uuidString.lowercased()
        return (URL(string: "http://\(name).local:8383")!, URL(string: "https://\(name).tail1234.ts.net:8443")!)
    }

    private func cache() -> URLCache { URLCache(memoryCapacity: 4 * 1024 * 1024, diskCapacity: 0) }

    private func answer(_ url: URL, cacheControl: String?, sent: Date? = nil, status: Int = 200) -> HTTPURLResponse {
        var headers = ["Content-Type": "image/jpeg"]
        if let cacheControl { headers["Cache-Control"] = cacheControl }
        if let sent {
            let formatter = DateFormatter()
            formatter.locale = Locale(identifier: "en_US_POSIX")
            formatter.timeZone = TimeZone(identifier: "GMT")
            formatter.dateFormat = "EEE, dd MMM yyyy HH:mm:ss 'GMT'"
            headers["Date"] = formatter.string(from: sent)
        }
        return HTTPURLResponse(url: url, statusCode: status, httpVersion: "HTTP/1.1", headerFields: headers)!
    }

    @Test func aPictureHasOneNameAtEitherAddress() {
        let (home, remote) = server()
        _ = ServerAddresses(home: home, remote: remote)
        let poster = "/api/v1/items/abc/poster?v=7&w=342"
        let atHome = FlexarrClient(baseURL: home).imageURL(poster, width: 342)!
        let away = FlexarrClient(baseURL: remote).imageURL(poster, width: 342)!
        #expect(away != atHome)
        #expect(ServerAddresses.atHome(away) == atHome)
        #expect(ServerAddresses.atHome(atHome) == atHome)
    }

    @Test func onlyAServersOwnAwayAddressIsRenamed() {
        let (home, remote) = server()
        _ = ServerAddresses(home: home, remote: remote)
        // Another site's picture, and a host that only starts like the server's, stay as they are.
        let elsewhere = URL(string: "https://image.tmdb.org/t/p/w342/poster.jpg")!
        #expect(ServerAddresses.atHome(elsewhere) == elsewhere)
        for lookalike in [remote.absoluteString + "0/api/v1/items/abc/poster",
                          remote.absoluteString.replacingOccurrences(of: ".ts.net", with: ".ts.net.example.com") + "/api/v1/items/abc/poster"] {
            let url = URL(string: lookalike)!
            #expect(ServerAddresses.atHome(url) == url)
        }
        // A server with no address for away from home has nothing to rename.
        let alone = server()
        _ = ServerAddresses(home: alone.home, remote: nil)
        let other = alone.remote.appending(path: "api/v1/items/abc/poster")
        #expect(ServerAddresses.atHome(other) == other)
    }

    @Test func aPictureFetchedAwayIsFoundUnderItsHomeAddress() throws {
        let (home, remote) = server()
        _ = ServerAddresses(home: home, remote: remote)
        let cache = cache()
        let away = remote.appending(path: "api/v1/items/abc/poster")
        let key = ServerAddresses.atHome(away)
        #expect(key == home.appending(path: "api/v1/items/abc/poster"))
        #expect(KeptArtwork.data(for: key, in: cache) == nil)

        let picture = Data(repeating: 7, count: 2048)
        KeptArtwork.keep(picture, from: answer(away, cacheControl: "public, max-age=2592000, immutable"), as: key, in: cache)
        #expect(KeptArtwork.data(for: key, in: cache) == picture)
        // Kept as an answer from the home address, which is what a request made at home looks for.
        let kept = try #require(cache.cachedResponse(for: URLRequest(url: key))?.response as? HTTPURLResponse)
        #expect(kept.url == key)
        #expect(kept.value(forHTTPHeaderField: "Cache-Control") == "public, max-age=2592000, immutable")
        #expect(kept.value(forHTTPHeaderField: "Content-Type") == "image/jpeg")
    }

    @Test func aPictureKeptAtHomeIsUsedAwayOnlyWhileTheServerSaidItsGood() {
        let (home, _) = server()
        let cache = cache()
        let key = home.appending(path: "api/v1/items/abc/poster")
        let picture = Data(repeating: 3, count: 1024)
        let fetched = Date(timeIntervalSince1970: 1_800_000_000)
        // What a request at home leaves in the cache: the server's answer, dated when it was sent.
        cache.storeCachedResponse(CachedURLResponse(response: answer(key, cacheControl: "private, max-age=600", sent: fetched), data: picture),
                                  for: URLRequest(url: key))
        #expect(KeptArtwork.data(for: key, in: cache, now: fetched.addingTimeInterval(60)) == picture)
        #expect(KeptArtwork.data(for: key, in: cache, now: fetched.addingTimeInterval(601)) == nil)
    }

    @Test func whatTheServerWantsAskedForEachTimeIsNeitherKeptNorUsed() {
        let (home, remote) = server()
        let cache = cache()
        let key = home.appending(path: "api/v1/profiles/1/image")
        let away = remote.appending(path: "api/v1/profiles/1/image")
        let picture = Data(repeating: 9, count: 512)
        for control in ["private, no-cache", "no-store", "public, max-age=600, no-cache", nil] {
            KeptArtwork.keep(picture, from: answer(away, cacheControl: control), as: key, in: cache)
            #expect(cache.cachedResponse(for: URLRequest(url: key)) == nil, "\(control ?? "none")")
        }
        // Nor an error, whatever it says.
        KeptArtwork.keep(picture, from: answer(away, cacheControl: "public, max-age=600", status: 503), as: key, in: cache)
        #expect(cache.cachedResponse(for: URLRequest(url: key)) == nil)
        // One a request at home left there without a time it's good for is asked for again.
        let now = Date()
        cache.storeCachedResponse(CachedURLResponse(response: answer(key, cacheControl: "private, no-cache", sent: now), data: picture),
                                  for: URLRequest(url: key))
        #expect(KeptArtwork.data(for: key, in: cache, now: now) == nil)
        // As is one with no date to count its age from.
        cache.storeCachedResponse(CachedURLResponse(response: answer(key, cacheControl: "public, max-age=600"), data: picture),
                                  for: URLRequest(url: key))
        #expect(KeptArtwork.data(for: key, in: cache, now: now) == nil)
    }
}
