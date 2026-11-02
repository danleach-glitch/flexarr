import FlexarrKit
import SwiftUI

/// An artist from outside the library: their photo and name, Follow artist, their albums (each to request), and the
/// artists fans also like.
struct ExternalArtistView: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    @Environment(\.horizontalSizeClass) private var sizeClass
    let preview: MusicExternal
    @State private var detail: MusicArtistDetail?
    @State private var error: String?

    private var columns: [GridItem] {
        let minimum: CGFloat = sizeClass == .regular ? 150 : 105
        return [GridItem(.adaptive(minimum: minimum, maximum: minimum * 1.45), spacing: 12, alignment: .top)]
    }

    var body: some View {
        ZStack {
            RoomBackground(ambient: .silver, intensity: 0.3)
            ScrollView(.vertical) {
                VStack(alignment: .leading, spacing: 24) {
                    header
                    if let detail {
                        if !detail.albums.isEmpty {
                            VStack(alignment: .leading, spacing: 12) {
                                SectionHeading(title: "Albums")
                                LazyVGrid(columns: columns, alignment: .leading, spacing: 20) {
                                    ForEach(detail.albums) { album in
                                        ExternalMusicCard(item: album, showsYear: true)
                                    }
                                }
                                .padding(.horizontal, Layout.edge)
                            }
                        }
                        if !detail.similar.isEmpty {
                            ExternalMusicRow(title: "Fans also like", items: detail.similar)
                        }
                    } else if let error {
                        StatusView(state: .failed(error) { Task { await load() } }).frame(height: 240)
                    } else {
                        ProgressView().frame(maxWidth: .infinity).padding(40)
                    }
                }
                .padding(.bottom, 32)
            }
            .refreshable { await load() }
        }
        .navigationBarTitleDisplayMode(.inline)
        .toolbarBackground(.hidden, for: .navigationBar)
        .task { await load() }
    }

    private var header: some View {
        VStack(spacing: 16) {
            ExternalMusicArtwork(image: detail?.image ?? preview.image, title: detail?.name ?? preview.title, circular: true, imageWidth: 600)
                .frame(width: sizeClass == .regular ? 240 : 180)
            Text(detail?.name ?? preview.title)
                .font(.marquee(sizeClass == .regular ? 44 : 34, weight: .heavy))
                .foregroundStyle(Color.screen)
                .multilineTextAlignment(.center)
                .lineLimit(2)
                .minimumScaleFactor(0.7)
            if let detail {
                HStack(spacing: 12) {
                    if let id = detail.libraryItemId {
                        Button { router.push(.musicItem(id: id, isArtist: true)) } label: {
                            Label("In your library", systemImage: Symbol.music)
                        }
                        .buttonStyle(ActionButtonStyle(prominent: true))
                    }
                    FollowArtistButton(following: model.isFollowing(preview, followed: detail.followed)) {
                        await model.requestMusic(.artist(deezerId: detail.deezerId))
                    }
                }
                .fixedSize()
            }
        }
        .frame(maxWidth: .infinity)
        .padding(.top, 12)
        .padding(.horizontal, Layout.edge)
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
