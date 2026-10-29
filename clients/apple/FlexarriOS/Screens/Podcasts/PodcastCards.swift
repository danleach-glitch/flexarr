import FlexarrKit
import SwiftUI

/// A show's square artwork, from the server's image cache, with its name on a gradient until it arrives.
struct PodcastArtwork: View {
    @Environment(AppModel.self) private var model
    let path: String?
    var title = ""
    var accent: String?
    var imageWidth = 500
    var cornerRadius: CGFloat = 8

    var body: some View {
        Color.seat
            .aspectRatio(1, contentMode: .fit)
            .overlay {
                RemoteImage(url: model.client?.imageURL(path, width: imageWidth)) {
                    ZStack {
                        LinearGradient(colors: [podcastGlow(accent).opacity(0.4), Color.seat], startPoint: .top, endPoint: .bottom)
                        Text(title)
                            .font(.marquee(15, weight: .bold))
                            .foregroundStyle(Color.screen.opacity(0.8))
                            .multilineTextAlignment(.center)
                            .padding(8)
                    }
                }
            }
            .clipShape(RoundedRectangle(cornerRadius: cornerRadius, style: .continuous))
    }
}

/// A show's ambient colour, lifted to read as a glow on the room background.
func podcastGlow(_ accent: String?) -> Color {
    Color(rgb: (RGB(hex: accent) ?? .silver).glow)
}

/// A followed show: its artwork (with its name beneath, unless the row is artwork alone) and how many episodes wait;
/// tapping opens it.
struct PodcastCard: View {
    @Environment(Router.self) private var router
    let podcast: Podcast
    var showsTitle = true

    var body: some View {
        Button { router.push(.item(podcast.summary)) } label: {
            VStack(alignment: .leading, spacing: 6) {
                PodcastArtwork(path: podcast.artwork.poster, title: podcast.title, accent: podcast.accentColor)
                    .overlay(alignment: .topTrailing) {
                        if podcast.unplayedCount > 0, podcast.isSubscribed {
                            Text(podcast.unplayedCount > 99 ? "99+" : String(podcast.unplayedCount))
                                .font(.caption2.weight(.bold))
                                .monospacedDigit()
                                .foregroundStyle(Color.room)
                                .padding(.horizontal, 6)
                                .frame(minWidth: 22, minHeight: 22)
                                .background(Capsule().fill(Color.screen))
                                .padding(6)
                        }
                    }
                if showsTitle {
                    Text(podcast.title)
                        .font(.subheadline.weight(.semibold))
                        .foregroundStyle(Color.screen)
                        .lineLimit(2)
                        .multilineTextAlignment(.leading)
                }
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(CardButtonStyle())
        .accessibilityLabel("\(podcast.title), \(podcast.countLine)")
    }
}

/// A show from Apple's directory: artwork, name and who makes it; tapping adds it to the library and opens it.
struct DirectoryCard: View {
    let show: DirectoryPodcast
    var isAdding = false
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            VStack(alignment: .leading, spacing: 6) {
                PodcastArtwork(path: show.artwork, title: show.title)
                    .overlay {
                        if isAdding {
                            ZStack {
                                Color.black.opacity(0.45)
                                ProgressView().tint(Color.screen)
                            }
                            .clipShape(RoundedRectangle(cornerRadius: 8, style: .continuous))
                        }
                    }
                    .overlay(alignment: .topTrailing) {
                        if show.subscribed {
                            Image(systemName: "checkmark.circle.fill")
                                .font(.title3)
                                .symbolRenderingMode(.palette)
                                .foregroundStyle(Color.room, Color.screen)
                                .padding(6)
                                .accessibilityLabel("Following")
                        }
                    }
                Text(show.title)
                    .font(.subheadline.weight(.semibold))
                    .foregroundStyle(Color.screen)
                    .lineLimit(2)
                    .multilineTextAlignment(.leading)
                if let author = show.author {
                    Text(author)
                        .font(.caption)
                        .foregroundStyle(Color.usher)
                        .lineLimit(1)
                }
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(CardButtonStyle())
        .disabled(!show.hasFeed)
        .opacity(show.hasFeed ? 1 : 0.5)
    }
}

/// An episode in a list: when it came out, its title and the start of its notes, how long is left and its download,
/// with Play (and how far it's got) on the right. Tapping the row opens the whole notes. In a `List`, swiping right marks it
/// played (or unplayed again), and swiping left downloads it or removes the download.
struct PodcastEpisodeRow: View {
    @Environment(AppModel.self) private var model
    @Environment(DownloadStore.self) private var downloads
    let episode: PodcastEpisode
    /// Lists of several shows' episodes name the show and show its artwork.
    var showsPodcast = false
    /// A short row (New episodes): without the notes.
    var compact = false
    /// Marks it played or unplayed in the list's own way (the show page shows it at once); otherwise it's sent and
    /// screens reload.
    var setPlayed: ((Bool) -> Void)?
    /// In Up Next: swiping left takes it off, rather than downloading.
    var removeFromUpNext: (() -> Void)?
    @State private var showingNotes = false

    private var isCurrent: Bool { model.music.current?.id == episode.id }

    var body: some View {
        row
        .padding(.vertical, compact ? 10 : 12)
        .contentShape(Rectangle())
        // Not a Button: in a List, the row's own buttons (Play, download) then take their own taps.
        .onTapGesture { showingNotes = true }
        .accessibilityAddTraits(.isButton)
        .accessibilityAction(named: "Show notes") { showingNotes = true }
        .accessibilityAction(named: episode.isPlayed ? "Mark as unplayed" : "Mark as played") { togglePlayed() }
        .swipeActions(edge: .leading, allowsFullSwipe: true) {
            Button { togglePlayed() } label: {
                Label(episode.isPlayed ? "Unplayed" : "Played", systemImage: episode.isPlayed ? "arrow.uturn.backward.circle" : "checkmark.circle.fill")
            }
            .tint(episode.isPlayed ? Color.aisle : Color.played)
            if removeFromUpNext == nil {
                Button { Task { await model.addToUpNext(episode.id, next: true) } } label: {
                    Label("Play next", systemImage: "text.line.first.and.arrowtriangle.forward")
                }
                .tint(Color.upNext)
            }
        }
        .swipeActions(edge: .trailing, allowsFullSwipe: removeFromUpNext != nil) {
            if let removeFromUpNext {
                Button(role: .destructive, action: removeFromUpNext) { Label("Remove from Up Next", systemImage: "minus.circle") }
            } else {
                Button { Task { await model.addToUpNext(episode.id, next: false) } } label: {
                    Label("Add to Up Next", systemImage: "text.line.last.and.arrowtriangle.forward")
                }
                .tint(Color.upNext)
                downloadSwipe
            }
        }
        .contextMenu {
            Button { model.playEpisode(episode.summary) } label: { Label(episode.progress != nil ? "Resume" : "Play", systemImage: "play.fill") }
            Button { Task { await model.addToUpNext(episode.id, next: true) } } label: {
                Label("Play next", systemImage: "text.line.first.and.arrowtriangle.forward")
            }
            Button { Task { await model.addToUpNext(episode.id, next: false) } } label: {
                Label("Add to Up Next", systemImage: "text.line.last.and.arrowtriangle.forward")
            }
            if let removeFromUpNext {
                Button(role: .destructive, action: removeFromUpNext) { Label("Remove from Up Next", systemImage: "minus.circle") }
            }
            Button { togglePlayed() } label: {
                Label(episode.isPlayed ? "Mark as unplayed" : "Mark as played", systemImage: episode.isPlayed ? "circle" : "checkmark.circle")
            }
            // Part way through: unplayed forgets the place, so it leaves the listening list.
            if episode.progress != nil {
                Button { markUnplayed() } label: { Label("Mark as unplayed", systemImage: "circle") }
            }
        }
        .sheet(isPresented: $showingNotes) {
            EpisodeSheet(episode: episode, setPlayed: setPlayed)
                .presentationDetents([.medium, .large])
                .presentationBackground(Color.room)
        }
    }

    @ViewBuilder
    private var downloadSwipe: some View {
            if let download = downloads.entry(for: episode.id) {
                Button(role: .destructive) { downloads.remove(download.id) } label: {
                    Label(download.isDownloaded ? "Remove download" : "Stop download", systemImage: "trash")
                }
            } else {
                Button { Task { try? await downloads.download(itemId: episode.id, quality: .original) } } label: {
                    Label("Download", systemImage: "arrow.down.circle")
                }
                .tint(Color.aisle)
            }
    }

    private var metaLine: some View {
        Text([showsPodcast ? episode.podcastTitle : nil, episode.dateLine].compactMap { $0 }.joined(separator: "  "))
            .font(.caption.weight(.semibold))
            .foregroundStyle(Color.usher)
            .lineLimit(1)
    }

    private var title: some View {
        Text(episode.title)
            .font(.body.weight(.semibold))
            .foregroundStyle(episode.isPlayed ? Color.usher : Color.screen)
            .lineLimit(2)
            .multilineTextAlignment(.leading)
            .fixedSize(horizontal: false, vertical: true)
            .frame(maxWidth: .infinity, alignment: .leading)
    }

    private var artwork: some View {
        PodcastArtwork(path: episode.artwork.thumb ?? episode.artwork.poster, title: episode.podcastTitle, accent: episode.accentColor,
                       imageWidth: 185, cornerRadius: 6)
            .frame(width: 56, height: 56)
    }

    /// Show and date, the title, two lines of notes (unless compact), then its length and download; Play on the
    /// right, beside the title.
    private var row: some View {
        // Play has a column of its own, centred on the title, so the text never runs under it.
        HStack(alignment: .center, spacing: 12) {
            if showsPodcast { artwork }
            HStack(alignment: .titleLine, spacing: 14) {
            VStack(alignment: .leading, spacing: 3) {
                metaLine
                title
                    .alignmentGuide(.titleLine) { $0[VerticalAlignment.center] }
                if !compact, let overview = episode.overview {
                    Text(overview)
                        .font(.subheadline)
                        .foregroundStyle(Color.usher)
                        .lineLimit(2)
                        .multilineTextAlignment(.leading)
                }
                HStack(spacing: 8) {
                    if let time = episode.timeLine {
                        Text(time)
                            .font(.caption.weight(.semibold))
                            .monospacedDigit()
                            .foregroundStyle(Color.usher)
                    }
                    EpisodeDownloadButton(episode: episode, small: true)
                }
                .padding(.top, compact ? 0 : 2)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            EpisodePlayButton(episode: episode)
                .alignmentGuide(.titleLine) { $0[VerticalAlignment.center] }
            }
        }
    }

    private func togglePlayed() {
        if let setPlayed {
            setPlayed(!episode.isPlayed)
            return
        }
        Task {
            await model.setWatched(episode.summary, !episode.isPlayed)
            await downloads.reconcilePodcasts()
        }
    }

    private func markUnplayed() {
        if let setPlayed {
            setPlayed(false)
            return
        }
        Task { await model.setWatched(episode.summary, false) }
    }
}

extension VerticalAlignment {
    /// The middle of an episode row's title, which Play (and the artwork) line up with.
    private enum TitleLine: AlignmentID {
        static func defaultValue(in context: ViewDimensions) -> CGFloat { context[VerticalAlignment.center] }
    }

    static let titleLine = VerticalAlignment(TitleLine.self)
}

extension Color {
    /// A swipe's "played" tint, as podcast apps use.
    static let played = Color(rgb: RGB(hex: "#6B5BB5")!)
    /// A swipe's "Up Next" tint.
    static let upNext = Color(rgb: RGB(hex: "#4A6FA5")!)
}

extension View {
    /// A row of a List that draws itself: no background, no separator, edge to edge.
    func bareListRow() -> some View {
        listRowInsets(EdgeInsets()).listRowBackground(Color.clear).listRowSeparator(.hidden)
    }

    /// An episode row in a List: on the shared edge, with a quiet separator, at a readable width on iPad.
    func episodeListRow() -> some View {
        frame(maxWidth: 760, alignment: .leading)
            .frame(maxWidth: .infinity)
            .listRowInsets(EdgeInsets(top: 0, leading: Layout.edge, bottom: 0, trailing: Layout.edge))
            .listRowBackground(Color.clear)
            .listRowSeparatorTint(Color.aisle)
    }
}

/// Play (or pause, for the one playing), ringed with how far the episode has got.
struct EpisodePlayButton: View {
    @Environment(AppModel.self) private var model
    let episode: PodcastEpisode

    var body: some View {
        let current = model.music.current?.id == episode.id
        let playing = current && model.music.isPlaying
        let fraction = current && model.music.duration > 0 ? model.music.position / model.music.duration : episode.progress ?? 0
        Button {
            if current { model.music.togglePlayPause() } else { model.playEpisode(episode.summary) }
        } label: {
            ZStack {
                Circle().stroke(Color.screen.opacity(0.25), lineWidth: 2)
                if fraction > 0 {
                    Circle().trim(from: 0, to: fraction).stroke(Color.screen, style: StrokeStyle(lineWidth: 2, lineCap: .round))
                        .rotationEffect(.degrees(-90))
                }
                Image(systemName: playing ? "pause.fill" : "play.fill")
                    .font(.caption.weight(.bold))
                    .foregroundStyle(Color.screen)
            }
            .frame(width: 30, height: 30)
            .contentShape(Circle())
        }
        .buttonStyle(.borderless)
        .accessibilityLabel(playing ? "Pause" : "Play \(episode.title)")
    }
}

/// An episode's download: an arrow, a ring while it comes, a tick once it's here. Tapping a tick offers to remove it.
/// Downloads asked for here use mobile data too; the ones the phone fetches by itself wait for Wi-Fi.
struct EpisodeDownloadButton: View {
    @Environment(DownloadStore.self) private var store
    let episode: PodcastEpisode
    var size: CGFloat = 30
    /// A small mark on a line of text (New episodes): the glyph about the text's height, the tap target still 44 pt.
    var small = false
    @State private var removing = false
    @State private var error: String?
    @State private var failure: String?

    var body: some View {
        let download = store.entry(for: episode.id)
        let waiting = download.map(store.isWaitingForWiFi) ?? false
        Button {
            switch download?.state {
            case nil: Task { await start() }
            case .failed(let reason): failure = reason
            default: removing = true
            }
        } label: {
            DownloadStateIcon(state: download?.state, waitingForWiFi: waiting, size: small ? 16 : size * 0.75)
                .font(small ? .system(size: 15, weight: .semibold) : .title3)
                .foregroundStyle(Color.screen)
                .frame(width: small ? 18 : size, height: small ? 18 : size)
                // At least 44 pt to tap, though it draws small.
                .contentShape(Rectangle().inset(by: small ? -13 : min(0, (size - 44) / 2)))
        }
        .buttonStyle(.borderless)
        .accessibilityLabel(waiting ? "Waiting for Wi-Fi" : DownloadStateIcon.accessibilityLabel(download?.state))
        .confirmationDialog(download?.isDownloaded == true ? "Remove this download?" : "Stop this download?", isPresented: $removing, titleVisibility: .visible) {
            Button(download?.isDownloaded == true ? "Remove download" : "Stop download", role: .destructive) {
                if let download { store.remove(download.id) }
            }
            Button("Cancel", role: .cancel) {}
        }
        .alert("Couldn't download this", isPresented: Binding(get: { failure != nil }, set: { if !$0 { failure = nil } })) {
            Button("Try again") {
                if let download { store.delete(download.id) }
                Task { await start() }
            }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text(failure ?? "")
        }
        .alert("Couldn't download this", isPresented: Binding(get: { error != nil }, set: { if !$0 { error = nil } })) {
            Button("OK", role: .cancel) {}
        } message: {
            Text(error ?? "")
        }
    }

    private func start() async {
        do {
            try await store.download(itemId: episode.id, quality: .original)
        } catch {
            self.error = ConnectionError(error).message
        }
    }
}

/// An episode's whole notes, with Play, its download and Mark as played.
struct EpisodeSheet: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    @Environment(\.dismiss) private var dismiss
    let episode: PodcastEpisode
    var setPlayed: ((Bool) -> Void)?
    @State private var full: PodcastEpisode?
    @State private var played: Bool?

    private var shown: PodcastEpisode { full ?? episode }

    var body: some View {
        ScrollView(.vertical) {
            VStack(alignment: .leading, spacing: 16) {
                HStack(alignment: .top, spacing: 14) {
                    PodcastArtwork(path: shown.artwork.thumb ?? shown.artwork.poster, title: shown.podcastTitle, accent: shown.accentColor, imageWidth: 342)
                        .frame(width: 88, height: 88)
                    VStack(alignment: .leading, spacing: 4) {
                        Text(shown.podcastTitle)
                            .font(.subheadline.weight(.semibold))
                            .foregroundStyle(Color.usher)
                        Text(shown.title)
                            .font(.title3.weight(.bold))
                            .foregroundStyle(Color.screen)
                        FactsLine(facts: [shown.dateLine, shown.timeLine, shown.explicit ? "Explicit" : nil].compactMap { $0 })
                            .font(.caption)
                    }
                }
                HStack(spacing: 12) {
                    // The episode that's playing pauses here, and carries on when paused, rather than starting again.
                    let isCurrent = model.music.current?.id == shown.id
                    let isPlaying = isCurrent && model.music.isPlaying
                    Button {
                        if isPlaying {
                            model.music.togglePlayPause()
                            return
                        }
                        // Playing (or carrying on) goes to the player: the notes give way to Now Playing.
                        if isCurrent { model.music.togglePlayPause() } else { model.playEpisode(shown.summary) }
                        dismiss()
                        Task {
                            try? await Task.sleep(for: .milliseconds(450)) // once this sheet has gone
                            router.showsNowPlaying = true
                        }
                    } label: {
                        Label(isPlaying ? "Pause" : isCurrent || shown.progress != nil ? "Resume" : "Play",
                              systemImage: isPlaying ? "pause.fill" : "play.fill")
                    }
                    .buttonStyle(ActionButtonStyle(ambient: podcastGlow(shown.accentColor), prominent: true))
                    let isPlayed = played ?? shown.isPlayed
                    Button {
                        if let setPlayed {
                            setPlayed(!isPlayed)
                            played = !isPlayed
                        } else {
                            Task {
                                await model.setWatched(shown.summary, !isPlayed)
                                played = !isPlayed
                            }
                        }
                    } label: {
                        Label(isPlayed ? "Mark as unplayed" : "Mark as played", systemImage: isPlayed ? "circle" : "checkmark.circle")
                    }
                    .buttonStyle(RoundActionStyle())
                    if shown.progress != nil, played == nil {
                        // Part way through: forget the place without marking it played.
                        Button {
                            if let setPlayed {
                                setPlayed(false)
                            } else {
                                Task { await model.setWatched(shown.summary, false) }
                            }
                            played = false
                        } label: {
                            Label("Mark as unplayed", systemImage: "circle")
                        }
                        .buttonStyle(RoundActionStyle())
                    }
                    EpisodeDownloadButton(episode: shown)
                        .frame(width: 48, height: 48)
                        .background(Circle().fill(Color.screen.opacity(0.14)))
                }
                if let notes = shown.overview {
                    Text(notes)
                        .font(.body)
                        .foregroundStyle(Color.screen.opacity(0.9))
                        .textSelection(.enabled)
                }
            }
            .padding(Layout.edge)
            .padding(.top, 12)
        }
        .task {
            guard let client = model.client else { return }
            full = try? await client.podcastEpisode(episode.id)
        }
    }
}
