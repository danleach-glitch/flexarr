import Foundation
import Observation

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

    /// Where the built-in packs are: FlexarrKit's resources, or (the Mac app, which compiles these files itself) the app's.
    static var packBundle: Bundle {
        #if SWIFT_PACKAGE
        Bundle.module
        #else
        Bundle.main
        #endif
    }

    /// The languages built into this app.
    public static var bundledCodes: [String] {
        (packBundle.urls(forResourcesWithExtension: "json", subdirectory: "Languages") ?? []).map { $0.deletingPathExtension().lastPathComponent }
    }

    /// Shows the device's own language, from the packs built in (for an app with no profile, like the Mac app's menu).
    public func showDeviceLanguage() {
        show(Self.deviceChoice(offered: Self.bundledCodes).flatMap(Self.newestOnDevice))
    }

    /// The pack built into this app for a language (de-AT tries de-AT then de).
    public static func bundled(_ code: String) -> LanguagePack? {
        for c in fallbacks(code) {
            if let url = packBundle.url(forResource: c, withExtension: "json", subdirectory: "Languages"),
               let data = try? Data(contentsOf: url), let pack = try? JSONDecoder().decode(LanguagePack.self, from: data) {
                return pack
            }
        }
        return nil
    }

    struct Kept: Codable {
        let pack: LanguagePack
    }

    private static var folder: URL? {
        let base = appGroup.flatMap { FileManager.default.containerURL(forSecurityApplicationGroupIdentifier: $0) }
            ?? FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask).first
        return base?.appending(path: "Languages", directoryHint: .isDirectory)
    }

    /// Whether `code` has the shape of a language's code (de, pt-BR, zh-Hans, es-419): letters, digits and hyphens
    /// only. A code names its pack's file, and comes from the server, so anything else (a path, say) is no language.
    static func isCode(_ code: String) -> Bool {
        !code.isEmpty && code.count <= 35 && code.allSatisfy { $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "-") }
    }

    /// The file a language's pack is kept in, in `folder`; nil for anything that isn't a language's code.
    static func file(for code: String, in folder: URL) -> URL? {
        isCode(code) ? folder.appending(path: "\(code).json", directoryHint: .notDirectory) : nil
    }

    static func kept(_ code: String) -> Kept? {
        guard let url = folder.flatMap({ file(for: code, in: $0) }), let data = try? Data(contentsOf: url) else { return nil }
        return try? JSONDecoder().decode(Kept.self, from: data)
    }

    /// Whether a pack the server sent when asked for `asked` is one to keep and show: it says it's that language (or
    /// the wider one the server fell back to: de for de-AT), by a code of the right shape. The pack's own word for
    /// what it is names its file, so one that says anything else is refused.
    static func accepts(_ pack: LanguagePack, askedFor asked: String) -> Bool {
        isCode(pack.code) && fallbacks(asked).contains(pack.code)
    }

    /// Keeps a pack downloaded for the language `asked`; false (and nothing kept) when it isn't one for that language.
    @discardableResult
    static func keep(_ pack: LanguagePack, askedFor asked: String) -> Bool {
        guard accepts(pack, askedFor: asked) else { return false }
        guard let folder, let file = file(for: pack.code, in: folder) else { return true }
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        if let data = try? JSONEncoder().encode(Kept(pack: pack)) {
            try? data.write(to: file, options: .atomic)
        }
        UserDefaults(suiteName: appGroup)?.set(pack.code, forKey: "flexarr.language")
        return true
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
