import Foundation
import Testing
@testable import FlexarrKit

/// JSON captured from a live Flexarr server (arrays trimmed to keep the fixtures small).
enum Fixture {
    static func data(_ name: String) throws -> Data {
        let url = try #require(Bundle.module.url(forResource: name, withExtension: "json", subdirectory: "Fixtures"))
        return try Data(contentsOf: url)
    }

    static func decode<T: Decodable>(_ type: T.Type, _ name: String) throws -> T {
        try FlexarrClient.decoder.decode(T.self, from: data(name))
    }
}

struct DecodingTests {
    @Test func systemStatus() throws {
        let status = try Fixture.decode(SystemStatus.self, "system-status")
        #expect(status.appName == "Flexarr")
        #expect(!status.version.isEmpty)
    }

    @Test func libraries() throws {
        let libraries = try Fixture.decode([Library].self, "libraries")
        #expect(libraries.count == 3)
        #expect(libraries.contains { $0.type == .movies && $0.name == "Movies" })
        #expect(libraries.contains { $0.type == .tvShows })
    }

    @Test func home() throws {
        let home = try Fixture.decode(Home.self, "home")

        #expect(!home.featured.isEmpty)
        let featured = home.featured[0]
        #expect(featured.title == "Curb Your Enthusiasm")
        #expect(featured.kind == .series)
        #expect(featured.accentColor == "#264c99")
        #expect(featured.artwork.logo?.hasPrefix("/api/v1/images/items/") == true)
        #expect(home.overview(for: home.featured[1].id) != nil || home.overview(for: featured.id) != nil)

        #expect(home.rows.map(\.style) == [.wide, .poster, .top10])
        let continueRow = home.rows[0]
        #expect(continueRow.id == "continue")
        #expect(continueRow.link == nil)
        let episode = continueRow.items[0]
        #expect(episode.kind == .episode)
        #expect(episode.seriesTitle == "Breaking Bad")
        #expect(episode.episodeCode == "S2E3")
        #expect(episode.userData.lastPlayedAt != nil, "no-offset .NET timestamp with 6 fractional digits")
        let progress = try #require(episode.progress)
        #expect(progress > 0.5 && progress < 0.55)
        #expect(home.rows[1].link?.contains("sort=added") == true)
        #expect(home.rows[2].items.count == 3)
    }

    @Test func seriesDetail() throws {
        let detail = try Fixture.decode(ItemDetail.self, "item-series")

        #expect(detail.item.kind == .series)
        #expect(detail.genres == ["Comedy"])
        #expect(detail.studio == "HBO")
        #expect(detail.ancestors.isEmpty)
        #expect(detail.children.allSatisfy { $0.kind == .season })
        #expect(detail.children.first?.seasonNumber == 1)
        #expect(detail.credits.first?.name == "Larry David")
        #expect(detail.credits.first?.role == .actor)
        #expect(detail.files.isEmpty)
        #expect(detail.item.facts.contains("12 seasons"))
    }

    @Test func seasonDetailHasEpisodesAndOverviews() throws {
        let detail = try Fixture.decode(ItemDetail.self, "item-season")

        #expect(detail.item.kind == .season)
        #expect(detail.ancestors.map(\.title) == ["Curb Your Enthusiasm"])
        #expect(detail.children.allSatisfy { $0.kind == .episode })
        let first = try #require(detail.children.first)
        #expect(first.thumbOrBackdrop != nil)
        #expect((detail.childOverviews[first.id] ?? nil) != nil)
    }

    @Test func movieDetailWithFiles() throws {
        let detail = try Fixture.decode(ItemDetail.self, "item-movie")

        #expect(detail.item.kind == .movie)
        let file = try #require(detail.files.first)
        #expect(file.container == "mkv")
        #expect(file.videoRange == .sdr)
        #expect(file.hdr10Plus == nil)
        #expect(file.size > 1_000_000_000)
        #expect(file.streams.contains { $0.type == .audio && $0.channels == 6 })
    }

    @Test func search() throws {
        let results = try Fixture.decode(SearchResults.self, "search")
        #expect(!results.titles.isEmpty)
        #expect(results.episodes.allSatisfy { $0.kind == .episode })
        #expect(results.people.first?.credits ?? 0 > 0)
        #expect(!results.isEmpty)
    }

    @Test func person() throws {
        let person = try Fixture.decode(Person.self, "person")
        #expect(person.name == "Larry David")
        #expect(person.credits.first?.item.title == "Curb Your Enthusiasm")
    }

    @Test func playbackInfo() throws {
        let info = try Fixture.decode(PlaybackInfo.self, "playback")

        #expect(info.method == .transcode)
        #expect(info.url.hasSuffix("/master.m3u8"))
        #expect(info.startPositionSeconds > 1500)
        #expect(info.markers.map(\.type) == [.intro, .credits])
        #expect(info.audioTracks.first?.codec == "eac3")
        #expect(info.subtitles.first?.isText == true)
        #expect(info.next?.kind == .episode)
        #expect(info.ancestors.map(\.kind) == [.series, .season])
        #expect(info.transcodeSummary?.contains("H.264") == true)
    }

    @Test func unknownEnumValuesDoNotFailDecoding() throws {
        let json = #"{"type":"Chapter","start":1,"end":2}"#
        let marker = try FlexarrClient.decoder.decode(Marker.self, from: Data(json.utf8))
        #expect(marker.type == .unknown)
    }

    @Test func aNewerServersAvailabilityAndDownloadQualityStillDecode() throws {
        let option = #"{"quality":"Tiny","estimatedBytes":1000,"available":true}"#
        #expect(try FlexarrClient.decoder.decode(DownloadOption.self, from: Data(option.utf8)).quality == .unknown)
        let qualities = try FlexarrClient.decoder.decode([DownloadQuality].self, from: Data(#"["Small","Tiny"]"#.utf8))
        #expect(qualities == [.small, .unknown])
        let states = try FlexarrClient.decoder.decode([Availability].self, from: Data(#"["Requested","Processing"]"#.utf8))
        #expect(states == [.requested, .unknown])
    }
}

extension ItemSummary {
    var thumbOrBackdrop: String? { artwork.thumb ?? artwork.backdrop }
}

struct MusicDecodingTests {
    @Test func artistDetailListsAlbumsNewestFirst() throws {
        let detail = try Fixture.decode(ItemDetail.self, "item-artist")

        #expect(detail.item.kind == .artist)
        #expect(detail.item.kind.isMusic)
        #expect(detail.item.title == "Dire Straits")
        #expect(detail.item.facts == ["4 albums", "9.4"])
        #expect(detail.genres.contains("Rock"))
        #expect(detail.children.allSatisfy { $0.kind == .album })
        #expect(detail.children.map(\.year) == [1991, 1982, 1980, 1979])
        #expect(detail.children.first?.artistName == "Dire Straits")
        #expect(detail.children.first?.seriesId == detail.item.id)
    }

    @Test func albumDetailHasNumberedTracks() throws {
        let detail = try Fixture.decode(ItemDetail.self, "item-album")

        #expect(detail.item.kind == .album)
        #expect(detail.item.title == "On Every Street")
        #expect(detail.item.facts == ["1991", "12 tracks", "7.5"])
        #expect(detail.item.facts(scores: false) == ["1991", "12 tracks"])
        #expect(detail.ancestors.map(\.kind) == [.artist])
        #expect(detail.children.allSatisfy { $0.kind == .track })
        #expect(detail.children.map(\.trackNumber) == [1, 2, 3, 4, 5])
        #expect(detail.children.first?.discNumber == nil)
        #expect(detail.children.first?.title == "Calling Elvis")
        #expect(detail.children.first?.durationSeconds == 387.4)
        #expect(ItemSummary.totalRuntime(of: detail.children) == "25m")
        let discs = ItemSummary.discs(of: detail.children)
        #expect(discs.count == 1)
        #expect(discs.first?.disc == 1)
        #expect(discs.first?.tracks.count == 5)
    }

    @Test func tracksAreOrderedAcrossAlbums() throws {
        let tracks = try Fixture.decode([ItemSummary].self, "tracks")

        #expect(tracks.count == 3)
        #expect(tracks.allSatisfy { $0.kind == .track && $0.seriesTitle == "Dire Straits" })
        // The album's artist is named unless the track has its own.
        #expect(tracks.prefix(2).allSatisfy { $0.artistName == "Dire Straits" && $0.trackArtist == nil })
        #expect(tracks[2].trackArtist == "Mark Knopfler" && tracks[2].artistName == "Mark Knopfler")
        // Oldest album first, then track order; the third is the next album's opener.
        #expect(tracks.map(\.trackNumber) == [1, 2, 1])
        #expect(tracks[0].parentId == tracks[1].parentId)
        #expect(tracks[2].parentId != tracks[0].parentId)
    }

    @Test func playlistCarriesItsTracksInOrder() throws {
        // A playlist as the server sends it, wrapped around the captured track summaries.
        let tracks = try Fixture.data("tracks")
        let entries = try FlexarrClient.decoder.decode([ItemSummary].self, from: tracks)
        let wrapped = try #require(JSONSerialization.jsonObject(with: tracks) as? [Any])
        let detailObject: [String: Any] = [
            "summary": ["id": "7c1d", "name": "Road trip", "trackCount": 3, "durationSeconds": 912.5,
                        "covers": ["/api/v1/images/items/a/poster", "/api/v1/images/items/b/poster"], "cover": NSNull(),
                        "createdAt": "2026-10-25T10:00:00", "updatedAt": "2026-10-25T11:30:00.123456"],
            "tracks": wrapped.enumerated().map { ["entryId": $0.offset + 5, "track": $0.element] },
        ]

        let detail = try FlexarrClient.decoder.decode(PlaylistDetail.self, from: JSONSerialization.data(withJSONObject: detailObject))

        #expect(detail.summary.name == "Road trip")
        #expect(detail.summary.trackCountLabel == "3 tracks")
        #expect(detail.summary.covers.count == 2)
        #expect(detail.summary.cover == nil)
        #expect(detail.tracks.map(\.entryId) == [5, 6, 7])
        #expect(detail.tracks.map(\.track) == entries)
    }

    @Test func homeRowOfPlaylistsHasContentWithNoItems() throws {
        let json = """
        {"id":"playlists","title":"Your playlists","style":"playlists","items":[],"link":null,
         "playlists":[{"id":"7c1d","name":"Road trip","trackCount":3,"durationSeconds":912.5,"covers":[],"cover":null,
                       "createdAt":"2026-10-25T10:00:00","updatedAt":"2026-10-25T11:30:00"}]}
        """
        let row = try FlexarrClient.decoder.decode(HomeRow.self, from: Data(json.utf8))
        #expect(row.style == .playlists)
        #expect(row.hasContent)
        #expect(row.playlists?.first?.name == "Road trip")

        let empty = try FlexarrClient.decoder.decode(HomeRow.self, from: Data(#"{"id":"x","title":"Nothing","style":"future","items":[],"link":null}"#.utf8))
        #expect(empty.style == .unknown)
        #expect(!empty.hasContent)
    }

    @Test func discsAreGroupedAndOrdered() throws {
        // The first track moved to a second disc still comes after the first disc's.
        var data = try #require(JSONSerialization.jsonObject(with: Fixture.data("tracks")) as? [[String: Any]])
        data[0]["discNumber"] = 2
        let tracks = try FlexarrClient.decoder.decode([ItemSummary].self, from: JSONSerialization.data(withJSONObject: data))

        let discs = ItemSummary.discs(of: tracks)

        #expect(discs.map(\.disc) == [1, 2])
        #expect(discs[0].tracks.count == 2)
        #expect(discs[1].tracks.map(\.title) == ["Once Upon A Time In The West"])
    }
}

struct DownloadDecodingTests {
    @Test func aTrackDownloadSaysWhatKindOfFileItComesAs() throws {
        let download = try Fixture.decode(OfflineDownload.self, "offline-track")
        #expect(download.item.kind == .track)
        #expect(download.quality == .original)
        #expect(download.status == .ready)
        #expect(download.fileName?.hasSuffix(".flac") == true)
        #expect(download.fileExtension == "flac")
    }

    @Test func aDownloadFromAnOlderServerIsAnMp4() throws {
        var json = try #require(JSONSerialization.jsonObject(with: Fixture.data("offline-track")) as? [String: Any])
        json.removeValue(forKey: "fileName")
        let download = try FlexarrClient.decoder.decode(OfflineDownload.self, from: JSONSerialization.data(withJSONObject: json))
        #expect(download.fileName == nil)
        #expect(download.fileExtension == "mp4")
    }

    @Test func aMusicLibrarysMixesRowAndAMixDecode() throws {
        let browse = #"""
        {"featured":[],"overviews":{},"upcoming":[],"downloading":0,"rows":[
         {"id":"music-mixes","title":"Made for you","style":"mixes","items":[],"link":null,"playlists":null,
          "mixes":[{"id":"a1b2c3d4","name":"Rock mix","description":"Foo Fighters, Muse and more","covers":["/c1","/c2"],"trackCount":40}],"channels":null},
         {"id":"music-top","title":"Most played","style":"tracks","items":[],"link":null}]}
        """#
        let home = try FlexarrClient.decoder.decode(Home.self, from: Data(browse.utf8))
        #expect(home.rows.map(\.id) == ["music-mixes", "music-top"])
        #expect(home.rows[0].style == .mixes)
        #expect(home.rows[0].mixes?.first?.name == "Rock mix")
        #expect(home.rows[0].mixes?.first?.covers.count == 2)
        #expect(home.rows[0].hasContent)
        #expect(!home.rows[1].hasContent)

        let detail = try FlexarrClient.decoder.decode(MixDetail.self, from: Data(#"""
        {"summary":{"id":"a1b2c3d4","name":"Rock mix","description":"Foo Fighters, Muse and more","covers":[],"trackCount":0},"tracks":[]}
        """#.utf8))
        #expect(detail.summary.id == "a1b2c3d4")
        #expect(detail.tracks.isEmpty)
    }
}
