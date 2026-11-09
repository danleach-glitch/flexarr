import FlexarrKit
import SwiftUI

/// The guide: channels down the left, three hours across. Up and down go between channels, left and right between
/// programmes; pressing past either edge moves the window an hour and a half. What's on now plays when chosen;
/// anything later shows what it is.
struct LiveGuideGrid: View {
    let client: FlexarrClient?
    /// The channels to show (the sidebar's category, or every channel over the player), in order; their programmes
    /// come from `guide`.
    let channels: [LiveChannel]
    let guide: LiveGuide?
    /// Plays a channel (from the Live TV screen), or changes to it (over the player).
    let onWatch: (LiveChannel) -> Void
    /// Adds a channel to Favourites or takes it off, from holding one of its programmes (nil: no menu).
    var onFavourite: ((LiveChannel) -> Void)? = nil
    /// A recording was set or taken off: the guide is fetched again for its marks.
    var onRecordingsChanged: (() -> Void)? = nil
    /// Where focus starts: what's on now on this channel (the one being watched), scrolled into view.
    var focusChannel: String? = nil
    /// Programmes can be set to record: not for a profile with an age limit, since a recording is for everyone.
    var records = true
    /// The end of the window, each time it moves: the guide is fetched further ahead as it nears the end of it.
    var onWindow: ((Date) -> Void)? = nil
    /// Said quietly above the channels (the guide couldn't be fetched for some of them).
    var note: String? = nil

    /// Where focus is in the grid: a programme, or the cell of a channel with nothing listed in the window.
    private enum GridFocus: Hashable {
        case programme(Int64)
        case noListings(String)
    }

    @FocusState private var focused: GridFocus?

    @State private var windowStart = LiveGuideGrid.halfHour(before: Date())
    @State private var details: LiveProgramme?
    @State private var detailsChannel: LiveChannel?
    @State private var now = Date()
    /// Something to tell the viewer about a recording (it clashes, or couldn't be set).
    @State private var notice: String?

    static let window: TimeInterval = 3 * 3600
    static let step: TimeInterval = 90 * 60
    /// The guide is asked for this far past the end of the window (`LiveGuidePlan`), and again once it reaches less
    /// than `keepAhead` past it: a page ahead, so it's there by the time the window gets there.
    static let askAhead: TimeInterval = 3 * step
    static let keepAhead: TimeInterval = step
    private static let channelWidth: CGFloat = 230
    private static let rowHeight: CGFloat = 96
    /// Made each time, so it follows a change of language.
    private static var clock: DateFormatter {
        let formatter = DateFormatter()
        formatter.locale = Localization.locale
        formatter.timeStyle = .short
        return formatter
    }

    var body: some View {
        GeometryReader { geometry in
            let width = max(600, geometry.size.width - Self.channelWidth - Layout.edge)
            let perSecond = width / Self.window
            VStack(alignment: .leading, spacing: 0) {
                timeline(width: width)
                if let note {
                    Text(note).font(.system(size: 22)).foregroundStyle(Color.usher).lineLimit(1)
                }
                if guide == nil {
                    StatusView(state: .loading)
                } else {
                    ScrollViewReader { proxy in
                        ScrollView(.vertical) {
                            LazyVStack(alignment: .leading, spacing: 8) {
                                ForEach(channels) { channel in
                                    row(channel, width: width, perSecond: perSecond)
                                        .id(channel.id)
                                }
                            }
                            .padding(.vertical, 12)
                            .padding(.trailing, Layout.edge)
                            .overlay(alignment: .topLeading) { nowLine(perSecond: perSecond) }
                        }
                        .scrollClipDisabled()
                        // All of it, so Right finds the guide from a sidebar row lower down than its channels go.
                        .focusSection()
                        .task(id: guide?.from) { await startFocus(proxy) }
                    }
                }
            }
        }
        .task {
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(30))
                now = Date()
            }
        }
        // Listings came for the channel focus is on (a wider guide, or the window moved), whose cell has gone with them.
        .onChange(of: listedWhileFocused) { _, channel in
            guard let channel else { return }
            Task {
                try? await Task.sleep(for: .milliseconds(60))
                focused = landing(on: channel)
            }
        }
        .alert(details?.displayTitle ?? "", isPresented: Binding(get: { details != nil }, set: { if !$0 { details = nil } })) {
            if let channel = detailsChannel {
                Button(t("Watch {channel} now", ["channel": channel.name])) { onWatch(channel) }
                if records, let details, details.end > now {
                    Button(DvrActions.recordLabel(details)) { act { try await toggleRecording(details, on: channel) } }
                    Button(DvrActions.seriesLabel(details)) { act { try await DvrActions.toggleSeries(details, on: channel.id, client: $0) } }
                }
            }
            Button(t("Close"), role: .cancel) {}
        } message: {
            if let details {
                Text(describe(details))
            }
        }
        // The guide comes without descriptions: the one shown is fetched as its details open.
        .task(id: details?.id) {
            guard let shown = details, shown.description == nil, let client else { return }
            let full = await client.programmeWithDescription(shown)
            if !Task.isCancelled, details?.id == full.id { details = full }
        }
        .alert(t("Recording"), isPresented: Binding(get: { notice != nil }, set: { if !$0 { notice = nil } })) {
            Button(t("OK"), role: .cancel) {}
        } message: {
            Text(notice ?? "")
        }
    }

    // MARK: Parts

    private func timeline(width: CGFloat) -> some View {
        HStack(spacing: 0) {
            Text(Self.day(windowStart))
                .font(.system(size: 22, weight: .semibold))
                .foregroundStyle(Color.usher)
                .frame(width: Self.channelWidth, alignment: .leading)
            ZStack(alignment: .leading) {
                ForEach(0..<6, id: \.self) { slot in
                    Text(Self.clock.string(from: windowStart.addingTimeInterval(Double(slot) * 1800)))
                        .font(.system(size: 22, weight: .semibold).monospacedDigit())
                        .foregroundStyle(Color.usher)
                        .offset(x: CGFloat(slot) * width / 6)
                }
            }
            .frame(width: width, alignment: .leading)
        }
        .frame(height: 44)
    }

    private func row(_ channel: LiveChannel, width: CGFloat, perSecond: CGFloat) -> some View {
        let windowEnd = windowStart.addingTimeInterval(Self.window)
        let programmes = programmes(on: channel.id)
        return HStack(spacing: 0) {
            HStack(spacing: 14) {
                Text(channel.numberText)
                    .font(.system(size: 20, weight: .bold).monospacedDigit())
                    .foregroundStyle(Color.usher)
                    .frame(width: 54, alignment: .trailing)
                RemoteImage(url: client?.imageURL(channel.logoUrl, width: 300), contentMode: .fit) {
                    Text(channel.name).font(.system(size: 18, weight: .semibold)).foregroundStyle(Color.screen).lineLimit(2)
                }
                .frame(width: 130, height: 60)
                .overlay(alignment: .topTrailing) {
                    if channel.isFavourite {
                        Image(systemName: "star.fill")
                            .font(.system(size: 14))
                            .foregroundStyle(Color.screen)
                            .offset(x: 10, y: -6)
                    }
                }
            }
            .frame(width: Self.channelWidth, alignment: .leading)

            ZStack(alignment: .leading) {
                // Nothing listed: still a place to stop on and watch the channel from, so up and down don't skip it. A
                // channel the guide hasn't got yet (it was asked for favourites, say) isn't known to have nothing.
                if programmes.isEmpty, guide?.channels.contains(where: { $0.id == channel.id }) == true {
                    Button {
                        onWatch(channel)
                    } label: {
                        Text(t("No listings"))
                            .font(.system(size: 22))
                            .padding(.leading, 20)
                            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .leading)
                    }
                    .buttonStyle(GuideCellStyle(onNow: false, past: false, recording: false, quiet: true))
                    .focused($focused, equals: .noListings(channel.id))
                    .contextMenu { favouriteButton(channel) }
                    .frame(width: width - 6, height: Self.rowHeight)
                    // As past either edge of a programme, the window moves. The same goes for Left at its start: on to
                    // the sidebar.
                    .onMoveCommand { direction in
                        if direction == .right {
                            move(by: Self.step)
                        }
                        if direction == .left, windowStart > earliest {
                            move(by: -Self.step)
                            keepFocus(on: channel)
                        }
                    }
                }
                ForEach(Array(programmes.enumerated()), id: \.element.id) { index, programme in
                    let start = max(programme.start, windowStart)
                    let end = min(programme.end, windowEnd)
                    let cellWidth = max(40, CGFloat(end.timeIntervalSince(start)) * perSecond - 6)
                    Button {
                        choose(programme, on: channel)
                    } label: {
                        // Too narrow for its title (a five-minute filler): a plain block, named when focused.
                        cell(programme).opacity(cellWidth < 90 ? 0 : 1)
                    }
                    .accessibilityLabel(programme.displayTitle)
                    .buttonStyle(GuideCellStyle(onNow: programme.start <= now && programme.end > now, past: programme.end <= now,
                                                recording: programme.isSetToRecord))
                    .focused($focused, equals: .programme(programme.id))
                    .contextMenu {
                        if records, programme.end > now {
                            Button { act { try await toggleRecording(programme, on: channel) } } label: {
                                Label(DvrActions.recordLabel(programme), systemImage: programme.isSetToRecord ? "record.circle.fill" : "record.circle")
                            }
                            Button { act { try await DvrActions.toggleSeries(programme, on: channel.id, client: $0) } } label: {
                                Label(DvrActions.seriesLabel(programme), systemImage: "square.stack")
                            }
                        }
                        favouriteButton(channel)
                    }
                    .frame(width: cellWidth, height: Self.rowHeight)
                    .offset(x: CGFloat(start.timeIntervalSince(windowStart)) * perSecond)
                    // Past either edge of the window, it moves, and focus stays on this programme while it's in view, or
                    // otherwise on this channel. Left from the first programme at the start of the guide goes on to the
                    // sidebar.
                    .onMoveCommand { direction in
                        if direction == .right, index == programmes.count - 1 {
                            move(by: Self.step)
                            keepFocus(on: programme, of: channel, refocusing: false)
                        }
                        if direction == .left, index == 0, windowStart > earliest {
                            move(by: -Self.step)
                            keepFocus(on: programme, of: channel, refocusing: true)
                        }
                    }
                }
            }
            .frame(width: width, height: Self.rowHeight, alignment: .leading)
        }
        .focusSection()
        // Spanning the window, a channel with nothing listed has no column of its own, and going on from it would land
        // level with its middle, an hour or more ahead, where Select opens details rather than playing.
        .defaultFocus($focused, landing(on: channel.id), priority: isOnNoListings ? .userInitiated : .automatic)
    }

    /// Adds a channel to Favourites or takes it off, where there's a menu for it.
    @ViewBuilder
    private func favouriteButton(_ channel: LiveChannel) -> some View {
        if let onFavourite {
            Button { onFavourite(channel) } label: {
                Label(channel.isFavourite ? t("Remove from favourites") : t("Add to favourites"),
                      systemImage: channel.isFavourite ? "star.slash" : "star")
            }
        }
    }

    private func cell(_ programme: LiveProgramme) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack(spacing: 8) {
                if programme.isSetToRecord {
                    Circle().fill(Self.recordRed).frame(width: 14, height: 14)
                        .accessibilityLabel(programme.isRecordingNow ? t("Recording now") : t("Set to record"))
                } else if programme.series == true {
                    Image(systemName: "square.stack.fill").font(.system(size: 16)).foregroundStyle(Self.recordRed)
                        .accessibilityLabel(t(Words.series))
                }
                Text(programme.displayTitle)
                    .font(.system(size: 23, weight: .semibold))
                    .lineLimit(1)
            }
            Text("\(Self.clock.string(from: programme.start)) – \(Self.clock.string(from: programme.end))")
                .font(.system(size: 18).monospacedDigit())
                .opacity(0.7)
                .lineLimit(1)
        }
        .padding(.horizontal, 14)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .leading)
    }

    @ViewBuilder
    private func nowLine(perSecond: CGFloat) -> some View {
        let offset = CGFloat(now.timeIntervalSince(windowStart)) * perSecond
        if offset >= 0, now < windowStart.addingTimeInterval(Self.window) {
            Rectangle()
                .fill(Color.record)
                .frame(width: 3)
                .frame(maxHeight: .infinity)
                .offset(x: Self.channelWidth + offset)
                .allowsHitTesting(false)
        }
    }

    // MARK: Behaviour

    private static let recordRed = Color.record

    /// What's listed on a channel in the window.
    private func programmes(on channel: String) -> [LiveProgramme] {
        let windowEnd = windowStart.addingTimeInterval(Self.window)
        return (guide?.channels.first { $0.id == channel }?.programmes ?? [])
            .filter { $0.end > windowStart && $0.start < windowEnd && min($0.end, windowEnd).timeIntervalSince(max($0.start, windowStart)) >= 5 * 60 }
    }

    private var isOnNoListings: Bool {
        if case .noListings = focused { true } else { false }
    }

    /// The channel with nothing listed that focus is on, once it has listings after all.
    private var listedWhileFocused: String? {
        guard case .noListings(let channel) = focused, !programmes(on: channel).isEmpty else { return nil }
        return channel
    }

    /// Where focus goes on a channel when it isn't coming from a programme: what's on now, or the first thing shown.
    private func landing(on channel: String) -> GridFocus {
        let shown = programmes(on: channel)
        return (shown.first { $0.start <= now && $0.end > now } ?? shown.first).map { .programme($0.id) } ?? .noListings(channel)
    }

    /// Moving back from a channel with nothing listed: the remote's press also takes focus left, so it's brought back.
    private func keepFocus(on channel: LiveChannel) {
        Task {
            try? await Task.sleep(for: .milliseconds(60))
            focused = landing(on: channel.id)
        }
    }

    /// Once the window has moved. Moving back, the remote's press also takes focus left, so it's always brought back
    /// (`refocusing`). Otherwise focus only needs help when the programme has gone from the window with the move.
    private func keepFocus(on programme: LiveProgramme, of channel: LiveChannel, refocusing: Bool) {
        let shown = programmes(on: channel.id)
        let inView = shown.contains { $0.id == programme.id }
        guard refocusing || !inView else { return }
        Task {
            try? await Task.sleep(for: .milliseconds(60))
            focused = inView ? .programme(programme.id) : shown.first.map { .programme($0.id) } ?? .noListings(channel.id)
        }
    }

    private func toggleRecording(_ programme: LiveProgramme, on channel: LiveChannel) async throws {
        guard let client else { return }
        notice = try await DvrActions.toggleRecording(programme, on: channel.id, client: client)
    }

    /// Does a recording action, then has the guide fetched again; says why if it couldn't be done.
    private func act(_ action: @escaping (FlexarrClient) async throws -> Void) {
        guard let client else { return }
        Task {
            do {
                try await action(client)
            } catch {
                notice = error.localizedDescription
            }
            onRecordingsChanged?()
        }
    }

    private func act(_ action: @escaping () async throws -> Void) {
        act { _ in try await action() }
    }

    private func choose(_ programme: LiveProgramme, on channel: LiveChannel) {
        if programme.start <= now, programme.end > now {
            onWatch(channel)
        } else {
            detailsChannel = channel
            details = programme
        }
    }

    /// Opens on the channel being watched, at what's on now.
    private func startFocus(_ proxy: ScrollViewProxy) async {
        guard let focusChannel, guide != nil else { return }
        proxy.scrollTo(focusChannel, anchor: .center)
        // Once the row is laid out, or focus has nowhere to land.
        try? await Task.sleep(for: .milliseconds(300))
        focused = landing(on: focusChannel)
    }

    /// The window goes back no further than the guide does.
    private var earliest: Date {
        guard let guide else { return windowStart }
        return Self.halfHour(before: max(guide.from, Date().addingTimeInterval(-3 * 3600)))
    }

    /// Moves the window, no earlier than the guide starts and no later than it ends.
    private func move(by seconds: TimeInterval) {
        guard let guide else { return }
        let earliest = earliest
        let latest = guide.until.addingTimeInterval(-Self.window)
        let next = windowStart.addingTimeInterval(seconds)
        withAnimation(.easeInOut(duration: 0.25)) {
            windowStart = min(max(next, earliest), max(earliest, latest))
        }
        onWindow?(windowStart.addingTimeInterval(Self.window))
    }

    private func describe(_ programme: LiveProgramme) -> String {
        var lines = ["\(Self.day(programme.start)) \(Self.clock.string(from: programme.start)) – \(Self.clock.string(from: programme.end))"]
        if let season = programme.season {
            lines.append(programme.episode.map { t("Series {season}, episode {episode}", ["season": season, "episode": $0]) }
                         ?? t("Series {season}", ["season": season]))
        }
        if programme.isNew { lines.append(t("New")) }
        if let description = programme.description { lines.append(description) }
        return lines.joined(separator: "\n")
    }

    static func halfHour(before date: Date) -> Date {
        let seconds = date.timeIntervalSinceReferenceDate
        return Date(timeIntervalSinceReferenceDate: (seconds / 1800).rounded(.down) * 1800)
    }

    private static func day(_ date: Date) -> String {
        let calendar = Calendar.current
        if calendar.isDateInToday(date) { return t("Today") }
        if calendar.isDateInTomorrow(date) { return t("Tomorrow") }
        return date.formatted(.dateTime.weekday(.wide).locale(Localization.locale))
    }
}

/// A programme: quiet until focused (white, dark text, as tvOS does); what's on now carries a bar, what's past is dim.
/// `quiet` is a channel with nothing listed: fainter, and as wide as the guide, so it hardly grows.
private struct GuideCellStyle: ButtonStyle {
    let onNow: Bool
    let past: Bool
    /// Set to record: outlined in red.
    let recording: Bool
    var quiet = false

    func makeBody(configuration: Configuration) -> some View {
        CellBody(configuration: configuration, onNow: onNow, past: past, recording: recording, quiet: quiet)
    }

    private struct CellBody: View {
        let configuration: Configuration
        let onNow: Bool
        let past: Bool
        let recording: Bool
        let quiet: Bool
        @Environment(\.isFocused) private var isFocused

        var body: some View {
            configuration.label
                .foregroundStyle(isFocused ? Color.room : quiet ? Color.usher : Color.screen)
                .background {
                    RoundedRectangle(cornerRadius: Radius.control, style: .continuous)
                        .fill(isFocused ? Color.screen : Color.seat.opacity(quiet ? 0.3 : onNow ? 0.95 : 0.6))
                }
                .overlay(alignment: .leading) {
                    if onNow, !isFocused {
                        RoundedRectangle(cornerRadius: 2).fill(Color.silver).frame(width: 4).padding(.vertical, 12)
                    }
                }
                .overlay {
                    if recording {
                        RoundedRectangle(cornerRadius: Radius.control, style: .continuous)
                            .strokeBorder(Color.record, lineWidth: 3)
                    }
                }
                .opacity(past && !isFocused ? 0.55 : 1)
                .scaleEffect(isFocused ? (quiet ? 1.01 : 1.04) : 1)
                .animation(.spring(response: 0.25, dampingFraction: 0.8), value: isFocused)
        }
    }
}
