import Foundation
import Observation

/// A language the server offers. Absent from servers before 0.61, which offer none.
public struct LanguageInfo: Codable, Sendable, Hashable, Identifiable {
    public let code: String
    public let name: String
    public let direction: String
    public let version: Int64
    public var id: String { code }
}

/// A language's strings for the Apple apps, keyed by their English (as `make strings` writes them).
public struct LanguagePack: Codable, Sendable, Equatable {
    public let code: String
    public let name: String
    public let direction: String
    public let version: Int64
    public let messages: [String: String]

    public init(code: String, name: String, direction: String, version: Int64, messages: [String: String]) {
        (self.code, self.name, self.direction, self.version, self.messages) = (code, name, direction, version, messages)
    }
}

/// A string in the language shown (docs/languages.md, *Writing a string*): `t("Mark as watched")`,
/// `t("Resume from {time}", ["time": time])`, `t("Play", ctx: "music")`. The English is the key, so `make strings`
/// collects it from the code: always a plain literal, never interpolated. A language without the string, or whose
/// translation can't be formatted, shows the English.
public func t(_ english: String, _ values: [String: Any] = [:], ctx: String? = nil) -> String {
    Localization.shared.translate(english, values, ctx: ctx)
}

/// Marks a string for `make strings` without translating it, for tables translated later with `t(value)`.
public func tk(_ english: String) -> String { english }

/// The packs the apps show: built in (FlexarrKit's Resources/Languages, written by `make strings`) and downloaded
/// from the server (GET /languages/{code}?scope=apple, kept in Caches or the App Group's container), the newer of the
/// two winning, string by string falling back to English.
public final class Localization: @unchecked Sendable {
    public static let shared = Localization()

    /// Posted on the main queue when the language shown changes.
    public static let changed = Notification.Name("FlexarrLanguageChanged")

    /// The App Group whose container extensions (widgets, notifications) read the pack from; nil keeps it in Caches.
    nonisolated(unsafe) public static var appGroup: String?

    private let lock = NSLock()
    private var pack: LanguagePack?
    private var parsed: [String: [MessageFormat.Part]] = [:]
    private var failed: Set<String> = []

    /// The language shown: a BCP 47 code, "en" for English.
    public var code: String { lock.withLock { pack?.code ?? "en" } }

    public var isRightToLeft: Bool { lock.withLock { pack?.direction == "rtl" } }

    /// The locale to format dates and numbers with: the language shown, in the device's own variant of it where it has
    /// one (English on a British phone writes 3 Mar 2025).
    public static var locale: Locale { formattingLocale(for: shared.code) }

    public static func formattingLocale(for code: String) -> Locale {
        let shown = code == "en-XA" ? "en" : code
        let base = shown.split(separator: "-").first.map(String.init) ?? shown
        let device = ([Locale.current.identifier.replacingOccurrences(of: "_", with: "-")] + Locale.preferredLanguages).first { tag in
            let parts = tag.split(separator: "-").map(String.init)
            return parts.first?.lowercased() == base && (shown == base || tag.lowercased() == shown.lowercased())
        }
        return Locale(identifier: device ?? shown)
    }

    func translate(_ message: String, _ values: [String: Any], ctx given: String?) -> String {
        // A shared word can carry its context in its key (music::Play).
        var english = message, ctx = given
        if given == nil, let sep = message.range(of: "::"), message[..<sep.lowerBound].allSatisfy({ $0.isLowercase || $0 == "-" }),
           message.first?.isLowercase == true {
            ctx = String(message[..<sep.lowerBound])
            english = String(message[sep.upperBound...])
        }
        let key = ctx.map { "\($0)::\(english)" } ?? english
        let (current, translation) = lock.withLock { (pack, pack?.messages[key]) }
        if let current, let translation, let parts = parts(current.code, key, translation) {
            return MessageFormat.format(parts, language: current.code, values: values)
        }
        guard let source = parts("en", key, english) else { return english }
        return MessageFormat.format(source, language: "en", values: values)
    }

    private func parts(_ language: String, _ key: String, _ message: String) -> [MessageFormat.Part]? {
        let cacheKey = "\(language)\u{0}\(key)"
        if let hit = lock.withLock({ parsed[cacheKey] }) { return hit }
        if lock.withLock({ failed.contains(cacheKey) }) { return nil }
        do {
            let p = try MessageFormat.parse(message)
            lock.withLock { parsed[cacheKey] = p }
            return p
        } catch {
            _ = lock.withLock { failed.insert(cacheKey) }
            return nil
        }
    }

    /// Shows a pack (nil: English).
    public func show(_ next: LanguagePack?) {
        let changed: Bool = lock.withLock {
            guard pack?.code != next?.code || pack?.version != next?.version else { return false }
            pack = next
            parsed = [:]
            failed = []
            return true
        }
        guard changed else { return }
        if Thread.isMainThread {
            NotificationCenter.default.post(name: Self.changed, object: nil)
        } else {
            DispatchQueue.main.async { NotificationCenter.default.post(name: Self.changed, object: nil) }
        }
    }

    // MARK: Packs on the device

    /// The pack built into this app for a language (de-AT tries de-AT then de).
    public static func bundled(_ code: String) -> LanguagePack? {
        for c in fallbacks(code) {
            if let url = Bundle.module.url(forResource: c, withExtension: "json", subdirectory: "Languages"),
               let data = try? Data(contentsOf: url), let pack = try? JSONDecoder().decode(LanguagePack.self, from: data) {
                return pack
            }
        }
        return nil
    }

    private struct Kept: Codable {
        let pack: LanguagePack
    }

    private static var folder: URL? {
        let base = appGroup.flatMap { FileManager.default.containerURL(forSecurityApplicationGroupIdentifier: $0) }
            ?? FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask).first
        return base?.appending(path: "Languages", directoryHint: .isDirectory)
    }

    private static func kept(_ code: String) -> Kept? {
        guard let url = folder?.appending(path: "\(code).json"), let data = try? Data(contentsOf: url) else { return nil }
        return try? JSONDecoder().decode(Kept.self, from: data)
    }

    private static func keep(_ pack: LanguagePack) {
        guard let folder else { return }
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        if let data = try? JSONEncoder().encode(Kept(pack: pack)) {
            try? data.write(to: folder.appending(path: "\(pack.code).json"), options: .atomic)
        }
        UserDefaults(suiteName: appGroup)?.set(pack.code, forKey: "flexarr.language")
    }

    /// The newest pack on the device for a language: built in or kept from the server.
    public static func newestOnDevice(_ code: String) -> LanguagePack? {
        let built = bundled(code)
        let downloaded = fallbacks(code).lazy.compactMap { kept($0)?.pack }.first
        switch (built, downloaded) {
        case let (b?, d?): return d.version > b.version ? d : b
        case let (b, d): return b ?? d
        }
    }

    /// The language this device showed last (for extensions, and to start in it before the server answers).
    public static var lastShown: String? {
        get { UserDefaults(suiteName: appGroup)?.string(forKey: "flexarr.language") }
        set { UserDefaults(suiteName: appGroup)?.set(newValue, forKey: "flexarr.language") }
    }

    /// Starts in the language shown last, before anything draws, so it doesn't flash English first.
    public func startWithLastShown() {
        if let code = Self.lastShown { show(Self.newestOnDevice(code)) }
    }

    /// The best of the device's languages among those offered (pt-BR, then pt); nil for English or none.
    public static func deviceChoice(offered: [String], preferred: [String] = Locale.preferredLanguages) -> String? {
        for tag in preferred {
            for c in fallbacks(normalise(tag)) {
                if c == "en" { return nil }
                if offered.contains(c) { return c }
            }
        }
        return nil
    }

    static func normalise(_ tag: String) -> String {
        let parts = tag.replacingOccurrences(of: "_", with: "-").split(separator: "-").map(String.init)
        guard let first = parts.first else { return tag }
        return ([first.lowercased()] + parts.dropFirst().map { $0.count == 4 ? $0.prefix(1).uppercased() + $0.dropFirst().lowercased() : $0.uppercased() })
            .joined(separator: "-")
    }

    static func fallbacks(_ code: String) -> [String] {
        var out: [String] = []
        var c = code
        while true {
            out.append(c)
            guard let cut = c.lastIndex(of: "-") else { return out }
            c = String(c[..<cut])
        }
    }

    /// Settles the language: the profile's choice (nil follows the device), shown from the device's newest copy at
    /// once, then from the server's if it has a newer one. Offline, the device's copy stays.
    public func settle(profileChoice: String?, client: FlexarrClient) async {
        let offered = (try? await client.languages()) ?? []
        let code = profileChoice ?? Self.deviceChoice(offered: offered.map(\.code))
        guard let code, code != "en" else {
            show(nil)
            Self.lastShown = nil
            return
        }
        let onDevice = Self.newestOnDevice(code)
        show(onDevice)
        // The list says each pack's version: download only when the server's is newer than the device's.
        let published = offered.first { Self.fallbacks(code).contains($0.code) }
        if let published, published.version > (onDevice?.version ?? -1), let pack = try? await client.languagePack(published.code) {
            Self.keep(pack)
            show(pack)
        } else if published == nil, onDevice == nil {
            show(nil)
        }
        Self.lastShown = self.code == "en" ? nil : self.code
    }
}

/// Watches the language shown, for SwiftUI: `.id(language.code)` redraws everything in a new one.
@MainActor @Observable
public final class LanguageState {
    public private(set) var code = Localization.shared.code
    public private(set) var isRightToLeft = Localization.shared.isRightToLeft
    public var locale: Locale { Localization.formattingLocale(for: code) }

    @ObservationIgnored private var observer: (any NSObjectProtocol)?

    public init() {
        observer = NotificationCenter.default.addObserver(forName: Localization.changed, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated {
                self?.code = Localization.shared.code
                self?.isRightToLeft = Localization.shared.isRightToLeft
            }
        }
    }
}

/// A file's size as the language shown writes it ("1.2 GB", "1,2 GB").
public func formatBytes(_ bytes: Int64) -> String {
    bytes.formatted(.byteCount(style: .file).locale(Localization.locale))
}
