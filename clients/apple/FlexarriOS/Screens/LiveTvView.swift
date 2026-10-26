import FlexarrKit
import SwiftUI

/// Live TV on iPhone and iPad: the channels with what's on now (and how far through) and what's next. Favourites
/// first, and shown on their own when there are any; tap to watch, swipe or hold to add or remove a favourite.
struct LiveTvView: View {
    @Environment(AppModel.self) private var model
    @State private var channels: [LiveChannel]?
    @State private var error: String?
    /// Unchosen, the view is Favourites when there are some, otherwise All.
    @State private var chosen: Filter?

    enum Filter: Hashable {
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
                    list
                }
            } else if let error {
                StatusView(state: .failed(error) { Task { await load() } })
            } else {
                StatusView(state: .loading)
            }
        }
        .background(RoomBackground(ambient: nil))
        .navigationTitle("Live TV")
        // What's on moves on: fetched again each minute while the screen is up.
        .task {
            while !Task.isCancelled {
                await load()
                try? await Task.sleep(for: .seconds(60))
            }
        }
        .refreshable { await load() }
    }

    private var favourites: [LiveChannel] { (channels ?? []).filter(\.isFavourite) }

    /// Favourites when there are any, otherwise All, until one is chosen (Favourites stays chosen even while empty).
    private var filter: Filter {
        chosen ?? (favourites.isEmpty ? .all : .favourites)
    }

    private var groups: [String] {
        var seen: [String] = []
        for name in (channels ?? []).compactMap(\.groupName) where !seen.contains(name) { seen.append(name) }
        return seen
    }

    private var shown: [LiveChannel] {
        let all = channels ?? []
        return switch filter {
        case .favourites: favourites
        case .all: all
        case .group(let name): all.filter { $0.groupName == name }
        }
    }

    /// The order channel up and down go through: favourites first, then the rest by number.
    private var order: [LiveChannel] { favourites + (channels ?? []).filter { !$0.isFavourite } }

    private var list: some View {
        List {
            ScrollView(.horizontal, showsIndicators: false) {
                HStack(spacing: 8) {
                    chip("Favourites", systemImage: "star.fill", .favourites)
                    chip("All", systemImage: nil, .all)
                    if groups.count > 1 {
                        ForEach(groups, id: \.self) { chip($0, systemImage: nil, .group($0)) }
                    }
                }
                .padding(.horizontal, Layout.edge)
            }
            .listRowInsets(EdgeInsets())
            .listRowBackground(Color.clear)
            .listRowSeparator(.hidden)

            ForEach(shown) { channel in
                ChannelRow(channel: channel, starting: model.liveTv.startingChannel == channel.id)
                    .contentShape(Rectangle())
                    .onTapGesture { model.watch(channel, among: order) }
                    .accessibilityAddTraits(.isButton)
                    .accessibilityAction(named: channel.isFavourite ? "Remove from Favourites" : "Add to Favourites") {
                        setFavourite(channel, !channel.isFavourite)
                    }
                    .swipeActions(edge: .leading, allowsFullSwipe: true) {
                        Button { setFavourite(channel, !channel.isFavourite) } label: {
                            Label(channel.isFavourite ? "Unfavourite" : "Favourite", systemImage: channel.isFavourite ? "star.slash" : "star.fill")
                        }
                        .tint(channel.isFavourite ? Color.aisle : Self.gold)
                    }
                    .contextMenu {
                        Button { model.watch(channel, among: order) } label: { Label("Watch", systemImage: "play.fill") }
                        Button { setFavourite(channel, !channel.isFavourite) } label: {
                            Label(channel.isFavourite ? "Remove from Favourites" : "Add to Favourites", systemImage: channel.isFavourite ? "star.slash" : "star")
                        }
                    }
                    .listRowBackground(Color.clear)
            }
            if favourites.isEmpty {
                Label("Swipe right on the channels you watch most to keep them first.", systemImage: "star")
                    .font(.footnote)
                    .foregroundStyle(Color.usher)
                    .listRowBackground(Color.clear)
                    .listRowSeparator(.hidden)
            }
        }
        .listStyle(.plain)
        .scrollContentBackground(.hidden)
    }

    static let gold = Color(red: 0.95, green: 0.77, blue: 0.45)

    private func chip(_ title: String, systemImage: String?, _ value: Filter) -> some View {
        let selected = filter == value
        return Button { chosen = value } label: {
            HStack(spacing: 6) {
                if let systemImage { Image(systemName: systemImage).foregroundStyle(Self.gold) }
                Text(title)
            }
            .font(.subheadline.weight(.semibold))
            .foregroundStyle(selected ? Color.screen : Color.usher)
            .padding(.horizontal, 14)
            .padding(.vertical, 8)
            .background(Capsule().strokeBorder(selected ? Color.screen : Color.aisle, lineWidth: 1))
        }
        .buttonStyle(.plain)
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

private struct ChannelRow: View {
    @Environment(AppModel.self) private var model
    @Environment(\.horizontalSizeClass) private var sizeClass
    let channel: LiveChannel
    let starting: Bool

    var body: some View {
        HStack(spacing: 14) {
            ZStack {
                RoundedRectangle(cornerRadius: 8, style: .continuous).fill(Color.seat)
                RemoteImage(url: model.client?.imageURL(channel.logoUrl, width: 300), contentMode: .fit) {
                    Text(channel.name.prefix(4))
                        .font(.marquee(18, weight: .bold))
                        .foregroundStyle(Color.usher)
                }
                .padding(8)
                if starting {
                    RoundedRectangle(cornerRadius: 8, style: .continuous).fill(.black.opacity(0.45))
                    ProgressView()
                }
            }
            .frame(width: logoWidth, height: logoWidth * 9 / 16)
            .overlay(alignment: .topTrailing) {
                if channel.isFavourite {
                    Image(systemName: "star.fill")
                        .font(.system(size: 10))
                        .foregroundStyle(LiveTvView.gold)
                        .padding(5)
                }
            }

            VStack(alignment: .leading, spacing: 4) {
                HStack(spacing: 6) {
                    Text(channel.numberText).monospacedDigit().foregroundStyle(Color.usher)
                    Text(channel.name).foregroundStyle(Color.usher).lineLimit(1)
                }
                .font(.caption.weight(.semibold))
                Text(channel.now?.displayTitle ?? "No listings")
                    .font(.body.weight(.semibold))
                    .foregroundStyle(Color.screen)
                    .lineLimit(1)
                if let now = channel.now {
                    ProgressView(value: now.progress())
                        .tint(.silver)
                        .frame(maxWidth: 220)
                }
                if let next = channel.next {
                    Text("Next: \(Self.clock.string(from: next.start)) \(next.title)")
                        .font(.caption)
                        .foregroundStyle(Color.usher)
                        .lineLimit(1)
                }
            }
            Spacer(minLength: 0)
        }
        .padding(.vertical, 6)
    }

    private var logoWidth: CGFloat { sizeClass == .regular ? 140 : 104 }

    private static let clock: DateFormatter = {
        let formatter = DateFormatter()
        formatter.timeStyle = .short
        return formatter
    }()
}

/// A favourite channel on Home: its logo on a dark plate with how far through what's on, and what's on beneath.
struct HomeChannelCard: View {
    @Environment(AppModel.self) private var model
    let channel: LiveChannel
    let order: [LiveChannel]
    let width: CGFloat

    var body: some View {
        Button { model.watch(channel, among: order) } label: {
            VStack(alignment: .leading, spacing: 6) {
                ZStack {
                    Color.seat
                    RemoteImage(url: model.client?.imageURL(channel.logoUrl, width: 400), contentMode: .fit) {
                        Text(channel.name.prefix(4))
                            .font(.marquee(22, weight: .bold))
                            .foregroundStyle(Color.usher)
                    }
                    .padding(.horizontal, width * 0.18)
                    .padding(.vertical, width * 0.1)
                    if model.liveTv.startingChannel == channel.id {
                        Color.black.opacity(0.45)
                        ProgressView()
                    }
                }
                .frame(width: width, height: width * 9 / 16)
                .overlay(alignment: .topLeading) {
                    Text(channel.numberText)
                        .font(.caption.weight(.bold).monospacedDigit())
                        .foregroundStyle(Color.usher)
                        .padding(8)
                }
                .overlay(alignment: .bottom) {
                    if let now = channel.now {
                        ProgressView(value: now.progress()).tint(.silver).padding(10)
                    }
                }
                .clipShape(RoundedRectangle(cornerRadius: 8, style: .continuous))
                Text(channel.now?.displayTitle ?? channel.name)
                    .font(.subheadline.weight(.semibold))
                    .foregroundStyle(Color.screen)
                    .lineLimit(1)
                Text(channel.name)
                    .font(.caption)
                    .foregroundStyle(Color.usher)
                    .lineLimit(1)
            }
            .frame(width: width, alignment: .leading)
        }
        .buttonStyle(.plain)
    }
}
