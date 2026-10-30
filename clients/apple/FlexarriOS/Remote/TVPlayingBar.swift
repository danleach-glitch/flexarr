import FlexarrKit
import SwiftUI

/// What a TV in the house is playing, in a bar above the tab bar while one is: its picture, the title, the TV's name
/// and play/pause, with a thin progress line. Tapping it opens the remote.
struct TVPlayingBar: View {
    @Environment(AppModel.self) private var model
    @Environment(TVRemote.self) private var remote
    @State private var showsRemote = false

    var body: some View {
        if let device = remote.current, let playing = device.nowPlaying {
            HStack(spacing: 12) {
                Button { showsRemote = true } label: {
                    HStack(spacing: 12) {
                        TVStill(item: playing.item, width: 400)
                            .frame(width: 64, height: 36)
                            .clipShape(RoundedRectangle(cornerRadius: 6, style: .continuous))
                        VStack(alignment: .leading, spacing: 1) {
                            Text(playing.item.displayTitle)
                                .font(.subheadline.weight(.semibold))
                                .foregroundStyle(Color.screen)
                            Label(device.name, systemImage: device.deviceType == .appleTv ? "appletv.fill" : "tv")
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
                Button { remote.togglePlayPause() } label: {
                    Label(playing.paused ? "Play" : "Pause", systemImage: playing.paused ? "play.fill" : "pause.fill")
                }
                .buttonStyle(RoundActionStyle())
                .frame(width: 40, height: 40)
                .sensoryFeedback(.impact(weight: .light), trigger: playing.paused)
            }
            .padding(.horizontal, 12)
            .padding(.vertical, 8)
            .background {
                RoundedRectangle(cornerRadius: 14, style: .continuous)
                    .fill(Color.seat)
                    .overlay(alignment: .bottom) {
                        // How far it has got, along the bottom edge, moving on while it plays.
                        TimelineView(.periodic(from: .now, by: 1)) { context in
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
            .task { if UserDefaults.standard.bool(forKey: "FlexarrShowRemote") { showsRemote = true } }
            #endif
            .sheet(isPresented: $showsRemote) {
                TVRemoteView()
                    .presentationDetents([.large])
                    .presentationDragIndicator(.hidden)
                    .presentationBackground(Color.room)
            }
            .transition(.move(edge: .bottom).combined(with: .opacity))
            .accessibilityElement(children: .contain)
            .accessibilityLabel("Playing on \(device.name): \(playing.item.displayTitle)")
        }
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
