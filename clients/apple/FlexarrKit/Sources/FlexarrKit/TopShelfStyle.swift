import Foundation

/// How the Apple TV's Top Shelf shows Continue Watching: posters, or wide artwork with each title's logo on it.
/// Chosen in the app's Settings and read by its Top Shelf extension, so it's kept beside the shared sign-in.
public enum TopShelfStyle: String, Codable, Sendable, CaseIterable {
    case posters
    case wide

    private static let account = "top-shelf-style"

    public static var current: TopShelfStyle {
        get { SharedSignIn.read(account: account).flatMap { String(data: $0, encoding: .utf8) }.flatMap(TopShelfStyle.init(rawValue:)) ?? .posters }
        set { SharedSignIn.write(Data(newValue.rawValue.utf8), account: account) }
    }
}
