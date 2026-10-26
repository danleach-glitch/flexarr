import Foundation

// MARK: - Models

/// Whether the server has a Live TV source (Dispatcharr), and how many channels it gave.
public struct LiveTvStatus: Codable, Sendable, Hashable {
    public let configured: Bool
    public let channels: Int
    public let refreshing: Bool
    public let refreshedAt: Date?
    /// Why the last refresh failed, in words.
    public let error: String?
    /// How far back a channel being watched can be rewound.
    public let rewindMinutes: Int
}

/// A programme in the guide.
public struct LiveProgramme: Codable, Sendable, Hashable, Identifiable {
    public let id: Int64
    public let start: Date
    public let end: Date
    public let title: String
    /// The episode's own title, when the guide gives one.
    public let subtitle: String?
    public let description: String?
    public let category: String?
    public let season: Int?
    public let episode: Int?
    public let isNew: Bool

    /// How far through it is at `now`, 0 to 1.
    public func progress(at now: Date = Date()) -> Double {
        let length = end.timeIntervalSince(start)
        return length > 0 ? min(1, max(0, now.timeIntervalSince(start) / length)) : 0
    }

    /// "Doctor Who: The Star Beast", or just the title when the episode's name repeats it or runs long.
    public var displayTitle: String {
        guard let subtitle, subtitle != title, subtitle.count < 60 else { return title }
        return "\(title): \(subtitle)"
    }
}

public struct LiveChannel: Codable, Sendable, Hashable, Identifiable {
    public let id: String
    /// The channel number viewers know it by; the source may use decimals.
    public let number: Double
    public let name: String
    public let group: String?
    /// A server path; logos are anonymous, like artwork.
    public let logoUrl: String?
    /// Starred by this profile.
    public let isFavourite: Bool
    public let now: LiveProgramme?
    public let next: LiveProgramme?

    /// "101", or "101.1" for a decimal one.
    public var numberText: String {
        number.rounded() == number ? String(Int(number)) : String(number)
    }

    /// The group's name without the provider's decoration: "UK| NEWS ᴴᴰ/ᴿᴬᵂ" reads "UK NEWS".
    public var groupName: String? {
        guard let group else { return nil }
        let words = group.unicodeScalars.filter { scalar in
            // Superscript and small-capital letters, as providers dress up their group names.
            !(0x1D00...0x1DBF).contains(scalar.value) && !(0x2070...0x209F).contains(scalar.value)
                && !(0x02B0...0x02FF).contains(scalar.value) && !(0x2C7C...0x2C7D).contains(scalar.value)
        }
        var text = String(String.UnicodeScalarView(words)).replacingOccurrences(of: "|", with: " ")
        while text.hasSuffix("/") || text.hasSuffix(" ") { text.removeLast() }
        text = text.split(separator: " ", omittingEmptySubsequences: true).joined(separator: " ")
        return text.isEmpty ? group : text
    }

    /// The same channel starred or not: a change shown before the server confirms it.
    public func with(favourite: Bool) -> LiveChannel {
        LiveChannel(id: id, number: number, name: name, group: group, logoUrl: logoUrl, isFavourite: favourite, now: now, next: next)
    }
}

/// A channel being watched. `playlist` is a live HLS playlist whose address is the viewer's capability.
public struct LiveWatch: Codable, Sendable, Hashable {
    public let viewer: String
    public let playlist: String
    public let channel: LiveChannel
    /// How the channel reaches the player ("Original", "Converted to 720p, deinterlaced").
    public let method: String
    public let rewindSeconds: Int
}

/// A channel in the guide, with its programmes in the window asked for.
public struct GuideChannel: Codable, Sendable, Hashable, Identifiable {
    public let id: String
    public let number: Double
    public let name: String
    public let group: String?
    public let logoUrl: String?
    public let isFavourite: Bool
    public let programmes: [LiveProgramme]

    /// The channel as the rest of Live TV knows it, with what's on at `now` and next.
    public func channel(at now: Date = Date()) -> LiveChannel {
        let on = programmes.first { $0.start <= now && $0.end > now }
        let next = programmes.first { $0.start > now }
        return LiveChannel(id: id, number: number, name: name, group: group, logoUrl: logoUrl, isFavourite: isFavourite, now: on, next: next)
    }
}

public struct LiveGuide: Codable, Sendable, Hashable {
    public let from: Date
    public let until: Date
    public let channels: [GuideChannel]
}

private struct WatchBody: Encodable {
    let replaces: String?
}

// MARK: - Client

extension FlexarrClient {
    /// Servers before Live TV have none.
    public func liveTvStatus() async throws -> LiveTvStatus? {
        do {
            return try await get("livetv/status")
        } catch FlexarrClientError.http(status: 404, _) {
            return nil
        }
    }

    /// Every channel's programmes from `from` for `hours` (at most a day).
    public func liveGuide(from: Date, hours: Int) async throws -> LiveGuide {
        try await get("livetv/guide", query: [
            .init(name: "from", value: ISO8601DateFormatter().string(from: from)),
            .init(name: "hours", value: String(hours)),
        ])
    }

    /// Every channel in number order, with what's on now and next.
    public func liveChannels() async throws -> [LiveChannel] {
        try await get("livetv/channels")
    }

    /// Starts watching a channel and returns once it can be played. `replaces` is the viewer's previous channel,
    /// let go first so its connection is free (providers often allow only one).
    public func watchLiveChannel(_ id: String, replacing previous: String? = nil) async throws -> LiveWatch {
        let data = try await send("POST", "livetv/channels/\(id)/watch", body: WatchBody(replaces: previous))
        return try decode(LiveWatch.self, from: data)
    }

    /// The player is still open, though paused (so not fetching anything).
    public func liveHeartbeat(viewer: String) async throws {
        _ = try await send("POST", "livetv/watch/\(viewer)/heartbeat")
    }

    public func stopLiveChannel(viewer: String) async throws {
        _ = try await send("DELETE", "livetv/watch/\(viewer)/")
    }

    public func setFavouriteChannel(_ id: String, _ favourite: Bool) async throws {
        _ = try await send(favourite ? "PUT" : "DELETE", "livetv/channels/\(id)/favourite")
    }
}
