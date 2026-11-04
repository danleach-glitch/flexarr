import Foundation

/// The newest Flexarr release with the Mac app's disk image, for the copy downloaded from flexarr.io (the App Store's
/// updates itself). Read from GitHub's latest release: its tag is the version, and it counts only when the disk image
/// is attached to it.
public struct AppRelease: Decodable, Sendable, Equatable {
    public let version: String
    public let hasMacDownload: Bool

    /// Where the newest disk image always is, whatever its version.
    public static let macDownloadURL = URL(string: "https://github.com/danleach-glitch/flexarr/releases/latest/download/Flexarr.dmg")!
    static let latestURL = URL(string: "https://api.github.com/repos/danleach-glitch/flexarr/releases/latest")!
    /// The disk image's name on every release (scripts/mac-player-dmg.sh).
    static let macAssetName = "Flexarr.dmg"

    private enum CodingKeys: String, CodingKey { case tagName = "tag_name", assets }
    private struct Asset: Decodable { let name: String }

    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        let tag = try container.decode(String.self, forKey: .tagName)
        version = tag.hasPrefix("v") ? String(tag.dropFirst()) : tag
        let assets = try container.decodeIfPresent([Asset].self, forKey: .assets) ?? []
        hasMacDownload = assets.contains { $0.name == Self.macAssetName }
    }

    /// Whether this release is a newer Mac app than `current` ("0.61.0"; a dev build counts by its numbers).
    public func isNewer(than current: String) -> Bool {
        guard hasMacDownload, let latest = ServerSupport.numbers(of: version), let installed = ServerSupport.numbers(of: current)
        else { return false }
        return installed.lexicographicallyPrecedes(latest)
    }

    /// The latest release, or nil when GitHub can't be reached or answers with something else.
    public static func latest(session: URLSession = .shared) async -> AppRelease? {
        var request = URLRequest(url: latestURL, timeoutInterval: 15)
        request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        guard let (data, response) = try? await session.data(for: request),
              (response as? HTTPURLResponse)?.statusCode == 200 else { return nil }
        return try? JSONDecoder().decode(AppRelease.self, from: data)
    }
}
