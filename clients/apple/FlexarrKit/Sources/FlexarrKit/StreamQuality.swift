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
