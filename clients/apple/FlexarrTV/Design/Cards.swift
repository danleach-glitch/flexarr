import FlexarrKit
import SwiftUI

/// A 2:3 poster with the title and year beneath it (text never sits on artwork).
struct PosterCard: View {
    @Environment(AppModel.self) private var model
    let item: ItemSummary
    var width: CGFloat = Layout.posterWidth
    /// Optional shared focus state so a grid can steer focus to this card (by the item's id).
    var focus: FocusState<String?>.Binding?
    let action: () -> Void

    var body: some View {
        ArtworkButton(ambient: item.glowColor, focus: focus, focusID: item.id, action: action) {
            PosterArtwork(item: item, width: width)
        } caption: { focused in
            VStack(alignment: .leading, spacing: 4) {
                Text(item.title)
                    .font(.system(size: 24, weight: .semibold))
                    .foregroundStyle(focused ? Color.screen : Color.screen.opacity(0.8))
                    .lineLimit(1)
                if let subtitle {
                    Text(subtitle)
                        .font(.system(size: 21))
                        .foregroundStyle(Color.usher)
                        .lineLimit(1)
                }
            }
            .frame(width: width, alignment: .leading)
        }
        .contextMenu { ItemContextMenu(item: item) }
    }

    private var subtitle: String? {
        if item.kind == .series, let count = item.userData.unwatchedCount, count > 0 {
            return [item.releaseYear.map(String.init), "\(count) new"].compactMap { $0 }.joined(separator: "  ")
        }
        return item.releaseYear.map(String.init)
    }
}

/// Poster artwork with a title placeholder, watched tick and progress bar.
struct PosterArtwork: View {
    @Environment(AppModel.self) private var model
    let item: ItemSummary
    var width: CGFloat = Layout.posterWidth

    var body: some View {
        RemoteImage(url: model.client?.imageURL(item.artwork.poster, width: 342)) {
            ZStack {
                LinearGradient(colors: [item.glowColor.opacity(0.35), Color.seat], startPoint: .top, endPoint: .bottom)
                Text(item.title)
                    .font(.marquee(30, weight: .bold))
                    .foregroundStyle(Color.screen.opacity(0.8))
                    .multilineTextAlignment(.center)
                    .padding(16)
            }
        }
        // Album covers and artist pictures are square; everything else is a 2:3 poster.
        .frame(width: width, height: item.kind.isMusic ? width : width * 1.5)
        .overlay(alignment: .bottom) {
            if let progress = item.progress {
                ProgressBar(value: progress).padding(12)
            }
        }
        .overlay(alignment: .topTrailing) {
            if item.userData.watched { WatchedBadge().padding(10) }
        }
        .clipShape(RoundedRectangle(cornerRadius: Radius.art, style: .continuous))
    }
}

/// 16:9 card for Continue Watching and episode lists, with a progress bar. A book in Continue watching shows its
/// square cover over a blur of itself.
struct WideCard: View {
    @Environment(AppModel.self) private var model
    let item: ItemSummary
    var width: CGFloat = Layout.wideWidth
    /// Optional shared focus state so a row can steer focus to this card (by the item's id).
    var focus: FocusState<String?>.Binding?
    let action: () -> Void

    var body: some View {
        ArtworkButton(ambient: item.glowColor, focus: focus, focusID: item.id, action: action) {
            Group {
                if item.isListening {
                    ListeningArt(item: item, imageWidth: 500, inset: 20, cornerRadius: Radius.art)
                } else {
                    RemoteImage(url: model.client?.imageURL(stillPath, width: 780)) {
                        ZStack {
                            LinearGradient(colors: [item.glowColor.opacity(0.4), Color.seat], startPoint: .topLeading, endPoint: .bottomTrailing)
                            Image(systemName: "play.fill").font(.system(size: 44)).foregroundStyle(Color.screen.opacity(0.5))
                        }
                    }
                }
            }
            .frame(width: width, height: width * 9 / 16)
            .overlay(alignment: .bottom) {
                if let progress = item.progress {
                    ZStack(alignment: .bottom) {
                        LinearGradient(colors: [.clear, .black.opacity(0.55)], startPoint: .top, endPoint: .bottom)
                            .frame(height: 90)
                        ProgressBar(value: progress).padding(16)
                    }
                }
            }
            .clipShape(RoundedRectangle(cornerRadius: Radius.art, style: .continuous))
        } caption: { focused in
            VStack(alignment: .leading, spacing: 4) {
                Text(item.isListening ? item.title : item.displayTitle)
                    .font(.system(size: 25, weight: .semibold))
                    .foregroundStyle(focused ? Color.screen : Color.screen.opacity(0.85))
                    .lineLimit(1)
                Text(secondLine)
                    .font(.system(size: 21))
                    .foregroundStyle(Color.usher)
                    .lineLimit(1)
            }
            .frame(width: width, alignment: .leading)
        }
        .contextMenu { ItemContextMenu(item: item, showsDetails: true) }
    }

    private var stillPath: String? {
        item.kind == .episode ? (item.artwork.thumb ?? item.artwork.backdrop) : (item.artwork.backdrop ?? item.artwork.thumb)
    }

    private var secondLine: String {
        let remaining = item.progress != nil
            ? item.effectiveDuration.flatMap { Format.remaining($0 - item.userData.positionSeconds) }
            : nil
        return [item.isListening ? item.listeningSubtitle : item.episodeSubtitle, remaining].compactMap { $0 }.joined(separator: "   ")
    }
}

/// Top 10: a giant outlined marquee numeral tucked behind the poster.
struct Top10Card: View {
    let rank: Int
    let item: ItemSummary
    let action: () -> Void

    var body: some View {
        HStack(alignment: .bottom, spacing: -36) {
            OutlinedNumeral(text: String(rank))
                .zIndex(0)
            PosterCard(item: item, width: 220, action: action)
                .zIndex(1)
        }
    }
}

struct OutlinedNumeral: View {
    let text: String

    var body: some View {
        let glyph = Text(text).font(.marquee(330, weight: .black)).kerning(-12)
        ZStack {
            // An outline drawn by stamping the glyph around a ring, then knocking out the middle.
            ForEach(0..<16, id: \.self) { index in
                let angle = Double(index) / 16 * 2 * .pi
                glyph.foregroundStyle(Color.usher.opacity(0.9))
                    .offset(x: cos(angle) * 3, y: sin(angle) * 3)
            }
            glyph.foregroundStyle(Color.room)
        }
        .fixedSize()
        .padding(.bottom, 40)
        .accessibilityHidden(true)
    }
}

struct WatchedBadge: View {
    var body: some View {
        Image(systemName: "checkmark")
            .font(.system(size: 18, weight: .heavy))
            .foregroundStyle(Color.room)
            .frame(width: 36, height: 36)
            .background(Circle().fill(Color.screen.opacity(0.92)))
    }
}

/// Round headshot with name and character, for cast rows and people results.
struct PersonCard: View {
    @Environment(AppModel.self) private var model
    let name: String
    let detail: String?
    let image: String?
    /// Shared focus state, so the row can steer focus to this card (see `RowFocus`).
    var focus: FocusState<String?>.Binding? = nil
    /// Which card `focus` means by this one: the person's id where the row steers focus.
    var focusID: String? = nil
    let action: () -> Void

    var body: some View {
        VStack(spacing: 16) {
            Button(action: action) {
                RemoteImage(url: model.client?.imageURL(image, width: 300)) {
                    ZStack {
                        Color.seat
                        Text(initials).font(.marquee(56, weight: .bold)).foregroundStyle(Color.usher)
                    }
                }
                .frame(width: 180, height: 180)
                .clipShape(Circle())
            }
            .buttonStyle(CircleCardStyle())
            .modifier(SharedFocus(binding: focus, id: focusID))

            VStack(spacing: 2) {
                Text(name).font(.system(size: 23, weight: .semibold)).foregroundStyle(Color.screen)
                if let detail { Text(detail).font(.system(size: 20)).foregroundStyle(Color.usher) }
            }
            .lineLimit(1)
            .frame(width: 210)
        }
    }

    private var initials: String {
        name.split(separator: " ").prefix(2).compactMap(\.first).map(String.init).joined()
    }
}

/// Long-press menu shared by cards.
struct ItemContextMenu: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    @Environment(\.opensTitlesOnLibraryTab) private var opensOnLibraryTab
    @Environment(\.inContinueWatching) private var inContinueWatching
    @Environment(\.leavesRow) private var leavesRow
    let item: ItemSummary
    var showsDetails = false

    var body: some View {
        if item.isListening, inContinueWatching {
            listeningMenu
        } else {
            menu
        }
    }

    /// A book in Continue watching: carry on, its page, and out of the row.
    @ViewBuilder
    private var listeningMenu: some View {
        Button { model.play(item) } label: {
            Label(item.isResumable ? "Resume" : "Play", systemImage: "play.fill")
        }
        if item.kind == .audiobook {
            Button { goToDetails() } label: { Label("Go to book", systemImage: "info.circle") }
            if !model.isKids {
                Button {
                    leavesRow?(item.id)
                    Task { await model.setBookFinished(item, true) }
                } label: {
                    Label("Mark as finished", systemImage: "checkmark.circle")
                }
            }
        }
        // Forgets the place without marking it finished.
        Button {
            leavesRow?(item.id)
            Task { await model.removeFromContinueWatching(item) }
        } label: {
            Label("Remove", systemImage: "minus.circle")
        }
    }

    @ViewBuilder
    private var menu: some View {
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
            Button { goToDetails() } label: {
                Label(item.kind == .episode ? "Go to episode" : "Go to details", systemImage: "info.circle")
            }
        }
        let watched = item.userData.watched || item.userData.unwatchedCount == 0
        // A child's history keeps itself: watching is the only way to change it.
        let marks = !model.isKids
        if marks {
            Button {
                leavesRow?(item.id)
                Task { await model.setWatched(item, !watched) }
            } label: {
                Label(watched ? "Mark as unwatched" : "Mark as watched", systemImage: watched ? "eye.slash" : "checkmark.circle")
            }
        }
        if model.hasWatchlist, item.kind == .movie || item.kind == .series {
            let onList = model.watchlistItems.contains(item.id)
            Button { Task { await model.setOnWatchlist(.library(item.id), !onList) } } label: {
                Label(onList ? "Remove from watchlist" : "Add to watchlist", systemImage: onList ? "bookmark.slash" : "bookmark")
            }
        }
        // Part way through: unwatched forgets the place, which takes it out of Continue Watching.
        if marks && item.isResumable && !watched {
            Button {
                leavesRow?(item.id)
                Task { await model.setWatched(item, false) }
            } label: {
                Label("Mark as unwatched", systemImage: "eye.slash")
            }
        } else if inContinueWatching, item.kind == .episode, !watched {
            Button {
                leavesRow?(item.id)
                Task { await model.removeFromContinueWatching(item) }
            } label: {
                Label("Remove", systemImage: "minus.circle")
            }
        }
        if marks, item.kind == .episode, item.seriesId != nil, let show = item.seriesTitle {
            Button {
                leavesRow?(item.id)
                Task { await model.setShowWatched(of: item) }
            } label: {
                Label("Mark \(show) as watched", systemImage: "checkmark.circle.fill")
            }
        }
    }

    private func goToDetails() {
        // From Home, on the title's library tab (MainTabView picks it); anywhere else, on this tab's own screens.
        if opensOnLibraryTab, TabBarEntry.libraries(model: model).contains(where: { $0.id == item.libraryId }) {
            model.pendingRoute = .item(item)
        } else {
            router.push(.item(item))
        }
    }
}
