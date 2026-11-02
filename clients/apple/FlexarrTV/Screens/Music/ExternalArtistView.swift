import FlexarrKit
import SwiftUI

/// An artist from outside the library: their photo and name, Follow artist, their albums (each to request), and the
/// artists fans also like.
struct ExternalArtistView: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    let preview: MusicExternal
    @State private var detail: MusicArtistDetail?
    @State private var error: String?

    private let columns = Array(repeating: GridItem(.fixed(Layout.posterWidth), spacing: Layout.gridSpacing, alignment: .top), count: 6)

    var body: some View {
        ZStack {
            RoomBackground(ambient: .silver, intensity: 0.4)
                .frame(width: 1920, height: 1080)
            ScrollView(.vertical) {
                VStack(alignment: .leading, spacing: 56) {
                    header
                        .padding(.horizontal, Layout.edge)
                    if let detail {
                        if !detail.albums.isEmpty {
                            VStack(alignment: .leading, spacing: 28) {
                                Text("Albums")
                                    .font(.marquee(40, weight: .bold))
                                    .foregroundStyle(Color.screen)
                                LazyVGrid(columns: columns, alignment: .leading, spacing: 56) {
                                    ForEach(detail.albums) { album in
                                        ExternalMusicCard(item: album, showsYear: true)
                                    }
                                }
                                .focusSection()
                            }
                            .padding(.horizontal, Layout.edge)
                        }
                        if !detail.similar.isEmpty {
                            ExternalMusicRow(title: "Fans also like", items: detail.similar, artists: true)
                        }
                    } else if let error {
                        StatusView(state: .failed(error) { Task { await load() } }).frame(height: 300)
                    } else {
                        StatusView(state: .loading).frame(height: 300)
                    }
                }
                .padding(.top, 130)
                .padding(.bottom, 90)
            }
            .scrollClipDisabled()
        }
        .task { await load() }
    }

    private var header: some View {
        HStack(alignment: .center, spacing: 60) {
            ExternalMusicArtwork(image: detail?.image ?? preview.image, title: detail?.name ?? preview.title, size: 360, circular: true)
            VStack(alignment: .leading, spacing: 22) {
                Text(detail?.name ?? preview.title)
                    .font(.marquee(84, weight: .heavy))
                    .foregroundStyle(Color.screen)
                    .lineLimit(2)
                    .minimumScaleFactor(0.6)
                // There from the start, so focus has somewhere to land while the rest loads.
                HStack(spacing: 26) {
                    if let id = detail?.libraryItemId {
                        Button { router.push(.musicItem(id: id, isArtist: true)) } label: {
                            Label("In your library", systemImage: Symbol.music)
                        }
                        .buttonStyle(ActionButtonStyle(prominent: true))
                    }
                    FollowArtistButton(following: model.isFollowing(preview, followed: detail?.followed ?? false)) {
                        await model.requestMusic(.artist(deezerId: preview.deezerId))
                    }
                    .takesFocusFromLink()
                }
                .focusSection()
                .padding(.top, 8)
            }
            Spacer(minLength: 0)
        }
    }

    private func load() async {
        guard let client = model.client else { return }
        do {
            detail = try await client.musicArtist(deezerId: preview.deezerId)
            error = nil
        } catch {
            self.error = ConnectionError(error).message
        }
    }
}
