import FlexarrKit
import SwiftUI

/// An outside album's cover or artist's photo, the title standing in until it arrives. Fills the width it's given.
struct ExternalMusicArtwork: View {
    @Environment(AppModel.self) private var model
    let image: String?
    let title: String
    var circular = false
    var imageWidth = 500

    var body: some View {
        Color.seat
            .aspectRatio(1, contentMode: .fit)
            .overlay {
                RemoteImage(url: model.client?.imageURL(image, width: imageWidth)) {
                    Text(circular ? initials : title)
                        .font(.marquee(circular ? 28 : 16, weight: .bold, scales: false))
                        .foregroundStyle(Color.screen.opacity(0.8))
                        .multilineTextAlignment(.center)
                        .padding(8)
                }
            }
            .clipShape(circular ? AnyShape(Circle()) : AnyShape(RoundedRectangle(cornerRadius: Radius.art, style: .continuous)))
    }

    private var initials: String {
        title.split(separator: " ").prefix(2).compactMap(\.first).map(String.init).joined()
    }
}

/// An artist or album the library doesn't have (or does, marked): tapping opens its page, and the corner button
/// requests the album or follows the artist.
struct ExternalMusicCard: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    let item: MusicExternal
    var width: CGFloat?
    /// An album's year under its title, on its artist's page; the artist's name elsewhere.
    var showsYear = false

    var body: some View {
        Button {
            if let route = item.route { router.push(route) }
        } label: {
            VStack(alignment: item.isArtist ? .center : .leading, spacing: item.isArtist ? 8 : 6) {
                ExternalMusicArtwork(image: item.image, title: item.title, circular: item.isArtist)
                    .padding(.horizontal, item.isArtist ? 6 : 0)
                VStack(alignment: item.isArtist ? .center : .leading, spacing: 1) {
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
        .overlay(alignment: .topTrailing) { badge }
        .contextMenu { menu }
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
        return parts.isEmpty ? nil : parts.joined(separator: ", ")
    }

    /// The one-tap request: a plus until asked, a tick after.
    @ViewBuilder private var badge: some View {
        if item.isAlbum, case .requestable = model.albumState(item) {
            badgeButton("Request album") { if let target = MusicRequestTarget.album(item) { await model.requestMusic(target) } }
        } else if item.isArtist, item.libraryItemId == nil, item.hasPage, !model.isFollowing(item) {
            badgeButton("Follow artist") { await model.requestMusic(.artist(deezerId: item.deezerId)) }
        }
    }

    private func badgeButton(_ label: String, _ action: @escaping () async -> Void) -> some View {
        Button { Task { await action() } } label: {
            Image(systemName: "plus")
                .font(.caption.weight(.bold))
                .foregroundStyle(Color.screen)
                .frame(width: 28, height: 28)
                .background(Circle().fill(.black.opacity(0.6)))
                .padding(6)
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityLabel(label)
    }

    @ViewBuilder private var menu: some View {
        if item.isAlbum, case .requestable = model.albumState(item), let target = MusicRequestTarget.album(item) {
            Button { Task { await model.requestMusic(target) } } label: { Label("Request album", systemImage: "plus") }
        }
        if item.isArtist, item.libraryItemId == nil, item.hasPage, !model.isFollowing(item) {
            Button { Task { await model.requestMusic(.artist(deezerId: item.deezerId)) } } label: { Label("Follow artist", systemImage: "plus") }
        }
        if item.isAlbum, let artist = item.artistPage {
            Button { router.push(.musicArtist(artist)) } label: { Label(artist.title, systemImage: "music.mic") }
        }
    }
}

/// The main button on an outside album's page, or beside each missing album: Request album, Requested, or In your
/// library (which opens it).
struct MusicAlbumRequestButton: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    let album: MusicExternal
    var ambient: Color?
    @State private var asking = false

    var body: some View {
        switch model.albumState(album) {
        case .requestable:
            Button {
                guard let target = MusicRequestTarget.album(album) else { return }
                asking = true
                Task {
                    await model.requestMusic(target)
                    asking = false
                }
            } label: {
                Label("Request album", systemImage: "plus")
            }
            .buttonStyle(ActionButtonStyle(ambient: ambient, prominent: true))
            .disabled(asking)
        case .requested:
            Label("Requested", systemImage: "checkmark")
                .labelStyle(.titleAndIcon)
                .font(.body.weight(.semibold))
                .foregroundStyle(Color.usher)
        case .inLibrary(let id):
            Button { router.push(.musicItem(id: id, isArtist: false)) } label: {
                Label("In your library", systemImage: Symbol.music)
            }
            .buttonStyle(ActionButtonStyle(ambient: ambient, prominent: true))
        case .none:
            EmptyView()
        }
    }
}

/// Follow artist, or Following once Lidarr watches for their new releases.
struct FollowArtistButton: View {
    @Environment(AppModel.self) private var model
    let following: Bool
    var ambient: Color?
    let follow: () async -> Void
    @State private var asking = false

    var body: some View {
        if following {
            Label("Following", systemImage: "checkmark")
                .labelStyle(.titleAndIcon)
                .font(.body.weight(.semibold))
                .foregroundStyle(Color.usher)
        } else {
            Button {
                asking = true
                Task {
                    await follow()
                    asking = false
                }
            } label: {
                Label("Follow artist", systemImage: "plus")
            }
            .buttonStyle(ActionButtonStyle(ambient: ambient))
            .disabled(asking)
        }
    }
}

/// A horizontal row of outside artists or albums under a heading.
struct ExternalMusicRow: View {
    @Environment(\.horizontalSizeClass) private var sizeClass
    let title: String
    let items: [MusicExternal]

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            SectionHeading(title: title)
            ScrollView(.horizontal) {
                LazyHStack(alignment: .top, spacing: Layout.rowSpacing) {
                    ForEach(items) { item in
                        ExternalMusicCard(item: item, width: Layout.posterWidth(sizeClass))
                    }
                }
                .padding(.horizontal, Layout.edge)
            }
            .scrollIndicators(.hidden)
        }
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
