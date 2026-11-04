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
                ProgressView().controlSize(.large).tint(.white)
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
                Color.black.opacity(0.7).ignoresSafeArea()
                    .onTapGesture { screen?.wakeFromSleep() }
            }
        }
        .foregroundStyle(.white)
        .environment(\.colorScheme, .dark)
    }

    // MARK: Top

    private var top: some View {
        HStack(alignment: .top, spacing: 14) {
            Button { screen?.close() } label: {
                Image(systemName: "xmark").font(.system(size: 15, weight: .semibold)).frame(width: 34, height: 34)
            }
            .buttonStyle(GlassCircle())
            .help(t("Close"))
            .accessibilityLabel(t("Close"))
            VStack(alignment: .leading, spacing: 2) {
                Text(state.title).font(.title3.weight(.bold)).lineLimit(1)
                if let subtitle = state.subtitle, !subtitle.isEmpty {
                    Text(subtitle).font(.callout).foregroundStyle(.white.opacity(0.75)).lineLimit(1)
                }
            }
            Spacer()
            if state.canPictureInPicture {
                Button { screen?.startPictureInPicture() } label: {
                    Image(systemName: "pip.enter").font(.system(size: 15, weight: .semibold)).frame(width: 34, height: 34)
                }
                .buttonStyle(GlassCircle())
                .help(t("Picture in Picture"))
                .accessibilityLabel(t("Picture in Picture"))
            }
            Button { screen?.toggleFullScreen() } label: {
                Image(systemName: state.isFullScreen ? "arrow.down.right.and.arrow.up.left" : "arrow.up.left.and.arrow.down.right")
                    .font(.system(size: 14, weight: .semibold)).frame(width: 34, height: 34)
            }
            .buttonStyle(GlassCircle())
            .help(state.isFullScreen ? t("Exit full screen") : t("Full screen"))
            .accessibilityLabel(state.isFullScreen ? t("Exit full screen") : t("Full screen"))
        }
        .padding(.horizontal, 24).padding(.top, state.isFullScreen ? 24 : 36).padding(.bottom, 40)
        .background(LinearGradient(colors: [.black.opacity(0.6), .clear], startPoint: .top, endPoint: .bottom).allowsHitTesting(false))
    }

    // MARK: Bottom

    private var bottom: some View {
        VStack(spacing: 12) {
            if state.prompt != nil {
                HStack { Spacer(); promptPill }
            }
            MacScrubber(state: state, screen: screen)
            HStack(spacing: 18) {
                Text(PlayerViewController.clock(state.position)).monospacedDigit().font(.callout).foregroundStyle(.white.opacity(0.8))
                Spacer()
                transport
                Spacer()
                Text(remaining).monospacedDigit().font(.callout).foregroundStyle(.white.opacity(0.8))
            }
            HStack(spacing: 10) {
                if let detail = state.detail { Text(detail).font(.caption).foregroundStyle(.white.opacity(0.6)).lineLimit(1) }
                Spacer()
                menus
            }
        }
        .padding(.horizontal, 28).padding(.top, 60).padding(.bottom, 22)
        .background(LinearGradient(colors: [.clear, .black.opacity(0.75)], startPoint: .top, endPoint: .bottom).allowsHitTesting(false))
    }

    private var remaining: String {
        if state.isLive { return state.behindLive < LiveTransport.liveSlack ? t("LIVE") : "−" + PlayerViewController.clock(state.behindLive) }
        return state.duration > 0 ? "−" + PlayerViewController.clock(max(0, state.duration - state.position)) : ""
    }

    private var transport: some View {
        HStack(spacing: 22) {
            if state.hasPrevious {
                Button { screen?.previous() } label: { Image(systemName: "backward.end.fill") }
                    .help(state.isLive ? t("Previous channel") : t("Previous episode"))
                    .accessibilityLabel(state.isLive ? t("Previous channel") : t("Previous episode"))
            }
            if !state.isLive || state.maxRate > 0 {
                Button { screen?.skip(-PlayerViewController.skipSeconds) } label: { Image(systemName: "gobackward.\(Int(PlayerViewController.skipSeconds))") }
                    .help(t("Skip back")).accessibilityLabel(t("Skip back"))
            }
            Button { screen?.togglePlayPause() } label: {
                Image(systemName: state.isPlaying ? "pause.fill" : "play.fill").font(.system(size: 30)).frame(width: 40)
            }
            .help(state.isPlaying ? t("Pause") : t(Words.play))
            .accessibilityLabel(state.isPlaying ? t("Pause") : t(Words.play))
            Button { screen?.skip(PlayerViewController.skipSeconds) } label: { Image(systemName: "goforward.\(Int(PlayerViewController.skipSeconds))") }
                .help(t("Skip forward")).accessibilityLabel(t("Skip forward"))
            if state.hasNext {
                Button { screen?.next() } label: { Image(systemName: "forward.end.fill") }
                    .help(state.isLive ? t("Next channel") : t("Next episode"))
                    .accessibilityLabel(state.isLive ? t("Next channel") : t("Next episode"))
            }
        }
        .font(.system(size: 20, weight: .semibold))
        .buttonStyle(.plain)
    }

    @ViewBuilder
    private var promptPill: some View {
        if let prompt = state.prompt {
            HStack(spacing: 0) {
                Button { screen?.promptTapped() } label: {
                    Label(prompt.title, systemImage: prompt.symbol)
                        .font(.callout.weight(.semibold)).monospacedDigit()
                        .padding(.horizontal, 16).padding(.vertical, 9)
                        .foregroundStyle(Color.black)
                        .background(Color.white, in: UnevenRoundedRectangle(topLeadingRadius: 18, bottomLeadingRadius: 18,
                                                                           bottomTrailingRadius: prompt.countingDown ? 0 : 18,
                                                                           topTrailingRadius: prompt.countingDown ? 0 : 18))
                }
                if prompt.countingDown {
                    Button { screen?.cancelNext() } label: {
                        Image(systemName: "xmark").font(.system(size: 11, weight: .bold))
                            .padding(.horizontal, 12).padding(.vertical, 12)
                            .background(Color.aisle.opacity(0.85), in: UnevenRoundedRectangle(bottomTrailingRadius: 18, topTrailingRadius: 18))
                    }
                    .accessibilityLabel(t(Words.cancel))
                }
            }
            .buttonStyle(.plain)
        }
    }

    // MARK: Menus

    private var menus: some View {
        HStack(spacing: 6) {
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
                .popover(isPresented: Binding(get: { state.audioSyncOpen }, set: { state.audioSyncOpen = $0 })) {
                    AudioSyncPopover(state: state, screen: screen)
                }
            }
            if !state.subtitleTracks.isEmpty || state.subtitleSearch.available {
                Menu { subtitleMenu } label: { Image(systemName: "captions.bubble") }
                    .help(t(Words.subtitles))
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
            }
            if !state.isLive {
                Menu {
                    let title = state.sleepMinutesLeft.map { t("Sleep timer ({minutes} min left)", ["minutes": $0]) } ?? t("Sleep timer")
                    Picker(title, selection: Binding(get: { state.sleepSetting }, set: { screen?.setSleep($0) })) {
                        Text(t("Off")).tag(SleepTimer.Setting?.none)
                        ForEach(SleepTimer.minuteChoices, id: \.self) { Text(SleepTimer.Setting.minutes($0).label).tag(SleepTimer.Setting?.some(.minutes($0))) }
                        if state.offersEndOfEpisode { Text(SleepTimer.Setting.endOfEpisode.label).tag(SleepTimer.Setting?.some(.endOfEpisode)) }
                    }
                    .pickerStyle(.inline)
                } label: { Image(systemName: state.sleepSetting == nil ? "moon.zzz" : "moon.zzz.fill") }
                .help(t("Sleep timer"))
            }
            MacRoutePicker(player: state.avPlayer)
                .frame(width: 26, height: 22)
                .help(t("AirPlay"))
        }
        .menuStyle(.borderlessButton)
        .menuIndicator(.hidden)
        .fixedSize()
        .font(.system(size: 15, weight: .semibold))
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
            Color.black.opacity(0.55).ignoresSafeArea()
            VStack(spacing: 14) {
                Text(t("Still watching?")).font(.system(size: 30, weight: .bold))
                Text(detail).font(.title3.weight(.medium)).monospacedDigit().foregroundStyle(.white.opacity(0.75))
                HStack(spacing: 12) {
                    Button(t("+{minutes} min", ["minutes": 15])) { screen?.setSleep(.minutes(15)) }
                        .buttonStyle(.borderedProminent).tint(.white).foregroundStyle(.black)
                    Button(t("Cancel timer")) { screen?.setSleep(nil) }
                        .buttonStyle(.bordered)
                }
                .controlSize(.large)
            }
        }
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
                Capsule().fill(.white.opacity(0.22))
                Capsule().fill(.white.opacity(0.35)).frame(width: width * min(1, state.buffered / duration))
                ForEach(Array(state.markers.enumerated()), id: \.offset) { _, marker in
                    Rectangle().fill(Color.silver.opacity(0.7))
                        .frame(width: max(2, width * (marker.end - marker.start) / duration), height: 3)
                        .offset(x: width * marker.start / duration)
                }
                Capsule().fill(.white).frame(width: width * min(1, shown / duration))
                Circle().fill(.white).frame(width: hover != nil || dragging != nil ? 14 : 0)
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
    }

    private func preview(at seconds: Double) -> some View {
        VStack(spacing: 4) {
            if let image = state.previews?.image(at: seconds) {
                Image(nsImage: image).resizable().aspectRatio(state.previews?.aspectRatio ?? 16 / 9, contentMode: .fill)
                    .frame(width: 160, height: 160 / (state.previews?.aspectRatio ?? 16 / 9))
                    .clipShape(RoundedRectangle(cornerRadius: 6))
            } else {
                Color.clear.frame(width: 160, height: 90)
            }
            Text(PlayerViewController.clock(seconds)).font(.caption.weight(.semibold)).monospacedDigit()
                .padding(.horizontal, 6).padding(.vertical, 2)
                .background(.black.opacity(0.6), in: Capsule())
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
        picker.setRoutePickerButtonColor(.white, for: .normal)
        picker.setRoutePickerButtonColor(NSColor(Color.silver), for: .active)
        picker.player = player
        return picker
    }

    func updateNSView(_ picker: AVRoutePickerView, context: Context) {
        picker.player = player
    }
}
