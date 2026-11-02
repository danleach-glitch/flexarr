import Foundation
import Testing
@testable import FlexarrKit

struct MusicRequestTests {
    @Test func discover() throws {
        let json = #"""
        {"rows":[{"id":"like","title":"Like artists you play","shape":"artists","items":[
          {"kind":"artist","deezerId":27,"title":"Daft Punk","artist":null,"artistDeezerId":null,"year":null,
           "image":"/api/v1/images/remote?url=x","availability":"None","libraryItemId":null}]},
         {"id":"new","title":"New from your artists","shape":"albums","items":[
          {"kind":"album","deezerId":302127,"title":"Discovery","artist":"Daft Punk","artistDeezerId":27,"year":2001,
           "image":null,"availability":"Available","libraryItemId":"3f2a","recordType":"album","musicBrainzId":"mb-1"},
          {"kind":"album","deezerId":9,"title":"Next","artist":"A","artistDeezerId":1,"year":2027,
           "image":null,"availability":"SomethingNewer","libraryItemId":null}]}],
         "genres":[{"id":152,"name":"Rock","image":null}]}
        """#
        let discover = try FlexarrClient.decoder.decode(MusicDiscover.self, from: Data(json.utf8))
        #expect(discover.rows.map(\.id) == ["like", "new"])
        #expect(discover.rows[0].isArtists)
        let artist = discover.rows[0].items[0]
        #expect(artist.isArtist)
        #expect(artist.isRequestable)
        #expect(artist.id == "artist-27")
        #expect(artist.recordType == nil)
        let owned = discover.rows[1].items[0]
        #expect(owned.availability == .available)
        #expect(owned.libraryItemId == "3f2a")
        #expect(!owned.isRequestable)
        #expect(owned.musicBrainzId == "mb-1")
        #expect(discover.rows[1].items[1].availability == .unknown)
        #expect(discover.genres.first?.name == "Rock")
    }

    @Test func artistAndAlbum() throws {
        let artist = try FlexarrClient.decoder.decode(MusicArtistDetail.self, from: Data(#"""
        {"deezerId":27,"name":"Daft Punk","image":null,"fans":4000000,"albums":[],"similar":[],"followed":true,
         "availability":"PartlyAvailable","libraryItemId":"a1"}
        """#.utf8))
        #expect(artist.followed)
        #expect(artist.availability == .partlyAvailable)
        #expect(artist.lidarrAnswered == nil)
        let album = try FlexarrClient.decoder.decode(MusicAlbumDetail.self, from: Data(#"""
        {"deezerId":302127,"title":"Discovery","artist":"Daft Punk","artistDeezerId":27,"image":null,"year":2001,"recordType":"album",
         "genres":["Electro"],"tracks":[{"title":"One More Time","durationSeconds":320,"number":1,"disc":1,"explicit":false}],
         "durationSeconds":3660,"availability":"Requested","libraryItemId":null}
        """#.utf8))
        #expect(album.tracks.first?.title == "One More Time")
        #expect(album.summary.id == "album-302127")
        #expect(album.summary.availability == .requested)
        #expect(!album.summary.isRequestable)
    }

    @Test func pagesSayWhenLidarrDidNotAnswer() throws {
        let artist = try FlexarrClient.decoder.decode(MusicArtistDetail.self, from: Data(#"""
        {"deezerId":27,"name":"Daft Punk","image":null,"fans":1,"albums":[],"similar":[],"followed":false,
         "availability":"None","libraryItemId":null,"lidarrAnswered":false}
        """#.utf8))
        #expect(artist.lidarrAnswered == false)
        let album = try FlexarrClient.decoder.decode(MusicAlbumDetail.self, from: Data(#"""
        {"deezerId":1,"title":"A","artist":"B","artistDeezerId":null,"image":null,"year":null,"recordType":null,"genres":[],
         "tracks":[],"durationSeconds":0,"availability":"None","libraryItemId":null,"lidarrAnswered":true}
        """#.utf8))
        #expect(album.lidarrAnswered == true)
        let extras = try FlexarrClient.decoder.decode(MusicArtistExtras.self, from: Data(#"""
        {"missing":[],"similar":[],"followed":false,"deezerId":27,"lidarrAnswered":false}
        """#.utf8))
        #expect(extras.lidarrAnswered == false)
        #expect(MusicArtistExtras.empty.lidarrAnswered == nil)
    }

    @Test func lidarrOnlyAlbumsRequestByMusicBrainz() throws {
        let extras = try FlexarrClient.decoder.decode(MusicArtistExtras.self, from: Data(#"""
        {"missing":[{"kind":"album","deezerId":0,"title":"Homework","artist":"Daft Punk","artistDeezerId":null,"year":1997,
          "image":null,"availability":"None","libraryItemId":null,"musicBrainzId":"rg-1"}],"similar":[],"followed":false,"deezerId":null}
        """#.utf8))
        let album = try #require(extras.missing.first)
        #expect(!album.hasPage)
        #expect(album.isRequestable)
        #expect(album.id == "album-rg-1")
        #expect(MusicRequestTarget.album(album) == .albumByMusicBrainz("rg-1"))
        #expect(extras.deezerId == nil)
    }

    @Test func oldServersHaveMusicRequestsOff() async throws {
        let (client, _) = stubbedClient(.init(status: 404, body: Data()))
        #expect(try await client.musicRequestStatus() == .off)
    }

    @Test func serversAnsweringWithTheWebPageHaveMusicRequestsOff() async throws {
        // Servers before music requests answer unknown paths with the web app's index.html and a 200.
        let (old, _) = stubbedClient(.init(status: 200, body: Data("<!doctype html>".utf8), contentType: "text/html"))
        #expect(try await old.musicRequestStatus() == .off)
        let (odd, _) = stubbedClient(.init(status: 200, body: Data(#"{"something":"else"}"#.utf8)))
        #expect(try await odd.musicRequestStatus() == .off)
        let (current, _) = stubbedClient(.init(status: 200, body: Data(#"{"configured":true,"reachable":false}"#.utf8)))
        #expect(try await current.musicRequestStatus() == MusicRequestStatus(configured: true, reachable: false))
    }

    @Test func repeatedItemsGetTheirOwnKeys() {
        let joint = MusicExternal(kind: "album", deezerId: 5, title: "Watch the Throne")
        let keys = [joint, joint].listed.map(\.id)
        #expect(Set(keys).count == 2)
    }

    @Test func releasesAreShelvedByKind() {
        func album(_ id: Int, _ type: String?) -> MusicExternal { MusicExternal(kind: "album", deezerId: id, title: "\(id)", recordType: type) }
        let shelves = [album(1, "album"), album(2, "single"), album(3, nil), album(4, "ep"), album(5, "compile")].shelves
        #expect(shelves.map(\.title) == ["Albums", "EPs and singles", "Compilations"])
        #expect(shelves[0].items.map(\.deezerId) == [1, 3])
        #expect(shelves[1].items.map(\.deezerId) == [2, 4])
        #expect([album(1, "single")].shelves.map(\.title) == ["EPs and singles"])
        #expect([MusicExternal]().shelves.isEmpty)
    }

    @Test func requestBodies() async throws {
        for (target, body) in [
            (MusicRequestTarget.album(deezerId: 5), #"{"deezerId":5,"kind":"album"}"#),
            (.albumByMusicBrainz("rg"), #"{"kind":"album","musicBrainzId":"rg"}"#),
            (.artist(deezerId: 27), #"{"deezerId":27,"kind":"artist"}"#),
            (.libraryArtist("a1"), #"{"artistId":"a1","kind":"artist"}"#),
        ] {
            let (client, host) = stubbedClient(.init(status: 202, body: Data()))
            try await client.requestMusic(target)
            let request = try #require(StubProtocol.recorded(host: host).first)
            #expect(request.httpMethod == "POST")
            #expect(request.url?.path == "/api/v1/requests/music")
            let sent = try JSONSerialization.jsonObject(with: request.httpBody ?? Data()) as? NSDictionary
            let expected = try JSONSerialization.jsonObject(with: Data(body.utf8)) as? NSDictionary
            #expect(sent == expected)
        }
    }

    @Test func requestErrorsCarryTheDetail() async throws {
        let (client, _) = stubbedClient(.init(status: 409, body: Data(#"{"title":"Conflict","detail":"Lidarr doesn't know this artist"}"#.utf8),
                                              contentType: "application/problem+json"))
        await #expect(throws: FlexarrClientError.http(status: 409, message: "Lidarr doesn't know this artist")) {
            try await client.requestMusic(.artist(deezerId: 1))
        }
    }
}

struct MusicUndoTests {
    private func sent(_ call: (FlexarrClient) async throws -> Void) async throws -> (path: String?, body: NSDictionary?) {
        let (client, host) = stubbedClient(.init(status: 204, body: Data()))
        try await call(client)
        let request = try #require(StubProtocol.recorded(host: host).first)
        #expect(request.httpMethod == "POST")
        return (request.url?.path, try JSONSerialization.jsonObject(with: request.httpBody ?? Data()) as? NSDictionary)
    }

    @Test func unfollowSendsOnlyTheIdGiven() async throws {
        let library = try await sent { try await $0.unfollowArtist(libraryArtistId: "a1") }
        #expect(library.path == "/api/v1/requests/music/unfollow")
        #expect(library.body == ["artistId": "a1"])
        let deezer = try await sent { try await $0.unfollowArtist(deezerId: 27) }
        #expect(deezer.body == ["deezerId": 27])
    }

    @Test func unrequestPrefersMusicBrainz() async throws {
        let lidarr = MusicExternal(kind: "album", deezerId: 5, title: "Homework", musicBrainzId: "rg-1")
        let byMbid = try await sent { try await $0.unrequestAlbum(lidarr) }
        #expect(byMbid.path == "/api/v1/requests/music/unrequest")
        #expect(byMbid.body == ["musicBrainzId": "rg-1"])
        let deezerOnly = MusicExternal(kind: "album", deezerId: 5, title: "Homework")
        #expect(try await sent { try await $0.unrequestAlbum(deezerOnly) }.body == ["deezerId": 5])
    }

    @Test func lidarrRefusalsCarryTheDetail() async throws {
        let (client, _) = stubbedClient(.init(status: 502, body: Data(#"{"title":"Bad Gateway","detail":"Lidarr isn't answering."}"#.utf8),
                                              contentType: "application/problem+json"))
        await #expect(throws: FlexarrClientError.http(status: 502, message: "Lidarr isn't answering.")) {
            try await client.unfollowArtist(deezerId: 1)
        }
    }
}
