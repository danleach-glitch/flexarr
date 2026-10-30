import FlexarrKit
import SwiftUI

/// A 2:3 poster with the title and year beneath it (text never sits on artwork).
struct PosterCard: View {
    @Environment(Router.self) private var router
    let item: ItemSummary
    var width: CGFloat?
    /// Called as the card opens its title (search notes the pick).
    var onOpen: (() -> Void)?

    var body: some View {
        Button {
            onOpen?()
            router.push(.item(item))
        } label: {
            VStack(alignment: .leading, spacing: 6) {
                PosterArtwork(item: item)
                VStack(alignment: .leading, spacing: 1) {
                    Text(item.title)
                        .font(.footnote.weight(.semibold))
                        .foregroundStyle(Color.screen.opacity(0.9))
                    if let subtitle {
                        Text(subtitle)
                            .font(.caption)
                            .foregroundStyle(Color.usher)
                    }
                }
                .lineLimit(1)
            }
            .frame(width: width)
            .contentShape(Rectangle())
        }
        .buttonStyle(CardButtonStyle())
        .contextMenu { ItemContextMenu(item: item) }
    }

    private var subtitle: String? {
        if item.kind == .series, let count = item.userData.unwatchedCount, count > 0 {
            return [item.releaseYear.map(String.init), "\(count) new"].compactMap { $0 }.joined(separator: "  ")
        }
        return item.releaseYear.map(String.init)
    }
}

/// Poster artwork with a title placeholder, watched tick and progress bar. Fills the width it's given.
struct PosterArtwork: View {
    @Environment(AppModel.self) private var model
    let item: ItemSummary

    var body: some View {
        Color.seat
            // Album covers and artist pictures are square; everything else is a 2:3 poster.
            .aspectRatio(item.kind.isMusic ? 1 : 2 / 3, contentMode: .fit)
            .overlay {
                RemoteImage(url: model.client?.imageURL(item.artwork.poster, width: 342)) {
                    ZStack {
                        LinearGradient(colors: [item.glowColor.opacity(0.35), Color.seat], startPoint: .top, endPoint: .bottom)
                        Text(item.title)
                            .font(.marquee(18, weight: .bold))
                            .foregroundStyle(Color.screen.opacity(0.8))
                            .multilineTextAlignment(.center)
                            .padding(8)
                    }
                }
            }
            .overlay(alignment: .bottom) {
                if let progress = item.progress {
                    ProgressBar(value: progress).padding(6)
                }
            }
            .overlay(alignment: .topTrailing) {
                if item.userData.watched { WatchedBadge().padding(6) }
            }
            .clipShape(RoundedRectangle(cornerRadius: 6, style: .continuous))
    }
}

/// 16:9 card for Continue Watching: tapping plays, with how much is left beneath it.
struct WideCard: View {
    @Environment(AppModel.self) private var model
    let item: ItemSummary
    let width: CGFloat
    var action: (() -> Void)?

    var body: some View {
        Button { action.map { $0() } ?? model.play(item) } label: {
            VStack(alignment: .leading, spacing: 6) {
                Color.seat
                    .frame(width: width, height: width * 9 / 16)
                    .overlay {
                        RemoteImage(url: model.client?.imageURL(stillPath, width: 640)) {
                            LinearGradient(colors: [item.glowColor.opacity(0.4), Color.seat], startPoint: .topLeading, endPoint: .bottomTrailing)
                        }
                    }
                    .overlay {
                        Image(systemName: "play.fill")
                            .font(.title3)
                            .foregroundStyle(Color.screen)
                            .frame(width: 44, height: 44)
                            .background(Circle().fill(.black.opacity(0.45)))
                    }
                    .overlay(alignment: .bottom) {
                        if let progress = item.progress {
                            ProgressBar(value: progress).padding(8)
                        }
                    }
                    .clipShape(RoundedRectangle(cornerRadius: 6, style: .continuous))
                VStack(alignment: .leading, spacing: 1) {
                    Text(item.displayTitle)
                        .font(.footnote.weight(.semibold))
                        .foregroundStyle(Color.screen.opacity(0.9))
                    Text(secondLine)
                        .font(.caption)
                        .foregroundStyle(Color.usher)
                }
                .lineLimit(1)
                .frame(width: width, alignment: .leading)
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(CardButtonStyle())
        .contextMenu { ItemContextMenu(item: item, showsDetails: true) }
    }

    private var stillPath: String? {
        item.kind == .episode ? (item.artwork.thumb ?? item.artwork.backdrop) : (item.artwork.backdrop ?? item.artwork.thumb)
    }

    private var secondLine: String {
        let remaining = item.progress != nil
            ? item.effectiveDuration.flatMap { Format.remaining($0 - item.userData.positionSeconds) }
            : nil
        return [item.episodeSubtitle, remaining].compactMap { $0 }.joined(separator: "   ")
    }
}

/// Top 10: a big outlined numeral tucked behind the poster.
struct Top10Card: View {
    let rank: Int
    let item: ItemSummary
    let width: CGFloat

    var body: some View {
        HStack(alignment: .bottom, spacing: -14) {
            OutlinedNumeral(text: String(rank), size: width * 1.15)
            PosterCard(item: item, width: width)
        }
    }
}

struct OutlinedNumeral: View {
    let text: String
    let size: CGFloat

    var body: some View {
        let glyph = Text(text).font(.marquee(size, weight: .black)).kerning(-size * 0.04)
        ZStack {
            // An outline drawn by stamping the glyph around a ring, then knocking out the middle.
            ForEach(0..<12, id: \.self) { index in
                let angle = Double(index) / 12 * 2 * .pi
                glyph.foregroundStyle(Color.usher.opacity(0.9))
                    .offset(x: cos(angle) * 1.5, y: sin(angle) * 1.5)
            }
            glyph.foregroundStyle(Color.room)
        }
        .fixedSize()
        .padding(.bottom, 24)
        .accessibilityHidden(true)
    }
}

struct WatchedBadge: View {
    var body: some View {
        Image(systemName: "checkmark")
            .font(.system(size: 10, weight: .heavy))
            .foregroundStyle(Color.room)
            .frame(width: 20, height: 20)
            .background(Circle().fill(Color.screen.opacity(0.92)))
            .accessibilityLabel("Watched")
    }
}

/// Round headshot with name and character, for cast rows and people results.
struct PersonCard: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    let id: String
    let name: String
    let detail: String?
    let image: String?
    /// Called as the card opens the person (search notes the pick).
    var onOpen: (() -> Void)?

    var body: some View {
        Button {
            onOpen?()
            router.push(.person(id: id, name: name))
        } label: {
            VStack(spacing: 8) {
                Color.seat
                    .frame(width: 84, height: 84)
                    .overlay {
                        RemoteImage(url: model.client?.imageURL(image, width: 200)) {
                            Text(initials).font(.marquee(28, weight: .bold)).foregroundStyle(Color.usher)
                        }
                    }
                    .clipShape(Circle())
                VStack(spacing: 1) {
                    Text(name).font(.caption.weight(.semibold)).foregroundStyle(Color.screen)
                    if let detail { Text(detail).font(.caption2).foregroundStyle(Color.usher) }
                }
                .lineLimit(1)
            }
            .frame(width: 96)
        }
        .buttonStyle(CardButtonStyle())
    }

    private var initials: String {
        name.split(separator: " ").prefix(2).compactMap(\.first).map(String.init).joined()
    }
}

/// Cards dim a touch while pressed rather than lifting, as touch has no focus to show.
struct CardButtonStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .opacity(configuration.isPressed ? 0.75 : 1)
            .scaleEffect(configuration.isPressed ? 0.98 : 1)
            .animation(.easeOut(duration: 0.12), value: configuration.isPressed)
    }
}

/// Long-press menu shared by cards.
struct ItemContextMenu: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    @Environment(\.inContinueWatching) private var inContinueWatching
    let item: ItemSummary
    var showsDetails = false

    var body: some View {
        if item.kind == .movie || item.kind == .episode {
            Button { model.play(item) } label: {
                Label(item.isResumable ? "Resume" : "Play", systemImage: "play.fill")
            }
            if item.isResumable {
                Button { model.play(item, fromBeginning: true) } label: {
                    Label("Play from beginning", systemImage: "arrow.counterclockwise")
                }
            }
        }
        if showsDetails {
            Button { router.push(.item(item)) } label: {
                Label(item.kind == .episode ? "Go to episode" : "Go to details", systemImage: "info.circle")
            }
        }
        let watched = item.userData.watched || item.userData.unwatchedCount == 0
        Button { Task { await model.setWatched(item, !watched) } } label: {
            Label(watched ? "Mark as unwatched" : "Mark as watched", systemImage: watched ? "eye.slash" : "checkmark.circle")
        }
        if model.hasWatchlist, item.kind == .movie || item.kind == .series {
            let onList = model.watchlistItems.contains(item.id)
            Button { Task { await model.setOnWatchlist(.library(item.id), !onList) } } label: {
                Label(onList ? "Remove from watchlist" : "Add to watchlist", systemImage: onList ? "bookmark.slash" : "bookmark")
            }
        }
        // Part way through: unwatched forgets the place, which takes it out of Continue Watching.
        if item.isResumable && !watched {
            Button { Task { await model.setWatched(item, false) } } label: {
                Label("Mark as unwatched", systemImage: "eye.slash")
            }
        } else if inContinueWatching, item.kind == .episode, !watched {
            Button { Task { await model.removeFromContinueWatching(item) } } label: {
                Label("Remove from Continue Watching", systemImage: "minus.circle")
            }
        }
        if item.kind == .episode, item.seriesId != nil, let show = item.seriesTitle {
            Button { Task { await model.setShowWatched(of: item) } } label: {
                Label("Mark \(show) as watched", systemImage: "checkmark.circle.fill")
            }
        }
    }
}

/// Clear logo when the title has one, otherwise the title set in marquee type.
struct TitleArt: View {
    @Environment(AppModel.self) private var model
    let item: ItemSummary
    var maxWidth: CGFloat
    var maxHeight: CGFloat
    var fontSize: CGFloat

    var body: some View {
        if let logo = model.client?.imageURL(item.artwork.logo, width: 600) {
            RemoteImage(url: logo, contentMode: .fit) {
                title
            }
            .frame(maxWidth: maxWidth, maxHeight: maxHeight, alignment: .bottomLeading)
            .shadow(color: .black.opacity(0.35), radius: 10)
            .accessibilityLabel(item.title)
        } else {
            title
        }
    }

    private var title: some View {
        Text(item.displayTitle)
            .font(.marquee(fontSize, weight: .black))
            .foregroundStyle(Color.screen)
            .lineLimit(2)
            .minimumScaleFactor(0.6)
            .frame(maxWidth: maxWidth * 1.2, alignment: .leading)
            .shadow(color: .black.opacity(0.35), radius: 10)
    }
}
