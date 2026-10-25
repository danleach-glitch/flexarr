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

private func stubbedClient(_ stub: StubProtocol.Stub, apiKey: String? = "secret") -> (FlexarrClient, String) {
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

    @Test func wrongPinIsAForbiddenWithTheServersMessage() async {
        let (client, _) = stubbedClient(.init(status: 403, body: Data(#"{"title":"That PIN isn't right.","status":403}"#.utf8)))

        await #expect(throws: FlexarrClientError.http(status: 403, message: "That PIN isn't right.")) {
            try await client.selectProfile("kid", pin: "0000")
        }
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
