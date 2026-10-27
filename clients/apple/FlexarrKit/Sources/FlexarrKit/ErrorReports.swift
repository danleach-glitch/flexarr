import Foundation

// Error reports (crashes and errors, to Sentry) are the server owner's choice, off unless turned on in the web app's
// Settings › Logs. What goes is scrubbed hard first: what went wrong and where in the code, never what was being
// watched, by whom, or where. The Sentry side lives in the apps; this is the part that can be tested anywhere.

/// Takes everything that could say who or where out of an error report's text: what the log redacts (sign-ins, long
/// hex runs, query strings), then server addresses, IP addresses, email addresses, file paths and anything quoted
/// (titles usually are).
public struct ErrorReportScrubber: Sendable {
    public var redactor: LogRedactor

    public init(redactor: LogRedactor = LogRedactor(maxLength: 2000)) {
        self.redactor = redactor
    }

    private struct Rule {
        let expression: NSRegularExpression
        let template: String
    }

    /// In order: emails before hosts (an email has one), file URLs before other URLs, URLs before bare paths.
    private static func rules() throws -> [Rule] {
        func rule(_ pattern: String, _ template: String) throws -> Rule {
            Rule(expression: try NSRegularExpression(pattern: pattern), template: template)
        }
        return [
            try rule(#"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)+"#, "<email>"),
            try rule(#"(?i)\bfile://\S+"#, "<path>"),
            // A URL keeps its scheme and path; the host (and port) goes.
            try rule(#"(?i)\b([a-z][a-z0-9+.-]*://)[^\s/"'<>?#]+"#, "$1<server>"),
            try rule(#"\b(?:25[0-5]|2[0-4]\d|1?\d?\d)(?:\.(?:25[0-5]|2[0-4]\d|1?\d?\d)){3}(?::\d+)?\b"#, "<ip>"),
            try rule(#"(?i)(?<![\w:])(?:[0-9a-f]{0,4}:){3,7}[0-9a-f]{0,4}(?![\w:])"#, "<ip>"),
            // Names the network gives machines: mini.local, nas.lan, tv.tailnet.ts.net.
            try rule(#"(?i)\b[a-z0-9-]+(?:\.[a-z0-9-]+)*\.(?:local|lan|home|internal|ts\.net)(?::\d+)?\b"#, "<server>"),
            try rule(#"~/\S*"#, "<path>"),
            // An absolute path: at least two parts, not the path of a URL already dealt with.
            try rule(#"(?<![\w:/>.])/(?:[^\s/"']+/)+[^\s"']*"#, "<path>"),
            try rule(#""[^"\n]*"|“[^”\n]*”|‘[^’\n]*’|«[^»\n]*»|(?<!\w)'[^'\n]*'(?!\w)"#, "<quoted>"),
        ]
    }

    /// Built once; nil if a pattern didn't compile, and then nothing is sent (see `scrub`).
    private static let compiled: [Rule]? = try? rules()

    /// The text with nothing identifying left, or nil when it couldn't be scrubbed (the report is then dropped).
    public func scrub(_ text: String) -> String? {
        guard let rules = Self.compiled else { return nil }
        var text = redactor.redact(text)
        for rule in rules {
            text = rule.expression.stringByReplacingMatches(in: text, range: NSRange(text.startIndex..., in: text),
                                                            withTemplate: rule.template)
        }
        return text
    }

    /// Of the device, only what kind it is: never its name, its storage or memory, or a hash tied to it.
    public static let deviceKeys: Set<String> = ["model", "model_id", "family", "arch", "simulator"]
    public static let osKeys: Set<String> = ["name", "version", "build"]
    public static let appKeys: Set<String> = ["app_version", "app_build", "build_type"]

    /// An event's contexts with only the device's model, the OS and the app's version left.
    public static func filterContexts(_ contexts: [String: [String: Any]]) -> [String: [String: Any]] {
        var kept: [String: [String: Any]] = [:]
        for (name, keys) in [("device", deviceKeys), ("os", osKeys), ("app", appKeys)] {
            guard let values = contexts[name]?.filter({ keys.contains($0.key) }), !values.isEmpty else { continue }
            kept[name] = values
        }
        return kept
    }

    /// Only the tags the app sets itself.
    public static let tagKeys: Set<String> = ["category"]

    public static func filterTags(_ tags: [String: String]?) -> [String: String]? {
        let kept = tags?.filter { tagKeys.contains($0.key) } ?? [:]
        return kept.isEmpty ? nil : kept
    }

    /// A file's name without where it is (an app's path holds an id for the install).
    public static func fileName(_ path: String?) -> String? {
        guard let path, path.contains("/") else { return path }
        return path.split(separator: "/").last.map(String.init)
    }
}

/// The last error-report setting seen for each server, so a crash right at launch is caught before the server has
/// said: the apps start reporting at launch only if the server it's signed in to had it on last time.
public struct ErrorReportCache: @unchecked Sendable {
    private let defaults: UserDefaults

    public init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
    }

    private func key(_ server: URL) -> String {
        let host = server.host()?.lowercased() ?? server.absoluteString
        return "errorReports.\(host):\(server.port.map(String.init) ?? "")"
    }

    /// The setting last seen for `server`; nil when it was off or never seen.
    public func setting(for server: URL) -> ErrorReportSetting? {
        guard let data = defaults.data(forKey: key(server)),
              let setting = try? JSONDecoder().decode(ErrorReportSetting.self, from: data), setting.enabled else { return nil }
        return setting
    }

    /// Keeps an enabled setting; anything else clears what was kept.
    public func save(_ setting: ErrorReportSetting?, for server: URL) {
        guard let setting, setting.enabled, let data = try? JSONEncoder().encode(setting) else {
            defaults.removeObject(forKey: key(server))
            return
        }
        defaults.set(data, forKey: key(server))
    }
}
