import Foundation

/// The qualities a film or episode can be streamed at (docs/remote-streaming.md): what the Quality menu offers, and
/// the steps the player moves down after stalls and back up after smooth minutes, away from home.
public enum StreamQuality: String, Codable, Sendable, CaseIterable {
    /// The server decides: the original at home, and away from home whatever the measured speed carries.
    case automatic
    /// The file as it is, wherever the device is.
    case original
    case p1080
    case p720
    case p480

    /// The fixed levels, highest first: the same heights and bitrates as the server's ladder.
    public static let levels: [StreamQuality] = [.p1080, .p720, .p480]

    /// The level's height (nil for Automatic and Original).
    public var height: Int? {
        switch self {
        case .p1080: 1080
        case .p720: 720
        case .p480: 480
        case .automatic, .original: nil
        }
    }

    /// The level's bitrate in bits per second (nil for Automatic and Original).
    public var bitrate: Int? {
        switch self {
        case .p1080: 8_000_000
        case .p720: 4_000_000
        case .p480: 1_500_000
        case .automatic, .original: nil
        }
    }

    /// What the menu calls it.
    public var label: String {
        switch self {
        case .automatic: t(Words.automatic)
        case .original: t(Words.originalQuality)
        case .p1080: "1080p"
        case .p720: "720p"
        case .p480: "480p"
        }
    }

    /// A fixed level, as opposed to Automatic or Original.
    public var isLevel: Bool { height != nil }

    /// Asks for this quality in `request`: a level's height and bitrate, Original's `original`, or nothing (Automatic).
    public func apply(to request: inout PlaybackRequest) {
        request.maxHeight = height
        request.maxBitrate = bitrate
        request.original = self == .original ? true : nil
    }

    /// The height a picture of `width` × `height` counts as: a film wider than 16:9 (1920 × 800) is still 1080p.
    public static func effectiveHeight(width: Double, height: Double) -> Int {
        guard width > 0, height > 0 else { return 0 }
        return Int(max(height, width * 9 / 16).rounded())
    }

    /// The first level below what's playing: `playing` (the level asked for, if one was) or the height of the stream
    /// on screen; 720p while that isn't known (the server's own choice with no reading). Nil when there's none lower.
    public static func stepDown(from playing: StreamQuality, playingHeight: Int?) -> StreamQuality? {
        guard let height = playing.height ?? playingHeight.flatMap({ $0 > 0 ? $0 : nil }) else { return .p720 }
        // A little under a level's height (a stream of 1072 lines, say) counts as that level.
        return levels.first { ($0.height ?? 0) < height - 16 }
    }

    /// Where a stream asked for at `asked` goes when it can't keep up and there's no level below it: the lowest level
    /// (480p, picked in the menu or stepped down to) back to Automatic, whose ladder goes on down to 360p for a link that
    /// slow (docs/remote-streaming.md). Nil for any other quality.
    public static func fallBack(from asked: StreamQuality) -> StreamQuality? {
        asked == levels.last ? .automatic : nil
    }

    /// How long a stream at the lowest level may show no picture, away from home, before it falls back to Automatic.
    public static let noPictureFallBackAfter: TimeInterval = 15

    /// The next quality up from the lowered `level`: the level above, or Automatic (the server's choice, the original
    /// when the speed carries it) once the level above would be as tall as `originalHeight`, the stream the title first
    /// played at. Nil from Automatic or Original.
    public static func stepUp(from level: StreamQuality, originalHeight: Int?) -> StreamQuality? {
        guard let index = levels.firstIndex(of: level) else { return nil }
        guard index > 0 else { return .automatic }
        let above = levels[index - 1]
        if let originalHeight, (above.height ?? 0) >= originalHeight - 16 { return .automatic }
        return above
    }

    /// The share of the speed a stream may take, and how much above its average its busy stretches run.
    public static let headroom = 0.75
    public static let peak = 1.5

    /// Whether `bitrate` fits comfortably in `speed` (both bits per second): its busy stretches within three quarters
    /// of the speed.
    public static func fits(bitrate: Int, speed: Int) -> Bool {
        Double(bitrate) * peak <= Double(speed) * headroom
    }

    // MARK: The menu

    /// What a picture `height` lines tall is called: "4K", a level's name within a few lines of it (a stream of 1072
    /// lines is 1080p), else its own "540p".
    public static func label(forHeight height: Int) -> String {
        if height >= 1800 { return "4K" }
        if let level = levels.first(where: { abs(($0.height ?? 0) - height) <= 16 }) { return level.label }
        return "\(height)p"
    }

    /// The height a file of the server's resolution label ("4K", "1080p", "720p", "SD") counts as.
    public static func height(resolutionLabel label: String?) -> Int? {
        switch label {
        case "4K": 2160
        case "1080p": 1080
        case "720p": 720
        case "SD": 480
        default: nil
        }
    }

    /// The menu's rows: Automatic, Original, then the levels below the file's own height (all of them while that
    /// isn't known). A level asked for that the file doesn't reach stays, so the tick is never lost.
    public static func choices(fileHeight: Int?, selected: StreamQuality) -> [StreamQuality] {
        let levels = levels.filter { level in
            guard let fileHeight, fileHeight > 0, let height = level.height else { return true }
            return height < fileHeight - 16 || level == selected
        }
        return [.automatic, .original] + levels
    }

    // MARK: The remembered choice

    static let memoryKey = "io.github.flexarr.streamQuality.away"

    /// The quality chosen in the menu on this device, for plays away from home (Automatic until one is chosen).
    public static func remembered(in memory: UserDefaults = .standard) -> StreamQuality {
        memory.string(forKey: memoryKey).flatMap(StreamQuality.init(rawValue:)) ?? .automatic
    }

    /// Keeps `quality` for plays away from home on this device.
    public static func remember(_ quality: StreamQuality, in memory: UserDefaults = .standard) {
        memory.set(quality.rawValue, forKey: memoryKey)
    }

    /// The quality a play starts on: the remembered choice away from home, and always Automatic at home.
    public static func forStart(awayFromHome: Bool, in memory: UserDefaults = .standard) -> StreamQuality {
        awayFromHome ? remembered(in: memory) : .automatic
    }
}

/// Watches a stream on Automatic for when to step its quality down or back up (docs/remote-streaming.md). Down: two
/// stalls within a minute, or one lasting over 10 seconds, not counting the first 3 seconds after a start or seek. Up:
/// 5 minutes without a stall, at most once every 5 minutes (the speed is the caller's to check, with
/// `StreamQuality.fits`). Pure: it's told what happens and when.
public struct QualityWatch: Sendable {
    public static let stallWindow: TimeInterval = 60
    public static let stallsToStep = 2
    public static let longStall: TimeInterval = 10
    public static let grace: TimeInterval = 3
    public static let smoothFor: TimeInterval = 5 * 60
    public static let stepUpEvery: TimeInterval = 5 * 60

    /// When the stream last started or was sought in: stalls in the next few seconds don't count.
    private var startedAt: Date
    /// When the current level began (a start, or a step).
    private var levelAt: Date
    private var stalls: [Date] = []
    private var stallBegan: Date?
    private var lastStall: Date?
    private var lastStepUp: Date?

    public init(at now: Date = Date()) {
        startedAt = now
        levelAt = now
    }

    /// The stream (re)started or was sought in.
    public mutating func started(at now: Date = Date()) {
        startedAt = now
        stallBegan = nil
    }

    /// A new level is playing (a step down or up): the stalls before it are forgotten.
    public mutating func stepped(up: Bool, at now: Date = Date()) {
        levelAt = now
        startedAt = now
        stalls = []
        stallBegan = nil
        if up { lastStepUp = now }
    }

    /// The picture stopped to wait for the stream. True when that's reason to step down now.
    public mutating func stalled(at now: Date = Date()) -> Bool {
        guard stallBegan == nil else { return shouldStepDown(at: now) }
        stallBegan = now
        lastStall = now
        if now.timeIntervalSince(startedAt) >= Self.grace {
            stalls = stalls.filter { now.timeIntervalSince($0) < Self.stallWindow } + [now]
        }
        return shouldStepDown(at: now)
    }

    /// Playing again after a stall.
    public mutating func resumed(at now: Date = Date()) {
        if stallBegan != nil { lastStall = now }
        stallBegan = nil
    }

    /// Whether the stream stalls enough to step down: two counted stalls within a minute, or one going on for over 10
    /// seconds (counted from the end of the grace after a start or seek).
    public func shouldStepDown(at now: Date = Date()) -> Bool {
        if stalls.filter({ now.timeIntervalSince($0) < Self.stallWindow }).count >= Self.stallsToStep { return true }
        guard let stallBegan else { return false }
        let from = max(stallBegan, startedAt.addingTimeInterval(Self.grace))
        return now.timeIntervalSince(from) > Self.longStall
    }

    /// Whether it's time to try the next quality up: no stall for 5 minutes on this level, playing now, and no step up
    /// in the last 5 minutes.
    public func mayStepUp(at now: Date = Date()) -> Bool {
        guard stallBegan == nil else { return false }
        let calmSince = max(levelAt, lastStall ?? levelAt)
        if now.timeIntervalSince(calmSince) < Self.smoothFor { return false }
        if let lastStepUp, now.timeIntervalSince(lastStepUp) < Self.stepUpEvery { return false }
        return true
    }
}

/// What the player's Quality menu shows (docs/design.md, Words › the Quality menu): the rows, the one ticked, and
/// the quiet second lines.
public struct QualityMenu: Equatable, Sendable {
    /// The quality asked for: the one ticked.
    public var selected: StreamQuality
    /// The rows, in order.
    public var choices: [StreamQuality]
    /// What's playing now ("720p", "Original"): Automatic's second line.
    public var playing: String?
    /// The file's picture ("4K HDR", "1080p"): Original's second line.
    public var file: String?

    public init(selected: StreamQuality, fileHeight: Int?, playing: String?, file: String?) {
        self.selected = selected
        choices = StreamQuality.choices(fileHeight: fileHeight, selected: selected)
        self.playing = playing
        self.file = file
    }

    /// A row's second line: what Automatic plays now, the file Original plays.
    public func detail(for choice: StreamQuality) -> String? {
        switch choice {
        case .automatic: playing
        case .original: file
        default: nil
        }
    }

    /// The button's badge: the choice, unless it's Automatic.
    public var badge: String? { selected == .automatic ? nil : selected.label }

    /// What a screen reader hears after "Quality": the choice, then what's playing when that says more ("Automatic,
    /// 720p").
    public var spokenValue: String {
        guard let playing, playing != selected.label else { return selected.label }
        return [selected.label, playing].joined(separator: ", ")
    }
}

extension MediaFile {
    /// The file's picture for the Quality menu: "4K HDR", "1080p". Nil when the file wasn't probed.
    public var qualityLabel: String? {
        guard let resolution = resolutionLabel else { return nil }
        let hdr = hdr10Plus == true || [.hdr10, .hlg, .dolbyVision].contains(videoRange)
        return hdr ? [resolution, "HDR"].joined(separator: " ") : resolution
    }

    /// The height the file counts as (a film wider than 16:9 by its width). Nil when it wasn't probed.
    public var effectiveHeight: Int? {
        guard let width, let height, width > 0, height > 0 else { return nil }
        return StreamQuality.effectiveHeight(width: Double(width), height: Double(height))
    }
}
