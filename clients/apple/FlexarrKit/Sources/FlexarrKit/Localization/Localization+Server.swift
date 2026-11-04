import Foundation

/// A language the server offers. Absent from servers before 0.61, which offer none.
public struct LanguageInfo: Codable, Sendable, Hashable, Identifiable {
    public let code: String
    public let name: String
    public let direction: String
    public let version: Int64
    public var id: String { code }
}

extension Localization {
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
        // The most specific the server has (de-AT, then de); English is never a pack.
        let published = Self.fallbacks(code).lazy.filter { $0 != "en" }.compactMap { c in offered.first { $0.code == c } }.first
        if let published, published.version > (onDevice?.version ?? -1), let pack = try? await client.languagePack(published.code) {
            Self.keep(pack)
            show(pack)
        } else if published == nil, onDevice == nil {
            show(nil)
        }
        Self.lastShown = self.code == "en" ? nil : self.code
    }
}
