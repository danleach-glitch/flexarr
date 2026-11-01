import FlexarrKit
import Foundation

/// When something in Coming soon comes, in a word: "Tonight", "Tomorrow", "Friday" within a week, otherwise
/// "Fri 7 Nov", as on the web.
enum UpcomingDay {
    static func label(_ date: Date, now: Date = .now, calendar: Calendar = .current) -> String {
        let days = calendar.dateComponents([.day], from: calendar.startOfDay(for: now), to: calendar.startOfDay(for: date)).day ?? 0
        switch days {
        case 0: return calendar.component(.hour, from: date) >= 17 ? "Tonight" : "Today"
        case 1: return "Tomorrow"
        case -1: return "Yesterday"
        case 2..<7: return date.formatted(.dateTime.weekday(.wide))
        default: return date.formatted(.dateTime.weekday(.abbreviated).day().month(.abbreviated))
        }
    }

    /// A day's heading on the Coming soon page: "Today", "Tomorrow", "Yesterday", otherwise "Fri 7 Nov".
    static func heading(_ day: Date, now: Date = .now, calendar: Calendar = .current) -> String {
        let days = calendar.dateComponents([.day], from: calendar.startOfDay(for: now), to: calendar.startOfDay(for: day)).day ?? 0
        switch days {
        case 0: return "Today"
        case 1: return "Tomorrow"
        case -1: return "Yesterday"
        default: return day.formatted(.dateTime.weekday(.abbreviated).day().month(.abbreviated))
        }
    }

    /// The card's second line: the episode ("S1E3 Title") or how a movie comes out ("In cinemas").
    static func detail(_ item: UpcomingItem) -> String {
        switch item.kind {
        case .episode: "\(code(item)) \(item.title)"
        default: item.when
        }
    }

    /// The card's title: the show or the film.
    static func title(_ item: UpcomingItem) -> String {
        item.seriesTitle ?? item.title
    }

    /// "S1E3", with no zero padding.
    static func code(_ item: UpcomingItem) -> String {
        "S\(item.seasonNumber ?? 0)E\(item.episodeNumber ?? 0)"
    }

    /// Ready to watch, or how far its download has got; nil otherwise.
    static func status(_ item: UpcomingItem) -> String? {
        if item.available == true { return "Ready to watch" }
        if let progress = item.download?.progress { return "Downloading \(Int((progress * 100).rounded()))%" }
        return nil
    }

    /// Opening it does something: it's in the library, or there's an overview to read.
    static func hasAction(_ item: UpcomingItem) -> Bool {
        item.libraryItemId != nil || !(item.overview ?? "").isEmpty
    }

    /// The Coming soon page: what's coming, a day at a time, and a show's episodes on the same day (a season dropping
    /// at once) as one entry, as on the web.
    struct Day: Identifiable {
        let date: Date
        let entries: [Entry]
        var id: Date { date }
    }

    struct Entry: Identifiable {
        let items: [UpcomingItem]
        var first: UpcomingItem { items[0] }
        var id: String { first.id }

        var title: String { UpcomingDay.title(first) }

        /// "S1E3 Title", "S2E1–E8, 8 episodes", or how a film comes out.
        var detail: String {
            guard items.count > 1, let last = items.last else { return UpcomingDay.detail(first) }
            let sameSeason = items.allSatisfy { $0.seasonNumber == first.seasonNumber }
            return sameSeason ? "\(UpcomingDay.code(first))–E\(last.episodeNumber ?? 0), \(items.count) episodes" : "\(items.count) episodes"
        }

        var status: String? {
            guard items.count > 1 else { return UpcomingDay.status(first) }
            if items.allSatisfy({ $0.available == true }) { return "Ready to watch" }
            return items.contains { $0.download != nil } ? "Downloading" : nil
        }
    }

    static func days(_ items: [UpcomingItem], calendar: Calendar = .current) -> [Day] {
        let byDay = Dictionary(grouping: items) { calendar.startOfDay(for: $0.date) }
        return byDay.keys.sorted().map { day in
            var order: [String] = []
            var groups: [String: [UpcomingItem]] = [:]
            for item in byDay[day]!.sorted(by: { $0.date < $1.date }) {
                let key = item.kind == .episode ? item.seriesTitle.map { "series:\($0)" } ?? item.id : item.id
                if groups[key] == nil { order.append(key) }
                groups[key, default: []].append(item)
            }
            return Day(date: day, entries: order.map { Entry(items: groups[$0]!) })
        }
    }
}
