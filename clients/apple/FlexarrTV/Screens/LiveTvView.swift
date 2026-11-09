import FlexarrKit
import SwiftUI

/// Live TV: categories down the left (Favourites, Recordings, All, the source's groups) and the guide on the right, three hours
/// across for the channels in the category. Moving onto a category shows it; choosing what's on now plays it; holding
/// a programme adds its channel to Favourites or takes it off, or records it. Favourites is where it opens when there
/// are any. Recordings, under Favourites, lists what's set to record and the series.
struct LiveTvView: View {
    @Environment(AppModel.self) private var model
    /// Off on the Live TV tab, whose lit name in the sidebar already says where you are.
    var showsTitle = true
    @State private var channels: [LiveChannel]?
    @State private var error: String?
    @State private var chosen: Category?
    @FocusState private var focusedCategory: Category?
    /// The guide as fetched: kept a while (`LiveGuide.keptFor`) rather than downloaded again at every look, and asked
    /// for (`LiveGuidePlan`) for the window shown and a margin, and for only the favourites while they're shown.
    @State private var kept: KeptLiveGuide?
    /// The guide being asked for, so the same isn't asked twice at once.
    @State private var asking: LiveGuideRequest?
    /// The end of the window the guide shows, once it has been moved on from where it opens.
    @State private var showingUntil: Date?
    /// A recording was set or taken off while the guide couldn't be fetched for it (Recordings was shown, or the guide
    /// was already being asked for): the next fetch is for its marks, whatever is kept.
    @State private var marksChanged = false
    /// How many recordings are coming up, for the sidebar.
    @State private var upcoming: Int?
    /// What went wrong refreshing the guide.
    @State private var refreshNote: String?
    /// The note is why it failed, rather than that it worked.
    @State private var refreshFailed = false
    /// Up on screen, rather than gone behind a player or another tab.
    @State private var isVisible = false

    enum Category: Hashable {
        case favourites
        case all
        case group(String)
        case recordings
    }

    var body: some View {
        Group {
            if let channels {
                if channels.isEmpty {
                    VStack(spacing: 28) {
                        StatusView(state: .empty(t("No channels yet. Dispatcharr gives Flexarr the channels it has for Flexarr’s user.")))
                        if model.canRefreshLiveTv {
                            refreshButton.frame(width: 400)
                        }
                    }
                } else {
                    HStack(alignment: .top, spacing: 50) {
                        sidebar
                        if category == .recordings {
                            LiveRecordingsView(onChanged: { Task { await loadGuide(force: true) } }, upcoming: $upcoming)
                        } else if category == .favourites, favourites.isEmpty {
                            noFavourites
                        } else {
                            LiveGuideGrid(client: model.client, channels: self.channels(in: category), guide: kept?.guide,
                                          onWatch: { model.watch($0, among: order) },
                                          onFavourite: { setFavourite($0, !$0.isFavourite) },
                                          onRecordingsChanged: { Task { await loadGuide(force: true) } }, records: !model.hasAgeLimit,
                                          onWindow: { showingUntil = $0 })
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
                // A channel plays over this screen rather than replacing it.
                if !model.liveTv.isWatching {
                    await load()
                    await model.reloadLiveTvStatus()
                }
                try? await Task.sleep(for: .seconds(60))
            }
        }
        // The guide: looked at every five minutes and fetched again once it's old, a recording has started or ended
        // since (its mark changes), or it wasn't had: a fetch that failed is tried again at the next look. Also as the
        // channels arrive, the category changes (Favourites has only its own) and the window moves towards its end.
        .task {
            while !Task.isCancelled {
                await loadGuide(countsUpcoming: true)
                try? await Task.sleep(for: .seconds(300))
            }
        }
        // The list shown changes when the channels arrive, and when a favourite is starred on another device.
        .onChange(of: channels(in: category).map(\.id)) { Task { await loadGuide() } }
        .onChange(of: category) { Task { await loadGuide() } }
        .onChange(of: showingUntil) { Task { await loadGuide() } }
        .onChange(of: focusedCategory) { _, category in
            if let category { chosen = category }
        }
        // How a refresh went is said here once the screen's up, however long after: it can finish while it's gone.
        .onAppear {
            isVisible = true
            sayRefreshResult()
        }
        .onDisappear { isVisible = false }
        .onChange(of: model.liveTvRefreshResult) { sayRefreshResult() }
    }

    private func sayRefreshResult() {
        guard isVisible, let result = model.takeLiveTvRefreshResult() else { return }
        switch result {
        case .worked:
            refreshNote = t(Words.guideRefreshed)
            refreshFailed = false
        case .failed(let problem):
            refreshNote = problem
            refreshFailed = true
        case .stillGoing: break // Said by `isLiveTvRefreshTakingAWhile`.
        }
        Task {
            await load()
            await loadGuide(force: true)
            if result == .worked {
                try? await Task.sleep(for: .seconds(6))
                if refreshNote == t(Words.guideRefreshed) { refreshNote = nil }
            }
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
        case .recordings: []
        }
    }

    /// The order channel up and down go through: the list being looked at.
    private var order: [LiveChannel] { channels(in: category) }

    private var sidebar: some View {
        VStack(alignment: .leading, spacing: 10) {
            if showsTitle {
                Text(t(Words.liveTv))
                    .font(.marquee(56, weight: .black))
                    .foregroundStyle(Color.screen)
                    .padding(.bottom, 24)
            }
            categoryButton(.favourites, t("Favourites"), systemImage: "star.fill")
            // A recording is for everyone, so a profile with an age limit has none to see or set.
            if !model.hasAgeLimit {
                categoryButton(.recordings, t("Recordings"), systemImage: "record.circle")
            }
            categoryButton(.all, t("All channels"), systemImage: "square.grid.3x3.fill")
            refreshButton
            if groups.count > 1 {
                ForEach(groups, id: \.self) { categoryButton(.group($0), $0, systemImage: nil) }
            }
        }
        .frame(width: 400, alignment: .leading)
        .focusSection()
    }

    /// For the main profile: refreshes the guide (and has Dispatcharr download it, with an admin key), and says how many
    /// channels still have no listings. Above the groups, of which there can be more than fit.
    @ViewBuilder private var refreshButton: some View {
        if model.canRefreshLiveTv {
            Button {
                // Stays focusable while it runs: disabled, focus would fall onto a category and show it.
                guard !model.isRefreshingLiveTv else { return }
                refreshNote = nil
                Task { await model.refreshLiveTv() }
            } label: {
                HStack(spacing: 14) {
                    // A spinner can't be seen on the focused button's light background, so it's the icon that turns.
                    Image(systemName: Symbol.refresh)
                        .symbolEffect(.rotate, options: .repeat(.continuous), isActive: model.isRefreshingLiveTv)
                        .foregroundStyle(Color.usher)
                        .frame(width: 32)
                    Text(model.isRefreshingLiveTv ? t(Words.refreshingGuide) : t(Words.refreshGuide)).lineLimit(1)
                    Spacer(minLength: 8)
                }
            }
            .buttonStyle(CategoryButtonStyle(selected: false))
            Group {
                if let refreshNote {
                    Text(refreshNote).lineLimit(3).foregroundStyle(refreshFailed ? Color.danger : Color.usher)
                }
                if model.isLiveTvRefreshTakingAWhile {
                    Text(t(Words.guideTakingAWhile)).lineLimit(3)
                }
                if let count = model.liveTvStatus?.channelsWithoutListings, count > 0 {
                    Text(t("{count, plural, one {# channel has no listings} other {# channels have no listings}}", ["count": count]))
                }
            }
            .font(.system(size: 22))
            .foregroundStyle(Color.usher)
            .padding(.horizontal, 22)
        }
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
                if let count = value == .recordings ? upcoming : channels(in: value).count {
                    Text(count, format: .number)
                        .font(.system(size: 22, weight: .semibold).monospacedDigit())
                        .opacity(0.6)
                }
            }
        }
        .buttonStyle(CategoryButtonStyle(selected: category == value))
        .focused($focusedCategory, equals: value)
    }

    // MARK: Channels

    private var noFavourites: some View {
        VStack(alignment: .leading, spacing: 14) {
            Label(t("No favourites yet"), systemImage: "star")
                .font(.system(size: 34, weight: .semibold))
                .foregroundStyle(Color.screen)
            Text(t("In All channels, press and hold a programme and choose Add to Favourites. Favourites come first here, on Home, and when changing channel."))
                .font(.system(size: 26))
                .foregroundStyle(Color.usher)
        }
        .frame(maxWidth: 900, alignment: .leading)
        .padding(.top, 60)
    }

    static let gold = Color.screen

    /// The count of recordings coming up (at each look, and after a recording was set or taken off), and the guide:
    /// always after a recording was set or taken off (`force`, for its marks), otherwise only when `LiveGuidePlan` says
    /// the one kept won't do. Not while Recordings is shown, nor before the channels are known (which list is shown).
    private func loadGuide(force: Bool = false, countsUpcoming: Bool = false) async {
        guard let client = model.client else { return }
        if !model.hasAgeLimit, force || countsUpcoming { upcoming = try? await client.recordings().upcoming.count }
        let force = force || marksChanged
        guard channels != nil, category != .recordings else {
            if force { marksChanged = true }
            return
        }
        let opens = LiveGuideGrid.halfHour(before: Date()).addingTimeInterval(LiveGuideGrid.window)
        guard let request = LiveGuidePlan.next(kept: kept, scope: category == .favourites ? .favourites : .all,
                                               shown: channels(in: category).map(\.id), showingUntil: max(showingUntil ?? opens, opens),
                                               keepAhead: LiveGuideGrid.keepAhead, askAhead: LiveGuideGrid.askAhead, force: force) else { return }
        guard request != asking else {
            // The one being asked for may have gone before the recording changed.
            if force { marksChanged = true }
            return
        }
        asking = request
        defer { if asking == request { asking = nil } }
        if let loaded = try? await client.liveGuide(request, kept: kept) {
            kept = loaded
            if force { marksChanged = false }
        } else if force {
            marksChanged = true
        }
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
                    RoundedRectangle(cornerRadius: Radius.panel, style: .continuous)
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
            .clipShape(RoundedRectangle(cornerRadius: Radius.art, style: .continuous))
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
