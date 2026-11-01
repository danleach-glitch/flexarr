import FlexarrKit
import SwiftUI

/// A series in reading order, the next book to read picked out, with Play for it.
struct BookSeriesView: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    let id: String
    let name: String
    @State private var series: BookSeries?
    @State private var error: String?
    @State private var loadedToken: Int?

    private var next: ItemSummary? {
        guard let series else { return nil }
        return series.books.first { $0.book.id == series.nextId }?.book
    }

    var body: some View {
        ScrollView(.vertical) {
            VStack(alignment: .leading, spacing: 20) {
                VStack(alignment: .leading, spacing: 6) {
                    Text(series?.name ?? name)
                        .font(.marquee(32, weight: .black))
                        .foregroundStyle(Color.screen)
                    if let series {
                        let finished = series.books.filter { $0.book.isFinishedBook }.count
                        Text(finished > 0 ? "\(finished) of \(series.books.count) finished" : series.books.count == 1 ? "1 book" : "\(series.books.count) books")
                            .font(.subheadline.weight(.medium))
                            .foregroundStyle(Color.usher)
                            .monospacedDigit()
                    }
                }
                .padding(.horizontal, Layout.edge)
                if let next {
                    Button { model.playBook(next) } label: {
                        Label(next.isResumable ? "Resume \(next.title)" : "Play \(next.title)", systemImage: "play.fill")
                            .lineLimit(1)
                            .frame(maxWidth: .infinity)
                    }
                    .buttonStyle(ActionButtonStyle(ambient: next.glowColor, prominent: true))
                    .frame(maxWidth: 520)
                    .padding(.horizontal, Layout.edge)
                }
                if let description = series?.description, !description.isEmpty {
                    Text(description)
                        .font(.subheadline)
                        .foregroundStyle(Color.screen.opacity(0.85))
                        .padding(.horizontal, Layout.edge)
                }
                if let series {
                    VStack(spacing: 4) {
                        ForEach(series.books) { entry in
                            Button { router.push(.item(entry.book)) } label: {
                                BookRow(item: entry.book, sequence: entry.sequence, highlighted: entry.book.id == series.nextId)
                                    .padding(.horizontal, Layout.edge)
                                    .padding(.vertical, 6)
                            }
                            .buttonStyle(CardButtonStyle())
                            .contextMenu { BookContextMenu(item: entry.book) }
                        }
                    }
                } else if let error {
                    StatusView(state: .failed(error) { Task { await load() } }).frame(height: 300)
                } else {
                    ProgressView().frame(maxWidth: .infinity).padding(40)
                }
            }
            .padding(.vertical, 12)
            .frame(maxWidth: 720)
            .frame(maxWidth: .infinity)
        }
        .background(RoomBackground(ambient: next?.glowColor ?? series?.books.first?.book.glowColor, intensity: 0.22))
        .navigationBarTitleDisplayMode(.inline)
        .refreshable { await load() }
        .task(id: model.refreshToken) {
            guard loadedToken != model.refreshToken else { return }
            await load()
        }
    }

    private func load() async {
        guard let client = model.client else { return }
        let token = model.refreshToken
        do {
            series = try await client.bookSeries(id)
            loadedToken = token
            error = nil
        } catch {
            if series == nil { self.error = ConnectionError(error).message }
        }
    }
}
