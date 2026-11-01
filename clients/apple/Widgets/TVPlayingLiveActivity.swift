import ActivityKit
import AppIntents
import SwiftUI
import WidgetKit

// What a TV in the house is playing, on the Lock Screen and in the Dynamic Island: the picture, the title, the TV's
// name, a progress bar that moves by itself while it plays, and back 10, play/pause and forward 10 (the jumps only for
// a TV that takes them). Tapping it opens the remote.

struct TVPlayingLiveActivity: Widget {
    var body: some WidgetConfiguration {
        ActivityConfiguration(for: TVPlayingAttributes.self) { context in
            LockScreenView(context: context)
                .activityBackgroundTint(Color.room.opacity(0.92))
                .activitySystemActionForegroundColor(Color.screen)
                .widgetURL(TVPalette.remoteURL(context.attributes))
        } dynamicIsland: { context in
            DynamicIsland {
                DynamicIslandExpandedRegion(.leading) {
                    TVArtwork(file: context.attributes.artworkFile, accent: context.attributes.accent)
                        .frame(width: 64, height: 36)
                        .clipShape(RoundedRectangle(cornerRadius: 6, style: .continuous))
                        .padding(.leading, 4)
                }
                DynamicIslandExpandedRegion(.center) {
                    VStack(alignment: .leading, spacing: 1) {
                        Text(context.attributes.title)
                            .font(.subheadline.weight(.semibold))
                        TVLabel(name: context.attributes.deviceName, isAppleTV: context.attributes.isAppleTV)
                    }
                    .lineLimit(1)
                    .frame(maxWidth: .infinity, alignment: .leading)
                }
                DynamicIslandExpandedRegion(.bottom) {
                    VStack(spacing: 6) {
                        TVProgress(state: context.state, stale: context.isStale)
                        TVButtons(attributes: context.attributes, paused: context.state.paused)
                    }
                    .padding(.horizontal, 4)
                }
            } compactLeading: {
                // Flexarr's mark, so the island says which app it is (Apple's TV symbol read as Apple's own).
                FlexarrMark()
                    .frame(width: 22, height: 22)
            } compactTrailing: {
                // What it's doing, not a button: a pause sign while paused, the waveform while it plays.
                Image(systemName: context.state.paused ? "pause.fill" : "waveform")
                    .foregroundStyle(Color.usher)
            } minimal: {
                Image(systemName: context.state.paused ? "pause.fill" : "waveform")
                    .foregroundStyle(Color.screen)
            }
            .widgetURL(TVPalette.remoteURL(context.attributes))
            .keylineTint(TVPalette.accent(context.attributes.accent))
        }
    }
}

private struct LockScreenView: View {
    let context: ActivityViewContext<TVPlayingAttributes>

    var body: some View {
        VStack(spacing: 12) {
            HStack(spacing: 12) {
                TVArtwork(file: context.attributes.artworkFile, accent: context.attributes.accent)
                    .frame(width: 80, height: 45)
                    .clipShape(RoundedRectangle(cornerRadius: 8, style: .continuous))
                VStack(alignment: .leading, spacing: 2) {
                    Text(context.attributes.title)
                        .font(.headline)
                        .foregroundStyle(Color.screen)
                    // The episode and the TV on one line under the title (beside it, a long TV name squeezed the title;
                    // on lines of their own, the panel was close to the Lock Screen's height limit).
                    HStack(spacing: 6) {
                        if let subtitle = context.attributes.subtitle {
                            Text(subtitle)
                                .font(.caption)
                                .foregroundStyle(Color.usher)
                        }
                        // The TV whole; the episode gives way.
                        TVLabel(name: context.attributes.deviceName, isAppleTV: context.attributes.isAppleTV)
                            .layoutPriority(1)
                            .fixedSize()
                    }
                }
                .lineLimit(1)
                Spacer(minLength: 0)
            }
            TVProgress(state: context.state, stale: context.isStale)
            TVButtons(attributes: context.attributes, paused: context.state.paused)
        }
        .padding(.horizontal, 16)
        .padding(.top, 14)
        .padding(.bottom, 6)
    }
}

/// The TV's name with its icon.
private struct TVLabel: View {
    let name: String
    let isAppleTV: Bool

    var body: some View {
        Label { Text(name) } icon: { TVIcon(isAppleTV: isAppleTV) }
            .font(.caption.weight(.semibold))
            .foregroundStyle(Color.usher)
            .lineLimit(1)
    }
}

/// The Flexarr mark (the app's BrandMark: three stacked bars on a 64-unit square), drawn with shapes for the island.
private struct FlexarrMark: View {
    var body: some View {
        GeometryReader { proxy in
            let unit = proxy.size.width / 64
            ZStack(alignment: .topLeading) {
                bar(x: 14, y: 15, width: 36, color: Color(red: 0.95, green: 0.93, blue: 0.91), tilt: -5, unit: unit)
                bar(x: 14, y: 28.5, width: 30, color: Color(red: 0.66, green: 0.76, blue: 0.93), tilt: 0, unit: unit)
                bar(x: 14, y: 42, width: 23, color: Color(red: 0.91, green: 0.63, blue: 0.75), tilt: 0, unit: unit)
            }
        }
        .accessibilityLabel("Flexarr")
    }

    private func bar(x: CGFloat, y: CGFloat, width: CGFloat, color: Color, tilt: Double, unit: CGFloat) -> some View {
        RoundedRectangle(cornerRadius: 5 * unit, style: .continuous)
            .fill(color)
            .frame(width: width * unit, height: 10 * unit)
            .rotationEffect(.degrees(tilt), anchor: UnitPoint(x: 0, y: (20 - y) / 10))
            .offset(x: x * unit, y: y * unit)
    }
}

private struct TVIcon: View {
    let isAppleTV: Bool

    var body: some View {
        Image(systemName: isAppleTV ? "appletv.fill" : "tv")
    }
}

/// How far it's got: moving by itself while it plays, still while paused, dimmed once it may be out of date.
private struct TVProgress: View {
    let state: TVPlayingAttributes.ContentState
    let stale: Bool

    var body: some View {
        Group {
            if let range = state.playingRange, !stale {
                ProgressView(timerInterval: range, countsDown: false) { EmptyView() } currentValueLabel: { EmptyView() }
            } else {
                ProgressView(value: state.duration > 0 ? min(1, state.position(at: Date()) / state.duration) : 0)
            }
        }
        .progressViewStyle(.linear)
        .tint(Color.screen)
        .opacity(stale ? 0.4 : 1)
    }
}

/// Back 10 seconds, play/pause and forward 10 (the jumps only for a TV that takes them), sent to the TV by the app.
/// Each a full 44 points to tap.
private struct TVButtons: View {
    let attributes: TVPlayingAttributes
    let paused: Bool

    var body: some View {
        HStack(spacing: 20) {
            if attributes.canSkip {
                button("back", "gobackward.10", label: "Back 10 seconds")
            }
            button(paused ? "resume" : "pause", paused ? "play.fill" : "pause.fill", label: paused ? "Play" : "Pause")
                .font(.title2.weight(.semibold))
            if attributes.canSkip {
                button("forward", "goforward.10", label: "Forward 10 seconds")
            }
        }
        .font(.title3.weight(.semibold))
        .buttonStyle(.plain)
        .foregroundStyle(Color.screen)
    }

    private func button(_ action: String, _ symbol: String, label: String) -> some View {
        Button(intent: TVRemoteIntent(deviceId: attributes.deviceId, action: action)) {
            Image(systemName: symbol)
                .frame(width: 44, height: 44)
                .contentShape(Rectangle())
        }
        .accessibilityLabel(label)
    }
}

/// The saved picture, or the title's colour.
private struct TVArtwork: View {
    let file: String?
    let accent: String?

    var body: some View {
        if let url = PodcastSnapshot.artwork(file), let image = UIImage(contentsOfFile: url.path(percentEncoded: false)) {
            Image(uiImage: image).resizable().aspectRatio(contentMode: .fill)
        } else {
            LinearGradient(colors: [TVPalette.accent(accent).opacity(0.7), Color.seat], startPoint: .topLeading, endPoint: .bottomTrailing)
        }
    }
}

private enum TVPalette {

    /// The link a tap on the activity opens: the remote, on this TV.
    static func remoteURL(_ attributes: TVPlayingAttributes) -> URL? {
        URL(string: "flexarr://remote/\(attributes.deviceId)")
    }

    /// "#rrggbb", or the app's silver-grey.
    static func accent(_ hex: String?) -> Color {
        guard let hex, hex.count == 7, hex.hasPrefix("#"), let value = UInt32(hex.dropFirst(), radix: 16) else { return Color.usher }
        return Color(red: Double(value >> 16 & 0xFF) / 255, green: Double(value >> 8 & 0xFF) / 255, blue: Double(value & 0xFF) / 255)
    }
}
