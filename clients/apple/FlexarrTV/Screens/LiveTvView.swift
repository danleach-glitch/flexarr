import FlexarrKit
import SwiftUI

/// Live TV: categories down the left (Favourites, All, the source's groups) and a dense list of channels on the
/// right, one row each with what's on, how far through, and what's next, so hundreds of channels stay quick to move
/// through with the remote. Moving onto a category shows it; selecting a channel plays it; holding one adds or removes
/// a favourite. Favourites is where it opens when there are any.
struct LiveTvView: View {
    @Environment(AppModel.self) private var model
    @State private var channels: [LiveChannel]?
    @State private var error: String?
    @State private var chosen: Category?
    @FocusState private var focusedCategory: Category?

    enum Category: Hashable {
        case favourites
        case all
        case group(String)
    }

    var body: some View {
        Group {
            if let channels {
                if channels.isEmpty {
                    StatusView(state: .empty("No channels yet. Dispatcharr gives Flexarr the channels it has for Flexarr's user."))
                } else {
                    HStack(alignment: .top, spacing: 50) {
                        sidebar
                        list
                    }
                    .padding(.leading, Layout.edge)
                    .padding(.top, 30)
                }
            } else if let error {
                StatusView(state: .failed(error) { Task { await load() } })
            } else {
                StatusView(state: .loading)
            }
        }
        .background(RoomBackground(ambient: nil))
        // What's on moves on: fetched again each minute while the screen is up.
        .task {
            while !Task.isCancelled {
                await load()
                try? await Task.sleep(for: .seconds(60))
            }
        }
        .onChange(of: focusedCategory) { _, category in
            if let category { chosen = category }
        }
    }

    // MARK: Categories

    private var favourites: [LiveChannel] { (channels ?? []).filter(\.isFavourite) }

    /// Favourites unless chosen otherwise, while there are any.
    private var category: Category {
        switch chosen ?? .favourites {
        case .favourites: favourites.isEmpty ? .all : .favourites
        case let other: other
        }
    }

    private var groups: [String] {
        var seen: [String] = []
        for name in (channels ?? []).compactMap(\.groupName) where !seen.contains(name) { seen.append(name) }
        return seen
    }

    private func channels(in category: Category) -> [LiveChannel] {
        let all = channels ?? []
        return switch category {
        case .favourites: favourites
        case .all: all
        case .group(let name): all.filter { $0.groupName == name }
        }
    }

    /// The order channel up and down go through: the list being looked at.
    private var order: [LiveChannel] { channels(in: category) }

    private var sidebar: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Live TV")
                .font(.marquee(56, weight: .black))
                .foregroundStyle(Color.screen)
                .padding(.bottom, 24)
            if !favourites.isEmpty {
                categoryButton(.favourites, "Favourites", systemImage: "star.fill")
            }
            categoryButton(.all, "All channels", systemImage: "square.grid.3x3.fill")
            if groups.count > 1 {
                ForEach(groups, id: \.self) { categoryButton(.group($0), $0, systemImage: nil) }
            }
        }
        .frame(width: 400, alignment: .leading)
        .focusSection()
    }

    private func categoryButton(_ value: Category, _ title: String, systemImage: String?) -> some View {
        Button { chosen = value } label: {
            HStack(spacing: 14) {
                if let systemImage {
                    Image(systemName: systemImage)
                        .foregroundStyle(value == .favourites ? Self.gold : Color.usher)
                        .frame(width: 32)
                } else {
                    Color.clear.frame(width: 32, height: 1)
                }
                Text(title).lineLimit(1)
                Spacer(minLength: 8)
                Text(channels(in: value).count, format: .number)
                    .font(.system(size: 22, weight: .semibold).monospacedDigit())
                    .opacity(0.6)
            }
        }
        .buttonStyle(CategoryButtonStyle(selected: category == value))
        .focused($focusedCategory, equals: value)
    }

    // MARK: Channels

    private var list: some View {
        ScrollView(.vertical) {
            LazyVStack(spacing: 10) {
                ForEach(channels(in: category)) { channel in
                    Button {
                        model.watch(channel, among: order)
                    } label: {
                        ChannelListRow(channel: channel, starting: model.liveTv.startingChannel == channel.id)
                    }
                    .buttonStyle(ChannelRowButtonStyle())
                    .contextMenu {
                        Button {
                            setFavourite(channel, !channel.isFavourite)
                        } label: {
                            Label(channel.isFavourite ? "Remove from Favourites" : "Add to Favourites", systemImage: channel.isFavourite ? "star.slash" : "star")
                        }
                    }
                }
            }
            .padding(.trailing, Layout.edge)
            .padding(.vertical, 20)
        }
        .scrollClipDisabled()
        .focusSection()
        .id(category)
    }

    static let gold = Color(red: 0.95, green: 0.77, blue: 0.45)

    private func load() async {
        guard let client = model.client else { return }
        do {
            channels = try await client.liveChannels()
            error = nil
        } catch {
            if channels == nil { self.error = ConnectionError(error).message }
        }
    }

    private func setFavourite(_ channel: LiveChannel, _ favourite: Bool) {
        guard let client = model.client else { return }
        channels = channels?.map { $0.id == channel.id ? $0.with(favourite: favourite) : $0 }
        Task {
            do {
                try await client.setFavouriteChannel(channel.id, favourite)
            } catch {
                channels = channels?.map { $0.id == channel.id ? $0.with(favourite: !favourite) : $0 }
            }
        }
    }
}

/// One channel in the list: number, logo, what's on with how far through, and what's next.
private struct ChannelListRow: View {
    @Environment(AppModel.self) private var model
    @Environment(\.isFocused) private var isFocused
    let channel: LiveChannel
    let starting: Bool

    var body: some View {
        HStack(spacing: 24) {
            Text(channel.numberText)
                .font(.system(size: 26, weight: .bold).monospacedDigit())
                .foregroundStyle(Color.usher)
                .frame(width: 80, alignment: .trailing)

            ZStack {
                RoundedRectangle(cornerRadius: 8, style: .continuous).fill(Color.room.opacity(0.7))
                RemoteImage(url: model.client?.imageURL(channel.logoUrl, width: 300), contentMode: .fit) {
                    Text(channel.name.prefix(4))
                        .font(.marquee(24, weight: .bold))
                        .foregroundStyle(Color.usher)
                }
                .padding(.horizontal, 14)
                .padding(.vertical, 8)
                if starting {
                    RoundedRectangle(cornerRadius: 8, style: .continuous).fill(.black.opacity(0.5))
                    ProgressView()
                }
            }
            .frame(width: 140, height: 78)

            VStack(alignment: .leading, spacing: 8) {
                HStack(spacing: 10) {
                    Text(channel.now?.displayTitle ?? "No listings")
                        .font(.system(size: 29, weight: .semibold))
                        .foregroundStyle(Color.screen)
                        .lineLimit(1)
                    if channel.isFavourite {
                        Image(systemName: "star.fill")
                            .font(.system(size: 18))
                            .foregroundStyle(LiveTvView.gold)
                    }
                }
                HStack(spacing: 16) {
                    Text(channel.name)
                    if let now = channel.now {
                        ProgressBar(value: now.progress(), tint: .silver)
                            .frame(width: 120)
                        Text(LiveTvController.timeRange(now)).monospacedDigit()
                    }
                }
                .font(.system(size: 22))
                .foregroundStyle(Color.usher)
                .lineLimit(1)
            }
            .frame(maxWidth: .infinity, alignment: .leading)

            if let next = channel.next {
                VStack(alignment: .leading, spacing: 6) {
                    Text("Next · \(LiveTvController.startTime(next))")
                        .font(.system(size: 20, weight: .semibold))
                        .foregroundStyle(Color.usher)
                    Text(next.title)
                        .font(.system(size: 23))
                        .foregroundStyle(Color.screen.opacity(0.8))
                        .lineLimit(1)
                }
                .frame(width: 320, alignment: .leading)
            }
        }
        .padding(.horizontal, 20)
        .padding(.vertical, 12)
    }
}

/// A channel row: a quiet panel that brightens and lifts a little when focused.
private struct ChannelRowButtonStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        RowBody(configuration: configuration)
    }

    private struct RowBody: View {
        let configuration: Configuration
        @Environment(\.isFocused) private var isFocused

        var body: some View {
            configuration.label
                .background {
                    RoundedRectangle(cornerRadius: 16, style: .continuous)
                        .fill(isFocused ? Color.screen.opacity(0.16) : Color.seat.opacity(0.55))
                }
                .overlay {
                    RoundedRectangle(cornerRadius: 16, style: .continuous)
                        .strokeBorder(Color.screen.opacity(isFocused ? 0.5 : 0), lineWidth: 2)
                }
                .scaleEffect(configuration.isPressed ? 0.99 : (isFocused ? 1.02 : 1))
                .shadow(color: .black.opacity(isFocused ? 0.4 : 0), radius: 20, y: 10)
                .animation(.spring(response: 0.25, dampingFraction: 0.8), value: isFocused)
        }
    }
}

/// A category in the sidebar: filled when focused, marked when it's the one shown.
private struct CategoryButtonStyle: ButtonStyle {
    let selected: Bool

    func makeBody(configuration: Configuration) -> some View {
        StyledBody(configuration: configuration, selected: selected)
    }

    private struct StyledBody: View {
        let configuration: Configuration
        let selected: Bool
        @Environment(\.isFocused) private var isFocused

        var body: some View {
            configuration.label
                .font(.system(size: 29, weight: .semibold))
                .foregroundStyle(isFocused ? Color.room : selected ? Color.screen : Color.usher)
                .padding(.horizontal, 22)
                .frame(height: 70)
                .background {
                    RoundedRectangle(cornerRadius: 14, style: .continuous)
                        .fill(isFocused ? AnyShapeStyle(Color.screen) : AnyShapeStyle(selected ? Color.screen.opacity(0.12) : Color.clear))
                }
                .scaleEffect(isFocused ? 1.04 : 1)
                .animation(.spring(response: 0.25, dampingFraction: 0.8), value: isFocused)
        }
    }
}

/// A favourite channel on Home: its logo on a dark plate with how far through what's on, and what's on beneath.
struct HomeChannelCard: View {
    @Environment(AppModel.self) private var model
    let channel: LiveChannel
    let order: [LiveChannel]

    private static let width: CGFloat = Layout.wideWidth * 0.8

    var body: some View {
        ArtworkButton(ambient: .silver) {
            model.watch(channel, among: order)
        } artwork: {
            ZStack {
                Color.seat
                RemoteImage(url: model.client?.imageURL(channel.logoUrl, width: 500), contentMode: .fit) {
                    Text(channel.name)
                        .font(.marquee(36, weight: .bold))
                        .foregroundStyle(Color.screen.opacity(0.8))
                        .multilineTextAlignment(.center)
                }
                .padding(.horizontal, 60)
                .padding(.vertical, 36)
                if model.liveTv.startingChannel == channel.id {
                    Color.black.opacity(0.45)
                    ProgressView()
                }
            }
            .frame(width: Self.width, height: Self.width * 9 / 16)
            .overlay(alignment: .topLeading) {
                Text(channel.numberText)
                    .font(.system(size: 22, weight: .bold).monospacedDigit())
                    .foregroundStyle(Color.usher)
                    .padding(14)
            }
            .overlay(alignment: .bottom) {
                if let now = channel.now {
                    ProgressBar(value: now.progress(), tint: .silver).padding(16)
                }
            }
            .clipShape(RoundedRectangle(cornerRadius: 8, style: .continuous))
        } caption: { focused in
            VStack(alignment: .leading, spacing: 4) {
                Text(channel.now?.displayTitle ?? channel.name)
                    .font(.system(size: 25, weight: .semibold))
                    .foregroundStyle(focused ? Color.screen : Color.screen.opacity(0.85))
                    .lineLimit(1)
                Text(channel.now.map { "\(channel.name)   \(LiveTvController.timeRange($0))" } ?? channel.name)
                    .font(.system(size: 21))
                    .foregroundStyle(Color.usher)
                    .lineLimit(1)
            }
            .frame(width: Self.width, alignment: .leading)
        }
    }
}
