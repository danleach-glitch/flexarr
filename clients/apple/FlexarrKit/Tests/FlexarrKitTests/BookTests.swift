import Foundation
import Testing
@testable import FlexarrKit

/// Audiobooks as a 0.58 server sends them (shapes from `src/Flexarr.Api/Books/BookDtos.cs`).
enum BookJSON {
    static let summary = ##"""
    {"id":"b1","libraryId":"lib","parentId":null,"kind":"Audiobook","title":"Project Hail Mary","year":2021,"seasonNumber":null,
     "episodeNumber":null,"episodeNumberEnd":null,"airDate":null,"childCount":0,"durationSeconds":58000,"resolution":null,
     "addedAt":"2026-10-30T10:00:00Z","artwork":{"poster":"/api/v1/images/items/b1/poster","backdrop":null,"logo":null,"thumb":null},
     "accentColor":null,"communityRating":null,"criticRating":null,"contentRating":null,"runtimeMinutes":null,"releaseDate":"2021-05-04",
     "userData":{"positionSeconds":1200,"watched":false,"playCount":0,"lastPlayedAt":"2026-10-30T11:00:00Z","unwatchedCount":null},
     "seriesTitle":null,
     "book":{"authors":"Andy Weir","authorCount":1,"narrator":"Ray Porter","series":"Standalone","sequence":"1","durationSeconds":58000,"finishedAt":null}}
    """##

    static func item(_ book: String = summary) -> String { book }

    static let manifest = ##"""
    {"itemId":"b1","durationSeconds":300,
     "files":[{"index":0,"fileId":"f0","offsetSeconds":0,"durationSeconds":100,"container":"mp3","audioCodec":"mp3","size":1000,"url":"/api/v1/playback/s/book/0","converted":false},
              {"index":1,"fileId":"f1","offsetSeconds":100,"durationSeconds":120,"container":"flac","audioCodec":"flac","size":2000,"url":"/api/v1/playback/s/book/1","converted":true},
              {"index":2,"fileId":"f2","offsetSeconds":220,"durationSeconds":80,"container":"m4b","audioCodec":"aac","size":800,"url":"/api/v1/playback/s/book/2","converted":false}],
     "chapters":[{"index":0,"title":"Opening","startSeconds":0,"endSeconds":60},{"index":1,"title":"Two","startSeconds":60,"endSeconds":220},
                 {"index":2,"title":"Three","startSeconds":220,"endSeconds":300}],
     "bookmarks":[{"id":"m1","positionSeconds":42.5,"note":"Good bit","createdAt":"2026-10-30T11:00:00.123456"}],
     "progress":{"positionSeconds":150,"finished":false,"finishedAt":null,"updatedAt":"2026-10-30T11:00:00Z","device":"Dan's iPad"},
     "speed":1.25,"listening":{"speed":1,"skipBackSeconds":15,"skipForwardSeconds":30,"smartRewind":true},"nextInSeries":null}
    """##
}

struct BookDecodingTests {
    @Test func aBookSummaryCarriesItsAuthorsAndSeries() throws {
        let item = try FlexarrClient.decoder.decode(ItemSummary.self, from: Data(BookJSON.summary.utf8))
        #expect(item.kind == .audiobook)
        #expect(item.isBook)
        #expect(item.book?.narrator == "Ray Porter")
        #expect(item.bookAuthors == "Andy Weir")
        #expect(item.book?.durationSeconds == 58000)
    }

    @Test func theManifestDecodes() throws {
        let manifest = try FlexarrClient.decoder.decode(BookManifest.self, from: Data(BookJSON.manifest.utf8))
        #expect(manifest.files.count == 3)
        #expect(manifest.files[1].converted)
        #expect(manifest.chapters.map(\.title) == ["Opening", "Two", "Three"])
        #expect(manifest.bookmarks.first?.note == "Good bit")
        #expect(manifest.progress.device == "Dan's iPad")
        #expect(manifest.listening.skipBackSeconds == 15)
        #expect(manifest.speed == 1.25)
    }

    @Test func playbackInfoCarriesABook() throws {
        let json = ##"""
        {"sessionId":"s","method":"DirectPlay","url":"/api/v1/playback/s/book/0","reasons":[],"durationSeconds":300,"startPositionSeconds":150,
         "fileId":"f0","audioTracks":[],"selectedAudioIndex":null,"subtitles":[],"selectedSubtitleKey":null,"subtitleBurnedIn":false,"markers":[],
         "item":\##(BookJSON.summary),"ancestors":[],"next":null,"transcodeSummary":null,"shuffle":false,"speed":1.25,"book":\##(BookJSON.manifest)}
        """##
        let info = try FlexarrClient.decoder.decode(PlaybackInfo.self, from: Data(json.utf8))
        #expect(info.book?.files.count == 3)
        #expect(info.book?.itemId == "b1")
    }

    @Test func aBookPageDecodes() throws {
        let json = ##"""
        {"item":\##(BookJSON.summary),"path":null,"originalTitle":null,"overview":"A lone astronaut.","studio":null,"status":null,
         "trailerYouTubeId":null,"genres":["Science Fiction"],"tvdbId":null,"tmdbId":null,"imdbId":null,"ancestors":[],"children":[],
         "credits":[{"personId":"p1","name":"Andy Weir","role":"Author","character":null,"image":null},
                    {"personId":"p2","name":"Ray Porter","role":"Narrator","character":null,"image":null},
                    {"personId":"p3","name":"Someone","role":"Illustrator","character":null,"image":null}],
         "files":[],"childOverviews":{},
         "book":{"subtitle":"A Novel","asin":"B08G9PRS1K","isbn":null,"publisher":"Audible Studios","language":"English","abridged":false,
                 "explicit":false,"durationSeconds":58000,"chapterSource":"Embedded","finishedAt":null,"speed":null,
                 "series":[{"id":"s1","name":"Hail Mary","sequence":"3","bookCount":7}],
                 "chapters":[{"index":0,"title":"One","startSeconds":0,"endSeconds":58000}],"bookmarks":[],
                 "moreInSeries":[],"byAuthor":[\##(BookJSON.summary)],"readByNarrator":[]}}
        """##
        let detail = try FlexarrClient.decoder.decode(ItemDetail.self, from: Data(json.utf8))
        #expect(detail.credits.map(\.role) == [.author, .narrator, .unknown])
        #expect(detail.book?.subtitle == "A Novel")
        #expect(detail.book?.chapterSource == .embedded)
        #expect(detail.book?.series.first?.placeLabel == "Book 3 of 7")
        #expect(detail.book?.byAuthor.count == 1)
    }

    @Test func shelvesSeriesAndPeopleDecode() throws {
        let shelves = ##"""
        {"continueListening":[\##(BookJSON.summary)],"nextInSeries":[],"recentlyAdded":[],
         "series":[{"id":"s1","name":"Hail Mary","bookCount":2,"finishedCount":1,"covers":[{"poster":"/p","backdrop":null,"logo":null,"thumb":null}],"next":null}],
         "authors":[{"id":"p1","name":"Andy Weir","image":null,"bookCount":3}],"narrators":[],"genres":[{"name":"Science Fiction","count":3}],"finished":[]}
        """##
        let decoded = try FlexarrClient.decoder.decode(BookShelves.self, from: Data(shelves.utf8))
        #expect(decoded.continueListening.count == 1)
        #expect(decoded.series.first?.covers.first?.poster == "/p")
        #expect(decoded.authors.first?.bookCount == 3)
        #expect(!decoded.isEmpty)

        let series = ##"""
        {"id":"s1","libraryId":"lib","name":"Hail Mary","description":null,"books":[{"book":\##(BookJSON.summary),"sequence":"1.5"}],"nextId":"b1"}
        """##
        let one = try FlexarrClient.decoder.decode(BookSeries.self, from: Data(series.utf8))
        #expect(one.books.first?.sequence == "1.5")
        #expect(one.nextId == "b1")
    }

    @Test func searchAndPersonHaveBooksFromNewerServers() throws {
        let older = ##"{"titles":[],"episodes":[],"people":[],"genres":[]}"##
        let newer = ##"{"titles":[],"episodes":[],"people":[],"genres":[],"books":[\##(BookJSON.summary)],"bookSeries":[{"id":"s1","name":"Hail Mary","bookCount":2}]}"##
        let before = try FlexarrClient.decoder.decode(SearchResults.self, from: Data(older.utf8))
        #expect(before.books == nil)
        #expect(before.isEmpty)
        let after = try FlexarrClient.decoder.decode(SearchResults.self, from: Data(newer.utf8))
        #expect(after.books?.count == 1)
        #expect(after.bookSeries?.first?.name == "Hail Mary")
        #expect(!after.isEmpty)

        let person = ##"{"id":"p1","name":"Andy Weir","image":null,"credits":[],"biography":"Writes."}"##
        #expect(try FlexarrClient.decoder.decode(Person.self, from: Data(person.utf8)).biography == "Writes.")
    }

    @Test func progressUpdatesSendTheirTime() throws {
        let update = BookProgressUpdate(positionSeconds: -3, device: "iPhone", recordedAt: Date(timeIntervalSince1970: 0), ended: false)
        let object = try #require(JSONSerialization.jsonObject(with: JSONEncoder().encode(update)) as? [String: Any])
        #expect(object["positionSeconds"] as? Double == 0)
        #expect(object["recordedAt"] as? String == "1970-01-01T00:00:00Z")
        #expect(object["device"] as? String == "iPhone")
    }

    @Test func filtersBecomeTheQuery() {
        var filter = BookFilter(progress: .inProgress, authorId: "p1", sort: .added)
        var query = Dictionary(uniqueKeysWithValues: filter.query.map { ($0.name, $0.value ?? "") })
        #expect(query["progress"] == "inprogress")
        #expect(query["author"] == "p1")
        #expect(query["sort"] == "added")
        #expect(query["descending"] == "true")
        filter.sort = .title
        filter.progress = .any
        query = Dictionary(uniqueKeysWithValues: filter.query.map { ($0.name, $0.value ?? "") })
        #expect(query["progress"] == nil)
        #expect(query["descending"] == "false")
    }

    @Test func onlyTheServersOwnAddressesCarryTheKey() throws {
        let client = FlexarrClient(baseURL: URL(string: "http://192.168.1.20:8383")!, apiKey: "secret")
        func file(_ url: String) -> BookFile { BookFile(index: 0, fileId: "f", offsetSeconds: 0, durationSeconds: 1, url: url) }

        let relative = try #require(client.bookFileRequest(file("/api/v1/items/b1/book/0")))
        #expect(relative.url?.absoluteString == "http://192.168.1.20:8383/api/v1/items/b1/book/0")
        #expect(relative.value(forHTTPHeaderField: "X-Api-Key") == "secret")
        #expect(client.bookFileRequest(file("http://192.168.1.20:8383/api/v1/items/b1/book/1"))?.value(forHTTPHeaderField: "X-Api-Key") == "secret")
        #expect(client.bookFileRequest(file("HTTP://192.168.1.20:8383/x")) != nil)

        // Another host, or another port on the same host, gets nothing (and so never the key).
        #expect(client.bookFileRequest(file("https://example.com/book.mp3")) == nil)
        #expect(client.bookFileRequest(file("http://192.168.1.20:9000/book.mp3")) == nil)
        #expect(client.bookFileRequest(file("http://192.168.1.20/book.mp3")) == nil)
        #expect(client.bookFileRequest(file("http://192.168.1.20.evil.com:8383/book.mp3")) == nil)

        // A scheme's own port matches the address written without one.
        let https = FlexarrClient(baseURL: URL(string: "https://flexarr.example.com")!, apiKey: "secret")
        #expect(https.bookFileRequest(file("https://flexarr.example.com:443/a.mp3")) != nil)
        #expect(https.bookFileRequest(file("http://flexarr.example.com/a.mp3")) == nil)
    }
}
