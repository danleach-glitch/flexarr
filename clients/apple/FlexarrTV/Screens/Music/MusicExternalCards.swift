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
/// it); holding it offers Request album or Follow artist. An album only Lidarr lists has no page: pressing it does
/// nothing, and holding it offers Request album.
struct ExternalMusicCard: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    @Environment(\.musicRequestsPaused) private var paused
    let item: MusicExternal
    var width: CGFloat = Layout.posterWidth
    /// An album's year under its title, on its artist's page; the artist's name elsewhere.
    var showsYear = false
    var focus: FocusState<String?>.Binding? = nil
    var focusID: String? = nil
    /// A request on its way: a second press waits for it rather than sending another.
    @State private var asking = false

    var body: some View {
        Group {
            if item.isArtist {
                VStack(spacing: 18) {
                    Button(action: open) {
                        ExternalMusicArtwork(image: item.image, title: item.title, size: width, circular: true)
                    }
                    .buttonStyle(CircleCardStyle())
                    .modifier(SharedFocus(binding: focus, id: focusID ?? item.id))
                    .accessibilityLabel(item.accessibilityName)
                    .accessibilityValue(subtitle ?? "")
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
                    .accessibilityHidden(true)
                }
            } else {
                ArtworkButton(ambient: .silver, focus: focus, focusID: focusID ?? item.id,
                              accessibilityText: [item.accessibilityName, subtitle].compactMap { $0 }.joined(separator: ", "),
                              action: open) {
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
                    .accessibilityHidden(true)
                }
            }
        }
        .contextMenu { menu }
    }

    private func open() {
        if let route = item.route { router.push(route) }
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
            Button { ask(target) } label: { Label("Request album", systemImage: Symbol.request) }
                .disabled(asking || paused)
        }
        if item.isArtist, item.libraryItemId == nil, item.hasPage, !model.isFollowing(item) {
            Button { ask(.artist(deezerId: item.deezerId)) } label: { Label("Follow artist", systemImage: Symbol.follow) }
                .disabled(asking || paused)
        }
        if item.isAlbum, let artist = item.artistPage {
            Button { router.push(.musicArtist(artist)) } label: { Label("Go to artist", systemImage: Symbol.goToArtist) }
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
                    ForEach(items.listed) { entry in
                        ExternalMusicCard(item: entry.item, width: entry.item.isArtist ? 220 : Layout.posterWidth, focus: focus,
                                          focusID: Self.focusID(focusPrefix, entry))
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

    /// A card's focus id in a row: the row's prefix and the item's place and id, which can't collide.
    static func focusID(_ prefix: String, _ entry: MusicListEntry) -> String { "\(prefix)/\(entry.id)" }
}

/// What a request button is doing: idle, sending, or failed with why (said beside it, the button offering Try again),
/// as a film's Request does.
private enum MusicSending: Equatable {
    case idle, sending
    case failed(String)

    var failure: String? {
        if case .failed(let message) = self { return message }
        return nil
    }
}

/// Why a request didn't go through, beside its button.
private struct RequestFailure: View {
    let message: String?

    var body: some View {
        if let message {
            Text(message)
                .font(.system(size: 24, weight: .medium))
                .foregroundStyle(Color.screen.opacity(0.85))
                .lineLimit(2)
                .frame(maxWidth: 700, alignment: .leading)
        }
    }
}

/// The main button on an outside album's page: Request album, Requested, or In your library (which opens it). One
/// button throughout, so focus never lands on nothing when it changes; none at all for an album that can't be asked
/// for.
struct MusicAlbumRequestButton: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    @Environment(\.musicRequestsPaused) private var paused
    let album: MusicExternal
    /// The page's first button, which takes focus when a link opens the page.
    var takesLinkFocus = false
    @State private var sending = MusicSending.idle

    var body: some View {
        let state = model.albumState(album)
        if case .none = state {
            EmptyView()
        } else {
            HStack(spacing: 30) {
                Button {
                    switch state {
                    case .requestable:
                        guard sending != .sending, !paused, let target = MusicRequestTarget.album(album) else { return }
                        sending = .sending
                        Task {
                            if let failure = await model.sendMusicRequest(target) { sending = .failed(failure) } else { sending = .idle }
                        }
                    case .inLibrary(let id):
                        router.push(.musicItem(id: id, isArtist: false))
                    case .requested, .none:
                        break
                    }
                } label: {
                    switch state {
                    case .requestable: Label(sending.failure == nil ? "Request album" : "Try again", systemImage: Symbol.request)
                    case .requested: Label("Requested", systemImage: Symbol.requested)
                    case .inLibrary: Label("In your library", systemImage: Symbol.play)
                    case .none: EmptyView()
                    }
                }
                .buttonStyle(ActionButtonStyle(prominent: isProminent(state)))
                .opacity(paused && isRequestable(state) ? 0.5 : 1)
                .takesFocusFromLink(takesLinkFocus)
                RequestFailure(message: isRequestable(state) ? sending.failure : nil)
            }
        }
    }

    private func isRequestable(_ state: MusicAlbumState) -> Bool {
        if case .requestable = state { return true }
        return false
    }

    private func isProminent(_ state: MusicAlbumState) -> Bool {
        switch state {
        case .requestable, .inLibrary: true
        default: false
        }
    }
}

/// Follow artist, or Following once Lidarr watches for their new releases. One button throughout (see above): while
/// it can't be pressed (Following, or before there's anything to follow by) it stays focusable but dimmed, and presses
/// do nothing.
struct FollowArtistButton: View {
    @Environment(AppModel.self) private var model
    @Environment(\.musicRequestsPaused) private var paused
    let following: Bool
    /// Off until there's something to follow by (the outside page's detail has loaded).
    var enabled = true
    let target: MusicRequestTarget
    /// The page's first button, which takes focus when a link opens the page.
    var takesLinkFocus = false
    @State private var sending = MusicSending.idle

    var body: some View {
        HStack(spacing: 30) {
            Button {
                guard !following, enabled, !paused, sending != .sending else { return }
                sending = .sending
                Task {
                    if let failure = await model.sendMusicRequest(target) { sending = .failed(failure) } else { sending = .idle }
                }
            } label: {
                if following {
                    Label("Following", systemImage: Symbol.following)
                } else {
                    Label(sending.failure == nil ? "Follow artist" : "Try again", systemImage: Symbol.follow)
                }
            }
            .buttonStyle(ActionButtonStyle())
            .opacity(following || !enabled || paused ? 0.5 : 1)
            .takesFocusFromLink(takesLinkFocus)
            RequestFailure(message: following ? nil : sending.failure)
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
