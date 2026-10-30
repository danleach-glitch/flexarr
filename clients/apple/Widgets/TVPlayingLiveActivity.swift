import ActivityKit
import AppIntents
import SwiftUI
import WidgetKit

// What a TV in the house is playing, on the Lock Screen and in the Dynamic Island: the picture, the title, the TV's
// name, a progress bar that moves by itself while it plays, and back 10, play/pause and forward 10. Tapping it opens
// the app (its Playing on TV bar opens the remote).

struct TVPlayingLiveActivity: Widget {
    var body: some WidgetConfiguration {
        ActivityConfiguration(for: TVPlayingAttributes.self) { context in
            LockScreenView(context: context)
                .activityBackgroundTint(TVPalette.room.opacity(0.92))
                .activitySystemActionForegroundColor(TVPalette.screen)
        } dynamicIsland: { context in
            DynamicIsland {
                DynamicIslandExpandedRegion(.leading) {
                    TVArtwork(file: context.attributes.artworkFile, accent: context.attributes.accent)
                        .frame(width: 64, height: 36)
                        .clipShape(RoundedRectangle(cornerRadius: 6, style: .continuous))
                        .padding(.leading, 4)
                }
                DynamicIslandExpandedRegion(.trailing) {
                    TVLabel(name: context.attributes.deviceName)
                        .padding(.trailing, 4)
                }
                DynamicIslandExpandedRegion(.center) {
                    Text(context.attributes.title)
                        .font(.subheadline.weight(.semibold))
                        .lineLimit(1)
                }
                DynamicIslandExpandedRegion(.bottom) {
                    VStack(spacing: 10) {
                        TVProgress(state: context.state, stale: context.isStale)
                        TVButtons(deviceId: context.attributes.deviceId, paused: context.state.paused)
                    }
                    .padding(.horizontal, 4)
                }
            } compactLeading: {
                Image(systemName: "appletv.fill")
                    .foregroundStyle(TVPalette.screen)
            } compactTrailing: {
                Image(systemName: context.state.paused ? "pause.fill" : "play.fill")
                    .foregroundStyle(TVPalette.usher)
            } minimal: {
                Image(systemName: context.state.paused ? "pause.fill" : "appletv.fill")
                    .foregroundStyle(TVPalette.screen)
            }
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
                        .foregroundStyle(TVPalette.screen)
                    if let subtitle = context.attributes.subtitle {
                        Text(subtitle)
                            .font(.caption)
                            .foregroundStyle(TVPalette.usher)
                    }
                }
                .lineLimit(1)
                Spacer(minLength: 0)
                TVLabel(name: context.attributes.deviceName)
            }
            TVProgress(state: context.state, stale: context.isStale)
            TVButtons(deviceId: context.attributes.deviceId, paused: context.state.paused)
        }
        .padding(16)
    }
}

/// The TV's name with its icon.
private struct TVLabel: View {
    let name: String

    var body: some View {
        Label(name, systemImage: "appletv.fill")
            .font(.caption.weight(.semibold))
            .foregroundStyle(TVPalette.usher)
            .lineLimit(1)
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
        .tint(TVPalette.screen)
        .opacity(stale ? 0.4 : 1)
    }
}

/// Back 10 seconds, play/pause and forward 10, sent to the TV by the app.
private struct TVButtons: View {
    let deviceId: String
    let paused: Bool

    var body: some View {
        HStack(spacing: 36) {
            Button(intent: TVRemoteIntent(deviceId: deviceId, action: "back")) {
                Image(systemName: "gobackward.10")
            }
            .accessibilityLabel("Back 10 seconds")
            Button(intent: TVRemoteIntent(deviceId: deviceId, action: paused ? "resume" : "pause")) {
                Image(systemName: paused ? "play.fill" : "pause.fill")
                    .font(.title2)
            }
            .accessibilityLabel(paused ? "Play" : "Pause")
            Button(intent: TVRemoteIntent(deviceId: deviceId, action: "forward")) {
                Image(systemName: "goforward.10")
            }
            .accessibilityLabel("Forward 10 seconds")
        }
        .font(.title3.weight(.semibold))
        .buttonStyle(.plain)
        .foregroundStyle(TVPalette.screen)
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
            LinearGradient(colors: [TVPalette.accent(accent).opacity(0.7), TVPalette.seat], startPoint: .topLeading, endPoint: .bottomTrailing)
        }
    }
}

private enum TVPalette {
    static let room = Color(red: 0x1B / 255, green: 0x17 / 255, blue: 0x20 / 255)
    static let seat = Color(red: 0x25 / 255, green: 0x20 / 255, blue: 0x2C / 255)
    static let screen = Color(red: 0xF2 / 255, green: 0xEE / 255, blue: 0xE7 / 255)
    static let usher = Color(red: 0xA3 / 255, green: 0x9A / 255, blue: 0xAE / 255)

    /// "#rrggbb", or the app's silver-grey.
    static func accent(_ hex: String?) -> Color {
        guard let hex, hex.count == 7, hex.hasPrefix("#"), let value = UInt32(hex.dropFirst(), radix: 16) else { return usher }
        return Color(red: Double(value >> 16 & 0xFF) / 255, green: Double(value >> 8 & 0xFF) / 255, blue: Double(value & 0xFF) / 255)
    }
}
