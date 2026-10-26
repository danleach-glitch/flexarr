import AppIntents
import SwiftUI
import WidgetKit

// The podcast widgets: Continue listening (Home Screen, small and medium, and the Lock Screen) and Up Next. They show
// what the app last saved to the app group (`PodcastSnapshot`) and never ask the server themselves; the app reloads
// them when podcast state changes.

@main
struct FlexarrWidgets: WidgetBundle {
    var body: some Widget {
        ContinueListeningWidget()
        UpNextWidget()
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

/// The app's colours, as the widgets use them.
private enum Palette {
    static let room = Color(red: 0x1B / 255, green: 0x17 / 255, blue: 0x20 / 255)
    static let seat = Color(red: 0x25 / 255, green: 0x20 / 255, blue: 0x2C / 255)
    static let screen = Color(red: 0xF2 / 255, green: 0xEE / 255, blue: 0xE7 / 255)
    static let usher = Color(red: 0xA3 / 255, green: 0x9A / 255, blue: 0xAE / 255)
}

/// An episode's saved square picture, or a quiet tile.
private struct EpisodeArtwork: View {
    let episode: PodcastSnapshot.Episode
    var cornerRadius: CGFloat = 10

    var body: some View {
        Group {
            if let url = PodcastSnapshot.artwork(episode.artworkFile), let image = UIImage(contentsOfFile: url.path(percentEncoded: false)) {
                Image(uiImage: image).resizable().aspectRatio(contentMode: .fill)
            } else {
                ZStack {
                    Palette.seat
                    Image(systemName: "mic.fill").foregroundStyle(Palette.usher)
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
                .containerBackground(for: .widget) { Palette.room }
        }
        .configurationDisplayName("Continue listening")
        .description("The podcast episode you're on, to carry on with.")
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
            .foregroundStyle(family == .accessoryRectangular ? Color.primary : Palette.usher)
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
                .foregroundStyle(Palette.screen)
                .lineLimit(2)
            progress(episode)
        }
        .widgetURL(PodcastSnapshot.playURL(episode.id))
    }

    private func medium(_ episode: PodcastSnapshot.Episode) -> some View {
        HStack(spacing: 14) {
            EpisodeArtwork(episode: episode, cornerRadius: 12)
            VStack(alignment: .leading, spacing: 4) {
                Text(episode.show)
                    .font(.caption.weight(.semibold))
                    .foregroundStyle(Palette.usher)
                    .lineLimit(1)
                Text(episode.title)
                    .font(.subheadline.weight(.semibold))
                    .foregroundStyle(Palette.screen)
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
                ProgressView(value: progress).tint(Palette.screen)
            }
            if let left = remaining(episode.remainingSeconds) {
                Text(left).font(.caption2.weight(.semibold)).foregroundStyle(Palette.usher).monospacedDigit()
            }
        }
    }

    private func playButton(_ episode: PodcastSnapshot.Episode) -> some View {
        Button(intent: TogglePodcastIntent(episodeId: episode.id)) {
            Image(systemName: snapshot.isPlaying ? "pause.fill" : "play.fill")
                .font(.body.weight(.bold))
                .foregroundStyle(Palette.room)
                .frame(width: 36, height: 36)
                .background(Circle().fill(Palette.screen))
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
                .containerBackground(for: .widget) { Palette.room }
        }
        .configurationDisplayName("Up Next")
        .description("The podcast episodes waiting in Up Next.")
        .supportedFamilies([.systemSmall, .systemMedium])
    }
}

struct UpNextView: View {
    @Environment(\.widgetFamily) private var family
    let snapshot: PodcastSnapshot

    var body: some View {
        let episodes = Array(snapshot.upNext.prefix(family == .systemSmall ? 1 : 3))
        VStack(alignment: .leading, spacing: 8) {
            Text("Up Next")
                .font(.caption.weight(.bold))
                .foregroundStyle(Palette.usher)
            if episodes.isEmpty {
                Spacer(minLength: 0)
                Text("Nothing in Up Next").font(.caption).foregroundStyle(Palette.usher)
                Spacer(minLength: 0)
            } else if family == .systemSmall, let first = episodes.first {
                EpisodeArtwork(episode: first).frame(width: 56, height: 56)
                Spacer(minLength: 0)
                Text(first.title).font(.caption.weight(.semibold)).foregroundStyle(Palette.screen).lineLimit(2)
            } else {
                ForEach(episodes) { episode in
                    Link(destination: PodcastSnapshot.playURL(episode.id)) {
                        HStack(spacing: 10) {
                            EpisodeArtwork(episode: episode, cornerRadius: 6).frame(width: 32, height: 32)
                            VStack(alignment: .leading, spacing: 0) {
                                Text(episode.title).font(.caption.weight(.semibold)).foregroundStyle(Palette.screen)
                                Text(episode.show).font(.caption2).foregroundStyle(Palette.usher)
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
