import Foundation
import Testing
@testable import FlexarrKit

struct ClientLogTests {
    private func entry(_ message: String, level: ClientLogLevel = .error, at seconds: TimeInterval = 0,
                       properties: [String: String]? = nil) -> ClientLogEntry {
        ClientLogEntry(timestamp: Date(timeIntervalSince1970: 1_800_000_000 + seconds), level: level, category: "playback",
                       message: message, properties: properties)
    }

    // MARK: Redaction

    @Test func redactsKnownSecretsWherever_theyAppear() {
        let redactor = LogRedactor(secrets: ["my-device-token-123"])
        #expect(redactor.redact("signed in with my-device-token-123 just now") == "signed in with <redacted> just now")
    }

    @Test func stripsQueryStringsFromURLsAndPaths() {
        let redactor = LogRedactor()
        #expect(redactor.redact("GET http://mini.local:8383/api/v1/items?api_key=abc&x=1 failed")
                == "GET http://mini.local:8383/api/v1/items failed")
        #expect(redactor.redact("fetching /Videos/1/stream.mp4?static=true&token=zzz") == "fetching /Videos/1/stream.mp4")
        #expect(redactor.redact("https://user:pass@host.ts.net/x#frag") == "https://host.ts.net/x")
        // A question in words is left alone.
        #expect(redactor.redact("Couldn't play? Try again") == "Couldn't play? Try again")
    }

    @Test func redactsNamedSecretsAndHeaders() {
        let redactor = LogRedactor()
        #expect(redactor.redact("X-Api-Key: hunter2") == "X-Api-Key: <redacted>")
        #expect(redactor.redact("Authorization: Bearer abc.def") == "Authorization: <redacted>")
        #expect(redactor.redact(#"{"token":"sekrit","name":"TV"}"#) == #"{"token":"<redacted>","name":"TV"}"#)
        #expect(redactor.redact("pin=1234 ok") == "pin=<redacted> ok")
    }

    @Test func redactsLongHexRuns_soSessionIdsNeverLeave() {
        let redactor = LogRedactor()
        let sid = "0123456789abcdef0123456789abcdef"
        #expect(redactor.redact("/api/v1/playback/\(sid)/master.m3u8") == "/api/v1/playback/<redacted>/master.m3u8")
        let push = String(repeating: "ab", count: 32)
        #expect(redactor.redact("push token \(push)") == "push token <redacted>")
        // Ids with dashes and the short playback id are kept.
        #expect(redactor.redact("item 019a4c1e-0d8a-7a2e-9f1b-2c3d4e5f6a7b, playback 1a2b3c4d")
                == "item 019a4c1e-0d8a-7a2e-9f1b-2c3d4e5f6a7b, playback 1a2b3c4d")
    }

    @Test func redactsAnEntrysMessageErrorAndProperties() {
        let redactor = LogRedactor(maxLength: 20)
        var raw = entry(String(repeating: "x", count: 50), properties: ["url": "/a/b?key=1", "playbackId": "1a2b3c4d"])
        raw.error = "at http://h/x?y=z"
        let clean = redactor.redact(raw)
        #expect(clean.message == String(repeating: "x", count: 20) + "…")
        #expect(clean.error == "at http://h/x")
        #expect(clean.properties == ["url": "/a/b", "playbackId": "1a2b3c4d"])
    }

    // MARK: Encoding

    @Test func encodesAsTheServerWantsIt() throws {
        let data = try JSONEncoder().encode(entry("Boom", level: .warning, properties: ["playbackId": "1a2b3c4d"]))
        let object = try #require(try JSONSerialization.jsonObject(with: data) as? [String: Any])
        #expect(object["level"] as? String == "Warning")
        #expect(object["category"] as? String == "playback")
        #expect((object["timestamp"] as? String)?.hasPrefix("2027-01-15T08:00:00") == true)
        #expect(object["error"] == nil)
        #expect((object["properties"] as? [String: String]) == ["playbackId": "1a2b3c4d"])
        let back = try JSONDecoder().decode(ClientLogEntry.self, from: data)
        #expect(back.message == "Boom")
        #expect(abs(back.timestamp.timeIntervalSince(entry("").timestamp)) < 0.001)
    }

    @Test func aLineIsOneLine() {
        var multi = entry("first\nsecond", properties: ["b": "2", "a": "1"])
        multi.error = "oops"
        let line = multi.line
        #expect(!line.contains("\n"))
        #expect(line.hasSuffix("ERROR [playback] first\\nsecond | error: oops | a=1 | b=2"))
    }

    // MARK: The queue

    @Test func onlyWarningsAndErrorsAreQueued() {
        var queue = ClientLogQueue()
        let queued = [ClientLogLevel.debug, .information, .warning, .error].map { queue.add(entry($0.rawValue, level: $0)) }
        #expect(queued == [false, false, true, true])
        #expect(queue.pending.map(\.message) == ["Warning", "Error"])
    }

    @Test func repeatsWaitingAreCountedOnTheOneQueued() {
        var queue = ClientLogQueue()
        queue.add(entry("Track failed", at: 0))
        queue.add(entry("Track failed", at: 1))
        queue.add(entry("Track failed", at: 2))
        #expect(queue.pending.count == 1)
        #expect(queue.pending[0].properties?["repeats"] == "2")
    }

    @Test func repeatsAfterSendingAreLeftOutForAWhile_thenCounted() {
        var queue = ClientLogQueue()
        queue.add(entry("Track failed", at: 0))
        _ = queue.nextBatch(now: Date(timeIntervalSince1970: 1_800_000_001))
        queue.delivered(at: Date(timeIntervalSince1970: 1_800_000_001))
        let soon = queue.add(entry("Track failed", at: 10))
        #expect(!soon)
        let again = queue.add(entry("Track failed", at: 20))
        #expect(!again)
        #expect(queue.pending.isEmpty)
        let later = queue.add(entry("Track failed", at: 400))
        #expect(later)
        #expect(queue.pending.first?.properties?["repeats"] == "2")
    }

    @Test func batchesAreAtMostAHundred_andRateCapped() {
        var queue = ClientLogQueue()
        queue.rateLimit = 150
        for index in 0..<200 { queue.add(entry("e\(index)", at: Double(index))) }
        let now = Date(timeIntervalSince1970: 1_800_000_300)
        let first = queue.nextBatch(now: now).count
        #expect(first == 100)
        // Nothing more goes out while a batch is out.
        let none = queue.nextBatch(now: now)
        #expect(none.isEmpty)
        queue.delivered(at: now)
        let second = queue.nextBatch(now: now).count
        #expect(second == 50)
        queue.delivered(at: now)
        let capped = queue.nextBatch(now: now)
        #expect(capped.isEmpty)
        #expect(queue.rateWait(now: now) == 600)
        // Once the window has passed, the rest go.
        let rest = queue.nextBatch(now: now.addingTimeInterval(600)).count
        #expect(rest == 50)
    }

    @Test func aFailedBatchStaysAndBacksOff_aRefusedOneIsDropped() {
        var queue = ClientLogQueue()
        queue.add(entry("a"))
        queue.add(entry("b"))
        let out = queue.nextBatch().count
        #expect(out == 2)
        queue.failed()
        #expect(queue.pending.count == 2)
        #expect(queue.retryDelay == 10)
        _ = queue.nextBatch()
        queue.failed()
        #expect(queue.retryDelay == 20)
        _ = queue.nextBatch()
        queue.refused()
        #expect(queue.pending.isEmpty)
        for _ in 0..<20 { _ = queue.nextBatch(); queue.failed() }
        #expect(queue.retryDelay == 900)
    }

    @Test func pastCapacityTheOldestGo_butNotWhatsBeingSent() {
        var queue = ClientLogQueue()
        queue.capacity = 3
        queue.add(entry("a"))
        queue.add(entry("b"))
        _ = queue.nextBatch() // a, b out
        queue.add(entry("c"))
        queue.add(entry("d"))
        #expect(queue.pending.map(\.message) == ["a", "b", "d"])
        #expect(queue.overflowed == 1)
        queue.delivered()
        #expect(queue.pending.map(\.message) == ["d"])
    }

    @Test func onlyWhatsWaitingIsKeptAcrossLaunches() throws {
        var queue = ClientLogQueue()
        queue.add(entry("a"))
        let saved = try JSONDecoder().decode(ClientLogQueue.self, from: JSONEncoder().encode(queue))
        #expect(saved.pending.map(\.message) == ["a"])
    }

    @Test func deliveryFromErrors() {
        #expect(ClientLogDelivery(FlexarrClientError.http(status: 429, message: nil)) == .refused)
        #expect(ClientLogDelivery(FlexarrClientError.http(status: 400, message: nil)) == .refused)
        #expect(ClientLogDelivery(FlexarrClientError.unauthorized) == .refused)
        #expect(ClientLogDelivery(FlexarrClientError.http(status: 503, message: nil)) == .failed)
        #expect(ClientLogDelivery(URLError(.notConnectedToInternet)) == .failed)
    }

    // MARK: The uploader

    @Test func uploaderKeepsWhatFailedOnDisk_andSendsItNextTime() async throws {
        let storage = FileManager.default.temporaryDirectory.appending(path: "pending-\(UUID().uuidString).json")
        defer { try? FileManager.default.removeItem(at: storage) }
        let offline = ClientLogUploader(storage: storage, debounce: .seconds(3600)) { _ in .failed }
        await offline.add(entry("a"))
        await offline.flush()
        #expect(await offline.pending.count == 1)

        let received = Received()
        let online = ClientLogUploader(storage: storage, debounce: .seconds(3600)) { batch in
            await received.add(batch)
            return .delivered
        }
        #expect(await online.pending.count == 1)
        await online.flush()
        #expect(await received.messages == ["a"])
        #expect(await online.pending.isEmpty)
        #expect(!FileManager.default.fileExists(atPath: storage.path()))
    }

    private actor Received {
        var messages: [String] = []
        func add(_ batch: [ClientLogEntry]) { messages += batch.map(\.message) }
    }

    // MARK: The file

    @Test func theFileRollsOver_keepingTheOneBefore() throws {
        let directory = FileManager.default.temporaryDirectory.appending(path: "logs-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: directory) }
        let file = ClientLogFile(directory: directory, maxFileBytes: 100)
        for index in 0..<30 { file.append("line \(String(format: "%02d", index)) xxxxxxxxxx") }
        let text = file.contents()
        let lines = text.split(separator: "\n")
        #expect(lines.last == "line 29 xxxxxxxxxx")
        // Two files of at most 100 bytes: the oldest lines are gone.
        #expect(text.utf8.count <= 200)
        #expect(!text.contains("line 00"))
        // In order.
        #expect(lines == lines.sorted())
    }

    // MARK: The endpoints

    @Test func sendsLogsAsJSON() async throws {
        let (client, host) = stubbedClient(.init(status: 202, body: Data()))
        try await client.sendLogs(platform: "tvos", appVersion: "0.44.0", entries: [entry("Boom")])
        let request = try #require(StubProtocol.recorded(host: host).last)
        #expect(request.url?.path() == "/api/v1/logs/client")
        #expect(request.value(forHTTPHeaderField: "Content-Type") == "application/json")
        let body = try #require(try JSONSerialization.jsonObject(with: request.httpBody ?? Data()) as? [String: Any])
        #expect(body["platform"] as? String == "tvos")
        #expect(body["appVersion"] as? String == "0.44.0")
        #expect((body["entries"] as? [[String: Any]])?.first?["message"] as? String == "Boom")
    }

    @Test func rateLimitedLogsThrow429() async {
        let (client, _) = stubbedClient(.init(status: 429, body: Data()))
        await #expect(throws: FlexarrClientError.http(status: 429, message: nil)) {
            try await client.sendLogs(platform: "ios", appVersion: "1", entries: [])
        }
    }

    @Test func sendsDiagnosticsAsPlainText() async throws {
        let (client, host) = stubbedClient(.init(status: 201, body: Data(#"{"id":"d1"}"#.utf8)))
        let id = try await client.sendDiagnostics(platform: "ios", appVersion: "0.44.0", note: "it froze", text: "log")
        #expect(id == "d1")
        let request = try #require(StubProtocol.recorded(host: host).last)
        #expect(request.url?.path() == "/api/v1/logs/client/diagnostics")
        let query = URLComponents(url: request.url!, resolvingAgainstBaseURL: false)?.queryItems ?? []
        #expect(query == [.init(name: "platform", value: "ios"), .init(name: "appVersion", value: "0.44.0"), .init(name: "note", value: "it froze")])
        #expect(request.value(forHTTPHeaderField: "Content-Type") == "text/plain; charset=utf-8")
        #expect(request.httpBody == Data("log".utf8))
    }

    // MARK: Diagnostics

    @Test func diagnosticsFitByLeavingOutTheOldestLines() {
        let log = (0..<100).map { "line \(String(format: "%03d", $0)) " + String(repeating: "x", count: 40) }.joined(separator: "\n")
        let body = ClientDiagnostics.body(header: "App: Flexarr", log: log, maxBytes: 1000)
        #expect(body.utf8.count <= 1000)
        #expect(body.hasPrefix("App: Flexarr\n\n… "))
        #expect(body.hasSuffix("line 099 " + String(repeating: "x", count: 40)))
        let small = ClientDiagnostics.body(header: "H", log: "one\ntwo")
        #expect(small == "H\n\none\ntwo")
    }
}
