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

    /// The card's second line: the episode ("S01E03 Title"), a book's author, or how a film comes out ("In cinemas").
    static func detail(_ item: UpcomingItem) -> String {
        switch item.kind {
        case .episode: String(format: "S%02dE%02d ", item.seasonNumber ?? 0, item.episodeNumber ?? 0) + item.title
        case .audiobook: item.seriesTitle ?? item.when
        default: item.when
        }
    }

    /// The card's title: the show, the film, or the book.
    static func title(_ item: UpcomingItem) -> String {
        item.kind == .audiobook ? item.title : item.seriesTitle ?? item.title
    }
}
