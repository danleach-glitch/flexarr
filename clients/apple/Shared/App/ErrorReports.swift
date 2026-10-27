import FlexarrKit
import Foundation
import os
import Sentry

/// Crash and error reports to Sentry, only while the signed-in server's owner has them on (web Settings › Logs); off
/// otherwise, and there's no switch in the app. Crash handlers have to be in place before the server can be asked, so
/// the last answer for each server is kept and reporting starts at launch from that; the server's current answer
/// then starts or stops it.
///
/// Only errors and crashes go: no performance data, sessions, breadcrumbs, screenshots or requests, and every event
/// is scrubbed (`ErrorReportScrubber`) on its way out. The "user" is the server's random install id.
enum ErrorReports {
    private static let dsn = "https://9e68c0c6f01402b248f4170e20654509@o4512326167232512.ingest.de.sentry.io/4512326210093136"
    private static let cache = ErrorReportCache()
    private static let log = Logger(subsystem: Bundle.main.bundleIdentifier ?? "io.github.flexarr", category: "errorreports")
    /// The install id reports carry while on; nil while off.
    private static let running = OSAllocatedUnfairLock<String??>(initialState: nil)

    /// What Settings › Server says under the error-reports row.
    static let privacyNote = "Set on your server, in Settings › Logs on the web. When on, crashes and errors go to Sentry, the error tracker Flexarr uses: what went wrong and where in the code, the app version, the OS and device model, and a random id. Never titles, file names, what you watch, names, addresses or keys."

    /// Whether reports are being sent now.
    static var isOn: Bool { running.withLock { $0 != nil } }

    /// At launch: starts if the server signed in to had reports on last time it was asked.
    static func resume(server: URL?) {
        guard let server, let setting = cache.setting(for: server) else { return }
        start(setting)
    }

    /// The server's answer (nil: it didn't say, so off): kept for next launch, and reports started or stopped to match.
    static func apply(_ setting: ErrorReportSetting?, server: URL) {
        cache.save(setting, for: server)
        if let setting, setting.enabled {
            start(setting)
        } else {
            stop()
        }
    }

    /// Signed out, or on to another server: stops until that server says otherwise.
    static func stop() {
        let wasOn = running.withLock { state in
            defer { state = nil }
            return state != nil
        }
        guard wasOn else { return }
        SentrySDK.close()
        log.info("Error reports off")
    }

    /// An error logged as an error: only its type, domain and code go, with the log's category, never the message.
    static func capture(_ error: any Error, category: String) {
        guard isOn else { return }
        let ns = error as NSError
        // A bare error of the same kind: its description and user info (which can hold titles and addresses) stay here.
        let bare = NSError(domain: ns.domain, code: ns.code)
        SentrySDK.capture(error: bare) { scope in
            scope.setTag(value: category, key: "category")
        }
    }

    private static func start(_ setting: ErrorReportSetting) {
        let id = setting.installId
        let already = running.withLock { state -> String?? in
            defer { state = .some(id) }
            return state
        }
        if let already {
            // Already on; a new install id only needs the user changed.
            if already != id { SentrySDK.setUser(id.map { User(userId: $0) }) }
            return
        }
        SentrySDK.start { options in
            options.dsn = dsn
            options.releaseName = release
            options.environment = "production"
            options.sendDefaultPii = false
            options.tracesSampleRate = 0
            options.enableAutoSessionTracking = false
            options.enableAutoBreadcrumbTracking = false
            options.enableNetworkBreadcrumbs = false
            options.enableNetworkTracking = false
            options.enableCaptureFailedRequests = false
            options.enableAutoPerformanceTracing = false
            options.enableSwizzling = false // Only needed for the tracing and breadcrumbs that are off.
            options.sendClientReports = false
            options.maxBreadcrumbs = 0
            options.enableAppHangTracking = true // A hang is an error too.
            #if os(iOS)
            options.attachScreenshot = false
            options.attachViewHierarchy = false
            options.enableMetricKit = false
            #endif
            options.beforeSend = { event in scrub(event) }
        }
        SentrySDK.setUser(id.map { User(userId: $0) })
        log.info("Error reports on")
    }

    #if os(tvOS)
    private static let platform = "tvos"
    #else
    private static let platform = "ios"
    #endif

    private static var release: String {
        let build = Bundle.main.infoDictionary?["CFBundleVersion"] as? String ?? "0"
        return "flexarr-\(platform)@\(Bundle.main.shortVersion)+\(build)"
    }

    /// The event with nothing that could say who, where or what was being watched; nil (not sent) if that couldn't
    /// be done.
    private static func scrub(_ event: Event) -> Event? {
        let scrubber = ErrorReportScrubber(redactor: LogRedactor(secrets: LogHub.shared.secrets, maxLength: 2000))
        func clean(_ text: String?) -> String?? {
            guard let text else { return .some(nil) }
            return scrubber.scrub(text).map { .some($0) }
        }

        event.breadcrumbs = nil
        event.request = nil
        event.extra = nil
        event.serverName = nil
        event.modules = nil
        event.error = nil
        event.transaction = nil
        event.tags = ErrorReportScrubber.filterTags(event.tags)
        event.context = event.context.map(ErrorReportScrubber.filterContexts)
        let id = running.withLock { $0 ?? nil }
        event.user = id.map { User(userId: $0) }

        if let message = event.message {
            guard let text = scrubber.scrub(message.formatted) else { return nil }
            event.message = SentryMessage(formatted: text)
        }
        for exception in event.exceptions ?? [] {
            guard case .some(let value) = clean(exception.value) else { return nil }
            exception.value = value
            if let mechanism = exception.mechanism {
                guard case .some(let desc) = clean(mechanism.desc) else { return nil }
                mechanism.desc = desc
                mechanism.data = nil
                mechanism.helpLink = nil
            }
        }
        // Where the app is installed has an id for the install in it: only the files' names go.
        for image in event.debugMeta ?? [] {
            image.codeFile = ErrorReportScrubber.fileName(image.codeFile)
        }
        let stacks = (event.threads ?? []).compactMap(\.stacktrace) + (event.exceptions ?? []).compactMap(\.stacktrace)
            + [event.stacktrace].compactMap { $0 }
        for frame in stacks.flatMap(\.frames) {
            frame.package = ErrorReportScrubber.fileName(frame.package)
            frame.fileName = ErrorReportScrubber.fileName(frame.fileName)
            frame.vars = nil
            frame.contextLine = nil
            frame.preContext = nil
            frame.postContext = nil
        }
        return event
    }
}
