import FlexarrKit
import SwiftUI

/// An outside album's cover or artist's photo, the title standing in until it arrives.
struct ExternalMusicArtwork: View {
    @Environment(AppModel.self) private var model
    let image: String?
    let title: String
    var size: CGFloat
    var circular = false

    var body: some View {
        RemoteImage(url: model.client?.imageURL(image, width: size > 400 ? 800 : 500)) {
            ZStack {
                LinearGradient(colors: [Color.silver.opacity(0.3), Color.seat], startPoint: .top, endPoint: .bottom)
                Text(circular ? initials : title)
                    .font(.marquee(circular ? size * 0.3 : max(24, size * 0.11), weight: .bold))
                    .foregroundStyle(Color.screen.opacity(0.8))
                    .multilineTextAlignment(.center)
                    .padding(size * 0.08)
            }
        }
        .frame(width: size, height: size)
        .clipShape(circular ? AnyShape(Circle()) : AnyShape(RoundedRectangle(cornerRadius: Radius.art, style: .continuous)))
    }

    private var initials: String {
        title.split(separator: " ").prefix(2).compactMap(\.first).map(String.init).joined()
    }
}

/// An artist or album from outside the library: pressing it opens its page (or the library's, when the household has
/// it); holding it offers Request album or Follow artist.
struct ExternalMusicCard: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    let item: MusicExternal
    var width: CGFloat = Layout.posterWidth
    /// An album's year under its title, on its artist's page; the artist's name elsewhere.
    var showsYear = false
    var focus: FocusState<String?>.Binding? = nil
    var focusID: String? = nil

    var body: some View {
        Group {
            if item.isArtist {
                VStack(spacing: 18) {
                    Button(action: open) {
                        ExternalMusicArtwork(image: item.image, title: item.title, size: width, circular: true)
                    }
                    .buttonStyle(CircleCardStyle())
                    .modifier(SharedFocus(binding: focus, id: focusID ?? item.id))
                    VStack(spacing: 2) {
                        Text(item.title)
                            .font(.system(size: 24, weight: .semibold))
                            .foregroundStyle(Color.screen)
                        if let subtitle {
                            Text(subtitle)
                                .font(.system(size: 21))
                                .foregroundStyle(Color.usher)
                        }
                    }
                    .lineLimit(1)
                    .frame(width: width + 30)
                }
            } else {
                ArtworkButton(ambient: .silver, focus: focus, focusID: focusID ?? item.id, action: open) {
                    ExternalMusicArtwork(image: item.image, title: item.title, size: width)
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
            }
        }
        .contextMenu { menu }
    }

    private func open() {
        if let route = item.route {
            router.push(route)
        } else if item.isAlbum, case .requestable = model.albumState(item), let target = MusicRequestTarget.album(item) {
            // An album only Lidarr lists has no page: pressing it asks for it.
            Task { await model.requestMusic(target) }
        }
    }

    private var subtitle: String? {
        var parts: [String] = []
        if item.isAlbum {
            if showsYear { if let year = item.year { parts.append(String(year)) } } else if let artist = item.artist { parts.append(artist) }
            switch model.albumState(item) {
            case .requested: parts.append("Requested")
            case .inLibrary: parts.append("In your library")
            default: break
            }
        } else if item.libraryItemId != nil {
            parts.append("In your library")
        } else if model.isFollowing(item) {
            parts.append("Following")
        }
        return parts.isEmpty ? nil : parts.joined(separator: "  ")
    }

    @ViewBuilder private var menu: some View {
        if item.isAlbum, case .requestable = model.albumState(item), let target = MusicRequestTarget.album(item) {
            Button { Task { await model.requestMusic(target) } } label: { Label("Request album", systemImage: "plus") }
        }
        if item.isArtist, item.libraryItemId == nil, item.hasPage, !model.isFollowing(item) {
            Button { Task { await model.requestMusic(.artist(deezerId: item.deezerId)) } } label: { Label("Follow artist", systemImage: "plus") }
        }
    }
}

/// A row of outside artists or albums under a heading.
struct ExternalMusicRow: View {
    let title: String
    let items: [MusicExternal]
    var artists: Bool? = nil
    var focus: FocusState<String?>.Binding? = nil
    var focusPrefix = ""

    var body: some View {
        let round = artists ?? items.allSatisfy(\.isArtist)
        VStack(alignment: .leading, spacing: 0) {
            RowHeading(title: title)
            ScrollView(.horizontal) {
                LazyHStack(alignment: .top, spacing: round ? 50 : Layout.rowSpacing) {
                    ForEach(items) { item in
                        ExternalMusicCard(item: item, width: item.isArtist ? 220 : Layout.posterWidth, focus: focus,
                                          focusID: "\(focusPrefix)/\(item.id)")
                    }
                }
                .padding(.horizontal, Layout.edge)
                .padding(.top, 28)
                .padding(.bottom, 40)
            }
            .scrollClipDisabled()
            .focusSection()
        }
    }
}

/// The main button on an outside album's page: Request album, Requested, or In your library (which opens it). One
/// button throughout, so focus never lands on nothing when it changes.
struct MusicAlbumRequestButton: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    let album: MusicExternal
    @State private var asking = false

    var body: some View {
        let state = model.albumState(album)
        Button {
            switch state {
            case .requestable:
                guard !asking, let target = MusicRequestTarget.album(album) else { return }
                asking = true
                Task {
                    await model.requestMusic(target)
                    asking = false
                }
            case .inLibrary(let id):
                router.push(.musicItem(id: id, isArtist: false))
            case .requested, .none:
                break
            }
        } label: {
            switch state {
            case .requestable: Label("Request album", systemImage: "plus")
            case .requested: Label("Requested", systemImage: "clock")
            case .inLibrary: Label("In your library", systemImage: Symbol.music)
            case .none: Label("Request album", systemImage: "minus.circle")
            }
        }
        .buttonStyle(ActionButtonStyle(prominent: isProminent(state)))
    }

    private func isProminent(_ state: MusicAlbumState) -> Bool {
        switch state {
        case .requestable, .inLibrary: true
        default: false
        }
    }
}

/// Follow artist, or Following once Lidarr watches for their new releases. One button throughout (see above).
struct FollowArtistButton: View {
    let following: Bool
    let follow: () async -> Void
    @State private var asking = false

    var body: some View {
        Button {
            guard !following, !asking else { return }
            asking = true
            Task {
                await follow()
                asking = false
            }
        } label: {
            Label(following ? "Following" : "Follow artist", systemImage: following ? "checkmark" : "plus")
        }
        .buttonStyle(ActionButtonStyle())
    }
}

/// Opens a library artist or album known only by its id.
struct MusicItemDestination: View {
    let id: String
    let isArtist: Bool

    var body: some View {
        if isArtist {
            ArtistView(id: id, preview: nil)
        } else {
            AlbumView(id: id, preview: nil)
        }
    }
}
