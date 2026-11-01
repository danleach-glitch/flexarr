import FlexarrKit
import SwiftUI

/// Settings › Podcasts & audiobooks: one speed, skip steps and smart rewind for both (a book with a speed of its own
/// keeps it), whether they join Continue watching, and what this phone downloads by itself. The web's page of the
/// same name. On a server from before 0.58 podcasts have only their speed here.
struct ListeningSettingsView: View {
    @Environment(AppModel.self) private var model
    @Environment(DownloadStore.self) private var downloads
    @State private var playback: PlaybackPreferences?
    @State private var failed = false
    /// Neither the podcast nor the book settings could be loaded (offline, say).
    @State private var loadFailed = false

    var body: some View {
        List {
            if speed == nil {
                Group {
                    if loadFailed {
                        StatusView(state: .failed("Your settings couldn't be loaded.", retry: { Task { await load() } }))
                    } else {
                        StatusView(state: .loading)
                    }
                }
                .listRowBackground(Color.clear)
            } else {
                Section {
                    if let speed {
                        Picker("Speed", selection: Binding(get: { speed }, set: { value in
                            save(podcasts: .init(speed: value), books: .init(speed: value))
                        })) {
                            ForEach(BookSpeed.choices(with: speed), id: \.self) { Text(BookSpeed.label($0)).tag($0) }
                        }
                        .listRowBackground(Color.seat)
                    }
                    if let shared {
                        Picker("Skip back", selection: Binding(get: { shared.skipBackSeconds }, set: { value in
                            save(podcasts: .init(skipBackSeconds: value), books: .init(skipBackSeconds: value))
                        })) {
                            ForEach(steps(shared.skipBackSeconds), id: \.self) { Text("\($0) seconds").tag($0) }
                        }
                        .listRowBackground(Color.seat)
                        Picker("Skip forward", selection: Binding(get: { shared.skipForwardSeconds }, set: { value in
                            save(podcasts: .init(skipForwardSeconds: value), books: .init(skipForwardSeconds: value))
                        })) {
                            ForEach(steps(shared.skipForwardSeconds), id: \.self) { Text("\($0) seconds").tag($0) }
                        }
                        .listRowBackground(Color.seat)
                        Toggle("Smart rewind", isOn: Binding(get: { shared.smartRewind }, set: { value in
                            save(podcasts: .init(smartRewind: value), books: .init(smartRewind: value))
                        }))
                        .settingsRow()
                    }
                    // Servers before 0.58 don't have it.
                    if let playback, let listening = playback.listeningInContinue {
                        Toggle("Books and podcasts in Continue watching", isOn: Binding(get: { listening }, set: { on in
                            var changed = playback
                            changed.listeningInContinue = on
                            savePlayback(changed, over: playback)
                        }))
                        .settingsRow()
                    }
                } header: {
                    Text("Your profile, on every device")
                } footer: {
                    VStack(alignment: .leading, spacing: 6) {
                        Text("A book with a speed of its own keeps it. Smart rewind starts a little earlier after a pause.")
                        if failed { Text("That didn't save. Try again.").foregroundStyle(Color.danger) }
                    }
                }
            }

            if let podcasts = model.podcastPreferences {
                Section {
                    Toggle("Download new episodes", isOn: Binding(get: { podcasts.autoDownload }, set: { value in
                        Task {
                            await model.updatePodcastPreferences(.init(autoDownload: value))
                            await downloads.reconcilePodcasts()
                        }
                    }))
                    .settingsRow()
                    Stepper("Keep the newest \(podcasts.keepEpisodes)", value: Binding(get: { podcasts.keepEpisodes }, set: { value in
                        Task {
                            await model.updatePodcastPreferences(.init(keepEpisodes: value))
                            await downloads.reconcilePodcasts()
                        }
                    }), in: 1...50)
                    .disabled(!podcasts.autoDownload)
                    .listRowBackground(Color.seat)
                } header: {
                    Text("Automatic downloads")
                } footer: {
                    Text("On Wi-Fi, the newest unplayed episodes of every show you follow. Played ones are deleted.")
                }

                Section {
                    SettingsLink("Notifications", icon: "bell.fill") { NotificationSettingsView() }
                } footer: {
                    Text("New episodes can come as notifications.")
                }
            }
        }
        .settingsPage("Podcasts & audiobooks")
        .task { await load() }
    }

    /// What's missing, asked for again: the podcast settings (with podcasts), the books' (with an audiobooks
    /// library) and the playback preferences.
    private func load() async {
        loadFailed = false
        if model.podcastPreferences == nil { await model.loadPodcastPreferences() }
        if model.bookListening == nil, model.libraries.contains(where: { $0.type == .audiobooks }) { await model.loadBookListening() }
        if let loaded = try? await model.client?.playbackPreferences() { playback = loaded }
        loadFailed = speed == nil
    }

    /// The one speed: the podcast settings' when podcasts are on, otherwise the books'.
    private var speed: Double? { model.podcastPreferences?.speed ?? model.bookListening?.speed }

    /// The skips and smart rewind, from the podcast settings when the server shares them (0.58 on), or the books'.
    private var shared: BookListening? {
        if let podcasts = model.podcastPreferences, let back = podcasts.skipBackSeconds, let forward = podcasts.skipForwardSeconds,
           let smart = podcasts.smartRewind {
            return BookListening(speed: podcasts.speed, skipBackSeconds: back, skipForwardSeconds: forward, smartRewind: smart)
        }
        return model.bookListening
    }

    /// Saved as podcast settings where the server shares them with books (0.58 on). A server that has book settings
    /// but doesn't share them yet (a build of main from before) takes them as book settings, with the speed for
    /// podcasts too.
    private func save(podcasts: PodcastPreferencesUpdate, books: BookListeningUpdate) {
        Task {
            if let preferences = model.podcastPreferences, preferences.hasListening || model.bookListening == nil {
                failed = await model.updatePodcastPreferences(podcasts) == nil
            } else {
                var ok = await model.updateBookListening(books)
                if podcasts.speed != nil, model.podcastPreferences != nil {
                    ok = await model.updatePodcastPreferences(.init(speed: podcasts.speed)) != nil && ok
                }
                failed = !ok
            }
        }
    }

    private func savePlayback(_ changed: PlaybackPreferences, over current: PlaybackPreferences) {
        playback = changed
        failed = false
        Task {
            do {
                try await model.client?.setPlaybackPreferences(changed)
                // Home's Continue watching row changes with it.
                model.invalidate()
            } catch {
                playback = current
                failed = true
            }
        }
    }

    private func steps(_ current: Int) -> [Int] {
        BookListening.skipSteps.contains(current) ? BookListening.skipSteps : (BookListening.skipSteps + [current]).sorted()
    }
}
