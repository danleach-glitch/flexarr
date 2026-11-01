import FlexarrKit
import SwiftUI

/// A book's square cover, with how far in it is (or a tick once finished).
struct BookCover: View {
    let item: ItemSummary
    var imageWidth = 500

    var body: some View {
        SquareArtwork(item: item, imageWidth: imageWidth)
            .overlay(alignment: .bottom) {
                if let progress = item.progress, !item.isFinishedBook {
                    ProgressBar(value: progress).padding(6)
                }
            }
            .overlay(alignment: .topTrailing) {
                if item.isFinishedBook { WatchedBadge(label: "Finished").padding(6) }
            }
    }
}

/// A book in a row or grid: its cover, title and author; tapping opens it.
struct BookCard: View {
    @Environment(Router.self) private var router
    @Environment(DownloadStore.self) private var downloads
    let item: ItemSummary
    var width: CGFloat?
    /// The series number instead of the author, on a series' own page.
    var sequence: String?
    var onOpen: (() -> Void)?

    var body: some View {
        Button {
            onOpen?()
            router.push(.item(item))
        } label: {
            VStack(alignment: .leading, spacing: 6) {
                BookCover(item: item)
                    .overlay(alignment: .bottomLeading) {
                        if downloads.entry(for: item.id)?.isDownloaded == true {
                            DownloadedMark()
                                .padding(4)
                                .background(Circle().fill(Color.room.opacity(0.85)))
                                .padding(5)
                        }
                    }
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
        .contextMenu { BookContextMenu(item: item) }
    }

    private var subtitle: String? {
        if let sequence { return "Book \(sequence)" }
        return item.bookAuthors
    }
}

/// Long-press menu for a book: play it, and mark it finished or not.
struct BookContextMenu: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    let item: ItemSummary

    var body: some View {
        Button { model.playBook(item) } label: {
            Label(item.isResumable ? "Resume" : "Play", systemImage: "play.fill")
        }
        Button { router.push(.item(item)) } label: { Label("Go to book", systemImage: "info.circle") }
        if !model.isKids {
            if !item.isFinishedBook {
                Button { Task { await model.setBookFinished(item, true) } } label: { Label("Mark as finished", systemImage: "checkmark.circle") }
            }
            // Unread: no place and not finished, as if never started.
            if item.isFinishedBook || item.isResumable {
                Button { Task { await model.setBookFinished(item, false) } } label: { Label("Mark as unread", systemImage: "arrow.uturn.backward") }
            }
        }
    }
}

/// A series: its first books' covers fanned, its name, and how many books (and how many finished).
struct SeriesCard: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    let series: BookSeriesSummary
    var width: CGFloat?

    var body: some View {
        Button { router.push(.bookSeries(id: series.id, name: series.name)) } label: {
            VStack(alignment: .leading, spacing: 6) {
                stack
                VStack(alignment: .leading, spacing: 1) {
                    Text(series.name)
                        .font(.footnote.weight(.semibold))
                        .foregroundStyle(Color.screen.opacity(0.9))
                    Text(count)
                        .font(.caption)
                        .foregroundStyle(Color.usher)
                        .monospacedDigit()
                }
                .lineLimit(1)
            }
            .frame(width: width)
            .contentShape(Rectangle())
        }
        .buttonStyle(CardButtonStyle())
    }

    private var count: String {
        let books = series.bookCount == 1 ? "1 book" : "\(series.bookCount) books"
        return series.finishedCount > 0 ? "\(series.finishedCount) of \(books)" : books
    }

    /// Up to three covers, each a little behind and to the right of the one before.
    private var stack: some View {
        let covers = Array(series.covers.prefix(3))
        return Color.clear
            .aspectRatio(1, contentMode: .fit)
            .overlay(alignment: .bottomLeading) {
                GeometryReader { proxy in
                    let step = proxy.size.width * 0.08
                    let side = proxy.size.width - step * CGFloat(max(0, covers.count - 1))
                    ZStack(alignment: .bottomLeading) {
                        if covers.isEmpty {
                            RoundedRectangle(cornerRadius: 6, style: .continuous).fill(Color.seat)
                                .overlay { Text(series.name).font(.marquee(16, weight: .bold)).foregroundStyle(Color.screen.opacity(0.8)).padding(8) }
                        }
                        ForEach(Array(covers.enumerated().reversed()), id: \.offset) { index, cover in
                            Color.seat
                                .frame(width: side, height: side)
                                .overlay { RemoteImage(url: model.client?.imageURL(cover.poster, width: 400)) { Color.seat } }
                                .clipShape(RoundedRectangle(cornerRadius: 6, style: .continuous))
                                .shadow(color: .black.opacity(index == 0 ? 0 : 0.4), radius: 4)
                                .brightness(index == 0 ? 0 : -0.12 * Double(index))
                                .offset(x: step * CGFloat(index), y: -step * CGFloat(index))
                        }
                    }
                    .frame(width: proxy.size.width, height: proxy.size.height, alignment: .bottomLeading)
                }
            }
    }
}

/// A book in a list: cover, title, author or series number, and length or time left.
struct BookRow: View {
    let item: ItemSummary
    var sequence: String?
    var highlighted = false

    var body: some View {
        HStack(spacing: 14) {
            BookCover(item: item, imageWidth: 200)
                .frame(width: 64, height: 64)
            VStack(alignment: .leading, spacing: 3) {
                if let sequence {
                    Text("Book \(sequence)")
                        .font(.caption.weight(.semibold))
                        .foregroundStyle(highlighted ? Color.silver : Color.usher)
                }
                Text(item.title)
                    .font(.subheadline.weight(.semibold))
                    .foregroundStyle(Color.screen)
                    .lineLimit(2)
                if let time = item.bookTimeLine {
                    Text(time)
                        .font(.caption)
                        .foregroundStyle(Color.usher)
                        .monospacedDigit()
                }
            }
            Spacer(minLength: 0)
            if highlighted {
                Text("Next")
                    .font(.caption.weight(.bold))
                    .foregroundStyle(Color.room)
                    .padding(.horizontal, 8)
                    .padding(.vertical, 3)
                    .background(Capsule().fill(Color.silver))
            }
        }
        .contentShape(Rectangle())
    }
}

extension ItemSummary {
    /// Finished by this profile (the server keeps it as watched).
    var isFinishedBook: Bool { userData.watched }

    /// "10h 20m", or "3h 2m left" part way through.
    var bookTimeLine: String? {
        let duration = book?.durationSeconds ?? effectiveDuration ?? 0
        if isResumable { return Format.remaining(duration - userData.positionSeconds) }
        return Format.runtime(seconds: duration)
    }
}

/// A moment's word at the top of the screen: a bookmark added, or why it wasn't.
struct NoticeCapsule: View {
    let notice: BookmarkNotice

    var body: some View {
        Label(notice.text, systemImage: notice.isError ? "exclamationmark.triangle.fill" : "bookmark.fill")
            .font(.subheadline.weight(.semibold))
            .monospacedDigit()
            .foregroundStyle(notice.isError ? Color.room : Color.screen)
            .padding(.horizontal, 16)
            .padding(.vertical, 10)
            .background(Capsule().fill(notice.isError ? Color.danger : Color.seat))
            .shadow(color: .black.opacity(0.3), radius: 12, y: 4)
            .transition(.move(edge: .top).combined(with: .opacity))
            .accessibilityAddTraits(.updatesFrequently)
            .onAppear { UIAccessibility.post(notification: .announcement, argument: notice.text) }
    }
}
