import Foundation
import Testing
@testable import FlexarrKit

/// Serves canned responses per host so tests can run in parallel.
final class StubProtocol: URLProtocol, @unchecked Sendable {
    struct Stub: Sendable {
        var status: Int
        var body: Data
        var contentType: String = "application/json"
    }

    nonisolated(unsafe) private static var stubs: [String: Stub] = [:]
    nonisolated(unsafe) private static var requests: [String: [URLRequest]] = [:]
    private static let lock = NSLock()

    static func register(host: String, _ stub: Stub) {
        lock.withLock { stubs[host] = stub }
    }

    static func recorded(host: String) -> [URLRequest] {
        lock.withLock { requests[host] ?? [] }
    }

    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override func startLoading() {
        let host = request.url?.host ?? ""
        var captured = request
        if let stream = request.httpBodyStream {
            stream.open()
            var data = Data()
            let buffer = UnsafeMutablePointer<UInt8>.allocate(capacity: 4096)
            while stream.hasBytesAvailable {
                let read = stream.read(buffer, maxLength: 4096)
                if read <= 0 { break }
                data.append(buffer, count: read)
            }
            buffer.deallocate()
            stream.close()
            captured.httpBody = data
        }
        let stub = Self.lock.withLock { () -> Stub? in
            Self.requests[host, default: []].append(captured)
            return Self.stubs[host]
        }
        guard let stub else {
            client?.urlProtocol(self, didFailWithError: URLError(.cannotConnectToHost))
            return
        }
        let response = HTTPURLResponse(url: request.url!, statusCode: stub.status, httpVersion: "HTTP/1.1",
                                       headerFields: ["Content-Type": stub.contentType])!
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: stub.body)
        client?.urlProtocolDidFinishLoading(self)
    }

    override func stopLoading() {}
}

func stubbedClient(_ stub: StubProtocol.Stub, apiKey: String? = "secret") -> (FlexarrClient, String) {
    let host = "test-\(UUID().uuidString.lowercased()).local"
    StubProtocol.register(host: host, stub)
    let configuration = URLSessionConfiguration.ephemeral
    configuration.protocolClasses = [StubProtocol.self]
    let client = FlexarrClient(baseURL: URL(string: "http://\(host):8383")!, apiKey: apiKey,
                               session: URLSession(configuration: configuration))
    return (client, host)
}

/// A client with a home address and one for away from home. Hosts with no stub can't be connected to (the stub
/// protocol fails them with `cannotConnectToHost`), which stands in for being away from home or Tailscale being off.
private func pairedClient(home: StubProtocol.Stub?, remote: StubProtocol.Stub?) -> (client: FlexarrClient, home: String, remote: String) {
    let homeHost = "home-\(UUID().uuidString.lowercased()).local"
    let remoteHost = "remote-\(UUID().uuidString.lowercased()).ts.net"
    if let home { StubProtocol.register(host: homeHost, home) }
    if let remote { StubProtocol.register(host: remoteHost, remote) }
    let configuration = URLSessionConfiguration.ephemeral
    configuration.protocolClasses = [StubProtocol.self]
    let addresses = ServerAddresses(home: URL(string: "http://\(homeHost):8383")!, remote: URL(string: "https://\(remoteHost):8443")!)
    let client = FlexarrClient(addresses: addresses, apiKey: "secret", session: URLSession(configuration: configuration))
    return (client, homeHost, remoteHost)
}

private let statusJSON = Data("""
{"appName":"Flexarr","version":"0.21.0","osDescription":"macOS","runtimeVersion":".NET 10","startTime":"2026-10-25T12:00:00Z","remoteUrl":"https://mini.tail1234.ts.net:8443"}
""".utf8)

private final class Counter: @unchecked Sendable {
    private let lock = NSLock()
    private var count = 0
    var value: Int { lock.withLock { count } }
    func add() { lock.withLock { count += 1 } }
}

struct FlexarrClientTests {
    @Test func sendsApiKeyHeaderAndDecodesStatus() async throws {
        let (client, host) = stubbedClient(.init(status: 200, body: try Fixture.data("system-status")))

        let status = try await client.systemStatus()

        #expect(status.appName == "Flexarr")
        let request = try #require(StubProtocol.recorded(host: host).first)
        #expect(request.value(forHTTPHeaderField: "X-Api-Key") == "secret")
        #expect(request.url?.path == "/api/v1/system/status")
    }

    @Test func pairingStartsAndPicksUpTheApprovedKey() async throws {
        let start = #"{"id":"0123456789abcdef0123456789abcdef","code":"K7M-Q4X","expiresAt":"2026-10-24T08:00:00Z"}"#
        let (client, host) = stubbedClient(.init(status: 200, body: Data(start.utf8)))

        let pairing = try await client.startPairing(deviceName: "Living room")

        #expect(pairing.code == "K7M-Q4X")
        let request = try #require(StubProtocol.recorded(host: host).first)
        #expect(request.httpMethod == "POST")
        #expect(request.url?.path == "/api/v1/pairing")

        let (polling, _) = stubbedClient(.init(status: 200, body: Data(#"{"status":"approved","apiKey":"the-key"}"#.utf8)))
        #expect(try await polling.pairingStatus(pairing.id) == PairingStatus(status: "approved", apiKey: "the-key"))

        let (turnedAway, _) = stubbedClient(.init(status: 200, body: Data(#"{"status":"expired","apiKey":null,"declined":true}"#.utf8)))
        #expect(try await turnedAway.pairingStatus(pairing.id).declined == true)
    }

    @Test func swapsTheApiKeyForADeviceToken() async throws {
        let (client, host) = stubbedClient(.init(status: 200, body: Data(#"{"id":"ab12","token":"device-token"}"#.utf8)))

        let token = try await client.deviceToken(deviceName: "Living room")

        #expect(token == "device-token")
        let request = try #require(StubProtocol.recorded(host: host).first)
        #expect(request.httpMethod == "POST")
        #expect(request.url?.path == "/api/v1/session/device")
        #expect(request.value(forHTTPHeaderField: "X-Api-Key") == "secret")

        let (me, _) = stubbedClient(.init(status: 200, body: Data(#"{"profileId":"owner","method":"api-key","zone":"Local","isAdmin":true,"tailscaleLogin":null}"#.utf8)))
        #expect(try await me.me() == SignInInfo(profileId: "owner", method: "api-key", isAdmin: true))
    }

    @Test func selectingAProfileSendsThePinAndReturnsItsToken() async throws {
        let body = ##"{"token":"kid.abc123","profile":{"id":"kid","name":"Sam","color":"#e8a87c","hasPin":true,"isMain":false,"isCurrent":true,"hiddenLibraryIds":[],"image":null}}"##
        let (client, host) = stubbedClient(.init(status: 200, body: Data(body.utf8)))

        let selection = try await client.selectProfile("kid", pin: "1234")

        #expect(selection.token == "kid.abc123")
        #expect(selection.profile.initial == "S")
        let request = try #require(StubProtocol.recorded(host: host).first)
        #expect(request.httpMethod == "POST")
        #expect(request.url?.path == "/api/v1/profiles/kid/select")
        #expect(String(data: request.httpBody ?? Data(), encoding: .utf8) == #"{"pin":"1234"}"#)
    }

    @Test func recentSearchesAreReadRecordedAndForgotten() async throws {
        let search = try #require(JSONSerialization.jsonObject(with: Fixture.data("search")) as? [String: Any])
        let recent = try JSONSerialization.data(withJSONObject: ["queries": ["fargo", "Coen"], "items": search["titles"] ?? []])
        let (client, host) = stubbedClient(.init(status: 200, body: recent))

        let found = try await client.recentSearches()

        #expect(found.queries == ["fargo", "Coen"])
        #expect(!found.items.isEmpty)
        #expect(StubProtocol.recorded(host: host).first?.url?.path == "/api/v1/search/recent")

        let (recorder, recorderHost) = stubbedClient(.init(status: 204, body: Data()))
        try await recorder.recordSearch(query: "Coen", itemId: nil)
        try await recorder.forgetRecentItem(itemId: "abc")
        try await recorder.clearRecentSearches()
        let sent = StubProtocol.recorded(host: recorderHost)
        #expect(sent.map(\.httpMethod) == ["POST", "DELETE", "DELETE"])
        // A person or genre pick sends only the words.
        #expect(String(data: sent[0].httpBody ?? Data(), encoding: .utf8) == #"{"query":"Coen"}"#)
        #expect(sent[1].url?.query == "itemId=abc")
        #expect(sent[2].url?.query == nil)
    }

    @Test func serversWithoutRecentSearchesHaveNone() async throws {
        let (client, _) = stubbedClient(.init(status: 404, body: Data()))

        #expect(try await client.recentSearches().isEmpty)
    }

    @Test func wrongPinIsAForbiddenWithTheServersMessage() async {
        let (client, _) = stubbedClient(.init(status: 403, body: Data(#"{"title":"That PIN isn't right.","status":403}"#.utf8)))

        await #expect(throws: FlexarrClientError.http(status: 403, message: "That PIN isn't right.")) {
            try await client.selectProfile("kid", pin: "0000")
        }
    }

    @Test func leavingAKidsProfileAsksForTheMainProfilesPin() async throws {
        let wanted = #"{"title":"Enter the admin PIN.","status":403,"parentPin":true}"#
        let (client, host) = stubbedClient(.init(status: 403, body: Data(wanted.utf8)))

        await #expect(throws: FlexarrClientError.parentPinNeeded(message: "Enter the admin PIN.")) {
            try await client.selectProfile("mum", parentPin: "2580")
        }
        let request = try #require(StubProtocol.recorded(host: host).first)
        #expect(String(data: request.httpBody ?? Data(), encoding: .utf8) == #"{"parentPin":"2580"}"#)

        let (locked, _) = stubbedClient(.init(status: 403, body: Data(#"{"title":"This device is locked to kids profiles.","status":403,"kidsLock":true}"#.utf8)))
        await #expect(throws: FlexarrClientError.kidsLocked(message: "This device is locked to kids profiles.")) {
            try await locked.selectProfile("mum")
        }
    }

    @Test func aWrongOwnPinAfterTheRightParentPinSaysWhich() async {
        let wrong = #"{"title":"That PIN isn't right.","status":403,"ownPin":true}"#
        let (client, _) = stubbedClient(.init(status: 403, body: Data(wrong.utf8)))

        await #expect(throws: FlexarrClientError.ownPinWrong(message: "That PIN isn't right.")) {
            try await client.selectProfile("mum", pin: "0000", parentPin: "2580")
        }
        #expect(FlexarrClientError.ownPinWrong(message: nil).errorDescription == "That PIN isn’t right.")
    }

    @Test func aRemovedGuestIsToldApartFromOtherRefusals() async {
        let removed = #"{"title":"This device was removed from the server.","status":403,"guestRemoved":true}"#
        let (client, _) = stubbedClient(.init(status: 403, body: Data(removed.utf8)))
        await #expect(throws: FlexarrClientError.guestRemoved(message: "This device was removed from the server.")) {
            try await client.me()
        }

        let (other, _) = stubbedClient(.init(status: 403, body: Data(#"{"title":"Not allowed.","status":403}"#.utf8)))
        await #expect(throws: FlexarrClientError.http(status: 403, message: "Not allowed.")) {
            try await other.me()
        }
    }

    @Test func locksToKidsAndReadsTheLimits() async throws {
        let body = ##"{"token":"ada.sig","profile":{"id":"ada","name":"Ada","color":"#e8a0bf","hasPin":false,"isMain":false,"isCurrent":true,"hiddenLibraryIds":[],"image":null,"maxRatingAge":8,"isKids":true}}"##
        let (client, host) = stubbedClient(.init(status: 200, body: Data(body.utf8)))

        let selection = try await client.lockToKids(pin: "2580", profileId: "ada")

        #expect(selection.profile.isKidsProfile && selection.profile.hasAgeLimit)
        let request = try #require(StubProtocol.recorded(host: host).first)
        #expect(request.url?.path == "/api/v1/profiles/kids-lock")

        let (me, _) = stubbedClient(.init(status: 200, body: Data(#"{"profileId":"ada","method":"device","isAdmin":false,"isKids":true,"maxRatingAge":8,"kidsLock":true}"#.utf8)))
        #expect(try await me.me().kidsLock == true)
        // Older servers say nothing of either.
        let old = Profile(id: "p", name: "P", color: "#fff")
        #expect(!old.isKidsProfile && !old.hasAgeLimit)
    }

    @Test func sendsTheChosenProfilesToken() async throws {
        let host = "test-\(UUID().uuidString.lowercased()).local"
        StubProtocol.register(host: host, .init(status: 200, body: Data("[]".utf8)))
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [StubProtocol.self]
        let client = FlexarrClient(baseURL: URL(string: "http://\(host):8383")!, apiKey: "secret", profileToken: "kid.abc123",
                                   session: URLSession(configuration: configuration))

        #expect(try await client.profiles().isEmpty)
        #expect(StubProtocol.recorded(host: host).first?.value(forHTTPHeaderField: "X-Flexarr-Profile") == "kid.abc123")
    }

    @Test func unauthorizedMapsToFriendlyError() async {
        let (client, _) = stubbedClient(.init(status: 401, body: Data()))

        await #expect(throws: FlexarrClientError.unauthorized) { try await client.home() }
        // Apple TVs connect with a code now, so that comes first.
        #expect(FlexarrClientError.unauthorized.errorDescription?.contains("Connect it again with a code") == true)
    }

    @Test func unauthorizedIsReportedToTheApp() async {
        let host = "test-\(UUID().uuidString.lowercased()).local"
        StubProtocol.register(host: host, .init(status: 401, body: Data()))
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [StubProtocol.self]
        let count = Counter()
        let client = FlexarrClient(baseURL: URL(string: "http://\(host):8383")!, apiKey: "revoked",
                                   session: URLSession(configuration: configuration), onUnauthorized: { count.add() })

        await #expect(throws: FlexarrClientError.unauthorized) { try await client.home() }
        #expect(count.value == 1)
    }

    @Test func otherFailuresAreNotReportedAsSignedOut() async {
        let host = "test-\(UUID().uuidString.lowercased()).local"
        StubProtocol.register(host: host, .init(status: 503, body: Data()))
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [StubProtocol.self]
        let count = Counter()
        let client = FlexarrClient(baseURL: URL(string: "http://\(host):8383")!, apiKey: "secret",
                                   session: URLSession(configuration: configuration), onUnauthorized: { count.add() })

        await #expect(throws: FlexarrClientError.self) { try await client.home() }
        #expect(count.value == 0)
    }

    @Test func htmlFallbackIsNotAFlexarrServer() async {
        // Unknown paths fall through to the web app's index.html with a 200.
        let (client, _) = stubbedClient(.init(status: 200, body: Data("<!doctype html>".utf8), contentType: "text/html"))

        await #expect(throws: FlexarrClientError.unexpectedResponse) { try await client.systemStatus() }
    }

    @Test func aServerWithoutAWatchlistAnswersNone() async throws {
        // Servers before 0.56 answer /watchlist/keys with the web app's page.
        let (old, _) = stubbedClient(.init(status: 200, body: Data("<!doctype html>".utf8), contentType: "text/html"))
        #expect(try await old.watchlistKeys() == nil)
        let (current, _) = stubbedClient(.init(status: 200, body: Data(#"{"libraryItemIds":["a"],"titles":["movie:1"]}"#.utf8)))
        #expect(try await current.watchlistKeys()?.libraryItemIds == ["a"])
    }

    @Test func readyFromTheWatchlistIsNoneOnAServerWithoutIt() async throws {
        let (old, _) = stubbedClient(.init(status: 200, body: Data("<!doctype html>".utf8), contentType: "text/html"))
        #expect(try await old.watchlistReady() == nil)
        let (current, _) = stubbedClient(.init(status: 200, body: Data("[]".utf8)))
        #expect(try await current.watchlistReady()?.isEmpty == true)
    }

    @Test func problemDetailsBecomeTheErrorMessage() async {
        let body = Data(#"{"title":"Not found","status":404,"detail":"No such item."}"#.utf8)
        let (client, _) = stubbedClient(.init(status: 404, body: body))

        await #expect(throws: FlexarrClientError.http(status: 404, message: "No such item.")) { try await client.item("x") }
    }

    @Test func nextUpReturnsNilOnNoContent() async throws {
        let (client, _) = stubbedClient(.init(status: 204, body: Data()))

        let next = try await client.nextUp("series")

        #expect(next == nil)
    }

    @Test func nextUpDecodesEpisode() async throws {
        let (client, host) = stubbedClient(.init(status: 200, body: try Fixture.data("next-up")))

        let next = try #require(try await client.nextUp("01a16beb-0034-7d4e-8037-c249052ffa37"))

        #expect(next.kind == .episode)
        #expect(next.episodeCode == "S1 E1")
        #expect(StubProtocol.recorded(host: host).first?.url?.path == "/api/v1/items/01a16beb-0034-7d4e-8037-c249052ffa37/next-up")
    }

    @Test func libraryItemsBuildsQuery() async throws {
        let (client, host) = stubbedClient(.init(status: 200, body: try Fixture.data("library-items")))

        let page = try await client.libraryItems("lib", sort: .added, skip: 60, take: 30)

        #expect(page.total > 0)
        let url = try #require(StubProtocol.recorded(host: host).first?.url)
        let query = URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems ?? []
        #expect(query.contains(.init(name: "sort", value: "added")))
        #expect(query.contains(.init(name: "skip", value: "60")))
        #expect(query.contains(.init(name: "take", value: "30")))
    }

    @Test func startPlaybackPostsTvOSProfile() async throws {
        let (client, host) = stubbedClient(.init(status: 200, body: try Fixture.data("playback")))

        let info = try await client.startPlayback(PlaybackRequest(itemId: "ep", profile: .tvOS(hdr: true)))

        #expect(info.method == .transcode)
        let request = try #require(StubProtocol.recorded(host: host).first)
        #expect(request.httpMethod == "POST")
        #expect(request.value(forHTTPHeaderField: "Content-Type") == "application/json")
        let body = try #require(request.httpBody)
        let json = try #require(try JSONSerialization.jsonObject(with: body) as? [String: Any])
        #expect(json["itemId"] as? String == "ep")
        #expect(json["startPositionSeconds"] == nil, "omitted so the server picks the resume point")
        let profile = try #require(json["profile"] as? [String: Any])
        #expect(profile["client"] as? String == "tvos")
        #expect(profile["hdr"] as? Bool == true)
        #expect(profile["containers"] as? [String] == ["mp4"])
        #expect(profile["videoCodecs"] as? [String] == ["h264", "hevc"])
        // Fragmented MP4: Dolby Digital Plus with its Atmos, and lossless FLAC up to 7.1.
        #expect(profile["audioCodecs"] as? [String] == ["aac", "ac3", "eac3", "mp3", "flac", "alac"])
        #expect(profile["maxHeight"] as? Int == 2160)
        #expect(profile["maxAudioChannels"] as? Int == 8)
        #expect(profile["maxBitrate"] as? Int == 0)
        #expect(profile["fmp4"] as? Bool == true)
    }

    @Test func progressAndStopHitSessionEndpoints() async throws {
        let (client, host) = stubbedClient(.init(status: 204, body: Data()))

        try await client.reportProgress(sessionId: "abc", positionSeconds: 61.5, paused: true)
        try await client.stopPlayback(sessionId: "abc")

        let requests = StubProtocol.recorded(host: host)
        #expect(requests.map(\.httpMethod) == ["POST", "DELETE"])
        #expect(requests.map(\.url?.path) == ["/api/v1/playback/abc/progress", "/api/v1/playback/abc"])
        let json = try #require(try JSONSerialization.jsonObject(with: requests[0].httpBody ?? Data()) as? [String: Any])
        #expect(json["positionSeconds"] as? Double == 61.5)
        #expect(json["paused"] as? Bool == true)
        #expect(json["ended"] as? Bool == false)
    }

    @Test func setWatchedPostsFlag() async throws {
        let (client, host) = stubbedClient(.init(status: 204, body: Data()))

        try await client.setWatched("item", watched: true)

        let request = try #require(StubProtocol.recorded(host: host).first)
        #expect(request.url?.path == "/api/v1/items/item/watched")
        #expect(String(data: request.httpBody ?? Data(), encoding: .utf8) == #"{"watched":true}"#)
    }

    @Test func omitsApiKeyHeaderWhenEmpty() async throws {
        let (client, host) = stubbedClient(.init(status: 200, body: try Fixture.data("libraries")), apiKey: nil)

        _ = try await client.libraries()

        #expect(StubProtocol.recorded(host: host).first?.value(forHTTPHeaderField: "X-Api-Key") == nil)
    }

    @Test func resolvesRelativeAndImageURLs() {
        let client = FlexarrClient(baseURL: URL(string: "http://mini.local:8383")!)

        #expect(client.url(for: "/api/v1/playback/s/master.m3u8")?.absoluteString == "http://mini.local:8383/api/v1/playback/s/master.m3u8")
        #expect(client.imageURL("/api/v1/images/items/x/poster?v=1", width: 342)?.absoluteString
            == "http://mini.local:8383/api/v1/images/items/x/poster?v=1&w=342")
        #expect(client.imageURL("/api/v1/images/people/p", width: 300)?.absoluteString
            == "http://mini.local:8383/api/v1/images/people/p?w=300")
        #expect(client.imageURL(nil, width: 300) == nil)
        #expect(client.wideArtworkURL(Artwork(backdrop: "/api/v1/images/items/x/backdrop?v=7"), width: 1280)?.absoluteString
            == "http://mini.local:8383/api/v1/images/items/x/wide?v=7&w=1280")
        #expect(client.wideArtworkURL(Artwork(poster: "/api/v1/images/items/x/poster?v=7"), width: 1280) == nil)
    }

    @Test(arguments: [
        ("mini.local", "http://mini.local:8383"),
        ("192.168.1.20:9000", "http://192.168.1.20:9000"),
        ("http://media-server.local:8383/", "http://media-server.local:8383"),
        ("https://media.example.com", "https://media.example.com"),
        ("  mini.local  ", "http://mini.local:8383"),
    ])
    func normalizesAddresses(input: String, expected: String) {
        #expect(FlexarrClient.normalizeAddress(input)?.absoluteString == expected)
    }

    @Test(arguments: ["", "not a url", "ftp://host", "http://"])
    func rejectsBadAddresses(input: String) {
        #expect(FlexarrClient.normalizeAddress(input) == nil)
    }
}

struct SubtitleChoiceTests {
    @Test func tellsTheServerWhatTheViewerPicked_OffIncluded() async throws {
        let (client, host) = stubbedClient(.init(status: 204, body: Data()))

        try await client.setSubtitle(sessionId: "s1", key: nil)
        try await client.setSubtitle(sessionId: "s1", key: "e2")

        let requests = StubProtocol.recorded(host: host)
        #expect(requests.count == 2)
        #expect(requests.first?.httpMethod == "PUT")
        #expect(requests.first?.url?.path == "/api/v1/playback/s1/subtitle")
        #expect(String(data: requests.first?.httpBody ?? Data(), encoding: .utf8) == #"{"key":"off"}"#)
        #expect(String(data: requests.last?.httpBody ?? Data(), encoding: .utf8) == #"{"key":"e2"}"#)
    }
}

struct PlayerControlsTests {
    @Test func movesAFilesSound() async throws {
        let (client, host) = stubbedClient(.init(status: 204, body: Data()))

        try await client.setAudioDelay(itemId: "ep", seconds: -0.15)

        let request = try #require(StubProtocol.recorded(host: host).first)
        #expect(request.httpMethod == "PUT")
        #expect(request.url?.path == "/api/v1/items/ep/audio-delay")
        let json = try #require(try JSONSerialization.jsonObject(with: request.httpBody ?? Data()) as? [String: Any])
        #expect(json["seconds"] as? Double == -0.15)
        #expect(json["restOfSeason"] == nil)
    }

    @Test func remembersAShowsSpeed() async throws {
        let (client, host) = stubbedClient(.init(status: 204, body: Data()))

        try await client.setPlaybackSpeed(itemId: "ep", speed: 1.25)

        let request = try #require(StubProtocol.recorded(host: host).first)
        #expect(request.httpMethod == "PUT")
        #expect(request.url?.path == "/api/v1/items/ep/playback-speed")
        #expect(String(data: request.httpBody ?? Data(), encoding: .utf8) == #"{"speed":1.25}"#)
    }

    @Test func theEngineSaysItMovesTheSoundItself_AVPlayerDoesNot() throws {
        let engine = try JSONSerialization.jsonObject(with: JSONEncoder().encode(
            DeviceProfile.engine(client: "tvos", hdr: true, dolbyVision: false, hdr10: true, hlg: true))) as? [String: Any]
        #expect(engine?["delaysAudio"] as? Bool == true)
        #expect(engine?["advancesAudio"] as? Bool == true)
        let avPlayer = try JSONSerialization.jsonObject(with: JSONEncoder().encode(DeviceProfile.tvOS(hdr: true))) as? [String: Any]
        #expect(avPlayer?["delaysAudio"] == nil)
        #expect(avPlayer?["advancesAudio"] == nil)
    }

    @Test func readsTheOffsetAndSpeed_andGoesWithoutOnOlderServers() throws {
        let old = try Fixture.decode(PlaybackInfo.self, "playback")
        #expect(old.audioDelaySeconds == nil)
        #expect(!old.hasPlayerControls)

        var json = try #require(try JSONSerialization.jsonObject(with: Fixture.data("playback")) as? [String: Any])
        json["audioDelaySeconds"] = 0.2
        json["audioDelayInStream"] = true
        json["speed"] = 1.5
        let info = try FlexarrClient.decoder.decode(PlaybackInfo.self, from: JSONSerialization.data(withJSONObject: json))
        #expect(info.audioDelaySeconds == 0.2)
        #expect(info.audioDelayInStream == true)
        #expect(info.speed == 1.5)
        #expect(info.hasPlayerControls)
    }
}

struct CommandStreamTests {
    @Test func commandStream_yieldsEachCommandEvent_andSkipsCommentsAndCRLF() async throws {
        let body = ": connected\n\nevent: command\r\ndata: {\"id\":\"a\",\"kind\":\"Play\",\"itemId\":\"x\",\"fromBeginning\":false,\"sentBy\":null,\"sentAt\":\"2026-10-25T10:00:00Z\"}\r\n\r\n"
            + ": ping\n\nevent: command\ndata: {\"id\":\"b\",\"kind\":\"Pause\",\"itemId\":null,\"fromBeginning\":false,\"sentBy\":\"Dan's iPhone\",\"sentAt\":\"2026-10-25T10:00:05Z\"}\n\n"
        let (client, host) = stubbedClient(.init(status: 200, body: Data(body.utf8), contentType: "text/event-stream"))
        var kinds: [DeviceCommandKind] = []
        for try await command in client.commandStream() { kinds.append(command.kind) }
        #expect(kinds == [.play, .pause])
        let request = try #require(StubProtocol.recorded(host: host).first)
        #expect(request.url?.path == "/api/v1/devices/commands")
        #expect(request.value(forHTTPHeaderField: "X-Api-Key") == "secret")
    }

    @Test func commandStream_refusedWithTheApiKey_throws403() async {
        let (client, _) = stubbedClient(.init(status: 403, body: Data("{\"detail\":\"Only a device\"}".utf8)))
        do {
            for try await _ in client.commandStream() {}
            Issue.record("expected a 403")
        } catch {
            #expect(error as? FlexarrClientError == .http(status: 403, message: nil))
        }
    }
}

struct PairingPromptTests {
    @Test func deviceEvents_carryCommandsAndPairingPrompts_andSkipUnknownOnes() async throws {
        let body = "event: pairing\ndata: {\"code\":\"ABC-DEF\",\"deviceName\":\"Living room\",\"expiresAt\":\"2026-10-29T12:00:00Z\",\"requestedFrom\":\"192.168.1.70\",\"zone\":\"Local\"}\n\n"
            + "event: something-new\ndata: {\"x\":1}\n\n"
            + "event: command\ndata: {\"id\":\"b\",\"kind\":\"Pause\",\"itemId\":null,\"fromBeginning\":false,\"sentBy\":null,\"sentAt\":\"2026-10-25T10:00:05Z\"}\n\n"
            + "event: pairing\ndata: {\"code\":\"GHJ-KLM\",\"deviceName\":\"Bedroom\",\"expiresAt\":\"2026-10-29T12:00:00Z\"}\n\n"
            + "event: pairing-ended\ndata: {\"code\":\"ABC-DEF\"}\n\n"
        let (client, _) = stubbedClient(.init(status: 200, body: Data(body.utf8), contentType: "text/event-stream"))
        var events: [DeviceEvent] = []
        for try await event in client.deviceEvents() { events.append(event) }
        #expect(events.count == 4)
        guard case .pairing(let first) = events[0] else { Issue.record("expected a pairing prompt first"); return }
        #expect(first.code == "ABC-DEF")
        #expect(first.deviceName == "Living room")
        #expect(first.requestedFrom == "192.168.1.70")
        #expect(first.zone == "Local")
        guard case .command(let command) = events[1] else { Issue.record("expected the command next"); return }
        #expect(command.kind == .pause)
        guard case .pairing(let second) = events[2] else { Issue.record("expected another prompt"); return }
        #expect(second.requestedFrom == nil)
        #expect(second.zone == nil)
        #expect(events[3] == .pairingEnded(code: "ABC-DEF"))

        // The commands alone, as before.
        let (commands, _) = stubbedClient(.init(status: 200, body: Data(body.utf8), contentType: "text/event-stream"))
        var kinds: [DeviceCommandKind] = []
        for try await command in commands.commandStream() { kinds.append(command.kind) }
        #expect(kinds == [.pause])
    }

    @Test func listsTheDevicesWaiting() async throws {
        let list = #"[{"code":"ABC-DEF","deviceName":"Living room","expiresAt":"2026-10-29T12:00:00Z","requestedFrom":null,"zone":"Tailscale"}]"#
        let (client, host) = stubbedClient(.init(status: 200, body: Data(list.utf8)))
        let waiting = try await client.waitingPairings()
        #expect(waiting.map(\.code) == ["ABC-DEF"])
        #expect(waiting.first?.zone == "Tailscale")
        #expect(StubProtocol.recorded(host: host).first?.url?.path == "/api/v1/pairing/waiting")
    }

    @Test func serversBeforeThePromptHaveNobodyWaiting() async throws {
        let (client, _) = stubbedClient(.init(status: 404, body: Data()))
        #expect(try await client.waitingPairings().isEmpty)
        let (kept, _) = stubbedClient(.init(status: 404, body: Data()))
        #expect(try await kept.waitingPairingsIfKept() == nil)
    }

    @Test func saysWhatKindOfDeviceIsWaiting_whenTheServerDoes() async throws {
        let list = #"""
        [{"code":"ABC-DEF","deviceName":"Living room","expiresAt":"2026-10-29T12:00:00Z","zone":"Local","deviceType":"AppleTv"},
         {"code":"GHJ-KLM","deviceName":"Kitchen","expiresAt":"2026-10-29T12:00:00Z","deviceType":"AndroidTv"},
         {"code":"NPQ-RST","deviceName":"Bedroom","expiresAt":"2026-10-29T12:00:00Z","deviceType":"Toaster"},
         {"code":"VWX-YZ2","deviceName":"Den","expiresAt":"2026-10-29T12:00:00Z","deviceType":null,"turnedAway":true},
         {"code":"345-678","deviceName":"Study","expiresAt":"2026-10-29T12:00:00Z"}]
        """#
        let (client, _) = stubbedClient(.init(status: 200, body: Data(list.utf8)))
        let waiting = try await client.waitingPairings()
        #expect(waiting.map(\.deviceType) == [.appleTv, .androidTv, .unknown, nil, nil])
        // Only the one turned away a moment ago (asking again by itself) isn't asked about.
        #expect(waiting.map(\.asks) == [true, true, true, false, true])
    }

    @Test func approvesAndDeclinesByCode() async throws {
        let (client, host) = stubbedClient(.init(status: 204, body: Data()))
        try await client.approvePairing(code: "ABC-DEF")
        try await client.declinePairing(code: "ABC-DEF")
        let requests = StubProtocol.recorded(host: host)
        #expect(requests.map(\.httpMethod) == ["POST", "POST"])
        #expect(requests.map(\.url?.path) == ["/api/v1/pairing/code/ABC-DEF/approve", "/api/v1/pairing/code/ABC-DEF/decline"])
        #expect(requests.first?.httpBody == Data("{}".utf8))
    }

    @Test func meSaysWhetherTheMainProfileIsLocked_andOlderServersDont() async throws {
        let body = #"{"profileId":"owner","method":"device","zone":"Local","isAdmin":false,"tailscaleLogin":null,"canUnlock":true,"adminPinMissing":false,"unlockedUntil":null}"#
        let (client, _) = stubbedClient(.init(status: 200, body: Data(body.utf8)))
        let me = try await client.me()
        #expect(me.canUnlock == true)
        #expect(me.adminPinMissing == false)
        #expect(me.unlockedUntil == nil)

        let unlocked = #"{"profileId":"owner","method":"device","zone":"Local","isAdmin":true,"tailscaleLogin":null,"canUnlock":false,"adminPinMissing":false,"unlockedUntil":"2026-10-29T12:15:00Z"}"#
        let (newer, _) = stubbedClient(.init(status: 200, body: Data(unlocked.utf8)))
        #expect(try await newer.me().unlockedUntil == Date(timeIntervalSince1970: 1_793_276_100))

        let (older, _) = stubbedClient(.init(status: 200, body: Data(#"{"profileId":"owner","method":"device","zone":"Local","isAdmin":true,"tailscaleLogin":null}"#.utf8)))
        let old = try await older.me()
        #expect(old.canUnlock == nil)
        #expect(old.adminPinMissing == nil)
    }

    @Test func unlockingSendsTheMainProfilesPin() async throws {
        let (client, host) = stubbedClient(.init(status: 200, body: Data(#"{"unlockedUntil":"2026-10-29T12:15:00Z","token":null}"#.utf8)))
        let until = try await client.unlockAdmin(pin: "1234")
        #expect(until == Date(timeIntervalSince1970: 1_793_276_100))
        let request = try #require(StubProtocol.recorded(host: host).first)
        #expect(request.httpMethod == "POST")
        #expect(request.url?.path == "/api/v1/admin/unlock")
        #expect(String(data: request.httpBody ?? Data(), encoding: .utf8) == #"{"pin":"1234"}"#)
    }

    @Test func unlockingRefusalsCarryTheServersMessage() async {
        let (wrong, _) = stubbedClient(.init(status: 403, body: Data(#"{"title":"Forbidden","detail":"That PIN isn't right.","status":403}"#.utf8)))
        await #expect(throws: FlexarrClientError.http(status: 403, message: "That PIN isn't right.")) {
            try await wrong.unlockAdmin(pin: "0000")
        }
        let tooMany = #"{"title":"Too many wrong PINs. Try again in 30 seconds.","status":429,"retryAfterSeconds":30}"#
        let (locked, _) = stubbedClient(.init(status: 429, body: Data(tooMany.utf8)))
        await #expect(throws: FlexarrClientError.http(status: 429, message: "Too many wrong PINs. Try again in 30 seconds.")) {
            try await locked.unlockAdmin(pin: "0000")
        }
        let (missing, _) = stubbedClient(.init(status: 409, body: Data(#"{"detail":"The main profile needs a PIN.","status":409,"pinMissing":true}"#.utf8)))
        await #expect(throws: FlexarrClientError.http(status: 409, message: "The main profile needs a PIN.")) {
            try await missing.unlockAdmin(pin: "1234")
        }
    }

    @Test func profilesSayWhetherChoosingThemAsksForThePin() throws {
        let decoder = JSONDecoder()
        let newer = ##"{"id":"owner","name":"Dan","color":"#a9c2ec","hasPin":true,"isMain":true,"isCurrent":false,"hiddenLibraryIds":[],"image":null,"asksPin":false}"##
        let main = try decoder.decode(Profile.self, from: Data(newer.utf8))
        #expect(main.needsPinToChoose == false)
        // Older servers don't say: a PIN means it's asked for.
        let older = ##"{"id":"kid","name":"Sam","color":"#e8a87c","hasPin":true,"isMain":false,"isCurrent":false,"hiddenLibraryIds":[],"image":null}"##
        let kid = try decoder.decode(Profile.self, from: Data(older.utf8))
        #expect(kid.asksPin == nil)
        #expect(kid.needsPinToChoose)
    }

    @Test func theAdminPinIsAskedForAsTheServerSays_OrForAMainProfileWithNoPinOfItsOwn() throws {
        let decoder = JSONDecoder()
        let guest = ##"{"id":"owner","name":"Dan","color":"#a9c2ec","hasPin":true,"isMain":true,"isCurrent":false,"hiddenLibraryIds":[],"image":null,"asksPin":true,"asksAdminPin":true}"##
        #expect(try decoder.decode(Profile.self, from: Data(guest.utf8)).asksAdminPinToChoose)
        // Servers before 0.57 don't say: only a main profile without a PIN of its own could mean the admin PIN.
        let older = ##"{"id":"owner","name":"Dan","color":"#a9c2ec","hasPin":true,"isMain":true,"isCurrent":false,"hiddenLibraryIds":[],"image":null}"##
        #expect(try decoder.decode(Profile.self, from: Data(older.utf8)).asksAdminPinToChoose == false)
        let noPin = ##"{"id":"owner","name":"Dan","color":"#a9c2ec","hasPin":false,"isMain":true,"isCurrent":false,"hiddenLibraryIds":[],"image":null}"##
        #expect(try decoder.decode(Profile.self, from: Data(noPin.utf8)).asksAdminPinToChoose)
    }

    @Test func tooManyApprovalsCarryTheServersMessage() async {
        let (client, _) = stubbedClient(.init(status: 429, body: Data(#"{"detail":"Too many devices connected this hour."}"#.utf8)))
        do {
            try await client.approvePairing(code: "ABC-DEF")
            Issue.record("expected a 429")
        } catch {
            #expect(error as? FlexarrClientError == .http(status: 429, message: "Too many devices connected this hour."))
        }
    }
}

struct MusicClientTests {
    @Test func fetchesAnArtistsTracksInOneRequest() async throws {
        let (client, host) = stubbedClient(.init(status: 200, body: try Fixture.data("tracks")))

        let tracks = try await client.tracks(of: "01a172cc-3620-79f8-aff0-b0091da8f986")

        #expect(tracks.count == 3)
        #expect(tracks.first?.kind == .track)
        let request = try #require(StubProtocol.recorded(host: host).first)
        #expect(request.httpMethod == "GET")
        #expect(request.url?.path == "/api/v1/items/01a172cc-3620-79f8-aff0-b0091da8f986/tracks")
    }

    @Test func fetchesATracksLyrics() async throws {
        let (client, host) = stubbedClient(.init(status: 200, body: try Fixture.data("lyrics")))

        let lyrics = try #require(try await client.lyrics(of: "01a172cc-3621-7948-b6de-6f49d8067a8c"))

        #expect(lyrics.synced)
        #expect(lyrics.lines.count == 4)
        #expect(lyrics.lines[1] == LyricLine(time: 15.5, text: "He takes the road as it comes"))
        #expect(StubProtocol.recorded(host: host).first?.url?.path == "/api/v1/items/01a172cc-3621-7948-b6de-6f49d8067a8c/lyrics")
    }

    @Test func aTrackWithoutLyricsHasNone() async throws {
        let (client, _) = stubbedClient(.init(status: 404, body: Data()))

        #expect(try await client.lyrics(of: "01a172cc-3621-7948-b6de-6f49d8067a8c") == nil)
    }
}

struct ServerAddressesTests {
    @Test func startsAtHomeAndOffersTheOtherAddress() {
        let home = URL(string: "http://mini.local:8383")!, remote = URL(string: "https://mini.ts.net:8443")!
        let addresses = ServerAddresses(home: home, remote: remote)
        #expect(addresses.current == home)
        #expect(addresses.alternate(to: home) == remote)
        #expect(addresses.alternate(to: remote) == home)
        #expect(ServerAddresses(home: home, remote: nil).alternate(to: home) == nil)
        // The same address twice is just the one address.
        #expect(ServerAddresses(home: home, remote: home).remote == nil)
    }

    @Test func checksHomeOncePerIntervalWhileAway() {
        let home = URL(string: "http://mini.local:8383")!, remote = URL(string: "https://mini.ts.net:8443")!
        let addresses = ServerAddresses(home: home, remote: remote)
        let start = Date()
        #expect(!addresses.shouldCheckHome(now: start), "at home there's nothing to check")
        addresses.reached(remote)
        #expect(addresses.isAway)
        #expect(!addresses.shouldCheckHome(now: start + 60), "leaving home starts the clock; it just failed")
        #expect(addresses.shouldCheckHome(now: start + ServerAddresses.homeCheckInterval + 1))
        #expect(!addresses.shouldCheckHome(now: start + ServerAddresses.homeCheckInterval + 2), "once per interval")
        addresses.reached(home)
        #expect(!addresses.isAway)
    }

    @Test func sharedInstancesAreOnePerPair() {
        let home = URL(string: "http://shared-\(UUID().uuidString).local:8383")!, remote = URL(string: "https://mini.ts.net:8443")!
        #expect(ServerAddresses.shared(home: home, remote: remote) === ServerAddresses.shared(home: home, remote: remote))
        #expect(ServerAddresses.shared(home: home, remote: remote) !== ServerAddresses.shared(home: home, remote: nil))
    }
}

struct RemoteFallbackTests {
    @Test func readsFallBackToTheRemoteAddressWhenHomeCannotConnect() async throws {
        let (client, home, remote) = pairedClient(home: nil, remote: .init(status: 200, body: statusJSON))

        let status = try await client.systemStatus()

        #expect(status.remoteUrl == URL(string: "https://mini.tail1234.ts.net:8443"))
        #expect(client.baseURL.host() == remote, "the address that answered is used from now on")
        #expect(client.addresses.isAway)
        #expect(StubProtocol.recorded(host: home).count == 1)
        #expect(StubProtocol.recorded(host: remote).count == 1)

        // The next request goes straight to the remote address (the home probe waits for its interval).
        _ = try await client.systemStatus()
        #expect(StubProtocol.recorded(host: home).count == 1)
        #expect(StubProtocol.recorded(host: remote).count == 2)
    }

    @Test func homeIsPreferredWhenItAnswers() async throws {
        let (client, home, remote) = pairedClient(home: .init(status: 200, body: statusJSON), remote: .init(status: 200, body: statusJSON))

        _ = try await client.systemStatus()

        #expect(client.baseURL.host() == home)
        #expect(StubProtocol.recorded(host: home).count == 1)
        #expect(StubProtocol.recorded(host: remote).isEmpty, "home answered before the remote attempt was due")
    }

    @Test func writesTryHomeFirstAndMoveOverOnlyWhenItCannotConnect() async throws {
        let (client, home, remote) = pairedClient(home: nil, remote: .init(status: 204, body: Data()))

        try await client.setWatched("item-1", watched: true)

        #expect(StubProtocol.recorded(host: home).count == 1)
        #expect(StubProtocol.recorded(host: remote).count == 1)
        #expect(StubProtocol.recorded(host: remote).first?.httpMethod == "POST")
        #expect(client.addresses.isAway)
    }

    @Test func anAnswerFromHomeIsNotRetriedRemotely() async throws {
        let (client, _, remote) = pairedClient(home: .init(status: 404, body: Data()), remote: .init(status: 200, body: statusJSON))

        await #expect(throws: FlexarrClientError.http(status: 404, message: nil)) {
            try await client.systemStatus()
        }
        #expect(StubProtocol.recorded(host: remote).isEmpty, "the server answered; the other address wouldn't say anything different")
        #expect(!client.addresses.isAway)
    }

    @Test func neitherAddressAnsweringIsOneError() async throws {
        let (client, home, remote) = pairedClient(home: nil, remote: nil)

        await #expect(throws: FlexarrClientError.unreachable) {
            try await client.systemStatus()
        }
        #expect(StubProtocol.recorded(host: home).count == 1)
        #expect(StubProtocol.recorded(host: remote).count == 1)
    }

    @Test func statusWithoutARemoteAddressStillDecodes() throws {
        let data = Data("""
        {"appName":"Flexarr","version":"0.20.1","osDescription":"macOS","runtimeVersion":".NET 10","startTime":"2026-10-25T12:00:00Z"}
        """.utf8)
        let status = try FlexarrClient.decoder.decode(SystemStatus.self, from: data)
        #expect(status.remoteUrl == nil)
    }
}
