import FlexarrKit
import SwiftUI

/// The remote for a TV in the house, as a sheet: which TV, its picture and title, a scrubber, the transport (back and
/// forward 10 seconds, previous and next episode, Skip Intro when the TV offers it), then soundtracks and subtitles,
/// speed and sound, Continue on this phone, Stop, and the season's other episodes. Menus and volume stay with Apple's
/// own remote in Control Centre. Buttons a TV's app can't take yet don't show.
struct TVRemoteView: View {
    @Environment(AppModel.self) private var model
    @Environment(TVRemote.self) private var remote
    @Environment(\.dismiss) private var dismiss
    /// Where the thumb is while the viewer drags it; nil when the TV's own position shows.
    @State private var scrubbing: Double?
    @State private var episodes: [ItemSummary] = []

    private static let skipSeconds: Double = 10

    var body: some View {
        ZStack {
            RoomBackground(ambient: remote.current?.nowPlaying?.item.glowColor, intensity: 0.45)
            if let device = remote.current, let playing = device.nowPlaying {
                ScrollView(.vertical) {
                    VStack(spacing: 22) {
                        Capsule().fill(Color.aisle).frame(width: 40, height: 5).padding(.top, 10)
                        header(device)
                        still(playing)
                        titles(playing.item)
                        TimelineView(.periodic(from: .now, by: 0.5)) { context in
                            let position = playing.position(at: context.date)
                            VStack(spacing: 18) {
                                scrubber(device, playing, position: position)
                                controls(device, playing, position: position)
                            }
                        }
                        if let prompt = playing.prompt, prompt != .unknown { promptButton(device, prompt) }
                        extras(device, playing)
                        if !episodes.isEmpty { episodeList(device, playing) }
                    }
                    .padding(.horizontal, 28)
                    .padding(.bottom, 32)
                    .frame(maxWidth: 560)
                    .frame(maxWidth: .infinity)
                }
                .scrollIndicators(.hidden)
            } else {
                StatusView(state: .empty("Nothing is playing on a TV."))
            }
        }
        .overlay(alignment: .bottom) {
            if let problem = remote.problem {
                Text(problem)
                    .font(.footnote.weight(.semibold))
                    .foregroundStyle(Color.screen)
                    .multilineTextAlignment(.center)
                    .padding(.horizontal, 16)
                    .padding(.vertical, 10)
                    .background(Capsule().fill(Color.seat).shadow(color: .black.opacity(0.4), radius: 12, y: 4))
                    .padding(.bottom, 24)
                    .padding(.horizontal, 20)
                    .transition(.move(edge: .bottom).combined(with: .opacity))
            }
        }
        .animation(.easeOut(duration: 0.25), value: remote.problem)
        .onAppear { remote.isOpen = true }
        .onDisappear { remote.isOpen = false }
        .task(id: remote.current?.nowPlaying?.item.parentId) { await loadEpisodes() }
        .onChange(of: remote.current == nil) { _, gone in if gone { dismiss() } }
    }

    // MARK: Parts

    /// The TV's name; with more than one playing, a menu to switch between them.
    @ViewBuilder
    private func header(_ device: Device) -> some View {
        let label = HStack(spacing: 6) {
            Image(systemName: device.deviceType == .appleTv ? "appletv.fill" : "tv")
            Text(device.name)
            if remote.playing.count > 1 { Image(systemName: "chevron.down").font(.caption.weight(.bold)) }
        }
        .font(.subheadline.weight(.semibold))
        .foregroundStyle(Color.usher)
        if remote.playing.count > 1 {
            Menu {
                Picker("TV", selection: Binding(get: { device.id }, set: { remote.selectedId = $0 })) {
                    ForEach(remote.playing) { other in
                        Text(other.name).tag(other.id)
                    }
                }
            } label: { label }
        } else {
            label
        }
    }

    private func still(_ playing: DeviceNowPlaying) -> some View {
        TVStill(item: playing.item)
            .aspectRatio(16 / 9, contentMode: .fit)
            .clipShape(RoundedRectangle(cornerRadius: 12, style: .continuous))
            .shadow(color: .black.opacity(0.5), radius: 24, y: 12)
            .shadow(color: playing.item.glowColor.opacity(0.3), radius: 40)
            // Smaller while paused, as Now Playing does.
            .scaleEffect(playing.paused ? 0.92 : 1)
            .animation(.spring(response: 0.45, dampingFraction: 0.8), value: playing.paused)
    }

    private func titles(_ item: ItemSummary) -> some View {
        VStack(spacing: 4) {
            Text(item.displayTitle)
                .font(.title3.weight(.bold))
                .foregroundStyle(Color.screen)
                .lineLimit(2)
                .multilineTextAlignment(.center)
            if let second = item.episodeSubtitle ?? item.releaseYear.map(String.init) {
                Text(second)
                    .font(.subheadline.weight(.medium))
                    .foregroundStyle(Color.usher)
                    .lineLimit(1)
            }
        }
    }

    private func scrubber(_ device: Device, _ playing: DeviceNowPlaying, position: Double) -> some View {
        let duration = playing.durationSeconds
        let shown = scrubbing ?? position
        return VStack(spacing: 2) {
            PlayerScrubber(position: position, duration: duration, scrubbing: $scrubbing) { remote.seek(to: $0) }
                .allowsHitTesting(device.takes(.seek))
            HStack {
                Text(Format.timestamp(shown))
                    .frame(maxWidth: .infinity, alignment: .leading)
                if duration > 0 {
                    // When it'll finish, at the speed it's playing.
                    Text("Ends \(Date().addingTimeInterval(max(0, duration - shown) / max(0.5, playing.rate ?? 1)).formatted(date: .omitted, time: .shortened))")
                }
                Text(duration > 0 ? "-" + Format.timestamp(max(0, duration - shown)) : "")
                    .frame(maxWidth: .infinity, alignment: .trailing)
            }
            .font(.caption.weight(.medium))
            .monospacedDigit()
            .foregroundStyle(Color.usher)
        }
    }

    private func controls(_ device: Device, _ playing: DeviceNowPlaying, position: Double) -> some View {
        HStack(spacing: 0) {
            if device.takes(.previous) {
                Button { remote.control(.previous) } label: { Label("Previous", systemImage: "backward.end.fill") }
                    .buttonStyle(TransportStyle(size: 44))
                Spacer()
            }
            if device.takes(.skip) {
                Button { remote.skip(by: -Self.skipSeconds) } label: { Label("Back 10 seconds", systemImage: "gobackward.10") }
                    .buttonStyle(TransportStyle(size: 56))
                Spacer()
            }
            Button { remote.togglePlayPause() } label: {
                Label(playing.paused ? "Play" : "Pause", systemImage: playing.paused ? "play.fill" : "pause.fill")
            }
            .buttonStyle(TransportStyle(size: 76, filled: true))
            .sensoryFeedback(.impact(weight: .medium), trigger: playing.paused)
            if device.takes(.skip) {
                Spacer()
                Button { remote.skip(by: Self.skipSeconds) } label: { Label("Forward 10 seconds", systemImage: "goforward.10") }
                    .buttonStyle(TransportStyle(size: 56))
            }
            if device.takes(.next) {
                Spacer()
                Button { remote.control(.next) } label: { Label("Next", systemImage: "forward.end.fill") }
                    .buttonStyle(TransportStyle(size: 44))
                    .disabled(playing.hasNext == false)
                    .opacity(playing.hasNext == false ? 0.35 : 1)
            }
        }
        .frame(maxWidth: .infinity)
    }

    /// The TV's Skip Intro, Skip Recap or Next Episode, here too.
    @ViewBuilder
    private func promptButton(_ device: Device, _ prompt: PlayerPrompt) -> some View {
        let next = prompt == .nextEpisode
        if device.takes(next ? .next : .skipSegment) {
            Button { remote.control(next ? .next : .skipSegment) } label: {
                Text(next ? "Next Episode" : prompt == .skipRecap ? "Skip Recap" : "Skip Intro")
                    .font(.subheadline.weight(.bold))
                    .foregroundStyle(Color.room)
                    .padding(.horizontal, 22)
                    .frame(minHeight: 44)
                    .background(Capsule().fill(Color.screen))
            }
            .buttonStyle(CardButtonStyle())
            .transition(.scale.combined(with: .opacity))
        }
    }

    /// Soundtracks and subtitles, speed, sound, Continue on this phone, and Stop: evenly spaced, each lit while it's set.
    private func extras(_ device: Device, _ playing: DeviceNowPlaying) -> some View {
        HStack(spacing: 0) {
            if let tracks = playing.tracks, device.takes(.audioTrack) || device.takes(.subtitleTrack),
               tracks.audio.count > 1 || !tracks.subtitles.isEmpty {
                tracksMenu(device, tracks).frame(maxWidth: .infinity)
            }
            if device.takes(.rate), let rate = playing.rate { speedMenu(rate).frame(maxWidth: .infinity) }
            if let sound = playing.sound { soundMenu(sound).frame(maxWidth: .infinity) }
            Button { continueHere(playing) } label: {
                Image(systemName: "iphone")
                    .font(.system(size: 18, weight: .medium))
                    .modifier(ExtraLabel(active: false))
            }
            .buttonStyle(.plain)
            .accessibilityLabel("Continue on this \(ThisDevice.name)")
            .frame(maxWidth: .infinity)
            Button { remote.stop() } label: {
                Image(systemName: "stop.fill")
                    .font(.system(size: 18, weight: .medium))
                    .modifier(ExtraLabel(active: false))
            }
            .buttonStyle(.plain)
            .accessibilityLabel("Stop")
            .frame(maxWidth: .infinity)
        }
    }

    private func tracksMenu(_ device: Device, _ tracks: PlayerTracks) -> some View {
        let subtitlesOn = tracks.subtitles.contains(where: \.selected)
        return Menu {
            if device.takes(.subtitleTrack), !tracks.subtitles.isEmpty {
                Picker("Subtitles", selection: Binding(get: { tracks.subtitles.first(where: \.selected)?.id },
                                                       set: { id in remote.setSubtitles(tracks.subtitles.first { $0.id == id }) })) {
                    Text("Off").tag(String?.none)
                    ForEach(tracks.subtitles) { Text($0.label).tag(String?.some($0.id)) }
                }
                .pickerStyle(.inline)
            }
            if device.takes(.audioTrack), tracks.audio.count > 1 {
                Picker("Audio", selection: Binding(get: { tracks.audio.first(where: \.selected)?.id ?? "" },
                                                   set: { id in if let track = tracks.audio.first(where: { $0.id == id }) { remote.setAudio(track) } })) {
                    ForEach(tracks.audio) { Text($0.label).tag($0.id) }
                }
                .pickerStyle(.inline)
            }
        } label: {
            Image(systemName: "captions.bubble")
                .font(.system(size: 18, weight: .medium))
                .modifier(ExtraLabel(active: subtitlesOn))
        }
        .accessibilityLabel("Audio and subtitles")
    }

    private func speedMenu(_ rate: Double) -> some View {
        Menu {
            Picker("Speed", selection: Binding(get: { rate }, set: { remote.setRate($0) })) {
                ForEach([0.5, 0.75, 1, 1.25, 1.5, 2], id: \.self) { Text(speedLabel($0)).tag($0) }
            }
        } label: {
            Text(speedLabel(rate))
                .font(.system(size: 16, weight: .semibold))
                .monospacedDigit()
                .modifier(ExtraLabel(active: rate != 1))
        }
        .accessibilityLabel("Speed, \(speedLabel(rate))")
    }

    private func speedLabel(_ rate: Double) -> String {
        rate == 1 ? "1×" : "\(rate.formatted(.number.precision(.fractionLength(0...2))))×"
    }

    /// The TV's dialogue boost, night mode and speakers.
    private func soundMenu(_ sound: DeviceSound) -> some View {
        let boost = sound.dialogueBoost.flatMap(SoundSettings.DialogueBoost.init(rawValue:)) ?? .off
        let night = sound.nightMode.flatMap(SoundSettings.NightMode.init(rawValue:)) ?? .off
        let speakers = sound.speakers.flatMap(SoundSettings.Speakers.init(rawValue:)) ?? .auto
        return Menu {
            Picker("Dialogue boost", selection: Binding(get: { boost }, set: { remote.setSound(DeviceSound(dialogueBoost: $0.rawValue)) })) {
                ForEach(SoundSettings.DialogueBoost.allCases, id: \.self) { Text($0.rawValue).tag($0) }
            }
            .pickerStyle(.menu)
            Picker("Night mode", selection: Binding(get: { night }, set: { remote.setSound(DeviceSound(nightMode: $0.rawValue)) })) {
                ForEach(SoundSettings.NightMode.allCases, id: \.self) { Text($0 == .scheduled ? "On a schedule" : $0.rawValue).tag($0) }
            }
            .pickerStyle(.menu)
            Picker("Speakers", selection: Binding(get: { speakers }, set: { remote.setSound(DeviceSound(speakers: $0.rawValue)) })) {
                ForEach(SoundSettings.Speakers.allCases, id: \.self) { Text($0.label).tag($0) }
            }
            .pickerStyle(.menu)
        } label: {
            Image(systemName: night == .off ? "speaker.wave.2" : "moon")
                .font(.system(size: 18, weight: .medium))
                .modifier(ExtraLabel(active: boost != .off || night != .off || speakers != .auto))
        }
        .accessibilityLabel("Sound")
    }

    /// The season's episodes: tapping one plays it on the TV.
    private func episodeList(_ device: Device, _ playing: DeviceNowPlaying) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Episodes")
                .font(.headline)
                .foregroundStyle(Color.screen)
            ForEach(episodes) { episode in
                let isPlaying = episode.id == playing.item.id
                Button { play(episode, on: device) } label: {
                    HStack(spacing: 12) {
                        TVStill(item: episode, width: 400)
                            .frame(width: 96, height: 54)
                            .clipShape(RoundedRectangle(cornerRadius: 6, style: .continuous))
                            .overlay(alignment: .bottom) {
                                if let progress = episode.progress, !isPlaying { ProgressBar(value: progress).padding(4) }
                            }
                        VStack(alignment: .leading, spacing: 2) {
                            Text(episode.episodeCode.map { "\($0)  \(episode.title)" } ?? episode.title)
                                .font(.subheadline.weight(.semibold))
                                .foregroundStyle(Color.screen)
                                .lineLimit(2)
                            if isPlaying {
                                Text("Playing")
                                    .font(.caption.weight(.semibold))
                                    .foregroundStyle(Color.silver)
                            } else if let minutes = episode.runtimeMinutes {
                                Text("\(minutes) min")
                                    .font(.caption)
                                    .foregroundStyle(Color.usher)
                            }
                        }
                        Spacer(minLength: 0)
                        if episode.userData.watched, !isPlaying { WatchedBadge() }
                    }
                    .contentShape(Rectangle())
                }
                .buttonStyle(CardButtonStyle())
                .disabled(isPlaying)
                .accessibilityHint(isPlaying ? "" : "Plays it on \(device.name)")
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(.top, 8)
    }

    // MARK: Actions

    private func loadEpisodes() async {
        guard let item = remote.current?.nowPlaying?.item, item.kind == .episode, let season = item.parentId,
              let detail = try? await model.client?.item(season) else {
            episodes = []
            return
        }
        episodes = detail.children.filter { $0.kind == .episode }
    }

    private func play(_ episode: ItemSummary, on device: Device) {
        Task {
            _ = try? await model.client?.play(on: device.id, itemId: episode.id)
            await remote.refresh()
        }
    }

    /// Stops it on the TV and carries on here from the same place.
    private func continueHere(_ playing: DeviceNowPlaying) {
        guard let client = model.client else { return }
        let position = playing.position(at: Date())
        let item = playing.item
        remote.stop()
        dismiss()
        model.player.play(itemId: item.id, preview: item, startAt: position, client: client)
    }
}
