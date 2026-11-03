import Foundation

// UI-agnostic presentation helpers shared by every Apple client. Wording follows
// docs/design.md: plain words ("Resume from 32:10"), no middle-dot meta strings.

public enum Format {
    /// "1h 56m", "47m", "2h".
    public static func runtime(minutes: Int) -> String? {
        guard minutes > 0 else { return nil }
        let h = minutes / 60, m = minutes % 60
        if h == 0 { return "\(m)m" }
        return m == 0 ? "\(h)h" : "\(h)h \(m)m"
    }

    public static func runtime(seconds: Double) -> String? {
        runtime(minutes: Int((seconds / 60).rounded()))
    }

    /// Player-style timestamp: "32:10", "1:02:05".
    public static func timestamp(_ seconds: Double) -> String {
        let total = max(0, Int(seconds))
        let h = total / 3600, m = (total % 3600) / 60, s = total % 60
        return h > 0 ? String(format: "%d:%02d:%02d", h, m, s) : String(format: "%d:%02d", m, s)
    }

    /// "42m left", "1h 3m left".
    public static func remaining(_ seconds: Double) -> String? {
        guard seconds >= 60 else { return nil }
        return runtime(seconds: seconds).map { "\($0) left" }
    }
}

extension ItemSummary {
    /// Duration in seconds from the probed file, falling back to metadata runtime.
    public var effectiveDuration: Double? {
        if let durationSeconds, durationSeconds > 0 { return durationSeconds }
        if let runtimeMinutes, runtimeMinutes > 0 { return Double(runtimeMinutes * 60) }
        return nil
    }

    /// Fraction watched (0...1) when the item has a resume point, otherwise nil.
    public var progress: Double? {
        guard !userData.watched, userData.positionSeconds > 0, let duration = effectiveDuration, duration > 0 else { return nil }
        return min(1, max(0, userData.positionSeconds / duration))
    }

    public var isResumable: Bool { progress != nil }

    /// "S2E3", "S1E1–E2", or nil for non-episodes and home videos (which go by their names).
    public var episodeCode: String? {
        guard kind == .episode, homeVideo != true, let episodeNumber else { return nil }
        let season = seasonNumber.map { "S\($0)" } ?? ""
        if let end = episodeNumberEnd, end > episodeNumber { return "\(season)E\(episodeNumber)–E\(end)" }
        return "\(season)E\(episodeNumber)"
    }

    /// The headline for cards and the player: the series name for episodes, else the title.
    public var displayTitle: String {
        kind == .episode ? (seriesTitle ?? title) : title
    }

    /// The second line for episodes: "S2 E3  Bit by a Dead Bee".
    public var episodeSubtitle: String? {
        guard kind == .episode else { return nil }
        return [episodeCode, title].compactMap { $0 }.joined(separator: "  ")
    }

    public var releaseYear: Int? {
        if let year { return year }
        if let releaseDate, let y = Int(releaseDate.prefix(4)) { return y }
        if let airDate, let y = Int(airDate.prefix(4)) { return y }
        return nil
    }

    /// IMDb and Rotten Tomatoes, shown beside their marks after the facts (docs/design.md › Scores). Older servers
    /// only send the critics' number, whose tomato then follows the score.
    public var scores: Scores {
        Scores(imdb: imdbRating, rottenTomatoes: rottenTomatoes
            ?? criticRating.map { RottenTomatoes(score: Int($0.rounded()), state: .unknown) })
    }

    /// Short facts for heroes and detail headers: year, runtime or seasons, rating, a plain score, resolution.
    public var facts: [String] { facts(scores: true) }

    /// The facts, leaving out the plain score when `scores` is false (for a kids profile, where it means nothing) or
    /// when the title has an IMDb score to show beside its mark instead (the plain one is usually IMDb's).
    public func facts(scores: Bool) -> [String] {
        var facts: [String] = []
        if let releaseYear { facts.append(String(releaseYear)) }
        switch kind {
        case .series:
            if childCount > 0 {
                let noun = homeVideo == true ? "folder" : "season"
                facts.append(childCount == 1 ? "1 \(noun)" : "\(childCount) \(noun)s")
            }
        case .season:
            if childCount > 0 {
                let noun = homeVideo == true ? "video" : "episode"
                facts.append(childCount == 1 ? "1 \(noun)" : "\(childCount) \(noun)s")
            }
        case .artist:
            if childCount > 0 { facts.append(childCount == 1 ? "1 album" : "\(childCount) albums") }
        case .album:
            if childCount > 0 { facts.append(childCount == 1 ? "1 track" : "\(childCount) tracks") }
        default:
            if let runtime = runtimeMinutes.flatMap(Format.runtime(minutes:)) ?? durationSeconds.flatMap(Format.runtime(seconds:)) {
                facts.append(runtime)
            }
        }
        if let contentRating, !contentRating.isEmpty { facts.append(contentRating) }
        if scores, self.scores.imdb == nil, let communityRating, communityRating > 0 { facts.append(String(format: "%.1f", communityRating)) }
        if let resolution, !resolution.isEmpty { facts.append(resolution) }
        return facts
    }
}

// MARK: - Music

extension ItemSummary {
    /// Who to name as the artist of a track or album: the track's own artist when it has one (a compilation's tracks,
    /// a featured artist), otherwise the album's, as the server sends it alongside the title. A podcast episode's is
    /// its show.
    public var artistName: String? { kind.isMusic ? trackArtist ?? seriesTitle : kind == .podcastEpisode ? seriesTitle : nil }

    /// "46m" for an album's tracks played end to end, or nil when none has a length.
    public static func totalRuntime(of tracks: [ItemSummary]) -> String? {
        let total = tracks.reduce(0.0) { $0 + ($1.durationSeconds ?? 0) }
        return total > 0 ? Format.runtime(seconds: total) : nil
    }

    /// Tracks in the order they play, grouped by disc. One group when the album is a single disc (or no disc is numbered).
    public static func discs(of tracks: [ItemSummary]) -> [(disc: Int, tracks: [ItemSummary])] {
        var groups: [Int: [ItemSummary]] = [:]
        for track in tracks { groups[track.discNumber ?? 1, default: []].append(track) }
        return groups.keys.sorted().map { (disc: $0, tracks: groups[$0]!) }
    }
}

extension Lyrics {
    /// The line being sung `time` seconds in: the last whose time has come, or nil before the first (and always for
    /// lyrics that aren't synced).
    public func activeLine(at time: Double) -> Int? {
        guard synced else { return nil }
        var active: Int?
        for (index, line) in lines.enumerated() {
            guard let at = line.time else { continue }
            if at <= time { active = index } else { break }
        }
        return active
    }
}

// MARK: - Ambient colour

/// An sRGB colour parsed from the server's "#rrggbb" `accentColor`.
public struct RGB: Sendable, Hashable {
    public var red: Double
    public var green: Double
    public var blue: Double

    public init(red: Double, green: Double, blue: Double) {
        self.red = red
        self.green = green
        self.blue = blue
    }

    public init?(hex: String?) {
        guard var text = hex?.trimmingCharacters(in: .whitespaces), !text.isEmpty else { return nil }
        if text.hasPrefix("#") { text.removeFirst() }
        if text.count == 3 { text = text.map { "\($0)\($0)" }.joined() }
        guard text.count == 6, let value = UInt32(text, radix: 16) else { return nil }
        red = Double((value >> 16) & 0xFF) / 255
        green = Double((value >> 8) & 0xFF) / 255
        blue = Double(value & 0xFF) / 255
    }

    /// Fallback accent when a title has no ambient colour: `--silver` #A9C2EC.
    public static let silver = RGB(hex: "#A9C2EC")!

    /// Relative luminance (WCAG), 0...1.
    public var luminance: Double {
        func channel(_ c: Double) -> Double { c <= 0.03928 ? c / 12.92 : pow((c + 0.055) / 1.055, 2.4) }
        return 0.2126 * channel(red) + 0.7152 * channel(green) + 0.0722 * channel(blue)
    }

    /// Mixes towards `other` by `amount` (0 keeps self, 1 returns other).
    public func mixed(with other: RGB, amount: Double) -> RGB {
        let t = min(1, max(0, amount))
        return RGB(red: red + (other.red - red) * t, green: green + (other.green - green) * t, blue: blue + (other.blue - blue) * t)
    }

    /// A version bright enough to read as a glow on the dark room background. Very dark
    /// artwork colours (navy, near-black) are lifted towards white so they still show.
    public var glow: RGB {
        let white = RGB(red: 1, green: 1, blue: 1)
        var colour = self
        var steps = 0
        while colour.luminance < 0.12 && steps < 10 {
            colour = colour.mixed(with: white, amount: 0.15)
            steps += 1
        }
        return colour
    }
}

extension ItemSummary {
    public var ambient: RGB { RGB(hex: accentColor) ?? .silver }
}

// MARK: - Playback timeline

/// What the player should offer at a given moment, derived from the server's markers.
public enum PlaybackPrompt: Equatable, Sendable {
    case none
    /// Inside an intro or recap: offer to jump to `to`.
    case skip(MarkerType, to: Double)
    /// Time to offer the next episode (see `NextEpisodeTiming`).
    case nextEpisode
}

/// When the player offers the next episode, counts down to it, plays it or closes (docs/next-episode-timing.md). The
/// countdown follows the playing position, so it holds when paused and goes when scrubbed back before it.
public struct NextEpisodeTiming: Equatable, Sendable {
    /// Next Episode shows from here.
    public var showAt: Double
    /// With auto-play on: the countdown runs from here to `nextAt`, where the next episode plays.
    public var countdownFrom: Double?
    public var nextAt: Double?
    /// Skip credits with auto-play off: the player closes here, as the credits start.
    public var closeAt: Double?

    /// The countdown's length, in seconds.
    public static let countdownSeconds: Double = 10
    /// With Skip credits and auto-play on, the countdown starts this long before the credits (so the next episode
    /// plays two seconds into them).
    public static let skipCreditsLead: Double = 8
    /// With no credits found, Next Episode shows this long before the end.
    public static let unmarkedLead: Double = 60

    public init(showAt: Double, countdownFrom: Double? = nil, nextAt: Double? = nil, closeAt: Double? = nil) {
        self.showAt = showAt
        self.countdownFrom = countdownFrom
        self.nextAt = nextAt
        self.closeAt = closeAt
    }

    /// Whole seconds left on the countdown at `time` (0...10), or nil when it isn't running.
    public func countdown(at time: Double) -> Int? {
        guard let countdownFrom, let nextAt, time >= countdownFrom else { return nil }
        return Int(min(Self.countdownSeconds, max(0, (nextAt - time).rounded(.up))))
    }

    /// The timing for an episode with a next one; nil offers no button before the end (an unknown length, or no credits
    /// found in an episode of two minutes or less).
    public static func of(markers: [Marker], duration: Double, skipCredits: Bool, autoPlayNext: Bool) -> NextEpisodeTiming? {
        guard duration > 0 else { return nil }
        if let credits = markers.first(where: { $0.type == .credits })?.start {
            switch (skipCredits, autoPlayNext) {
            case (true, true):
                let from = max(0, credits - skipCreditsLead)
                let next = credits + countdownSeconds - skipCreditsLead
                return NextEpisodeTiming(showAt: from, countdownFrom: from, nextAt: min(next, duration))
            case (false, true):
                // Credits inside the last 10 seconds: the countdown (and so the button) starts first.
                let from = max(0, duration - countdownSeconds)
                return NextEpisodeTiming(showAt: min(credits, from), countdownFrom: from, nextAt: duration)
            case (true, false):
                return NextEpisodeTiming(showAt: credits, closeAt: credits)
            case (false, false):
                return NextEpisodeTiming(showAt: credits)
            }
        }
        guard duration > 120 else { return nil }
        let show = duration - unmarkedLead
        return autoPlayNext ? NextEpisodeTiming(showAt: show, countdownFrom: duration - countdownSeconds, nextAt: duration) : NextEpisodeTiming(showAt: show)
    }
}

extension PlaybackInfo {
    public func nextEpisodeTiming(_ preferences: PlaybackPreferences) -> NextEpisodeTiming? {
        guard next != nil else { return nil }
        return NextEpisodeTiming.of(markers: markers, duration: durationSeconds, skipCredits: preferences.skipCredits, autoPlayNext: preferences.autoPlayNext)
    }

    public func prompt(at time: Double, preferences: PlaybackPreferences) -> PlaybackPrompt {
        Self.prompt(at: time, markers: markers, nextAt: nextEpisodeTiming(preferences)?.showAt)
    }

    /// Pure version of `prompt(at:preferences:)` so it can be tested without a full PlaybackInfo. `nextAt` is when Next
    /// Episode shows (`NextEpisodeTiming.showAt`), nil for never.
    public static func prompt(at time: Double, markers: [Marker], nextAt: Double?) -> PlaybackPrompt {
        for marker in markers where marker.type == .intro || marker.type == .recap {
            // Stop offering the skip in the last second so it doesn't flash as the marker ends.
            if time >= marker.start && time < marker.end - 1 {
                return .skip(marker.type, to: marker.end)
            }
        }
        if let nextAt, time >= nextAt { return .nextEpisode }
        return .none
    }
}

// MARK: - Subtitles

/// A subtitle choice as the player lists it: its name, language and whether it's forced-only.
public struct SubtitleOption: Sendable, Equatable {
    public var name: String
    public var language: String?
    public var isForced: Bool

    public init(name: String, language: String?, isForced: Bool) {
        self.name = name
        self.language = language
        self.isForced = isForced
    }
}

public enum SubtitleMatch {
    /// Which of the player's `options` is the server's subtitle `key`, or nil for none (no subtitle, one burned into
    /// the picture, or one the player doesn't list). Matched by the name the server gave it in the playlist, then by
    /// language and forced-ness, then by language.
    public static func option(for key: String?, in info: PlaybackInfo, among options: [SubtitleOption]) -> Int? {
        guard !info.subtitleBurnedIn, let key, let track = info.subtitles.first(where: { $0.key == key }), track.isText else { return nil }
        return option(for: track, among: options)
    }

    public static func option(for track: SubtitleTrack, among options: [SubtitleOption]) -> Int? {
        if let index = options.firstIndex(where: { $0.name == track.label }) { return index }
        let language = normalized(track.language)
        guard language != nil else { return nil }
        return options.firstIndex { normalized($0.language) == language && $0.isForced == track.isForced }
            ?? options.firstIndex { normalized($0.language) == language }
    }

    /// The server's key for the player's `option`, the reverse of `option(for:in:among:)`.
    public static func key(for option: SubtitleOption, in info: PlaybackInfo) -> String? {
        let text = info.subtitles.filter(\.isText)
        if let track = text.first(where: { $0.label == option.name }) { return track.key }
        let language = normalized(option.language)
        guard language != nil else { return nil }
        return (text.first { normalized($0.language) == language && $0.isForced == option.isForced }
            ?? text.first { normalized($0.language) == language })?.key
    }

    /// "eng", "en" and "en-GB" all compare as "en".
    static func normalized(_ language: String?) -> String? {
        guard let language, !language.isEmpty, language != "und" else { return nil }
        let code = Locale.Language(identifier: language).languageCode
        return code?.identifier(.alpha2) ?? code?.identifier ?? language.lowercased()
    }
}

/// What Home says when it has nothing to show: no library yet, a first scan under way, or libraries whose folders
/// hold nothing Flexarr recognised. Libraries are added and connected in a browser, so the wording points there.
public struct EmptyHomeMessage: Sendable, Hashable {
    public let title: String
    public let body: String
    /// The folders looked in, when the libraries are there but empty.
    public let folders: [String]

    /// Nil while some library has something in it; podcasts come built in, so they don't count. For a profile with an
    /// age limit, named by `limitedProfile`, it's one line: libraries and folders are a grown-up's business.
    public init?(libraries: [Library], limitedProfile: String? = nil) {
        let media = libraries.filter { $0.type != .podcasts }
        guard media.allSatisfy({ $0.itemCount == 0 }) else { return nil }
        if let limitedProfile {
            title = "Nothing here for \(limitedProfile) yet"
            body = ""
            folders = []
            return
        }
        guard !media.isEmpty else {
            title = "Nothing to watch yet"
            body = "Open Flexarr in a browser and add a library in Settings › Libraries."
            folders = []
            return
        }
        let names = Self.list(media.map(\.name))
        let wanted = Self.list(media.map { Self.kind($0.type) }.reduce(into: []) { kinds, kind in
            if !kinds.contains(kind) { kinds.append(kind) }
        })
        let scanning = media.compactMap(\.scan)
        if !scanning.isEmpty {
            let total = scanning.reduce(0) { $0 + $1.total }, done = scanning.reduce(0) { $0 + $1.processed }
            title = "Looking through your library"
            body = "Flexarr is reading the files in \(names)\(total > 0 ? " (\(done) of \(total))" : ""). Your \(wanted) appear here as they’re recognised."
            folders = []
            return
        }
        folders = media.flatMap(\.folders)
        title = "Your library is empty"
        body = "Add files to \(folders.count > 1 ? "your library folders" : "your library folder"), or connect Sonarr or Radarr in Flexarr on the web."
    }

    private static func kind(_ type: LibraryType) -> String {
        switch type {
        case .movies: "movies"
        case .tvShows: "shows"
        case .music: "music"
        case .podcasts: "podcasts"
        case .homeVideos: "home videos"
        case .audiobooks: "audiobooks"
        case .unknown: "titles"
        }
    }

    /// "Films", "Films and TV", "Films, TV and Music".
    private static func list(_ names: [String]) -> String {
        guard let last = names.last else { return "" }
        return names.count == 1 ? last : "\(names.dropLast().joined(separator: ", ")) and \(last)"
    }
}

// MARK: - Scores

/// A title's two scores (docs/design.md › Scores): IMDb out of 10 and Rotten Tomatoes critics with its tomato.
public struct Scores: Sendable, Hashable {
    public let imdb: Double?
    public let rottenTomatoes: RottenTomatoes?

    public init(imdb: Double?, rottenTomatoes: RottenTomatoes?) {
        self.imdb = imdb
        // The tomato follows the score when the server didn't say (or said something this app doesn't know).
        self.rottenTomatoes = rottenTomatoes.map {
            $0.state == .unknown ? RottenTomatoes(score: $0.score, state: $0.score >= 60 ? .fresh : .rotten) : $0
        }
    }

    public var isEmpty: Bool { imdb == nil && rottenTomatoes == nil }

    /// "7.8", as beside IMDb's mark.
    public var imdbText: String? { imdb.map { String(format: "%.1f", $0) } }

    /// For VoiceOver: "IMDb 7.8 out of 10".
    public var imdbLabel: String? { imdbText.map { "IMDb \($0) out of 10" } }

    /// For VoiceOver: "Rotten Tomatoes 92%, Certified Fresh".
    public var rottenTomatoesLabel: String? {
        rottenTomatoes.map { "Rotten Tomatoes \($0.score)%, \($0.state.words)" }
    }
}

extension RottenTomatoesState {
    /// What Rotten Tomatoes calls it.
    public var words: String {
        switch self {
        case .certifiedFresh: "Certified Fresh"
        case .fresh: "Fresh"
        case .rotten: "Rotten"
        case .unknown: "Rotten Tomatoes"
        }
    }
}
