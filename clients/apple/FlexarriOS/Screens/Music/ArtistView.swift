import FlexarrKit
import SwiftUI

/// An artist: their picture and name, Play and Shuffle for everything they have, and their albums newest first.
struct ArtistView: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    @State private var page: MusicPageModel
    @Environment(\.scenePhase) private var scenePhase
    /// Only the screen on show refreshes when the app comes back, not every one left open in a tab or below it.
    @State private var isVisible = false
    @Environment(\.horizontalSizeClass) private var sizeClass

    init(id: String, preview: ItemSummary?) {
        _page = State(initialValue: MusicPageModel(id: id, preview: preview))
    }

    private var ambient: Color { page.item?.glowColor ?? .silver }
    private var columns: [GridItem] {
        let minimum: CGFloat = sizeClass == .regular ? 150 : 105
        return [GridItem(.adaptive(minimum: minimum, maximum: minimum * 1.45), spacing: 12, alignment: .top)]
    }

    var body: some View {
        ZStack {
            RoomBackground(ambient: ambient, intensity: 0.3)
            if let item = page.item {
                ScrollView(.vertical) {
                    VStack(alignment: .leading, spacing: 24) {
                        header(item)
                        if !page.albums.isEmpty {
                            VStack(alignment: .leading, spacing: 12) {
                                SectionHeading(title: "Albums")
                                LazyVGrid(columns: columns, alignment: .leading, spacing: 20) {
                                    ForEach(page.albums) { album in
                                        AlbumCard(item: album)
                                    }
                                }
                                .padding(.horizontal, Layout.edge)
                            }
                        } else if page.detail != nil {
                            StatusView(state: .empty("No albums yet.")).frame(height: 160)
                        }
                    }
                    .padding(.bottom, 32)
                }
                .refreshable { await load() }
            } else if let error = page.error {
                StatusView(state: .failed(error) { Task { await load() } })
            } else {
                StatusView(state: .loading)
            }
        }
        .navigationBarTitleDisplayMode(.inline)
        .toolbarBackground(.hidden, for: .navigationBar)
        .task(id: model.refreshToken) { await load(ifFresh: true) }
        .onAppear { isVisible = true }
        .onDisappear { isVisible = false }
        .onChange(of: scenePhase) { _, phase in
            if phase == .active, isVisible { Task { await load(ifFresh: true) } }
        }
    }

    private func header(_ item: ItemSummary) -> some View {
        VStack(spacing: 16) {
            SquareArtwork(item: item, circular: true)
                .frame(width: sizeClass == .regular ? 240 : 180)
                .shadow(color: .black.opacity(0.4), radius: 20, y: 10)
            VStack(spacing: 6) {
                Text(item.title)
                    .font(.marquee(sizeClass == .regular ? 44 : 34, weight: .heavy))
                    .foregroundStyle(Color.screen)
                    .multilineTextAlignment(.center)
                    .lineLimit(2)
                    .minimumScaleFactor(0.7)
                FactsLine(facts: item.facts)
                if let genres = page.detail?.genres, !genres.isEmpty {
                    Text(genres.prefix(3).joined(separator: ", "))
                        .font(.subheadline.weight(.medium))
                        .foregroundStyle(Color.usher)
                        .lineLimit(1)
                }
            }
            HStack(spacing: 12) {
                Button { Task { await model.playAll(of: page, shuffled: false, router: router) } } label: {
                    Label("Play", systemImage: "play.fill")
                }
                .buttonStyle(ActionButtonStyle(ambient: ambient, prominent: true))
                Button { Task { await model.playAll(of: page, shuffled: true, router: router) } } label: {
                    Label("Shuffle", systemImage: "shuffle")
                }
                .buttonStyle(ActionButtonStyle(ambient: ambient))
                if page.isQueueing { ProgressView() }
            }
            .disabled(page.albums.isEmpty || page.isQueueing)
            // Centred in a stack that fills the width, the buttons were handed less than they need and wrapped.
            .fixedSize()
            if let overview = page.detail?.overview, !overview.isEmpty {
                Text(overview)
                    .font(.subheadline)
                    .lineSpacing(3)
                    .foregroundStyle(Color.screen.opacity(0.88))
                    .lineLimit(4)
                    .frame(maxWidth: 680, alignment: .leading)
                    .padding(.horizontal, Layout.edge)
            }
        }
        .frame(maxWidth: .infinity)
        .padding(.top, 12)
    }

    private func load(ifFresh: Bool = false) async {
        guard let client = model.client else { return }
        await page.load(client: client, onlyIfStale: ifFresh)
    }
}
