import AVKit
import FlexarrKit
import SwiftUI

/// What the Mac's player controls show, kept by the player screen and drawn by `MacPlayerControls`.
@MainActor @Observable
final class MacPlayerState {
    var title = ""
    var subtitle: String?
    var detail: String?
    var phase: TransportPhase = .loading
    var position: Double = 0
    var duration: Double = 0
    var buffered: Double = 0
    var controlsVisible = true
    var hasPrevious = false
    var hasNext = false
    var isLive = false
    var behindLive: Double = 0
    /// Next Episode, Skip intro, Back to live: the pill above the timeline.
    var prompt: (title: String, symbol: String, countingDown: Bool)?
    var flash: String?
    var audioTracks: [TransportTrack] = []
    var activeAudio: String?
    var subtitleTracks: [TransportTrack] = []
    var activeSubtitle: String?
    var subtitleDelay: Double?
    var subtitleSize: SubtitleSize = .standard
    var subtitlePlacement = SubtitlePlacement()
    var subtitleSearch: (available: Bool, files: Set<String>, resyncing: Bool) = (false, [], false)
    var audioSync: Double?
    var audioSyncOpen = false
    var rate: Float = 1
    var maxRate: Float = 2
    var sleepSetting: SleepTimer.Setting?
    var sleepMinutesLeft: Int?
    var offersEndOfEpisode = false
    /// "Still watching?" with the time left, or nil.
    var sleepAsking: String?
    var asleep = false
    var canPictureInPicture = false
    var isFullScreen = false
    var markers: [Marker] = []
    var previews: TrickplayPreviews?
    var avPlayer: AVPlayer?

    var isPlaying: Bool { phase == .playing || phase == .buffering }
    var isLoading: Bool { phase == .loading || phase == .buffering }
}

/// The Mac's player controls, over the picture: the title and Close at the top; the timeline, transport and menus at
/// the bottom, on a gradient; the Next Episode pill above the timeline; the sleep timer's question over everything.
/// They come up when the pointer moves and go after a few seconds of playing.
struct MacPlayerControls: View {
    let state: MacPlayerState
    weak var screen: PlayerViewController?

    var body: some View {
        ZStack {
            if state.isLoading && !state.asleep {
                ProgressView().controlSize(.large).tint(Color.screen)
            }
            if let flash = state.flash {
                Text(flash)
                    .font(.title3.weight(.semibold))
                    .padding(.horizontal, 18).padding(.vertical, 10)
                    .background(.ultraThinMaterial, in: Capsule())
                    .transition(.opacity)
            }
            VStack(spacing: 0) {
                top
                Spacer()
                bottom
            }
            .opacity(state.controlsVisible ? 1 : 0)
            .animation(.easeInOut(duration: 0.2), value: state.controlsVisible)
            if state.prompt != nil, !state.controlsVisible {
                VStack {
                    Spacer()
                    HStack { Spacer(); promptPill }
                        .padding(.trailing, 32).padding(.bottom, 40)
                }
            }
            if let asking = state.sleepAsking { sleepQuestion(asking) }
            if state.asleep {
                Color.room.opacity(0.7).ignoresSafeArea()
                    .onTapGesture { screen?.wakeFromSleep() }
            }
        }
        .foregroundStyle(Color.screen)
        .environment(\.colorScheme, .dark)
        // Up to the window's very top: the close button sits beside the window's own buttons, not under them.
        .ignoresSafeArea()
    }

    // MARK: Top

    /// The player's grid: one margin for the close button, the title, the timeline and the controls; the top row
    /// below the window's own buttons (or near the top in full screen, where there are none).
    static let edge: CGFloat = 36
    private var topInset: CGFloat { state.isFullScreen ? 28 : 44 }

    private var top: some View {
        HStack(alignment: .center, spacing: 16) {
            Button { screen?.close() } label: {
                Image(systemName: "xmark").font(.system(size: 17, weight: .semibold)).frame(width: 44, height: 44)
            }
            .buttonStyle(GlassCircle())
            .help(t("Close"))
            .accessibilityLabel(t("Close"))
            VStack(alignment: .leading, spacing: 3) {
                Text(state.title).font(.system(size: 22, weight: .bold)).lineLimit(1)
                if let subtitle = state.subtitle, !subtitle.isEmpty {
                    Text(subtitle).font(.system(size: 15, weight: .medium)).foregroundStyle(Color.screen.opacity(0.75)).lineLimit(1)
                }
            }
            Spacer(minLength: 24)
            if state.canPictureInPicture {
                Button { screen?.startPictureInPicture() } label: {
                    Image(systemName: "pip.enter").font(.system(size: 17, weight: .semibold)).frame(width: 44, height: 44)
                }
                .buttonStyle(GlassCircle())
                .help(t("Picture in picture"))
                .accessibilityLabel(t("Picture in picture"))
            }
        }
        .padding(.horizontal, Self.edge)
        .padding(.top, topInset).padding(.bottom, 60)
        .background(LinearGradient(colors: [Color.room.opacity(0.7), .clear], startPoint: .top, endPoint: .bottom).allowsHitTesting(false))
    }

    // MARK: Bottom

    /// The timeline across the margin, its times under its two ends; then the facts on the left, play and the skips in
    /// the middle, the menus on the right, on one line.
    private var bottom: some View {
        VStack(spacing: 10) {
            if state.prompt != nil {
                HStack { Spacer(); promptPill }.padding(.bottom, 6)
            }
            MacScrubber(state: state, screen: screen)
            PlayerTimes(state: state)
            ZStack {
                HStack {
                    if let detail = state.detail {
                        Text(detail).font(.system(size: 13, weight: .medium)).foregroundStyle(Color.screen.opacity(0.6)).lineLimit(1)
                    }
                    Spacer()
                    menus
                }
                transport
            }
            .padding(.top, 4)
        }
        .padding(.horizontal, Self.edge).padding(.top, 80).padding(.bottom, state.isFullScreen ? 36 : 28)
        .background(LinearGradient(colors: [.clear, Color.room.opacity(0.8)], startPoint: .top, endPoint: .bottom).allowsHitTesting(false))
    }

    private var transport: some View {
        HStack(spacing: 28) {
            if state.hasPrevious {
                transportButton("backward.end.fill", size: 22, label: state.isLive ? t("Previous channel") : t("Previous episode")) { screen?.previous() }
            } else {
                Color.clear.frame(width: 44, height: 44)
            }
            if !state.isLive || state.maxRate > 0 {
                transportButton("gobackward.\(Int(PlayerViewController.skipSeconds))", size: 26, label: t("Skip back")) {
                    screen?.skip(-PlayerViewController.skipSeconds)
                }
            }
            Button { screen?.togglePlayPause() } label: {
                Image(systemName: state.isPlaying ? "pause.fill" : "play.fill")
                    .font(.system(size: 28, weight: .semibold))
                    .frame(width: 68, height: 68)
            }
            .buttonStyle(GlassCircle())
            .help(state.isPlaying ? t("Pause") : t(Words.play))
            .accessibilityLabel(state.isPlaying ? t("Pause") : t(Words.play))
            transportButton("goforward.\(Int(PlayerViewController.skipSeconds))", size: 26, label: t("Skip forward")) {
                screen?.skip(PlayerViewController.skipSeconds)
            }
            if state.hasNext {
                transportButton("forward.end.fill", size: 22, label: state.isLive ? t("Next channel") : t("Next episode")) { screen?.next() }
            } else {
                Color.clear.frame(width: 44, height: 44)
            }
        }
    }

    private func transportButton(_ symbol: String, size: CGFloat, label: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Image(systemName: symbol).font(.system(size: size, weight: .semibold)).frame(width: 44, height: 44)
        }
        .buttonStyle(HoverCircle())
        .help(label)
        .accessibilityLabel(label)
    }

    @ViewBuilder
    private var promptPill: some View {
        if let prompt = state.prompt {
            HStack(spacing: 0) {
                Button { screen?.promptTapped() } label: {
                    Label(prompt.title, systemImage: prompt.symbol)
                        .font(.callout.weight(.semibold)).monospacedDigit()
                        .padding(.horizontal, 16).padding(.vertical, 9)
                        .foregroundStyle(Color.room)
                        .background(Color.screen.opacity(0.95), in: UnevenRoundedRectangle(topLeadingRadius: Radius.panel, bottomLeadingRadius: Radius.panel,
                                                                           bottomTrailingRadius: prompt.countingDown ? 0 : Radius.panel,
                                                                           topTrailingRadius: prompt.countingDown ? 0 : Radius.panel))
                }
                if prompt.countingDown {
                    Button { screen?.cancelNext() } label: {
                        Image(systemName: "xmark").font(.system(size: 11, weight: .bold))
                            .padding(.horizontal, 12).padding(.vertical, 12)
                            .background(Color.aisle.opacity(0.85), in: UnevenRoundedRectangle(bottomTrailingRadius: Radius.panel, topTrailingRadius: Radius.panel))
                    }
                    .accessibilityLabel(t(Words.cancel))
                }
            }
            .buttonStyle(.plain)
        }
    }

    // MARK: Menus

    private var menus: some View {
        HStack(spacing: 4) {
            if state.isLive {
                Button { screen?.showChannels() } label: { Label(t("Channels"), systemImage: "list.bullet").labelStyle(.iconOnly) }
                    .help(t("Channels"))
            }
            if state.audioTracks.count > 1 || state.audioSync != nil {
                Menu {
                    if state.audioTracks.count > 1 {
                        Picker(t("Audio"), selection: Binding(get: { state.activeAudio }, set: { if let id = $0 { screen?.chooseAudio(id) } })) {
                            ForEach(state.audioTracks) { Text($0.label).tag(String?.some($0.id)) }
                        }
                        .pickerStyle(.inline)
                    }
                    if let sync = state.audioSync {
                        Button { state.audioSyncOpen = true } label: {
                            Text(t("Audio sync"))
                            Text(sync == 0 ? t("As the file is") : PlayerViewController.audioSyncLabel(sync))
                        }
                    }
                } label: { Image(systemName: "waveform") }
                .help(t("Audio"))
                .accessibilityLabel(t("Audio"))
                .popover(isPresented: Binding(get: { state.audioSyncOpen }, set: { state.audioSyncOpen = $0 })) {
                    AudioSyncPopover(state: state, screen: screen)
                }
            }
            if !state.subtitleTracks.isEmpty || state.subtitleSearch.available {
                Menu { subtitleMenu } label: { Image(systemName: "captions.bubble") }
                    .help(t(Words.subtitles))
                    .accessibilityLabel(t(Words.subtitles))
            }
            if !state.isLive, state.maxRate > 1 {
                Menu {
                    Picker(t("Speed"), selection: Binding(get: { state.rate }, set: { screen?.chooseRate($0) })) {
                        ForEach(Set(PlayerViewController.rateChoices + [state.rate]).sorted().filter { $0 <= state.maxRate }, id: \.self) {
                            Text(PlayerViewController.rateLabel($0)).tag($0)
                        }
                    }
                    .pickerStyle(.inline)
                } label: {
                    if state.rate == 1 { Image(systemName: "gauge.with.dots.needle.67percent") } else { Text(PlayerViewController.rateLabel(state.rate)).monospacedDigit() }
                }
                .help(t("Speed"))
                .accessibilityLabel(t("Speed"))
                .accessibilityValue(PlayerViewController.rateLabel(state.rate))
            }
            if !state.isLive {
                Menu {
                    let title = state.sleepMinutesLeft.map { t("Sleep timer, {minutes} min left", ["minutes": $0]) } ?? t("Sleep timer")
                    Picker(title, selection: Binding(get: { state.sleepSetting }, set: { screen?.setSleep($0) })) {
                        Text(t("Off")).tag(SleepTimer.Setting?.none)
                        ForEach(SleepTimer.minuteChoices, id: \.self) { Text(SleepTimer.Setting.minutes($0).label).tag(SleepTimer.Setting?.some(.minutes($0))) }
                        if state.offersEndOfEpisode { Text(SleepTimer.Setting.endOfEpisode.label).tag(SleepTimer.Setting?.some(.endOfEpisode)) }
                    }
                    .pickerStyle(.inline)
                } label: { Image(systemName: state.sleepSetting == nil ? "moon.zzz" : "moon.zzz.fill") }
                .help(t("Sleep timer"))
                .accessibilityLabel(t("Sleep timer"))
            }
            MacRoutePicker(player: state.avPlayer)
                .frame(width: 44, height: 44)
                .help(t("AirPlay"))
                .accessibilityLabel(t("AirPlay"))
            Button { screen?.toggleFullScreen() } label: {
                Image(systemName: state.isFullScreen ? "arrow.down.right.and.arrow.up.left" : "arrow.up.left.and.arrow.down.right")
            }
            .help(state.isFullScreen ? t("Exit full screen") : t("Full screen"))
            .accessibilityLabel(state.isFullScreen ? t("Exit full screen") : t("Full screen"))
        }
        .menuStyle(.button)
        .buttonStyle(HoverCircle())
        .menuIndicator(.hidden)
        .fixedSize()
        .font(.system(size: 19, weight: .semibold))
        .labelStyle(.iconOnly)
    }

    @ViewBuilder
    private var subtitleMenu: some View {
        if !state.subtitleTracks.isEmpty {
            Picker(t(Words.subtitles), selection: Binding(get: { state.activeSubtitle }, set: { screen?.chooseSubtitle(id: $0) })) {
                Text(t("Off")).tag(String?.none)
                ForEach(state.subtitleTracks) { Text($0.label).tag(String?.some($0.id)) }
            }
            .pickerStyle(.inline)
            if let delay = state.subtitleDelay {
                Picker(t("Timing"), selection: Binding(get: { delay }, set: { screen?.chooseSubtitleDelay($0) })) {
                    ForEach(Set(PlayerViewController.delayChoices + [delay]).sorted(), id: \.self) { Text(PlayerViewController.delayLabel($0)).tag($0) }
                }
            }
            Picker(t("Size"), selection: Binding(get: { state.subtitleSize }, set: { screen?.chooseSubtitleSize($0) })) {
                ForEach(SubtitleSize.allCases, id: \.self) { Text($0.label).tag($0) }
            }
            Picker(t("Position"), selection: Binding(get: { state.subtitlePlacement.position }, set: {
                screen?.chooseSubtitlePlacement(SubtitlePlacement(position: $0, inBlackBars: state.subtitlePlacement.inBlackBars))
            })) {
                ForEach(SubtitlePosition.allCases, id: \.self) { Text($0.label).tag($0) }
            }
            Toggle(t("In the black bars"), isOn: Binding(get: { state.subtitlePlacement.inBlackBars }, set: {
                screen?.chooseSubtitlePlacement(SubtitlePlacement(position: state.subtitlePlacement.position, inBlackBars: $0))
            }))
        }
        if state.subtitleSearch.available {
            Divider()
            Button(t("Search for subtitles…")) { screen?.searchSubtitles() }
            let showing = state.subtitleTracks.first { $0.id == state.activeSubtitle }?.key
            if state.subtitleSearch.resyncing {
                Button(t("Re-syncing…")) {}.disabled(true)
            } else if let showing, state.subtitleSearch.files.contains(showing) {
                Button(t("Re-sync to audio")) { screen?.resyncSubtitle(showing) }
            }
        }
    }

    private func sleepQuestion(_ detail: String) -> some View {
        ZStack {
            Color.room.opacity(0.55).ignoresSafeArea()
            VStack(spacing: 14) {
                Text(t("Still watching?")).font(.system(size: 30, weight: .bold))
                Text(detail).font(.title3.weight(.medium)).monospacedDigit().foregroundStyle(Color.screen.opacity(0.75))
                HStack(spacing: 12) {
                    Button(t("+{minutes} min", ["minutes": 15])) { screen?.setSleep(.minutes(15)) }
                        .buttonStyle(.borderedProminent).tint(Color.screen).foregroundStyle(Color.room)
                    Button(t("Cancel timer")) { screen?.setSleep(nil) }
                        .buttonStyle(.bordered)
                }
                .controlSize(.large)
            }
        }
    }
}

/// The times under the timeline's ends: its own view, so only it redraws as the clock moves.
private struct PlayerTimes: View {
    let state: MacPlayerState

    var body: some View {
        HStack {
            Text(PlayerViewController.clock(state.position))
            Spacer()
            Text(remaining)
        }
        .font(.system(size: 14, weight: .semibold)).monospacedDigit()
        .foregroundStyle(Color.screen.opacity(0.8))
        .accessibilityElement(children: .combine)
    }

    private var remaining: String {
        if state.isLive {
            return state.behindLive < LiveTransport.liveSlack ? t("Live") : t("−{time}", ["time": PlayerViewController.clock(state.behindLive)])
        }
        return state.duration > 0 ? t("−{time}", ["time": PlayerViewController.clock(max(0, state.duration - state.position))]) : ""
    }
}

/// The timeline: buffered and played, the intro and credits marked, a preview thumbnail and the time under the pointer
/// while hovering, a drag to scrub (the picture moves when it's let go).
private struct MacScrubber: View {
    let state: MacPlayerState
    weak var screen: PlayerViewController?
    @State private var hover: CGFloat?
    @State private var dragging: Double?

    var body: some View {
        GeometryReader { geometry in
            let width = max(1, geometry.size.width)
            let duration = max(state.duration, 0.001)
            let shown = dragging ?? state.position
            ZStack(alignment: .leading) {
                Capsule().fill(Color.screen.opacity(0.22))
                Capsule().fill(Color.screen.opacity(0.35)).frame(width: width * min(1, state.buffered / duration))
                ForEach(Array(state.markers.enumerated()), id: \.offset) { _, marker in
                    Rectangle().fill(Color.silver.opacity(0.7))
                        .frame(width: max(2, width * (marker.end - marker.start) / duration), height: 3)
                        .offset(x: width * marker.start / duration)
                }
                Capsule().fill(Color.screen).frame(width: width * min(1, shown / duration))
                Circle().fill(Color.screen).frame(width: hover != nil || dragging != nil ? 14 : 0)
                    .offset(x: width * min(1, shown / duration) - 7)
            }
            .frame(height: hover != nil || dragging != nil ? 7 : 5)
            .frame(maxHeight: .infinity)
            .contentShape(Rectangle())
            .overlay(alignment: .topLeading) {
                if let x = hover ?? dragging.map({ CGFloat($0 / duration) * width }), state.duration > 0 {
                    preview(at: Double(x / width) * state.duration)
                        .offset(x: min(max(0, x - 80), width - 160), y: -118)
                        .allowsHitTesting(false)
                }
            }
            .onContinuousHover { phase in
                if case .active(let point) = phase { hover = point.x } else { hover = nil }
            }
            .gesture(DragGesture(minimumDistance: 0)
                .onChanged { value in
                    guard !state.isLive || state.duration > 0 else { return }
                    dragging = max(0, min(duration, Double(value.location.x / width) * duration))
                    screen?.scrubbing(true)
                }
                .onEnded { _ in
                    if let dragging { screen?.seek(to: dragging) }
                    dragging = nil
                    screen?.scrubbing(false)
                })
        }
        .frame(height: 18)
        // VoiceOver: the time, and up or down a step to move ten seconds.
        .accessibilityElement()
        .accessibilityLabel(t("Timeline"))
        .accessibilityValue(PlayerViewController.clock(state.position))
        .accessibilityAdjustableAction { direction in
            guard state.duration > 0 else { return }
            let step: Double = direction == .increment ? 10 : direction == .decrement ? -10 : 0
            screen?.seek(to: max(0, min(state.duration, state.position + step)))
        }
    }

    private func preview(at seconds: Double) -> some View {
        VStack(spacing: 4) {
            if let image = state.previews?.image(at: seconds) {
                Image(nsImage: image).resizable().aspectRatio(state.previews?.aspectRatio ?? 16 / 9, contentMode: .fill)
                    .frame(width: 160, height: 160 / (state.previews?.aspectRatio ?? 16 / 9))
                    .clipShape(RoundedRectangle(cornerRadius: Radius.art, style: .continuous))
            } else {
                Color.clear.frame(width: 160, height: 90)
            }
            Text(PlayerViewController.clock(seconds)).font(.caption.weight(.semibold)).monospacedDigit()
                .padding(.horizontal, 6).padding(.vertical, 2)
                .background(Color.room.opacity(0.6), in: Capsule())
        }
    }
}

/// Nudging the sound earlier or later than the picture, in 10 ms steps up to two seconds either way.
private struct AudioSyncPopover: View {
    let state: MacPlayerState
    weak var screen: PlayerViewController?

    var body: some View {
        let seconds = state.audioSync ?? 0
        VStack(spacing: 10) {
            Text(t("Audio sync")).font(.headline)
            Text(PlayerViewController.audioSyncLabel(seconds)).font(.title2.weight(.semibold)).monospacedDigit()
            Text(seconds > 0.0005 ? t("Sound later") : seconds < -0.0005 ? t("Sound earlier") : t("As the file is"))
                .font(.callout).foregroundStyle(.secondary)
            HStack {
                Button { screen?.nudgeAudio(by: -0.05) } label: { Image(systemName: "minus") }
                Slider(value: Binding(get: { seconds }, set: { screen?.setAudioSync(($0 * 100).rounded() / 100) }), in: -2...2)
                    .frame(width: 180)
                Button { screen?.nudgeAudio(by: 0.05) } label: { Image(systemName: "plus") }
            }
        }
        .padding(18)
    }
}

/// An icon over the picture in a 44 pt circle that lights up under the pointer.
private struct HoverCircle: ButtonStyle {
    @State private var hovering = false

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .frame(minWidth: 44, minHeight: 44)
            .background(Circle().fill(Color.screen.opacity(configuration.isPressed ? 0.24 : hovering ? 0.14 : 0)))
            .contentShape(Circle())
            .onHover { hovering = $0 }
            .animation(.easeOut(duration: 0.12), value: hovering)
    }
}

/// A round glass button over the picture.
private struct GlassCircle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .background(.ultraThinMaterial, in: Circle())
            .opacity(configuration.isPressed ? 0.7 : 1)
    }
}

/// AirPlay and the Mac's other outputs, for what's playing.
private struct MacRoutePicker: NSViewRepresentable {
    let player: AVPlayer?

    func makeNSView(context: Context) -> AVRoutePickerView {
        let picker = AVRoutePickerView()
        picker.isRoutePickerButtonBordered = false
        picker.setRoutePickerButtonColor(NSColor(Color.screen), for: .normal)
        picker.setRoutePickerButtonColor(NSColor(Color.silver), for: .active)
        picker.player = player
        return picker
    }

    func updateNSView(_ picker: AVRoutePickerView, context: Context) {
        picker.player = player
    }
}
