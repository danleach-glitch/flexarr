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

/// An artist or album the library doesn't have (or does, marked): tapping opens its page; holding it offers Request
/// album or Follow, and an album's artist.
struct ExternalMusicCard: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    @Environment(\.musicRequestsPaused) private var paused
    let item: MusicExternal
    var width: CGFloat?
    /// An album's year under its title, on its artist's page; the artist's name elsewhere.
    var showsYear = false
    /// "Artist", or "Album" and the artist, as search's film cards say Movie or Show.
    var showsKind = false
    /// A request on its way: a second press waits for it rather than sending another.
    @State private var asking = false
    /// A card with no page of its own (an album only Lidarr lists) offers its actions when tapped.
    @State private var choosing = false
    /// Unrequest was picked: asks first, as the album page's Requested button does.
    @State private var confirmingUnrequest = false

    var body: some View {
        Button {
            if let route = item.route { router.push(route) } else if hasChoices { choosing = true }
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
        .accessibilityLabel(item.accessibilityName)
        .accessibilityValue(state ?? "")
        .contextMenu { menu(fromChooser: false) }
        .confirmationDialog(item.title, isPresented: $choosing, titleVisibility: .visible) {
            menu(fromChooser: true)
            Button(t(Words.cancel), role: .cancel) {}
        }
        .background {
            Color.clear.confirmationDialog(MusicWords.unrequest(item.title), isPresented: $confirmingUnrequest,
                                           titleVisibility: .visible) {
                Button(MusicWords.unrequestAction, role: .destructive) { unrequest() }
                Button(MusicWords.keepIt, role: .cancel) {}
            }
        }
    }

    /// Whether the hold menu (and the chooser of a card with no page) has anything in it.
    private var hasChoices: Bool {
        if item.isAlbum {
            switch model.albumState(item) {
            case .requestable where MusicRequestTarget.album(item) != nil, .requested: return true
            default: return item.artistPage != nil
            }
        }
        return canFollow
    }

    private var subtitle: String? {
        var parts: [String] = []
        if showsKind { parts.append(item.isArtist ? t("Artist") : t("Album")) }
        if item.isAlbum {
            if showsYear { if let year = item.year { parts.append(String(year)) } } else if let artist = item.artist { parts.append(artist) }
        }
        if let state { parts.append(state) }
        return parts.isEmpty ? nil : parts.joined(separator: ", ")
    }

    /// Requested, In your library or Following.
    private var state: String? {
        if item.isAlbum {
            switch model.albumState(item) {
            case .requested: return MusicWords.requested
            case .inLibrary: return t(Words.inYourLibrary)
            default: return nil
            }
        }
        if model.isFollowing(item) { return MusicWords.following }
        return item.libraryItemId != nil ? t(Words.inYourLibrary) : nil
    }

    /// A library artist can be followed too, on servers that say whether it is (0.59 on).
    private var canFollow: Bool {
        item.isArtist && (item.libraryItemId == nil || item.followed != nil) && item.hasPage && !model.isFollowing(item)
    }

    @ViewBuilder private func menu(fromChooser: Bool) -> some View {
        if item.isAlbum, case .requestable = model.albumState(item), let target = MusicRequestTarget.album(item) {
            Button { ask(target) } label: { Label(MusicWords.requestAlbum, systemImage: Symbol.request) }
                .disabled(asking || paused)
        }
        if item.isAlbum, case .requested = model.albumState(item) {
            Button(role: .destructive) {
                guard !asking, !paused else { return }
                if fromChooser {
                    // The chooser closes first: a second dialog can't open while it's still on its way out.
                    Task {
                        try? await Task.sleep(for: .milliseconds(400))
                        confirmingUnrequest = true
                    }
                } else {
                    confirmingUnrequest = true
                }
            } label: { Label(MusicWords.unrequestAction, systemImage: Symbol.requested) }
                .disabled(asking || paused)
        }
        if canFollow {
            Button { ask(.artist(deezerId: item.deezerId)) } label: { Label(MusicWords.follow, systemImage: Symbol.follow) }
                .disabled(asking || paused)
        }
        if item.isAlbum, let artist = item.artistPage {
            Button { router.push(.musicArtist(artist)) } label: { Label(t(Words.goToArtist), systemImage: Symbol.goToArtist) }
        }
    }

    private func unrequest() {
        guard !asking, !paused else { return }
        asking = true
        Task {
            if let failure = await model.unrequestAlbum(item) { model.requestError = failure }
            asking = false
        }
    }

    private func ask(_ target: MusicRequestTarget) {
        guard !asking, !paused else { return }
        asking = true
        Task {
            await model.requestMusic(target)
            asking = false
        }
    }
}

/// The round Request album button on an album's page (`MusicActions.request`), or Requested, shown on, which offers to
/// cancel the request. Nothing for an album in the library (the page opens it) or one that can't be asked for.
/// `labelled` spells it out, for a page where it's the only button.
struct MusicAlbumRequestButton: View {
    @Environment(AppModel.self) private var model
    @Environment(\.musicRequestsPaused) private var paused
    let album: MusicExternal
    var ambient: Color = .silver
    var labelled = false
    @State private var asking = false
    @State private var confirming = false

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
                Label(MusicWords.requestAlbum, systemImage: Symbol.request)
            }
            .buttonStyle(RoundActionStyle(ambient: ambient, labelled: labelled))
            .disabled(asking || paused)
            .accessibilityLabel(MusicWords.requestAlbum)
        case .requested:
            Button { confirming = true } label: {
                Label(MusicWords.requested, systemImage: Symbol.requested)
            }
            .buttonStyle(RoundActionStyle(selected: true, ambient: ambient, labelled: labelled))
            .disabled(asking || paused)
            .accessibilityLabel(MusicWords.requested)
            .accessibilityHint(MusicWords.unrequestHint)
            .accessibilityAddTraits(.isSelected)
            .confirmationDialog(MusicWords.unrequest(album.title), isPresented: $confirming, titleVisibility: .visible) {
                Button(MusicWords.unrequestAction, role: .destructive) {
                    asking = true
                    Task {
                        if let failure = await model.unrequestAlbum(album) { model.requestError = failure }
                        asking = false
                    }
                }
                Button(MusicWords.keepIt, role: .cancel) {}
            }
        case .inLibrary, .none:
            EmptyView()
        }
    }
}

/// The round Follow button on an artist's page (`MusicActions.follow`), or Following, shown on, which offers to stop.
/// By the library artist's id on a library page, Deezer's on an outside one; following marks both, so either page
/// says Following. `labelled` spells it out, for a page where it's the only button.
struct FollowArtistButton: View {
    @Environment(AppModel.self) private var model
    @Environment(\.musicRequestsPaused) private var paused
    let name: String
    let following: Bool
    var libraryId: String? = nil
    var deezerId: Int? = nil
    var ambient: Color = .silver
    var labelled = false
    /// Off until there's something to follow by (the outside page's detail has loaded).
    var enabled = true
    /// After unfollowing went through (a placeholder artist leaves the library then).
    var unfollowed: () -> Void = {}
    @State private var asking = false
    @State private var confirming = false

    var body: some View {
        Button {
            if following {
                confirming = true
            } else {
                follow()
            }
        } label: {
            Label(following ? MusicWords.following : MusicWords.follow, systemImage: following ? Symbol.following : Symbol.follow)
        }
        .buttonStyle(RoundActionStyle(selected: following, ambient: ambient, labelled: labelled))
        .disabled(asking || paused || !enabled)
        .accessibilityLabel(following ? MusicWords.following : MusicWords.follow)
        .accessibilityHint(following ? MusicWords.stopFollowingHint : "")
        .accessibilityAddTraits(following ? .isSelected : [])
        .confirmationDialog(MusicWords.stopFollowing(name), isPresented: $confirming, titleVisibility: .visible) {
            Button(MusicWords.stopFollowingAction, role: .destructive) { unfollow() }
            Button(MusicWords.keepFollowing, role: .cancel) {}
        }
    }

    private func follow() {
        asking = true
        Task {
            if let failure = await model.followArtist(libraryId: libraryId, deezerId: deezerId) { model.requestError = failure }
            asking = false
        }
    }

    private func unfollow() {
        asking = true
        Task {
            if let failure = await model.unfollowArtist(libraryId: libraryId, deezerId: deezerId) {
                model.requestError = failure
            } else {
                unfollowed()
            }
            asking = false
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
                    ForEach(items.listed) { entry in
                        ExternalMusicCard(item: entry.item, width: Layout.posterWidth(sizeClass))
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
