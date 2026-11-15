import Foundation

/// Which files in an app's downloads folder nothing holds any more, to be cleared away at launch.
public enum DownloadSweep {
    /// The names the downloads and collections hold (their files, books' folders, pictures).
    public static func known(fileNames: [String?]) -> Set<String> {
        Set(fileNames.compactMap { $0 }.filter { !$0.isEmpty })
    }

    /// Of the names in the folder, those to remove: not `known`, not a saved list (or a copy kept of one that didn't
    /// read: every `.json`), and not hidden (the system's own).
    public static func unknown(_ names: [String], known: Set<String>) -> [String] {
        names.filter { !known.contains($0) && !$0.hasPrefix(".") && !$0.lowercased().hasSuffix(".json") }.sorted()
    }
}
