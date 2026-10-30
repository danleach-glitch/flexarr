import FlexarrKit
import SwiftUI

/// The remote for a TV in the house, as a sheet: which TV, its picture and title, a scrubber, the transport (back and
/// forward 10 seconds, previous and next episode, Skip Intro when the TV offers it), then soundtracks and subtitles,
/// speed and sound, Continue on iPhone and Stop, and the season's other episodes. Menus and volume stay with Apple's
/// own remote in Control Centre. Buttons a TV's app can't take yet don't show. Side by side in landscape.
struct TVRemoteView: View {
    @Environment(AppModel.self) private var model
    @Environment(TVRemote.self) private var remote
    @Environment(\.dismiss) private var dismiss
    @Environment(\.verticalSizeClass) private var verticalSizeClass
    @Environment(\.dynamicTypeSize) private var dynamicTypeSize
    /// Where the thumb is while the viewer drags it; nil when the TV's own position shows.
    @State private var scrubbing: Double?
    @State private var episodes: [ItemSummary] = []
    @State private var continuing = false
    @ScaledMetric(relativeTo: .body) private var extraIcon: CGFloat = 18

    private static let skipSeconds: Double = 10

    var body: some View {
        ZStack {
            RoomBackground(ambient: remote.current?.nowPlaying?.item.glowColor, intensity: 0.45)
            if let device = remote.current, let playing = device.nowPlaying {
                ScrollView(.vertical) {
                    VStack(spacing: 22) {
                        Capsule().fill(Color.aisle).frame(width: 40, height: 5).padding(.top, 10)
                        header(device)
                        if verticalSizeClass == .compact {
                            // Landscape on a phone: the picture beside the controls, so play/pause needs no scroll.
                            HStack(alignment: .top, spacing: 28) {
                                VStack(spacing: 14) {
                                    still(playing)
                                    titles(playing.item)
                                }
                                .frame(maxWidth: 300)
                                VStack(spacing: 18) { playback(device, playing) }
                            }
                        } else {
                            still(playing)
                            titles(playing.item)
                            playback(device, playing)
                        }
                        if !episodes.isEmpty { episodeList(device, playing) }
                    }
                    .padding(.horizontal, 28)
                    .padding(.bottom, 32)
                    .frame(maxWidth: verticalSizeClass == .compact ? 820 : 560)
                    .frame(maxWidth: .infinity)
                    .animation(.easeOut(duration: 0.25), value: playing.prompt)
                }
                .scrollIndicators(.hidden)
            } else {
                StatusView(state: .empty("Nothing is playing on a TV."))
            }
        }
        .overlay(alignment: .bottom) { RemoteNoticeView().padding(.bottom, 16) }
        .task(id: remote.current?.nowPlaying?.item.parentId) { await loadEpisodes() }
        .task(id: remote.current == nil) {
            // The TV stopped: a moment on the empty state, then the sheet goes (unless it comes back).
            guard remote.current == nil, !continuing else { return }
            try? await Task.sleep(for: .seconds(2))
            if !Task.isCancelled, remote.current == nil { dismiss() }
        }
    }

    /// The scrubber, the transport, the TV's button over the picture and the extras.
    @ViewBuilder
    private func playback(_ device: Device, _ playing: DeviceNowPlaying) -> some View {
        TimelineView(.animation(minimumInterval: 0.5, paused: playing.paused)) { context in
            let position = playing.position(at: context.date)
            VStack(spacing: 18) {
                scrubber(device, playing, position: position)
                controls(device, playing)
            }
        }
        if let prompt = playing.prompt, prompt != .unknown { promptButton(device, prompt) }
        extras(device, playing)
        actions(device, playing)
    }

    // MARK: Parts

    /// The TV's name; with more than one playing, a menu to switch between them.
    @ViewBuilder
    private func header(_ device: Device) -> some View {
        let label = HStack(spacing: 6) {
            Image(systemName: device.deviceType == .androidTv ? "tv" : "appletv.fill")
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
            .accessibilityLabel("TV, \(device.name)")
            .accessibilityHint("Switches to another TV")
        } else {
            label
                .accessibilityElement(children: .combine)
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
            .accessibilityHidden(true)
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
        .accessibilityElement(children: .combine)
    }

    private func scrubber(_ device: Device, _ playing: DeviceNowPlaying, position: Double) -> some View {
        let duration = playing.durationSeconds
        let shown = scrubbing ?? position
        let ends = duration > 0
            ? Text("Ends \(Date().addingTimeInterval(max(0, duration - shown) / max(0.5, playing.rate ?? 1)).formatted(date: .omitted, time: .shortened))")
            : nil
        return VStack(spacing: 2) {
            PlayerScrubber(position: position, duration: duration, step: Self.skipSeconds, scrubbing: $scrubbing) { remote.seek(to: $0) }
                .disabled(!device.takes(.seek))
                .allowsHitTesting(device.takes(.seek))
            HStack {
                Text(Format.timestamp(shown))
                    .frame(maxWidth: .infinity, alignment: .leading)
                // At the largest sizes there's no room between the times: it goes underneath.
                if !dynamicTypeSize.isAccessibilitySize { ends }
                Text(duration > 0 ? "-" + Format.timestamp(max(0, duration - shown)) : "")
                    .frame(maxWidth: .infinity, alignment: .trailing)
            }
            if dynamicTypeSize.isAccessibilitySize { ends }
        }
        .font(.caption.weight(.medium))
        .monospacedDigit()
        .foregroundStyle(Color.usher)
    }

    private func controls(_ device: Device, _ playing: DeviceNowPlaying) -> some View {
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

    /// Soundtracks and subtitles, speed and sound: evenly spaced, each lit while it's set.
    @ViewBuilder
    private func extras(_ device: Device, _ playing: DeviceNowPlaying) -> some View {
        let tracks = playing.tracks.flatMap { tracks in
            (device.takes(.audioTrack) || device.takes(.subtitleTrack)) && (tracks.audio.count > 1 || !tracks.subtitles.isEmpty) ? tracks : nil
        }
        let rate = device.takes(.rate) ? playing.rate : nil
        let sound = device.takes(.sound) ? playing.sound : nil
        if tracks != nil || rate != nil || sound != nil {
            HStack(spacing: 0) {
                if let tracks { tracksMenu(device, tracks).frame(maxWidth: .infinity) }
                if let rate { speedMenu(rate).frame(maxWidth: .infinity) }
                if let sound { soundMenu(sound).frame(maxWidth: .infinity) }
            }
        }
    }

    /// Continue on iPhone, and Stop set apart from it (Undo follows a stop).
    private func actions(_ device: Device, _ playing: DeviceNowPlaying) -> some View {
        HStack(spacing: 12) {
            Button { continueHere(playing) } label: {
                Label("Continue on \(ThisDevice.name)", systemImage: "iphone")
                    .labelStyle(.titleAndIcon)
                    .font(.subheadline.weight(.semibold))
                    .foregroundStyle(Color.screen)
                    .padding(.horizontal, 16)
                    .frame(minHeight: 44)
                    .background(Capsule().fill(Color.screen.opacity(0.12)))
            }
            .buttonStyle(CardButtonStyle())
            .disabled(continuing)
            Spacer(minLength: 12)
            Button { Task { await remote.stop() } } label: {
                Label("Stop", systemImage: "stop.fill")
                    .labelStyle(.titleAndIcon)
                    .font(.subheadline.weight(.semibold))
                    .foregroundStyle(Color.danger)
                    .padding(.horizontal, 16)
                    .frame(minHeight: 44)
                    .overlay(Capsule().strokeBorder(Color.danger.opacity(0.6), lineWidth: 1))
            }
            .buttonStyle(CardButtonStyle())
            .accessibilityLabel("Stop on \(device.name)")
        }
    }

    private func tracksMenu(_ device: Device, _ tracks: PlayerTracks) -> some View {
        let subtitles = tracks.subtitles.first(where: \.selected)
        let audio = tracks.audio.first(where: \.selected)
        return Menu {
            if device.takes(.subtitleTrack), !tracks.subtitles.isEmpty {
                Picker("Subtitles", selection: Binding(get: { subtitles?.id },
                                                       set: { id in remote.setSubtitles(tracks.subtitles.first { $0.id == id }) })) {
                    Text("Off").tag(String?.none)
                    ForEach(tracks.subtitles) { Text($0.label).tag(String?.some($0.id)) }
                }
                .pickerStyle(.inline)
            }
            if device.takes(.audioTrack), tracks.audio.count > 1 {
                Picker("Audio", selection: Binding(get: { audio?.id ?? "" },
                                                   set: { id in if let track = tracks.audio.first(where: { $0.id == id }) { remote.setAudio(track) } })) {
                    ForEach(tracks.audio) { Text($0.label).tag($0.id) }
                }
                .pickerStyle(.inline)
            }
        } label: {
            Image(systemName: "captions.bubble")
                .font(.system(size: extraIcon, weight: .medium))
                .modifier(ExtraLabel(active: subtitles != nil))
        }
        .accessibilityLabel("Audio and subtitles")
        .accessibilityValue([subtitles.map { "Subtitles \($0.label)" } ?? "Subtitles off", audio?.label].compactMap { $0 }.joined(separator: ", "))
    }

    private func speedMenu(_ rate: Double) -> some View {
        Menu {
            Picker("Speed", selection: Binding(get: { rate }, set: { remote.setRate($0) })) {
                ForEach([0.5, 0.75, 1, 1.25, 1.5, 2], id: \.self) { Text(speedLabel($0)).tag($0) }
            }
        } label: {
            Text(speedLabel(rate))
                .font(.system(size: extraIcon - 2, weight: .semibold))
                .monospacedDigit()
                .modifier(ExtraLabel(active: rate != 1))
        }
        .accessibilityLabel("Speed")
        .accessibilityValue(speedLabel(rate))
    }

    private func speedLabel(_ rate: Double) -> String {
        rate == 1 ? "1×" : "\(rate.formatted(.number.precision(.fractionLength(0...2))))×"
    }

    /// The TV's dialogue boost, night mode and speakers.
    private func soundMenu(_ sound: DeviceSound) -> some View {
        let boost = sound.dialogueBoost.flatMap(SoundSettings.DialogueBoost.init(rawValue:)) ?? .off
        let night = sound.nightMode.flatMap(SoundSettings.NightMode.init(rawValue:)) ?? .off
        let speakers = sound.speakers.flatMap(SoundSettings.Speakers.init(rawValue:)) ?? .auto
        let set = [boost != .off ? "Dialogue boost \(boost.rawValue)" : nil, night != .off ? "Night mode \(night == .scheduled ? "on a schedule" : "on")" : nil,
                   speakers != .auto ? "Speakers \(speakers.label)" : nil].compactMap { $0 }
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
                .font(.system(size: extraIcon, weight: .medium))
                .modifier(ExtraLabel(active: !set.isEmpty))
        }
        .accessibilityLabel("Sound")
        .accessibilityValue(set.isEmpty ? "As it is" : set.joined(separator: ", "))
    }

    /// The season's episodes: tapping one plays it on the TV.
    private func episodeList(_ device: Device, _ playing: DeviceNowPlaying) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Episodes")
                .font(.headline)
                .foregroundStyle(Color.screen)
            ForEach(episodes) { episode in
                let isPlaying = episode.id == playing.item.id
                let isStarting = episode.id == remote.starting
                Button { remote.play(episode) } label: {
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
                        if isStarting {
                            ProgressView().tint(Color.screen)
                        } else if episode.userData.watched, !isPlaying {
                            WatchedBadge()
                        }
                    }
                    .contentShape(Rectangle())
                }
                .buttonStyle(CardButtonStyle())
                .disabled(isPlaying || isStarting)
                .accessibilityHint(isPlaying ? "" : "Plays it on \(device.name)")
                .accessibilityValue(isStarting ? "Starting" : "")
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

    /// Stops it on the TV and, once the TV has, carries on here from the same place. If the TV didn't stop, the remote
    /// stays with the reason.
    private func continueHere(_ playing: DeviceNowPlaying) {
        guard let client = model.client, !continuing else { return }
        let position = playing.position(at: Date())
        let item = playing.item
        let player = model.player
        continuing = true
        Task {
            defer { continuing = false }
            guard await remote.stop(offeringUndo: false) else { return }
            dismiss()
            // Once the sheet has gone: the player can't come up over it.
            try? await Task.sleep(for: .milliseconds(450))
            player.play(itemId: item.id, preview: item, startAt: position, client: client)
        }
    }
}
