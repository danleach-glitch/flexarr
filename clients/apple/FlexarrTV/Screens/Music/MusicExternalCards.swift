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
/// it); holding it offers Request album (or Unrequest) or Follow, and an album's artist. An album only Lidarr lists has
/// no page: pressing it offers the same, as the iPhone's card does.
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
    /// A card with no page of its own offers its actions when pressed.
    @State private var choosing = false
    /// Unrequest was picked: asks first, as the album page's Requested button does.
    @State private var confirmingUnrequest = false

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
        .contextMenu { menu(fromChooser: false) }
        .confirmationDialog(item.title, isPresented: $choosing, titleVisibility: .visible) {
            menu(fromChooser: true)
            Button(t(Words.cancel), role: .cancel) {}
        }
        .background {
            Color.clear.undoConfirmation(MusicWords.unrequest(item.title), isPresented: $confirmingUnrequest,
                                         keep: MusicWords.keepIt, undo: MusicWords.unrequestAction) { unrequest() }
        }
    }

    private func open() {
        if let route = item.route {
            router.push(route)
        } else if hasChoices {
            choosing = true
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

    /// A library artist can be followed too, on servers that say whether it is (0.59 on).
    private var canFollow: Bool {
        item.isArtist && (item.libraryItemId == nil || item.followed != nil) && item.hasPage && !model.isFollowing(item)
    }

    private var subtitle: String? {
        var parts: [String] = []
        if item.isAlbum {
            if showsYear { if let year = item.year { parts.append(String(year)) } } else if let artist = item.artist { parts.append(artist) }
            switch model.albumState(item) {
            case .requested: parts.append(MusicWords.requested)
            case .inLibrary: parts.append(t(Words.inYourLibrary))
            default: break
            }
        } else if model.isFollowing(item) {
            parts.append(MusicWords.following)
        } else if item.libraryItemId != nil {
            parts.append(t(Words.inYourLibrary))
        }
        return parts.isEmpty ? nil : parts.joined(separator: "  ")
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

/// Whether a request's button is still up when its answer comes. The button changes at once, so people often leave
/// straight away: a failure then goes to the app's alert, as nobody's looking beside the button any more. A class, as a
/// button that has gone keeps no state of its own to ask.
@MainActor
private final class ButtonPresence {
    var isShown = false

    /// What the button says after this answer: nothing when it went through; why not, beside it, while it's up.
    func settled(_ failure: String?, model: AppModel) -> MusicSending {
        guard let failure else { return .idle }
        if isShown { return .failed(failure) }
        model.requestError = failure
        return .idle
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

/// An outside album's page button (`MusicActions.request`): a round Request album, or Requested, shown on, which
/// offers to cancel; or In your library (which opens it), spelled out. One button throughout, so focus never lands on
/// nothing when it changes; none at all for an album that can't be asked for. While Lidarr isn't answering it stays
/// focusable but dimmed, and presses do nothing.
struct MusicAlbumRequestButton: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    @Environment(\.musicRequestsPaused) private var paused
    let album: MusicExternal
    /// The page's first button, which takes focus when a link opens the page.
    var takesLinkFocus = false
    /// The page's focused round button, for its name under the row.
    var focus: FocusState<MusicActions.Button?>.Binding? = nil
    @State private var sending = MusicSending.idle
    @State private var confirming = false
    @State private var presence = ButtonPresence()

    var body: some View {
        content
            .onAppear { presence.isShown = true }
            .onDisappear { presence.isShown = false }
    }

    @ViewBuilder private var content: some View {
        let state = model.albumState(album)
        if case .none = state {
            EmptyView()
        } else if case .inLibrary(let id) = state {
            Button { router.push(.musicItem(id: id, isArtist: false)) } label: {
                Label(t(Words.inYourLibrary), systemImage: Symbol.play)
            }
            .buttonStyle(ActionButtonStyle(prominent: true))
            .modifier(ActionFocus(binding: focus, button: .request))
            .takesFocusFromLink(takesLinkFocus)
        } else {
            let requested = isRequested(state)
            HStack(spacing: 30) {
                Button {
                    guard sending != .sending, !paused else { return }
                    if requested { confirming = true } else { request() }
                } label: {
                    Label(requested ? MusicWords.requested : MusicWords.requestAlbum, systemImage: requested ? Symbol.requested : Symbol.request)
                }
                .buttonStyle(ActionButtonStyle(iconOnly: true, selected: requested))
                .opacity(paused ? 0.5 : 1)
                .accessibilityLabel(requested ? MusicWords.requested : MusicWords.requestAlbum)
                .accessibilityHint(requested ? MusicWords.unrequestHint : "")
                .modifier(ActionFocus(binding: focus, button: .request))
                .takesFocusFromLink(takesLinkFocus)
                .undoConfirmation(MusicWords.unrequest(album.title), isPresented: $confirming, keep: MusicWords.keepIt,
                                  undo: MusicWords.unrequestAction) { unrequest() }
                RequestFailure(message: sending.failure)
            }
        }
    }

    private func isRequested(_ state: MusicAlbumState) -> Bool {
        if case .requested = state { return true }
        return false
    }

    private func request() {
        guard let target = MusicRequestTarget.album(album) else { return }
        sending = .sending
        Task {
            settle(await model.sendMusicRequest(target))
        }
    }

    private func unrequest() {
        sending = .sending
        Task {
            settle(await model.unrequestAlbum(album))
        }
    }

    private func settle(_ failure: String?) {
        sending = presence.settled(failure, model: model)
    }
}

/// An artist's round Follow button (`MusicActions.follow`), or Following, shown on, which offers to stop. One button
/// throughout (see above): while it can't be pressed (before there's anything to follow by, or Lidarr isn't answering)
/// it stays focusable but dimmed, and presses do nothing. By the library artist's id on a library page, Deezer's on an
/// outside one.
struct FollowArtistButton: View {
    @Environment(AppModel.self) private var model
    @Environment(\.musicRequestsPaused) private var paused
    let name: String
    let following: Bool
    var libraryId: String? = nil
    var deezerId: Int? = nil
    var ambient: Color? = nil
    /// Off until there's something to follow by (the outside page's detail has loaded).
    var enabled = true
    /// The page's first button, which takes focus when a link opens the page.
    var takesLinkFocus = false
    /// The page's focused round button, for its name under the row.
    var focus: FocusState<MusicActions.Button?>.Binding? = nil
    /// After unfollowing went through (a placeholder artist leaves the library then).
    var unfollowed: () -> Void = {}
    @State private var sending = MusicSending.idle
    @State private var confirming = false
    @State private var presence = ButtonPresence()

    var body: some View {
        HStack(spacing: 30) {
            Button {
                guard enabled, !paused, sending != .sending else { return }
                if following { confirming = true } else { follow() }
            } label: {
                Label(following ? MusicWords.following : MusicWords.follow, systemImage: following ? Symbol.following : Symbol.follow)
            }
            .buttonStyle(ActionButtonStyle(ambient: ambient, iconOnly: true, selected: following))
            .opacity(!enabled || paused ? 0.5 : 1)
            .accessibilityLabel(following ? MusicWords.following : MusicWords.follow)
            .accessibilityHint(following ? MusicWords.stopFollowingHint : "")
            .modifier(ActionFocus(binding: focus, button: .follow))
            .takesFocusFromLink(takesLinkFocus)
            .undoConfirmation(MusicWords.stopFollowing(name), isPresented: $confirming, keep: MusicWords.keepFollowing,
                              undo: MusicWords.stopFollowingAction) { unfollow() }
            RequestFailure(message: sending.failure)
        }
        .onAppear { presence.isShown = true }
        .onDisappear { presence.isShown = false }
    }

    private func settle(_ failure: String?) {
        sending = presence.settled(failure, model: model)
    }

    private func follow() {
        sending = .sending
        Task {
            settle(await model.followArtist(libraryId: libraryId, deezerId: deezerId))
        }
    }

    private func unfollow() {
        sending = .sending
        Task {
            if let failure = await model.unfollowArtist(libraryId: libraryId, deezerId: deezerId) {
                settle(failure)
            } else {
                settle(nil)
                unfollowed()
            }
        }
    }
}

extension View {
    /// Asks before taking music back (Stop following, Unrequest), Keep being the safe answer. Keep has the cancel role:
    /// tvOS sets the cancel button first and focuses it (checked on tvOS 27, both as a confirmation dialog and an
    /// alert), so pressing straight through keeps things as they were, and so does Menu. Without a cancel button the
    /// system adds a Cancel of its own as well.
    func undoConfirmation(_ title: String, isPresented: Binding<Bool>, keep: String, undo: String,
                          action: @escaping () -> Void) -> some View {
        confirmationDialog(title, isPresented: isPresented, titleVisibility: .visible) {
            Button(keep, role: .cancel) {}
            Button(undo, role: .destructive, action: action)
        }
    }
}

/// Ties a round button to its page's focus, for the focused button's name under the row.
struct ActionFocus: ViewModifier {
    let binding: FocusState<MusicActions.Button?>.Binding?
    let button: MusicActions.Button

    func body(content: Content) -> some View {
        if let binding { content.focused(binding, equals: button) } else { content }
    }
}

/// The name of a music page's focused round button, under the row (as a film's page does); the line keeps its height
/// so nothing jumps.
struct FocusedActionName: View {
    let name: String?

    var body: some View {
        Text(name ?? " ")
            .font(.system(size: 24, weight: .medium))
            .foregroundStyle(Color.screen.opacity(0.75))
            .animation(.easeOut(duration: 0.15), value: name)
            .accessibilityHidden(true)
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
