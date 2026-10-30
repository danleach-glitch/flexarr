import FlexarrKit
import SwiftUI

/// What a TV in the house is playing, in a bar above the tab bar while one is: a close button, its picture, the title,
/// the TV's name and play/pause, with a thin progress line. Tapping it opens the remote; closing it puts the bar away
/// until the TV plays something else (nothing on the TV changes: someone may be watching).
struct TVPlayingBar: View {
    @Environment(AppModel.self) private var model
    @Environment(TVRemote.self) private var remote

    var body: some View {
        VStack(spacing: 0) {
            if let device = remote.barDevice, let playing = device.nowPlaying {
                bar(device, playing)
            }
        }
        .animation(.easeInOut(duration: 0.3), value: remote.barDevice?.id)
    }

    private func bar(_ device: Device, _ playing: DeviceNowPlaying) -> some View {
        HStack(spacing: 12) {
            Button {
                withAnimation { remote.hideBar(device) }
            } label: {
                Image(systemName: "xmark")
                    .font(.footnote.weight(.semibold))
                    .foregroundStyle(Color.usher)
                    .frame(width: 44, height: 44)
                    .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .accessibilityLabel("Hide bar")
            .accessibilityHint("Keeps playing on \(device.name)")
            .padding(.leading, -8)
            .padding(.trailing, -10)
            Button { remote.open(on: device.id) } label: {
                HStack(spacing: 12) {
                    TVStill(item: playing.item, width: 240)
                        .frame(width: 64, height: 36)
                        .clipShape(RoundedRectangle(cornerRadius: 6, style: .continuous))
                    VStack(alignment: .leading, spacing: 1) {
                        Text(playing.item.displayTitle)
                            .font(.subheadline.weight(.semibold))
                            .foregroundStyle(Color.screen)
                        Label(device.name, systemImage: device.deviceType == .androidTv ? "tv" : "appletv.fill")
                            .labelStyle(.titleAndIcon)
                            .font(.caption)
                            .foregroundStyle(Color.usher)
                    }
                    .lineLimit(1)
                    Spacer(minLength: 0)
                }
                .contentShape(Rectangle())
            }
            .buttonStyle(CardButtonStyle())
            .accessibilityHint("Opens the remote")
            Button { remote.togglePlayPause(on: device) } label: {
                Label(playing.paused ? "Play" : "Pause", systemImage: playing.paused ? "play.fill" : "pause.fill")
            }
            .buttonStyle(RoundActionStyle())
            .frame(width: 48, height: 48)
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 6)
        .background {
            RoundedRectangle(cornerRadius: 14, style: .continuous)
                .fill(Color.seat)
                .overlay(alignment: .bottom) {
                    // How far it has got, along the bottom edge, moving on while it plays (still while paused).
                    TimelineView(.animation(minimumInterval: 1, paused: playing.paused)) { context in
                        GeometryReader { proxy in
                            let position = playing.position(at: context.date)
                            Capsule().fill(Color.screen.opacity(0.7))
                                .frame(width: max(4, proxy.size.width * (playing.durationSeconds > 0 ? position / playing.durationSeconds : 0)), height: 2)
                        }
                    }
                    .frame(height: 2)
                    .padding(.horizontal, 14)
                    .padding(.bottom, 4)
                }
                .shadow(color: .black.opacity(0.35), radius: 12, y: 4)
        }
        .padding(.horizontal, 10)
        .padding(.bottom, 6)
        #if DEBUG
        // `-FlexarrShowRemote YES` opens the remote as soon as a TV is playing.
        .task { if UserDefaults.standard.bool(forKey: "FlexarrShowRemote") { remote.showsRemote = true } }
        #endif
        .transition(.move(edge: .bottom).combined(with: .opacity))
        .accessibilityElement(children: .contain)
        .accessibilityLabel("Playing on \(device.name): \(playing.item.displayTitle)")
        // Undo for a stop on this TV, while it's offered.
        .accessibilityActions {
            if remote.notice?.undo?.deviceId == device.id { Button("Undo stop") { remote.undoStop() } }
        }
    }
}

/// What the remote has to say for a moment, over the bars and in the remote: a command that didn't work, or "Stopped"
/// with Undo.
struct RemoteNoticeView: View {
    @Environment(TVRemote.self) private var remote

    var body: some View {
        VStack(spacing: 0) {
            if let notice = remote.notice {
                HStack(spacing: 14) {
                    Text(notice.text)
                        .font(.footnote.weight(.semibold))
                        .foregroundStyle(Color.screen)
                        .multilineTextAlignment(.leading)
                    if notice.undo != nil {
                        Button("Undo") { remote.undoStop() }
                            .font(.footnote.weight(.bold))
                            .foregroundStyle(Color.silver)
                            .frame(minHeight: 44)
                    }
                }
                .padding(.horizontal, 16)
                .padding(.vertical, notice.undo == nil ? 10 : 0)
                // A rounded rectangle rather than a capsule: at the largest text sizes the words take several lines.
                .background(RoundedRectangle(cornerRadius: 20, style: .continuous).fill(Color.seat).shadow(color: .black.opacity(0.4), radius: 12, y: 4))
                .padding(.horizontal, 20)
                .padding(.bottom, 8)
                .transition(.move(edge: .bottom).combined(with: .opacity))
            }
        }
        .animation(.easeOut(duration: 0.25), value: remote.notice)
    }
}

/// A title's 16:9 picture: an episode's own still, else the backdrop, else its colour.
struct TVStill: View {
    @Environment(AppModel.self) private var model
    let item: ItemSummary
    var width = 1000

    var body: some View {
        Color.seat.overlay {
            RemoteImage(url: model.client?.imageURL(path, width: width)) {
                LinearGradient(colors: [item.glowColor.opacity(0.5), Color.seat], startPoint: .topLeading, endPoint: .bottomTrailing)
            }
        }
    }

    private var path: String? {
        item.kind == .episode ? (item.artwork.thumb ?? item.artwork.backdrop) : (item.artwork.backdrop ?? item.artwork.thumb)
    }
}
