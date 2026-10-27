import Foundation
import os

// The apps' own log, as the server takes it: warnings and errors sent by themselves in small batches, everything
// kept in a file on the device for the times someone sends it on purpose (Settings › Send diagnostics). It only ever
// goes to the person's own server.

/// How serious a log entry is, named as the server's log levels are.
public enum ClientLogLevel: String, Codable, Sendable, Comparable, CaseIterable {
    case debug = "Debug"
    case information = "Information"
    case warning = "Warning"
    case error = "Error"

    private var rank: Int {
        switch self {
        case .debug: 0
        case .information: 1
        case .warning: 2
        case .error: 3
        }
    }

    public static func < (lhs: ClientLogLevel, rhs: ClientLogLevel) -> Bool { lhs.rank < rhs.rank }

    /// Sent to the server as they happen (the rest only with diagnostics).
    public var isSentAutomatically: Bool { self >= .warning }

    /// The level's short name in the log file, padded so messages line up.
    var tag: String {
        switch self {
        case .debug: "DEBUG"
        case .information: "INFO "
        case .warning: "WARN "
        case .error: "ERROR"
        }
    }
}

/// One line of the log. Encodes as `POST /logs/client` wants it, the time as an ISO 8601 string.
public struct ClientLogEntry: Codable, Sendable, Equatable {
    public var timestamp: Date
    public var level: ClientLogLevel
    public var category: String
    public var message: String
    public var error: String?
    public var properties: [String: String]?

    public init(timestamp: Date = Date(), level: ClientLogLevel, category: String, message: String, error: String? = nil,
                properties: [String: String]? = nil) {
        self.timestamp = timestamp
        self.level = level
        self.category = category
        self.message = message
        self.error = error
        self.properties = properties?.isEmpty == true ? nil : properties
    }

    private enum CodingKeys: String, CodingKey {
        case timestamp, level, category, message, error, properties
    }

    private static let iso = Date.ISO8601FormatStyle(includingFractionalSeconds: true)

    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        let time = try container.decode(String.self, forKey: .timestamp)
        timestamp = (try? Self.iso.parse(time)) ?? FlexarrDate.parse(time) ?? Date()
        level = try container.decode(ClientLogLevel.self, forKey: .level)
        category = try container.decode(String.self, forKey: .category)
        message = try container.decode(String.self, forKey: .message)
        error = try container.decodeIfPresent(String.self, forKey: .error)
        properties = try container.decodeIfPresent([String: String].self, forKey: .properties)
    }

    public func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode(Self.iso.format(timestamp), forKey: .timestamp)
        try container.encode(level, forKey: .level)
        try container.encode(category, forKey: .category)
        try container.encode(message, forKey: .message)
        try container.encodeIfPresent(error, forKey: .error)
        try container.encodeIfPresent(properties, forKey: .properties)
    }

    /// The entry as a line of the log file: time, level, category, message, then the error and properties.
    public var line: String {
        var text = "\(Self.iso.format(timestamp)) \(level.tag) [\(category)] \(message)"
        if let error { text += " | error: \(error)" }
        for (key, value) in (properties ?? [:]).sorted(by: { $0.key < $1.key }) { text += " | \(key)=\(value)" }
        return text.replacingOccurrences(of: "\r\n", with: "\\n").replacingOccurrences(of: "\n", with: "\\n")
    }

    /// What makes two entries the same thing happening again (the time and properties aside).
    var repeatKey: String { "\(level.rawValue)|\(category)|\(message)|\(error ?? "")" }
}

// MARK: - Redaction

/// Takes what mustn't leave the device out of a log message: the sign-in (API key, tokens), session ids (the stream
/// URLs' credential) and anything that looks like one, query strings, and passwords in URLs.
public struct LogRedactor: Sendable {
    /// Known secrets (this device's API key or token, the profile's token, the push token), replaced wherever
    /// they appear.
    public var secrets: [String]
    /// Longer messages are cut here.
    public var maxLength: Int

    public static let placeholder = "<redacted>"

    public init(secrets: [String] = [], maxLength: Int = 4000) {
        self.secrets = secrets
        self.maxLength = maxLength
    }

    /// `api_key=…`, `X-Api-Key: …`, `"token": "…"`, `Authorization: Bearer …`.
    private static let namedSecret = try! NSRegularExpression(
        pattern: #"(?i)\b(x-api-key|x-flexarr-profile|api[_-]?key|access[_-]?token|refresh[_-]?token|device[_-]?token|push[_-]?token|token|password|passwd|pin|secret|authorization)(["']?\s*[:=]\s*["']?)(?:bearer\s+)?[^\s&"',;})\]]+"#)
    /// The user and password in `scheme://user:pass@host`.
    private static let userInfo = try! NSRegularExpression(
        pattern: #"(?i)(\b[a-z][a-z0-9+.-]*://)[^\s/@"'<>]+@"#)
    /// A query string after a URL or a path (anything with a slash before the `?`).
    private static let query = try! NSRegularExpression(
        pattern: #"([^\s?#"'<>]*/[^\s?#"'<>]*)\?[^\s"'<>]+"#)
    /// A URL's fragment.
    private static let fragment = try! NSRegularExpression(
        pattern: #"(?i)(\b[a-z][a-z0-9+.-]*://[^\s#"'<>]*)#[^\s"'<>]+"#)
    /// 32 or more hex digits in a row: API keys, tokens, session ids and push tokens all look like this.
    private static let hexRun = try! NSRegularExpression(pattern: #"[0-9a-fA-F]{32,}"#)

    public func redact(_ text: String) -> String {
        var text = text
        for secret in secrets where secret.count >= 6 {
            text = text.replacingOccurrences(of: secret, with: Self.placeholder)
        }
        // Query strings go whole, before named secrets leave a placeholder in one.
        text = Self.replace(Self.userInfo, in: text, with: "$1")
        text = Self.replace(Self.query, in: text, with: "$1")
        text = Self.replace(Self.fragment, in: text, with: "$1")
        text = Self.replace(Self.namedSecret, in: text, with: "$1$2\(Self.placeholder)")
        text = Self.replace(Self.hexRun, in: text, with: Self.placeholder)
        if text.count > maxLength { text = String(text.prefix(maxLength)) + "…" }
        return text
    }

    /// The entry with its message, error and properties redacted.
    public func redact(_ entry: ClientLogEntry) -> ClientLogEntry {
        var entry = entry
        entry.category = redact(entry.category)
        entry.message = redact(entry.message)
        entry.error = entry.error.map(redact)
        entry.properties = entry.properties?.mapValues(redact)
        return entry
    }

    private static func replace(_ expression: NSRegularExpression, in text: String, with template: String) -> String {
        expression.stringByReplacingMatches(in: text, range: NSRange(text.startIndex..., in: text), withTemplate: template)
    }
}

// MARK: - The queue of warnings and errors to send

/// Warnings and errors waiting to go to the server, in order. The same thing happening over and over is sent once
/// (with how many more times it happened), sending is capped per ten minutes, and what can't be sent waits, up to
/// `capacity` entries, oldest dropped first. Only `pending` is kept across launches.
public struct ClientLogQueue: Codable, Sendable {
    public static let batchSize = 100

    public private(set) var pending: [ClientLogEntry] = []
    /// At most this many wait; past it the oldest go.
    public var capacity = 500
    /// The same entry again within this long is counted rather than queued.
    public var repeatWindow: TimeInterval = 300
    /// At most this many entries are sent per `rateWindow`.
    public var rateLimit = 300
    public var rateWindow: TimeInterval = 600
    /// Sends in a row that failed (no connection, server error), for the backoff.
    public private(set) var failures = 0
    /// Entries lost to the capacity since launch.
    public private(set) var overflowed = 0

    /// The first entries of `pending`, out being sent: left alone until the answer comes.
    private var inFlight = 0
    /// When each entry was last queued, by `repeatKey`.
    private var lastQueued: [String: Date] = [:]
    /// Repeats left out since then, by `repeatKey`; the next one queued says how many.
    private var leftOut: [String: Int] = [:]
    /// Entries sent, and when, within the rate window.
    private var sent: [(date: Date, count: Int)] = []

    private enum CodingKeys: String, CodingKey { case pending }

    public init() {}

    /// Queues a warning or error. False when it wasn't queued: not a warning or error, or a repeat (counted on the
    /// one already waiting, or on the next one queued).
    @discardableResult
    public mutating func add(_ entry: ClientLogEntry) -> Bool {
        guard entry.level.isSentAutomatically else { return false }
        let key = entry.repeatKey
        if let index = pending[inFlight...].lastIndex(where: { $0.repeatKey == key }) {
            pending[index].properties = Self.counting(pending[index].properties, more: 1)
            return false
        }
        if let last = lastQueued[key], entry.timestamp.timeIntervalSince(last) < repeatWindow {
            leftOut[key, default: 0] += 1
            return false
        }
        var entry = entry
        if let repeats = leftOut.removeValue(forKey: key) { entry.properties = Self.counting(entry.properties, more: repeats) }
        lastQueued[key] = entry.timestamp
        if lastQueued.count > 500 { lastQueued = lastQueued.filter { entry.timestamp.timeIntervalSince($0.value) < repeatWindow } }
        pending.append(entry)
        let over = pending.count - capacity
        if over > 0 {
            let start = min(inFlight, pending.count)
            let end = min(start + over, pending.count)
            pending.removeSubrange(start..<end)
            overflowed += end - start
        }
        return true
    }

    /// The next entries to send (none while some are out, or while the rate cap is reached), marked as out.
    public mutating func nextBatch(now: Date = Date()) -> [ClientLogEntry] {
        guard inFlight == 0 else { return [] }
        sent.removeAll { now.timeIntervalSince($0.date) >= rateWindow }
        let allowance = rateLimit - sent.reduce(0) { $0 + $1.count }
        let count = min(Self.batchSize, max(0, allowance), pending.count)
        inFlight = count
        return Array(pending.prefix(count))
    }

    /// The batch out arrived.
    public mutating func delivered(at now: Date = Date()) {
        sent.append((now, inFlight))
        pending.removeFirst(inFlight)
        inFlight = 0
        failures = 0
    }

    /// The server refused the batch out (it's malformed, or too much is being sent): it's let go, not retried.
    public mutating func refused() {
        pending.removeFirst(inFlight)
        inFlight = 0
    }

    /// The batch out couldn't be sent: it stays, and the next try waits longer.
    public mutating func failed() {
        inFlight = 0
        failures += 1
    }

    /// How long to wait after `failures` failed sends: 10 seconds, doubling, at most 15 minutes.
    public var retryDelay: TimeInterval {
        guard failures > 0 else { return 0 }
        return min(10 * pow(2, Double(min(failures, 12) - 1)), 900)
    }

    /// While the rate cap holds entries back, how long until some can go.
    public func rateWait(now: Date = Date()) -> TimeInterval? {
        guard !pending.isEmpty, inFlight == 0 else { return nil }
        let live = sent.filter { now.timeIntervalSince($0.date) < rateWindow }
        guard live.reduce(0, { $0 + $1.count }) >= rateLimit, let oldest = live.map(\.date).min() else { return nil }
        return max(1, rateWindow - now.timeIntervalSince(oldest))
    }

    /// `properties` with `repeats` (how many more times it happened) raised by `more`.
    private static func counting(_ properties: [String: String]?, more: Int) -> [String: String] {
        var properties = properties ?? [:]
        properties["repeats"] = String((properties["repeats"].flatMap(Int.init) ?? 0) + more)
        return properties
    }
}

// MARK: - Sending

/// How a batch sent to the server went.
public enum ClientLogDelivery: Sendable, Equatable {
    case delivered
    /// Refused (4xx, 429 included): let go rather than retried.
    case refused
    /// Didn't get there (no connection, a server error): tried again later.
    case failed
    /// Not signed in: kept until there's a server to send to.
    case unavailable

    public init(_ error: Error) {
        switch error {
        case FlexarrClientError.unauthorized: self = .refused
        case FlexarrClientError.http(let status, _) where (400..<500).contains(status): self = .refused
        default: self = .failed
        }
    }
}

/// Sends warnings and errors to the server a few seconds after they happen, in batches, keeping what couldn't be
/// sent (on disk too, for the next launch) and trying again with backoff. Its own failures go to the system log
/// only, never back into itself.
public actor ClientLogUploader {
    public typealias Send = @Sendable ([ClientLogEntry]) async -> ClientLogDelivery

    private var queue = ClientLogQueue()
    private let storage: URL?
    private let debounce: Duration
    private let send: Send
    private var scheduled: Task<Void, Never>?
    private var flushing = false
    private static let log = Logger(subsystem: Bundle.main.bundleIdentifier ?? "io.github.flexarr", category: "logs")

    /// `storage`: the file the waiting entries are kept in between launches.
    public init(storage: URL?, debounce: Duration = .seconds(5), send: @escaping Send) {
        self.storage = storage
        self.debounce = debounce
        self.send = send
        if let storage, let data = try? Data(contentsOf: storage),
           let saved = try? JSONDecoder().decode(ClientLogQueue.self, from: data) {
            queue = saved
        }
    }

    public var pending: [ClientLogEntry] { queue.pending }

    public func add(_ entry: ClientLogEntry) {
        guard queue.add(entry) else { return }
        save()
        schedule(after: debounce)
    }

    /// Sends what's waiting soon: there's a server to send to now, say.
    public func kick() {
        guard !queue.pending.isEmpty else { return }
        schedule(after: debounce)
    }

    /// Sends what's waiting now, batch after batch, until it's all gone or something stops it.
    public func flush() async {
        scheduled?.cancel()
        scheduled = nil
        guard !flushing else { return }
        flushing = true
        defer { flushing = false }
        while true {
            let batch = queue.nextBatch()
            guard !batch.isEmpty else {
                if let wait = queue.rateWait() { schedule(after: .seconds(wait)) }
                return
            }
            switch await send(batch) {
            case .delivered:
                queue.delivered()
                save()
            case .refused:
                Self.log.error("The server refused \(batch.count) log entries; dropped")
                queue.refused()
                save()
            case .unavailable:
                queue.failed()
                return
            case .failed:
                queue.failed()
                save()
                Self.log.info("Couldn't send \(batch.count) log entries; trying again in \(Int(self.queue.retryDelay))s")
                schedule(after: .seconds(queue.retryDelay))
                return
            }
        }
    }

    private func schedule(after delay: Duration) {
        guard scheduled == nil else { return }
        scheduled = Task { [weak self] in
            try? await Task.sleep(for: delay)
            guard !Task.isCancelled else { return }
            await self?.scheduledFlush()
        }
    }

    private func scheduledFlush() async {
        scheduled = nil
        await flush()
    }

    private func save() {
        guard let storage else { return }
        do {
            if queue.pending.isEmpty {
                try? FileManager.default.removeItem(at: storage)
            } else {
                try JSONEncoder().encode(queue).write(to: storage, options: .atomic)
            }
        } catch {
            Self.log.error("Couldn't keep the log entries waiting to be sent: \(error.localizedDescription, privacy: .public)")
        }
    }
}

// MARK: - The log file

/// Every entry, as lines in a file that rolls over at `maxFileBytes` (the one before is kept), so diagnostics have the
/// recent past even after a relaunch or a crash. Written on a queue of its own, never on the caller's thread.
public final class ClientLogFile: @unchecked Sendable {
    public let directory: URL
    private let maxFileBytes: Int
    private let queue = DispatchQueue(label: "io.github.flexarr.logfile", qos: .utility)
    // Touched only on `queue`.
    private var handle: FileHandle?
    private var size = 0

    public var current: URL { directory.appending(path: "flexarr.log") }
    public var previous: URL { directory.appending(path: "flexarr.1.log") }

    public init(directory: URL, maxFileBytes: Int = 1_000_000) {
        self.directory = directory
        self.maxFileBytes = maxFileBytes
    }

    public func append(_ line: String) {
        queue.async { self.write(line) }
    }

    /// Both files, oldest line first, once everything appended so far is written.
    public func contents() -> String {
        queue.sync {
            try? handle?.synchronize()
            let old = (try? String(contentsOf: previous, encoding: .utf8)) ?? ""
            let new = (try? String(contentsOf: current, encoding: .utf8)) ?? ""
            return old + new
        }
    }

    /// Waits for everything appended so far to be written.
    public func flush() {
        queue.sync { try? handle?.synchronize() }
    }

    private func write(_ line: String) {
        let data = Data((line + "\n").utf8)
        if handle == nil { open() }
        if size + data.count > maxFileBytes, size > 0 { rotate() }
        guard let handle else { return }
        do {
            try handle.write(contentsOf: data)
            size += data.count
        } catch {
            self.handle = nil
        }
    }

    private func open() {
        let manager = FileManager.default
        try? manager.createDirectory(at: directory, withIntermediateDirectories: true)
        if !manager.fileExists(atPath: current.path()) { manager.createFile(atPath: current.path(), contents: nil) }
        handle = try? FileHandle(forWritingTo: current)
        size = Int((try? handle?.seekToEnd()) ?? 0)
    }

    private func rotate() {
        try? handle?.close()
        handle = nil
        let manager = FileManager.default
        try? manager.removeItem(at: previous)
        try? manager.moveItem(at: current, to: previous)
        open()
    }
}

// MARK: - Diagnostics

public enum ClientDiagnostics {
    /// The most the server takes.
    public static let maxBytes = 5 * 1024 * 1024

    /// The header, a blank line, then the log, its oldest lines left out as needed to fit `maxBytes`.
    public static func body(header: String, log: String, maxBytes: Int = maxBytes) -> String {
        let head = header.hasSuffix("\n") ? header + "\n" : header + "\n\n"
        var budget = maxBytes - head.utf8.count
        guard budget > 0 else { return String(decoding: Data(head.utf8).prefix(maxBytes), as: UTF8.self) }
        if log.utf8.count <= budget { return head + log }
        // Keep the newest lines that fit, with a line saying how many went.
        let lines = log.split(separator: "\n", omittingEmptySubsequences: false)
        budget -= 80 // the note
        var kept: [Substring] = []
        for line in lines.reversed() {
            let cost = line.utf8.count + 1
            guard budget - cost >= 0 else { break }
            budget -= cost
            kept.append(line)
        }
        let note = "… \(lines.count - kept.count) older lines left out to fit"
        return head + note + "\n" + kept.reversed().joined(separator: "\n")
    }
}
