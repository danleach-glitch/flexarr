import FlexarrKit
import SwiftUI

/// An artist: their picture and name, Play and Shuffle for everything they have, and their albums newest first.
struct ArtistView: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    @State private var page: MusicPageModel
    @Environment(\.scenePhase) private var scenePhase

    private let columns = Array(repeating: GridItem(.fixed(Layout.posterWidth), spacing: Layout.gridSpacing, alignment: .top), count: 6)

    init(id: String, preview: ItemSummary?) {
        _page = State(initialValue: MusicPageModel(id: id, preview: preview))
    }

    private var ambient: Color { page.item?.glowColor ?? .silver }

    var body: some View {
        ZStack {
            RoomBackground(ambient: ambient, intensity: 0.4)
            if let item = page.item {
                ScrollView(.vertical) {
                    VStack(alignment: .leading, spacing: 56) {
                        header(item)
                        if !page.albums.isEmpty {
                            VStack(alignment: .leading, spacing: 28) {
                                Text("Albums")
                                    .font(.marquee(40, weight: .bold))
                                    .foregroundStyle(Color.screen)
                                LazyVGrid(columns: columns, alignment: .leading, spacing: 56) {
                                    ForEach(page.albums) { album in
                                        AlbumCard(item: album) { router.push(.item(album)) }
                                    }
                                }
                                .focusSection()
                            }
                        } else if page.detail != nil {
                            StatusView(state: .empty("No albums yet."))
                                .frame(height: 200)
                        }
                    }
                    .padding(.horizontal, Layout.edge)
                    .padding(.top, 130)
                    .padding(.bottom, 90)
                }
                .scrollClipDisabled()
            } else if let error = page.error {
                StatusView(state: .failed(error) { Task { await load() } })
            } else {
                StatusView(state: .loading)
            }
        }
        .task(id: model.refreshToken) { await load(ifFresh: true) }
        .onChange(of: scenePhase) { _, phase in
            if phase == .active { Task { await load(ifFresh: true) } }
        }
    }

    private func header(_ item: ItemSummary) -> some View {
        HStack(alignment: .center, spacing: 60) {
            SquareArtwork(item: item, size: 360, circular: true)
                .shadow(color: .black.opacity(0.4), radius: 30, y: 16)
            VStack(alignment: .leading, spacing: 22) {
                Text(item.title)
                    .font(.marquee(84, weight: .heavy))
                    .foregroundStyle(Color.screen)
                    .lineLimit(2)
                    .minimumScaleFactor(0.6)
                HStack(spacing: 22) {
                    FactsLine(facts: item.facts(scores: !model.isKids))
                    if let genres = page.detail?.genres, !genres.isEmpty {
                        Text(genres.prefix(3).joined(separator: ", "))
                            .font(.system(size: 26, weight: .medium))
                            .foregroundStyle(Color.usher)
                            .lineLimit(1)
                    }
                }
                if let overview = page.detail?.overview, !overview.isEmpty {
                    Text(overview)
                        .font(.system(size: 26))
                        .lineSpacing(4)
                        .foregroundStyle(Color.screen.opacity(0.88))
                        .lineLimit(3)
                        .frame(maxWidth: 1000, alignment: .leading)
                }
                HStack(spacing: 26) {
                    Button { Task { await model.playAll(of: page, shuffled: false, router: router) } } label: {
                        Label("Play", systemImage: "play.fill")
                    }
                    .buttonStyle(ActionButtonStyle(ambient: ambient, prominent: true))
                    .takesFocusFromLink()
                    Button { Task { await model.playAll(of: page, shuffled: true, router: router) } } label: {
                        Label("Shuffle", systemImage: "shuffle")
                    }
                    .buttonStyle(ActionButtonStyle(ambient: ambient))
                    if page.isQueueing {
                        ProgressView().scaleEffect(1.2)
                    }
                }
                .disabled(page.albums.isEmpty)
                .focusSection()
                .padding(.top, 8)
            }
            Spacer(minLength: 0)
        }
    }

    private func load(ifFresh: Bool = false) async {
        guard let client = model.client else { return }
        await page.load(client: client, onlyIfStale: ifFresh)
    }
}
