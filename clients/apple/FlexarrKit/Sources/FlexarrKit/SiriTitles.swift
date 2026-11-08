import Foundation

/// Which titles Siri is taught by name on the iPhone, iPad and Mac. Each name is matched against every spoken phrase,
/// and teaching the system hundreds of them made its `linkd` swell (on the Apple TV, to hundreds of MB), so only a
/// short list goes: followed podcasts, then films and shows in progress, then the newest added. Any other title is
/// still played with "Play something in Flexarr", which asks for it.
public enum SiriTitles {
    /// About how many names, podcasts included.
    public static let cap = 150

    /// A film, show or podcast by name.
    public struct Title: Equatable, Sendable {
        public let id: String
        public let title: String
        public let year: Int?
        public let kind: MediaKind

        public init(id: String, title: String, year: Int?, kind: MediaKind) {
            self.id = id
            self.title = title
            self.year = year
            self.kind = kind
        }
    }

    /// `podcasts` first (they count towards the cap), then what's in progress (`inProgress`, Home's Continue watching:
    /// a film as itself, an episode as its show), then `added` newest first, films and shows together, each once.
    public static func pick(podcasts: [Title], inProgress: [ItemSummary], added: [ItemSummary], cap: Int = cap) -> [Title] {
        var picked = Array(podcasts.prefix(cap))
        var seen = Set(picked.map(\.id))
        func add(_ title: Title) {
            guard picked.count < cap, seen.insert(title.id).inserted else { return }
            picked.append(title)
        }
        for item in inProgress {
            switch item.kind {
            case .movie, .series:
                add(Title(id: item.id, title: item.title, year: item.releaseYear, kind: item.kind))
            case .episode, .season:
                if let show = item.seriesId, let name = item.seriesTitle { add(Title(id: show, title: name, year: nil, kind: .series)) }
            default:
                break
            }
        }
        for item in added.sorted(by: { $0.addedAt > $1.addedAt }) where item.kind == .movie || item.kind == .series {
            add(Title(id: item.id, title: item.title, year: item.releaseYear, kind: item.kind))
        }
        return picked
    }
}
