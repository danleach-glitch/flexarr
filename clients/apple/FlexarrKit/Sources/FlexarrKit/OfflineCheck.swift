import Foundation

/// Which of the titles a device has downloaded the profile may still play (POST /offline/check). A title a parent has
/// since blocked, put above a lowered age limit or hidden the library of is `hidden`; one whose file is offline just
/// now (a drive not mounted) is `missing`, never `hidden`; one the server no longer has is `gone`. Absent from servers
/// before the release after 0.62.1.
public struct OfflineCheck: Codable, Sendable, Equatable {
    public var allowed: [String]
    public var hidden: [String]
    public var missing: [String]
    public var gone: [String]

    public init(allowed: [String] = [], hidden: [String] = [], missing: [String] = [], gone: [String] = []) {
        self.allowed = allowed
        self.hidden = hidden
        self.missing = missing
        self.gone = gone
    }

    /// The asked-about ids whose downloads should come off the device: the hidden ones, and the gone ones, but those
    /// only when the server knew at least one of the titles asked about. A server that knows none of them is most
    /// likely not the one they came from, and nothing is taken off for it. Allowed and missing ones always stay, as
    /// does anything the answer doesn't mention. Ids are compared without regard to case.
    public func toRemove(asked: [String]) -> Set<String> {
        let askedIds = Set(asked.map { $0.lowercased() })
        let known = !(allowed.isEmpty && hidden.isEmpty && missing.isEmpty)
        var remove = Set(hidden.map { $0.lowercased() })
        if known { remove.formUnion(gone.map { $0.lowercased() }) }
        // Never one the server also calls allowed or missing.
        remove.subtract(allowed.map { $0.lowercased() })
        remove.subtract(missing.map { $0.lowercased() })
        return remove.intersection(askedIds)
    }

    mutating func merge(_ other: OfflineCheck) {
        allowed += other.allowed
        hidden += other.hidden
        missing += other.missing
        gone += other.gone
    }
}

private struct OfflineCheckBody: Encodable {
    let itemIds: [String]
}

extension FlexarrClient {
    /// Asks which of these downloaded titles the profile may still play, a thousand at a time (the server takes up to
    /// 5000). Throws for a server before the check (404, or the web app's page) as for any failure: the caller then
    /// leaves its downloads as they are.
    public func offlineCheck(itemIds: [String]) async throws -> OfflineCheck {
        var answer = OfflineCheck()
        var start = 0
        while start < itemIds.count {
            let chunk = Array(itemIds[start..<min(start + 1000, itemIds.count)])
            let data = try await send("POST", "offline/check", body: OfflineCheckBody(itemIds: chunk))
            answer.merge(try decode(OfflineCheck.self, from: data))
            start += 1000
        }
        return answer
    }
}
