import Foundation
import Testing
@testable import FlexarrKit

struct MusicLibraryClientTests {
    @Test func albumsAskForTheirSortGenreAndPage() async throws {
        let body = Data(#"{"total":1,"items":[]}"#.utf8)
        let (client, host) = stubbedClient(.init(status: 200, body: body))
        let page = try await client.libraryAlbums("lib", sort: .added, genre: "Trip hop", skip: 60, take: 30)
        #expect(page?.total == 1)
        let url = try #require(StubProtocol.recorded(host: host).first?.url)
        #expect(url.path == "/api/v1/libraries/lib/albums")
        let query = URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems ?? []
        #expect(query.contains(.init(name: "sort", value: "added")))
        #expect(query.contains(.init(name: "genre", value: "Trip hop")))
        #expect(query.contains(.init(name: "skip", value: "60")))
        #expect(query.contains(.init(name: "take", value: "30")))
    }

    @Test func albumsAndAlbumGenresAreNilFromAnOlderServer() async throws {
        let (client, _) = stubbedClient(.init(status: 404, body: Data()))
        #expect(try await client.libraryAlbums("lib") == nil)
        #expect(try await client.libraryAlbumGenres("lib") == nil)
    }

    @Test func albumGenresDecodeWithTheirBackdrop() async throws {
        let body = Data(#"[{"name":"Trip hop","count":2,"backdrop":"/api/v1/images/items/a/backdrop"},{"name":"Dubstep","count":1,"backdrop":null}]"#.utf8)
        let (client, host) = stubbedClient(.init(status: 200, body: body))
        let genres = try #require(try await client.libraryAlbumGenres("lib"))
        #expect(genres.map(\.name) == ["Trip hop", "Dubstep"])
        #expect(genres[0].backdrop != nil)
        #expect(genres[1].backdrop == nil)
        #expect(StubProtocol.recorded(host: host).first?.url?.path == "/api/v1/libraries/lib/album-genres")
    }

    @Test func aGenresTracksCanComeShuffled() async throws {
        let (client, host) = stubbedClient(.init(status: 200, body: Data("[]".utf8)))
        _ = try await client.genreTracks("lib", genre: "Rock", shuffled: true)
        _ = try await client.radio("artist")
        let urls = StubProtocol.recorded(host: host).compactMap(\.url)
        #expect(urls.first?.path == "/api/v1/libraries/lib/tracks")
        #expect(urls.first?.query?.contains("shuffle=true") == true)
        #expect(urls.last?.path == "/api/v1/items/artist/radio")
    }
}
