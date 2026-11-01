import FlexarrKit
import SwiftUI

/// The channels, over the live picture: Last watched first (the channel before this one, to flip between two), then
/// every channel in number order, each with what's on and how far through it is. Opens on Last watched when there's
/// one (flipping back is one press), otherwise on the channel being watched; choosing another changes to it.
struct LiveChannelPicker: View {
    let client: FlexarrClient
    let currentId: String?
    /// What the player was given (the category it started from), until every channel has been fetched.
    let channels: [LiveChannel]
    let previous: LiveChannel?
    let onPick: (LiveChannel) -> Void
    let onClose: () -> Void

    @State private var all: [LiveChannel]?
    @FocusState private var focused: String?

    private static let gold = Color.screen

    var body: some View {
        #if os(tvOS)
        HStack(spacing: 0) {
            Spacer(minLength: 0)
            VStack(alignment: .leading, spacing: 18) {
                Text("Channels")
                    .font(.marquee(48, weight: .black))
                    .foregroundStyle(Color.screen)
                    .padding(.horizontal, 40)
                list
            }
            .padding(.top, 60)
            .frame(width: 820)
            .background(Color.room.opacity(0.94))
        }
        .ignoresSafeArea()
        .onExitCommand(perform: onClose)
        .task { await refresh() }
        #else
        NavigationStack {
            list
                .navigationTitle("Channels")
                .navigationBarTitleDisplayMode(.inline)
                .toolbar { Button("Done", action: onClose) }
        }
        .task { await refresh() }
        #endif
    }

    private var list: some View {
        ScrollViewReader { proxy in
            ScrollView(.vertical) {
                LazyVStack(alignment: .leading, spacing: rowSpacing) {
                    if let previous {
                        heading("Last watched")
                        let channel = listed.first { $0.id == previous.id } ?? previous
                        Button { onPick(channel) } label: { row(channel) }
                            .buttonStyle(PickerRowStyle(current: false))
                            .focused($focused, equals: Self.lastWatched)
                            .id(Self.lastWatched)
                        heading("All channels").padding(.top, 12)
                    }
                    ForEach(listed) { channel in
                        Button { onPick(channel) } label: { row(channel) }
                            .buttonStyle(PickerRowStyle(current: channel.id == currentId))
                            .focused($focused, equals: channel.id)
                            .id(channel.id)
                    }
                }
                .padding(.horizontal, horizontalPadding)
                .padding(.vertical, 20)
            }
            .onAppear {
                // Opens on Last watched, so flipping back is one press; otherwise where the viewer is.
                if previous != nil {
                    proxy.scrollTo(Self.lastWatched, anchor: .top)
                    focused = Self.lastWatched
                } else if let currentId {
                    proxy.scrollTo(currentId, anchor: .center)
                    focused = currentId
                }
            }
        }
    }

    /// Every channel in number order: fetched as the list opens, the player's own until then.
    private var listed: [LiveChannel] {
        (all ?? channels).sorted { ($0.number, $0.name) < ($1.number, $1.name) }
    }

    /// Focus for the Last watched row (its channel is in the list below too).
    private static let lastWatched = "last-watched"

    private func heading(_ text: String) -> some View {
        Text(text)
            .font(headingFont)
            .foregroundStyle(Color.usher)
            .padding(.horizontal, 14)
    }

    private func row(_ channel: LiveChannel) -> some View {
        HStack(spacing: logoSpacing) {
            Text(channel.numberText)
                .font(numberFont)
                .foregroundStyle(Color.usher)
                .frame(width: numberWidth, alignment: .trailing)
            ZStack {
                RoundedRectangle(cornerRadius: 6, style: .continuous).fill(Color.room.opacity(0.8))
                RemoteImage(url: client.imageURL(channel.logoUrl, width: 300), contentMode: .fit) {
                    Text(channel.name.prefix(4)).font(.marquee(logoTextSize, weight: .bold)).foregroundStyle(Color.usher)
                }
                .padding(6)
            }
            .frame(width: logoWidth, height: logoWidth * 9 / 16)
            VStack(alignment: .leading, spacing: 4) {
                HStack(spacing: 8) {
                    Text(channel.now?.displayTitle ?? "No listings")
                        .font(titleFont)
                        .foregroundStyle(Color.screen)
                        .lineLimit(1)
                    if channel.isFavourite {
                        Image(systemName: "star.fill").font(starFont).foregroundStyle(Self.gold)
                    }
                }
                HStack(spacing: 10) {
                    Text(channel.name).lineLimit(1)
                    if let now = channel.now {
                        ProgressView(value: now.progress()).tint(.silver).frame(width: progressWidth)
                    }
                }
                .font(detailFont)
                .foregroundStyle(Color.usher)
            }
            Spacer(minLength: 0)
        }
        .padding(.horizontal, 14)
        .padding(.vertical, rowPadding)
    }

    /// Every channel and what's on now, fetched as the list opens (the player has only the category it started
    /// from, and its copy may be an hour old).
    private func refresh() async {
        guard let latest = try? await client.liveChannels() else { return }
        all = latest
    }

    #if os(tvOS)
    private let rowSpacing: CGFloat = 8, horizontalPadding: CGFloat = 30, logoSpacing: CGFloat = 20, numberWidth: CGFloat = 70, logoWidth: CGFloat = 120
    private let rowPadding: CGFloat = 10, progressWidth: CGFloat = 110, logoTextSize: CGFloat = 20
    private let numberFont = Font.system(size: 24, weight: .bold).monospacedDigit(), titleFont = Font.system(size: 27, weight: .semibold)
    private let detailFont = Font.system(size: 21), starFont = Font.system(size: 16)
    private let headingFont = Font.system(size: 22, weight: .semibold)
    #else
    private let rowSpacing: CGFloat = 2, horizontalPadding: CGFloat = 12, logoSpacing: CGFloat = 12, numberWidth: CGFloat = 36, logoWidth: CGFloat = 76
    private let rowPadding: CGFloat = 6, progressWidth: CGFloat = 80, logoTextSize: CGFloat = 14
    private let numberFont = Font.caption.weight(.bold).monospacedDigit(), titleFont = Font.body.weight(.semibold)
    private let detailFont = Font.caption, starFont = Font.system(size: 10)
    private let headingFont = Font.footnote.weight(.semibold)
    #endif
}

/// A row in a list over the picture: brighter when focused (Apple TV) or pressed, marked when it's the current one
/// (the channel being watched). The subtitle search uses it too.
struct PickerRowStyle: ButtonStyle {
    let current: Bool

    func makeBody(configuration: Configuration) -> some View {
        RowBody(configuration: configuration, current: current)
    }

    private struct RowBody: View {
        let configuration: Configuration
        let current: Bool
        @Environment(\.isFocused) private var isFocused

        var body: some View {
            configuration.label
                .background {
                    RoundedRectangle(cornerRadius: 12, style: .continuous)
                        .fill(isFocused || configuration.isPressed ? Color.screen.opacity(0.16) : current ? Color.screen.opacity(0.07) : Color.clear)
                }
                .overlay(alignment: .leading) {
                    if current {
                        Capsule().fill(Color.silver).frame(width: 4).padding(.vertical, 10)
                    }
                }
                .scaleEffect(isFocused ? 1.02 : 1)
                .animation(.spring(response: 0.25, dampingFraction: 0.8), value: isFocused)
        }
    }
}
