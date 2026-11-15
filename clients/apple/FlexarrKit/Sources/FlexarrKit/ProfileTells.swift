import Foundation

/// What the server is still to be told about downloads (one has come, one was deleted), each kept with the profile it's
/// of. The server answers about a download only to a sign-in as its own profile (404 for anyone else's), so each waits
/// for a sign-in as that profile: told as someone else, it would only be dropped. As Android's `ProfileTells`.
public enum ProfileTells {
    /// Between the profile and the download's id: neither ever holds it.
    private static let separator: Character = "\u{1F}"

    /// Download `id`, to be told as `profileId`.
    public static func entry(_ id: String, profileId: String) -> String { "\(profileId)\(separator)\(id)" }

    /// An entry's profile (nil for one kept before entries said whose they were) and download id.
    public static func parse(_ entry: String) -> (profileId: String?, id: String) {
        guard let at = entry.firstIndex(of: separator) else { return (nil, entry) }
        return (String(entry[..<at]), String(entry[entry.index(after: at)...]))
    }

    /// The entries a sign-in as `profileId` tells now, each with its download id: its own, and any kept before entries
    /// said whose they were (told by whoever's signed in, as they always were). None while nobody's known to be chosen.
    public static func due(_ entries: Set<String>, profileId: String?) -> [(entry: String, id: String)] {
        guard let profileId else { return [] }
        return entries.sorted().compactMap { entry in
            let (owner, id) = parse(entry)
            return owner == nil || owner == profileId ? (entry, id) : nil
        }
    }
}
