import Foundation

/// The oldest server these apps support. Anything a newer server added is optional in the models ("Absent from
/// servers before …"); anything older than this is not catered for, and the apps say so: the connect screen won't
/// sign in to it, and a sign-in kept from before shows a banner. Android's `ServerSupport` keeps the same floor.
public enum ServerSupport {
    public static let minimumVersion = "0.50.0"

    /// What the connect screen and the banner say (docs/design.md, Words).
    public static let updateMessage = "Update your Flexarr server"

    /// Whether `version` ("0.49.2", "0.60.0-dev.1a2b3c4", "0.59.0+d4c892c…") is older than `minimumVersion`. A dev
    /// build counts by its numbers, so "0.60.0-dev.x" is new enough; a version that doesn't read never counts as old.
    public static func isTooOld(_ version: String?) -> Bool {
        guard let version, let found = numbers(of: version), let minimum = numbers(of: minimumVersion) else { return false }
        return found.lexicographicallyPrecedes(minimum)
    }

    /// "1.2.3" (or "v1.2.3", "1.2.3-dev.x", "1.2.3+build") as [1, 2, 3]; "1.2" as [1, 2, 0]. Nil when it doesn't read.
    static func numbers(of version: String) -> [Int]? {
        var core = version.trimmingCharacters(in: .whitespaces)
        if core.first == "v" || core.first == "V" { core.removeFirst() }
        core = String(core.prefix(while: { $0 != "+" && $0 != "-" }))
        let parts = core.split(separator: ".", omittingEmptySubsequences: false)
        guard (2...3).contains(parts.count) else { return nil }
        var numbers: [Int] = []
        for part in parts {
            guard !part.isEmpty, part.allSatisfy(\.isASCII), part.allSatisfy(\.isNumber), let number = Int(part) else { return nil }
            numbers.append(number)
        }
        return numbers + Array(repeating: 0, count: 3 - numbers.count)
    }
}
