import AppIntents
import SwiftUI
import WidgetKit

// The podcast widgets: Continue listening (Home Screen, small and medium, and the Lock Screen) and Up Next, and the
// Watchlist's ready titles. They show what the app last saved to the app group (`PodcastSnapshot`, `WatchlistSnapshot`)
// and never ask the server themselves; the app reloads them when its state changes.

@main
struct FlexarrWidgets: WidgetBundle {
    var body: some Widget {
        ContinueListeningWidget()
        UpNextWidget()
        WatchlistWidget()
        TVPlayingLiveActivity()
    }
}

// MARK: - Timeline

struct SnapshotEntry: TimelineEntry {
    let date: Date
    let snapshot: PodcastSnapshot
}

struct SnapshotProvider: TimelineProvider {
    func placeholder(in context: Context) -> SnapshotEntry {
        SnapshotEntry(date: Date(), snapshot: .preview)
    }

    func getSnapshot(in context: Context, completion: @escaping (SnapshotEntry) -> Void) {
        let saved = PodcastSnapshot.load()
        completion(SnapshotEntry(date: Date(), snapshot: context.isPreview && saved.current == nil ? .preview : saved))
    }

    func getTimeline(in context: Context, completion: @escaping (Timeline<SnapshotEntry>) -> Void) {
        // One entry: the app asks for a new timeline whenever there's something new to show.
        completion(Timeline(entries: [SnapshotEntry(date: Date(), snapshot: PodcastSnapshot.load())], policy: .never))
    }
}

extension PodcastSnapshot {
    /// What the widget gallery shows before the app has saved anything.
    static let preview = PodcastSnapshot(
        current: Episode(id: "preview", title: "The Fall of Rome", show: "The History Hour", artworkFile: nil, progress: 0.4, remainingSeconds: 1500),
        isPlaying: false,
        upNext: [
            Episode(id: "a", title: "The Lost Legion", show: "The History Hour", artworkFile: nil, progress: nil, remainingSeconds: 2700),
            Episode(id: "b", title: "Signals", show: "Science Weekly", artworkFile: nil, progress: nil, remainingSeconds: 1800),
        ],
        updatedAt: Date())
}

// MARK: - Look

// The app's colours come from Shared/Design/Tokens.swift, compiled into the widgets too.

/// An episode's saved square picture, or a quiet tile.
private struct EpisodeArtwork: View {
    let episode: PodcastSnapshot.Episode
    var cornerRadius: CGFloat = Radius.art

    var body: some View {
        Group {
            if let url = PodcastSnapshot.artwork(episode.artworkFile), let image = UIImage(contentsOfFile: url.path(percentEncoded: false)) {
                Image(uiImage: image).resizable().aspectRatio(contentMode: .fill)
            } else {
                ZStack {
                    Color.seat
                    Image(systemName: "mic.fill").foregroundStyle(Color.usher)
                }
            }
        }
        .aspectRatio(1, contentMode: .fit)
        .clipShape(RoundedRectangle(cornerRadius: cornerRadius, style: .continuous))
    }
}

private func remaining(_ seconds: Double?) -> String? {
    guard let seconds, seconds >= 60 else { return nil }
    let minutes = Int((seconds / 60).rounded())
    return minutes >= 60 ? "\(minutes / 60)h \(minutes % 60)m left" : "\(minutes)m left"
}

// MARK: - Continue listening

struct ContinueListeningWidget: Widget {
    var body: some WidgetConfiguration {
        StaticConfiguration(kind: "io.github.flexarr.continue-listening", provider: SnapshotProvider()) { entry in
            ContinueListeningView(snapshot: entry.snapshot)
                .containerBackground(for: .widget) { Color.room }
        }
        .configurationDisplayName("Continue listening")
        .description("The book or podcast episode you're on, to carry on with.")
        .supportedFamilies([.systemSmall, .systemMedium, .accessoryRectangular])
    }
}

struct ContinueListeningView: View {
    @Environment(\.widgetFamily) private var family
    let snapshot: PodcastSnapshot

    var body: some View {
        if let episode = snapshot.current {
            switch family {
            case .accessoryRectangular: lockScreen(episode)
            case .systemMedium: medium(episode)
            default: small(episode)
            }
        } else {
            VStack(spacing: 6) {
                Image(systemName: "mic.fill").font(.title3)
                Text("Nothing to carry on with").font(.caption).multilineTextAlignment(.center)
            }
            .foregroundStyle(family == .accessoryRectangular ? Color.primary : Color.usher)
        }
    }

    private func small(_ episode: PodcastSnapshot.Episode) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(alignment: .top) {
                EpisodeArtwork(episode: episode).frame(width: 56, height: 56)
                Spacer(minLength: 0)
                playButton(episode)
            }
            Spacer(minLength: 0)
            Text(episode.title)
                .font(.caption.weight(.semibold))
                .foregroundStyle(Color.screen)
                .lineLimit(2)
            progress(episode)
        }
        .widgetURL(PodcastSnapshot.playURL(episode.id))
    }

    private func medium(_ episode: PodcastSnapshot.Episode) -> some View {
        HStack(spacing: 14) {
            EpisodeArtwork(episode: episode, cornerRadius: Radius.art)
            VStack(alignment: .leading, spacing: 4) {
                Text(episode.show)
                    .font(.caption.weight(.semibold))
                    .foregroundStyle(Color.usher)
                    .lineLimit(1)
                Text(episode.title)
                    .font(.subheadline.weight(.semibold))
                    .foregroundStyle(Color.screen)
                    .lineLimit(3)
                Spacer(minLength: 0)
                HStack {
                    progress(episode)
                    playButton(episode)
                }
            }
        }
        .widgetURL(PodcastSnapshot.playURL(episode.id))
    }

    private func lockScreen(_ episode: PodcastSnapshot.Episode) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(episode.show).font(.caption2.weight(.semibold)).lineLimit(1).widgetAccentable()
            Text(episode.title).font(.caption.weight(.semibold)).lineLimit(2)
            if let progress = episode.progress {
                ProgressView(value: progress)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .widgetURL(PodcastSnapshot.playURL(episode.id))
    }

    private func progress(_ episode: PodcastSnapshot.Episode) -> some View {
        VStack(alignment: .leading, spacing: 3) {
            if let progress = episode.progress {
                ProgressView(value: progress).tint(Color.screen)
            }
            if let left = remaining(episode.remainingSeconds) {
                Text(left).font(.caption2.weight(.semibold)).foregroundStyle(Color.usher).monospacedDigit()
            }
        }
    }

    private func playButton(_ episode: PodcastSnapshot.Episode) -> some View {
        Button(intent: TogglePodcastIntent(episodeId: episode.id)) {
            Image(systemName: snapshot.isPlaying ? "pause.fill" : "play.fill")
                .font(.body.weight(.bold))
                .foregroundStyle(Color.room)
                .frame(width: 36, height: 36)
                .background(Circle().fill(Color.screen))
        }
        .buttonStyle(.plain)
        .accessibilityLabel(snapshot.isPlaying ? "Pause" : "Play")
    }
}

// MARK: - Up Next

struct UpNextWidget: Widget {
    var body: some WidgetConfiguration {
        StaticConfiguration(kind: "io.github.flexarr.up-next", provider: SnapshotProvider()) { entry in
            UpNextView(snapshot: entry.snapshot)
                .containerBackground(for: .widget) { Color.room }
        }
        .configurationDisplayName("Up next")
        .description("The podcast episodes waiting in Up next.")
        .supportedFamilies([.systemSmall, .systemMedium])
    }
}

struct UpNextView: View {
    @Environment(\.widgetFamily) private var family
    let snapshot: PodcastSnapshot

    var body: some View {
        let episodes = Array(snapshot.upNext.prefix(family == .systemSmall ? 1 : 3))
        VStack(alignment: .leading, spacing: 8) {
            Text("Up next")
                .font(.caption.weight(.bold))
                .foregroundStyle(Color.usher)
            if episodes.isEmpty {
                Spacer(minLength: 0)
                Text("Nothing in Up next").font(.caption).foregroundStyle(Color.usher)
                Spacer(minLength: 0)
            } else if family == .systemSmall, let first = episodes.first {
                EpisodeArtwork(episode: first).frame(width: 56, height: 56)
                Spacer(minLength: 0)
                Text(first.title).font(.caption.weight(.semibold)).foregroundStyle(Color.screen).lineLimit(2)
            } else {
                ForEach(episodes) { episode in
                    Link(destination: PodcastSnapshot.playURL(episode.id)) {
                        HStack(spacing: 10) {
                            EpisodeArtwork(episode: episode, cornerRadius: Radius.art).frame(width: 32, height: 32)
                            VStack(alignment: .leading, spacing: 0) {
                                Text(episode.title).font(.caption.weight(.semibold)).foregroundStyle(Color.screen)
                                Text(episode.show).font(.caption2).foregroundStyle(Color.usher)
                            }
                            .lineLimit(1)
                            Spacer(minLength: 0)
                        }
                    }
                }
                Spacer(minLength: 0)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .widgetURL(episodes.first.map { PodcastSnapshot.playURL($0.id) })
    }
}

// MARK: - Watchlist

struct WatchlistEntryTimeline: TimelineEntry {
    let date: Date
    let snapshot: WatchlistSnapshot
}

struct WatchlistProvider: TimelineProvider {
    func placeholder(in context: Context) -> WatchlistEntryTimeline {
        WatchlistEntryTimeline(date: Date(), snapshot: .preview)
    }

    func getSnapshot(in context: Context, completion: @escaping (WatchlistEntryTimeline) -> Void) {
        let saved = WatchlistSnapshot.load()
        completion(WatchlistEntryTimeline(date: Date(), snapshot: context.isPreview && saved.ready.isEmpty ? .preview : saved))
    }

    func getTimeline(in context: Context, completion: @escaping (Timeline<WatchlistEntryTimeline>) -> Void) {
        completion(Timeline(entries: [WatchlistEntryTimeline(date: Date(), snapshot: WatchlistSnapshot.load())], policy: .never))
    }
}

extension WatchlistSnapshot {
    /// What the widget gallery shows before the app has saved anything.
    static let preview = WatchlistSnapshot(ready: [
        Title(id: "a", title: "Dune: Part Two", posterFile: nil),
        Title(id: "b", title: "Severance", posterFile: nil),
        Title(id: "c", title: "Arrival", posterFile: nil),
        Title(id: "d", title: "Slow Horses", posterFile: nil),
    ], updatedAt: Date())
}

struct WatchlistWidget: Widget {
    var body: some WidgetConfiguration {
        StaticConfiguration(kind: WatchlistSnapshot.widgetKind, provider: WatchlistProvider()) { entry in
            WatchlistWidgetView(snapshot: entry.snapshot)
                .containerBackground(for: .widget) { Color.room }
                .widgetURL(WatchlistSnapshot.watchlistURL)
        }
        .configurationDisplayName("Watchlist")
        .description("What's on your watchlist and ready to watch.")
        .supportedFamilies([.systemSmall, .systemMedium])
    }
}

struct WatchlistWidgetView: View {
    @Environment(\.widgetFamily) private var family
    let snapshot: WatchlistSnapshot

    var body: some View {
        if snapshot.ready.isEmpty {
            VStack(spacing: 6) {
                Image(systemName: "bookmark").font(.title3)
                Text("Nothing here yet").font(.caption).multilineTextAlignment(.center)
            }
            .foregroundStyle(Color.usher)
        } else {
            VStack(alignment: .leading, spacing: 8) {
                Text("Ready to watch")
                    .font(.caption.weight(.semibold))
                    .foregroundStyle(Color.usher)
                    .lineLimit(1)
                HStack(spacing: 8) {
                    ForEach(snapshot.ready.prefix(family == .systemMedium ? 4 : 2)) { title in
                        // A small widget is one tap target (its Links are ignored): that opens the watchlist.
                        if family == .systemMedium {
                            Link(destination: WatchlistSnapshot.itemURL(title.id)) { poster(title) }
                        } else {
                            poster(title)
                        }
                    }
                }
            }
        }
    }

    private func poster(_ title: WatchlistSnapshot.Title) -> some View {
        Group {
            if let url = PodcastSnapshot.artwork(title.posterFile), let image = UIImage(contentsOfFile: url.path(percentEncoded: false)) {
                Image(uiImage: image).resizable().aspectRatio(contentMode: .fill)
            } else {
                ZStack {
                    Color.seat
                    Text(title.title)
                        .font(.caption2.weight(.semibold))
                        .foregroundStyle(Color.screen)
                        .multilineTextAlignment(.center)
                        .padding(4)
                }
            }
        }
        .aspectRatio(2 / 3, contentMode: .fit)
        .clipShape(RoundedRectangle(cornerRadius: Radius.art, style: .continuous))
        .accessibilityLabel(title.title)
    }
}
