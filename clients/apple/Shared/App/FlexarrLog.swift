import FlexarrKit
import Foundation
import os
import UIKit

/// One part of the app's log (playback, music, downloads…). Every entry goes to the system log as before, and to a
/// file on the device that Settings › Send diagnostics sends; warnings and errors are also sent to the server by
/// themselves (see `LogHub`). Messages are redacted first, so sign-ins, session ids and query strings never leave.
///
/// Say which playback an entry is about with `playbackId` (`PlaybackInfo.playbackId`, never the session id), so it
/// can be followed from the app into the server's log.
struct FlexarrLog: Sendable {
    let category: String
    private let logger: Logger

    init(_ category: String) {
        self.category = category
        logger = Logger(subsystem: Bundle.main.bundleIdentifier ?? "io.github.flexarr", category: category)
    }

    func debug(_ message: String, error: (any Error)? = nil, _ properties: [String: String?] = [:]) {
        write(.debug, message, error, properties)
    }

    func info(_ message: String, error: (any Error)? = nil, _ properties: [String: String?] = [:]) {
        write(.information, message, error, properties)
    }

    func warning(_ message: String, error: (any Error)? = nil, _ properties: [String: String?] = [:]) {
        write(.warning, message, error, properties)
    }

    func error(_ message: String, error: (any Error)? = nil, _ properties: [String: String?] = [:]) {
        write(.error, message, error, properties)
    }

    private func write(_ level: ClientLogLevel, _ message: String, _ error: (any Error)?, _ properties: [String: String?]) {
        let entry = LogHub.shared.record(ClientLogEntry(level: level, category: category, message: message,
                                                        error: error.map(Self.describe), properties: properties.compactMapValues { $0 }))
        var text = entry.message
        if let error = entry.error { text += " | error: \(error)" }
        for (key, value) in (entry.properties ?? [:]).sorted(by: { $0.key < $1.key }) { text += " | \(key)=\(value)" }
        // Already redacted, so public: Console shows it in full.
        switch level {
        case .debug: logger.debug("\(text, privacy: .public)")
        case .information: logger.info("\(text, privacy: .public)")
        case .warning: logger.notice("\(text, privacy: .public)")
        case .error: logger.error("\(text, privacy: .public)")
        }
    }

    /// What went wrong, and for a system error its domain and code, which say more than the sentence.
    private static func describe(_ error: any Error) -> String {
        if error is FlexarrClientError { return "\(error.localizedDescription) (\(error))" }
        let ns = error as NSError
        return "\(ns.localizedDescription) (\(ns.domain) \(ns.code))"
    }
}

/// Where every `FlexarrLog` entry goes: redacted, then the file on the device and, for warnings and errors, the
/// uploader that sends them to the signed-in server. Never sends anywhere else.
final class LogHub: Sendable {
    static let shared = LogHub()

    #if os(tvOS)
    static let platform = "tvos"
    #else
    static let platform = "ios"
    #endif

    let file: ClientLogFile
    private let uploader: ClientLogUploader
    private let redactor = OSAllocatedUnfairLock(initialState: LogRedactor())
    private let client: OSAllocatedUnfairLock<FlexarrClient?>

    private init() {
        let caches = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask).first ?? FileManager.default.temporaryDirectory
        let directory = caches.appending(path: "Logs")
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        file = ClientLogFile(directory: directory)
        let client = OSAllocatedUnfairLock<FlexarrClient?>(initialState: nil)
        self.client = client
        let version = Bundle.main.shortVersion
        uploader = ClientLogUploader(storage: directory.appending(path: "waiting.json")) { entries in
            guard let client = client.withLock({ $0 }) else { return .unavailable }
            do {
                try await client.sendLogs(platform: LogHub.platform, appVersion: version, entries: entries)
                return .delivered
            } catch {
                return ClientLogDelivery(error)
            }
        }
    }

    /// Redacts the entry, writes it to the file and queues it for the server if it's a warning or error. Returns
    /// it as it was kept.
    @discardableResult
    func record(_ entry: ClientLogEntry) -> ClientLogEntry {
        let entry = redactor.withLock { $0.redact(entry) }
        file.append(entry.line)
        if entry.level.isSentAutomatically {
            let uploader = uploader
            Task.detached(priority: .utility) { await uploader.add(entry) }
        }
        return entry
    }

    /// The server to send to (nil when signed out), whose sign-in is also kept out of the log from now on.
    func signedIn(_ client: FlexarrClient?) {
        self.client.withLock { $0 = client }
        redactor.withLock { redactor in
            redactor.secrets = [client?.apiKey, client?.profileToken].compactMap { $0 }.filter { !$0.isEmpty }
        }
        guard client != nil else { return }
        let uploader = uploader
        Task.detached(priority: .utility) { await uploader.kick() }
    }

    /// Sends the whole log, with a few lines about this device on top, and returns the server's id for it.
    func sendDiagnostics(client: FlexarrClient, header: String, note: String?) async throws -> String {
        let file = file
        let body = await Task.detached(priority: .userInitiated) {
            ClientDiagnostics.body(header: header, log: file.contents())
        }.value
        let note = note?.trimmingCharacters(in: .whitespacesAndNewlines)
        return try await client.sendDiagnostics(platform: Self.platform, appVersion: Bundle.main.shortVersion,
                                                note: note?.isEmpty == false ? note : nil, text: body)
    }
}
