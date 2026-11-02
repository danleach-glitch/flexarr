import FlexarrKit
import SwiftUI

/// An artist from outside the library: their photo and name, Follow, their albums (each to request), and the
/// artists fans also like.
struct ExternalArtistView: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    @Environment(\.horizontalSizeClass) private var sizeClass
    let preview: MusicExternal
    @State private var detail: MusicArtistDetail?
    @State private var error: String?
    @State private var status: MusicRequestStatus?
    @Environment(\.musicRequestsPaused) private var pausedAbove

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
                        // Shelved as the web does: Albums, EPs and singles, Compilations.
                        ForEach(detail.albums.shelves) { shelf in
                            VStack(alignment: .leading, spacing: 12) {
                                SectionHeading(title: shelf.title)
                                LazyVGrid(columns: columns, alignment: .leading, spacing: 20) {
                                    ForEach(shelf.items.listed) { entry in
                                        ExternalMusicCard(item: entry.item, showsYear: true)
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
            .refreshable { await load(fresh: true) }
            .environment(\.musicRequestsPaused, paused)
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
                // Nothing to play here (`MusicActions`): In your library opens the artist's own page, then Follow, spelled
                // out when it's alone.
                ActionRowLayout {
                    if let id = detail.libraryItemId {
                        Button { router.push(.musicItem(id: id, isArtist: true)) } label: {
                            Label("In your library", systemImage: Symbol.play)
                        }
                        .buttonStyle(ActionButtonStyle(prominent: true))
                    }
                    if MusicActions(kind: .artist, platform: .phone, hasMusic: false, canAsk: status?.configured == true).has(.follow) {
                        FollowArtistButton(name: detail.name, following: model.isFollowing(preview, followed: detail.followed),
                                           libraryId: detail.libraryItemId, deezerId: detail.deezerId,
                                           labelled: detail.libraryItemId == nil)
                    }
                }
                if paused {
                    Text(MusicWords.lidarrNotAnswering)
                        .font(.footnote)
                        .foregroundStyle(Color.usher)
                }
            }
        }
        .frame(maxWidth: .infinity)
        .padding(.top, 12)
        .padding(.horizontal, Layout.edge)
    }

    /// Lidarr is connected but isn't answering (or didn't for this page, so what's asked for is unknown): the buttons
    /// wait, greyed.
    private var paused: Bool {
        pausedAbove || (status?.configured == true && (status?.reachable == false || detail?.lidarrAnswered == false))
    }

    private func load(fresh: Bool = false) async {
        guard let client = model.client else { return }
        async let asked = model.musicRequestStatus(fresh: fresh)
        do {
            detail = try await client.musicArtist(deezerId: preview.deezerId)
            error = nil
        } catch {
            self.error = ConnectionError(error).message
        }
        status = await asked
    }
}
