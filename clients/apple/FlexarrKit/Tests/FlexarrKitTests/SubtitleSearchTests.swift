import Foundation
import Testing
@testable import FlexarrKit

struct SubtitleSearchTests {
    @Test func playbackFromAnOlderServerHasNoSubtitleSearch() throws {
        let info = try Fixture.decode(PlaybackInfo.self, "playback")

        #expect(info.canSearchSubtitles == nil)
        #expect(info.subtitles.allSatisfy { $0.isExternal == nil })
    }

    @Test func playbackSaysWhenSubtitlesCanBeSearchedAndWhichAreFiles() throws {
        var json = try #require(try JSONSerialization.jsonObject(with: Fixture.data("playback")) as? [String: Any])
        json["canSearchSubtitles"] = true
        var subtitles = try #require(json["subtitles"] as? [[String: Any]])
        subtitles[0]["isExternal"] = false
        subtitles[1]["isExternal"] = true
        json["subtitles"] = subtitles

        let info = try FlexarrClient.decoder.decode(PlaybackInfo.self, from: JSONSerialization.data(withJSONObject: json))

        #expect(info.canSearchSubtitles == true)
        #expect(info.subtitles.map(\.isExternal) == [false, true, nil])
    }

    @Test func detailFromAnOlderServerHasNoSubtitleSearch() throws {
        let detail = try Fixture.decode(ItemDetail.self, "item-movie")

        #expect(detail.canSearchSubtitles == nil)
    }

    @Test func detailSaysWhenSubtitlesCanBeSearched() throws {
        var json = try #require(try JSONSerialization.jsonObject(with: Fixture.data("item-movie")) as? [String: Any])
        json["canSearchSubtitles"] = true

        let detail = try FlexarrClient.decoder.decode(ItemDetail.self, from: JSONSerialization.data(withJSONObject: json))

        #expect(detail.canSearchSubtitles == true)
        #expect(detail.files.first?.id != nil, "the search is for the first file, the one playback picks")
    }

    @Test func searchAsksForTheFileAndDecodesResultsInOrder() async throws {
        let body = """
        {"results":[
         {"id":"tok1","provider":"opensubtitlescom","language":"en","languageLabel":"English","release":"Show.S01E01.1080p.WEB",
          "hearingImpaired":false,"forced":false,"score":96,"matchesFile":true,"uploader":"someone"},
         {"id":"tok2","provider":"podnapisi","language":"en","languageLabel":"English (SDH)","release":"Show.S01E01.720p.HDTV",
          "hearingImpaired":true,"forced":false,"score":71,"matchesFile":false,"uploader":null}
        ]}
        """
        let (client, host) = stubbedClient(.init(status: 200, body: Data(body.utf8)))

        let results = try await client.searchSubtitles(itemId: "item", fileId: "file")

        #expect(results.map(\.id) == ["tok1", "tok2"])
        #expect(results[0].matchesFile && results[0].score == 96)
        #expect(results[1].hearingImpaired && results[1].uploader == nil)
        let request = try #require(StubProtocol.recorded(host: host).first)
        #expect(request.httpMethod == "GET")
        #expect(request.url?.path == "/api/v1/items/item/subtitles/search")
        #expect(request.url?.query == "fileId=file")
        #expect(request.timeoutInterval >= 120, "a search takes a minute or more")
    }

    @Test func downloadSendsTheResultOrLeavesItOutForTheBestMatch() async throws {
        let (client, host) = stubbedClient(.init(status: 200, body: Data(#"{"key":"x1","label":"English"}"#.utf8)))

        let picked = try await client.downloadSubtitles(itemId: "item", fileId: "file", result: "tok1")
        let best = try await client.downloadSubtitles(itemId: "item", fileId: "file")

        #expect(picked == DownloadedSubtitles(key: "x1", label: "English"))
        #expect(best.key == "x1")
        let requests = StubProtocol.recorded(host: host)
        #expect(requests.map(\.httpMethod) == ["POST", "POST"])
        #expect(requests.map(\.url?.path) == ["/api/v1/items/item/subtitles/download", "/api/v1/items/item/subtitles/download"])
        let first = try #require(try JSONSerialization.jsonObject(with: requests[0].httpBody ?? Data()) as? [String: Any])
        #expect(first["result"] as? String == "tok1")
        #expect(first["fileId"] as? String == "file")
        let second = try #require(try JSONSerialization.jsonObject(with: requests[1].httpBody ?? Data()) as? [String: Any])
        #expect(second["result"] == nil)
        #expect(second["fileId"] as? String == "file")
    }

    @Test func syncStartsAndReportsItsState() async throws {
        let (start, startHost) = stubbedClient(.init(status: 202, body: Data(#"{"state":"running","key":"x0"}"#.utf8)))
        try await start.startSubtitleSync(itemId: "item", fileId: "file", key: "x0")
        let request = try #require(StubProtocol.recorded(host: startHost).first)
        #expect(request.httpMethod == "POST")
        #expect(request.url?.path == "/api/v1/items/item/subtitles/sync")
        let json = try #require(try JSONSerialization.jsonObject(with: request.httpBody ?? Data()) as? [String: Any])
        #expect(json["key"] as? String == "x0")
        #expect(json["fileId"] as? String == "file")

        let (status, statusHost) = stubbedClient(.init(status: 200, body: Data(#"{"state":"failed","key":"x0","error":"Bazarr gave up."}"#.utf8)))
        let state = try await status.subtitleSync(itemId: "item", fileId: "file")
        #expect(state.state == .failed)
        #expect(state.error == "Bazarr gave up.")
        #expect(StubProtocol.recorded(host: statusHost).first?.url?.query == "fileId=file")
    }

    @Test func syncStatesFromNewerServersStillDecode() throws {
        let none = try FlexarrClient.decoder.decode(SubtitleSyncStatus.self, from: Data(#"{"state":"none"}"#.utf8))
        #expect(none.state == .idle)
        #expect(none.key == nil)
        let later = try FlexarrClient.decoder.decode(SubtitleSyncStatus.self, from: Data(#"{"state":"queued"}"#.utf8))
        #expect(later.state == .unknown)
    }

    @Test func aSyncAlreadyRunningIsAConflictWithTheServersWords() async throws {
        let problem = #"{"title":"Conflict","status":409,"detail":"A re-sync of this video is already running."}"#
        let (client, _) = stubbedClient(.init(status: 409, body: Data(problem.utf8)))

        await #expect(throws: FlexarrClientError.http(status: 409, message: "A re-sync of this video is already running.")) {
            try await client.startSubtitleSync(itemId: "item", fileId: "file", key: "x0")
        }
    }
}
