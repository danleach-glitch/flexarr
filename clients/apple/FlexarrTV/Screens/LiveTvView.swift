import FlexarrKit
import SwiftUI

/// Live TV: categories down the left (Favourites, All, the source's groups) and the guide on the right, three hours
/// across for the channels in the category. Moving onto a category shows it; choosing what's on now plays it; holding
/// a programme adds its channel to Favourites or takes it off. Favourites is where it opens when there are any.
struct LiveTvView: View {
    @Environment(AppModel.self) private var model
    @State private var channels: [LiveChannel]?
    @State private var error: String?
    @State private var chosen: Category?
    @FocusState private var focusedCategory: Category?
    @State private var guide: LiveGuide?

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
                        if category == .favourites, favourites.isEmpty {
                            noFavourites
                        } else {
                            LiveGuideGrid(client: model.client, channels: self.channels(in: category), guide: guide,
                                          onWatch: { model.watch($0, among: order) },
                                          onFavourite: { setFavourite($0, !$0.isFavourite) })
                        }
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
        // The guide: a day's worth, fetched again every five minutes.
        .task {
            while !Task.isCancelled {
                await loadGuide()
                try? await Task.sleep(for: .seconds(300))
            }
        }
        .onChange(of: focusedCategory) { _, category in
            if let category { chosen = category }
        }
    }

    // MARK: Categories

    private var favourites: [LiveChannel] { (channels ?? []).filter(\.isFavourite) }

    /// Where the screen opens: Favourites when there are any, otherwise All channels. Moving onto Favourites shows it
    /// even while it's empty, with how to add some.
    private var category: Category {
        chosen ?? (favourites.isEmpty ? .all : .favourites)
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
            categoryButton(.favourites, "Favourites", systemImage: "star.fill")
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

    private var noFavourites: some View {
        VStack(alignment: .leading, spacing: 14) {
            Label("No favourites yet", systemImage: "star")
                .font(.system(size: 34, weight: .semibold))
                .foregroundStyle(Color.screen)
            Text("In All channels, press and hold a programme and choose Add to Favourites. Favourites come first here, on Home, and when changing channel.")
                .font(.system(size: 26))
                .foregroundStyle(Color.usher)
        }
        .frame(maxWidth: 900, alignment: .leading)
        .padding(.top, 60)
    }

    static let gold = Color(red: 0.95, green: 0.77, blue: 0.45)

    private func loadGuide() async {
        guard let client = model.client else { return }
        if let loaded = try? await client.liveGuide(from: Date().addingTimeInterval(-3600), hours: 24) { guide = loaded }
    }

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
