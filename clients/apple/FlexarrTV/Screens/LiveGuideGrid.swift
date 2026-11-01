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

    @FocusState private var focusedProgramme: Int64?

    @State private var windowStart = LiveGuideGrid.halfHour(before: Date())
    @State private var details: LiveProgramme?
    @State private var detailsChannel: LiveChannel?
    @State private var now = Date()
    /// Something to tell the viewer about a recording (it clashes, or couldn't be set).
    @State private var notice: String?

    static let window: TimeInterval = 3 * 3600
    static let step: TimeInterval = 90 * 60
    private static let channelWidth: CGFloat = 230
    private static let rowHeight: CGFloat = 96
    private static let clock: DateFormatter = {
        let formatter = DateFormatter()
        formatter.timeStyle = .short
        return formatter
    }()

    var body: some View {
        GeometryReader { geometry in
            let width = max(600, geometry.size.width - Self.channelWidth - Layout.edge)
            let perSecond = width / Self.window
            VStack(alignment: .leading, spacing: 0) {
                timeline(width: width)
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
        .alert(details?.displayTitle ?? "", isPresented: Binding(get: { details != nil }, set: { if !$0 { details = nil } })) {
            if let channel = detailsChannel {
                Button("Watch \(channel.name) now") { onWatch(channel) }
                if records, let details, details.end > now {
                    Button(DvrActions.recordLabel(details)) { act { try await toggleRecording(details, on: channel) } }
                    Button(DvrActions.seriesLabel(details)) { act { try await DvrActions.toggleSeries(details, on: channel.id, client: $0) } }
                }
            }
            Button("Close", role: .cancel) {}
        } message: {
            if let details {
                Text(describe(details))
            }
        }
        .alert("Recording", isPresented: Binding(get: { notice != nil }, set: { if !$0 { notice = nil } })) {
            Button("OK", role: .cancel) {}
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
        let programmes = (guide?.channels.first { $0.id == channel.id }?.programmes ?? [])
            .filter { $0.end > windowStart && $0.start < windowEnd && min($0.end, windowEnd).timeIntervalSince(max($0.start, windowStart)) >= 5 * 60 }
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
                if programmes.isEmpty {
                    Text("No listings")
                        .font(.system(size: 22))
                        .foregroundStyle(Color.usher)
                        .padding(.leading, 20)
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
                    .focused($focusedProgramme, equals: programme.id)
                    .contextMenu {
                        if records, programme.end > now {
                            Button { act { try await toggleRecording(programme, on: channel) } } label: {
                                Label(DvrActions.recordLabel(programme), systemImage: programme.isSetToRecord ? "record.circle.fill" : "record.circle")
                            }
                            Button { act { try await DvrActions.toggleSeries(programme, on: channel.id, client: $0) } } label: {
                                Label(DvrActions.seriesLabel(programme), systemImage: "square.stack")
                            }
                        }
                        if let onFavourite {
                            Button { onFavourite(channel) } label: {
                                Label(channel.isFavourite ? "Remove from Favourites" : "Add to Favourites",
                                      systemImage: channel.isFavourite ? "star.slash" : "star")
                            }
                        }
                    }
                    .frame(width: cellWidth, height: Self.rowHeight)
                    .offset(x: CGFloat(start.timeIntervalSince(windowStart)) * perSecond)
                    // Past either edge of the window, it moves, and focus stays on this programme (still in view). Left
                    // from the first programme at the start of the guide goes on to the sidebar.
                    .onMoveCommand { direction in
                        if direction == .right, index == programmes.count - 1 { move(by: Self.step) }
                        if direction == .left, index == 0, windowStart > earliest {
                            move(by: -Self.step)
                            Task {
                                try? await Task.sleep(for: .milliseconds(60))
                                focusedProgramme = programme.id
                            }
                        }
                    }
                }
            }
            .frame(width: width, height: Self.rowHeight, alignment: .leading)
        }
        .focusSection()
    }

    private func cell(_ programme: LiveProgramme) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack(spacing: 8) {
                if programme.isSetToRecord {
                    Circle().fill(Self.recordRed).frame(width: 14, height: 14)
                        .accessibilityLabel(programme.isRecordingNow ? "Recording now" : "Set to record")
                } else if programme.series == true {
                    Image(systemName: "square.stack.fill").font(.system(size: 16)).foregroundStyle(Self.recordRed)
                        .accessibilityLabel("Series")
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
        guard let focusChannel, let guide else { return }
        proxy.scrollTo(focusChannel, anchor: .center)
        let on = guide.channels.first { $0.id == focusChannel }?.programmes.first { $0.start <= Date() && $0.end > Date() }
        // Once the row is laid out, or focus has nowhere to land.
        try? await Task.sleep(for: .milliseconds(300))
        focusedProgramme = on?.id
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
    }

    private func describe(_ programme: LiveProgramme) -> String {
        var lines = ["\(Self.day(programme.start)) \(Self.clock.string(from: programme.start)) – \(Self.clock.string(from: programme.end))"]
        if let season = programme.season { lines.append(programme.episode.map { "Series \(season), episode \($0)" } ?? "Series \(season)") }
        if programme.isNew { lines.append("New") }
        if let description = programme.description { lines.append(description) }
        return lines.joined(separator: "\n")
    }

    static func halfHour(before date: Date) -> Date {
        let seconds = date.timeIntervalSinceReferenceDate
        return Date(timeIntervalSinceReferenceDate: (seconds / 1800).rounded(.down) * 1800)
    }

    private static func day(_ date: Date) -> String {
        let calendar = Calendar.current
        if calendar.isDateInToday(date) { return "Today" }
        if calendar.isDateInTomorrow(date) { return "Tomorrow" }
        return date.formatted(.dateTime.weekday(.wide))
    }
}

/// A programme: quiet until focused (white, dark text, as tvOS does); what's on now carries a bar, what's past is dim.
private struct GuideCellStyle: ButtonStyle {
    let onNow: Bool
    let past: Bool
    /// Set to record: outlined in red.
    let recording: Bool

    func makeBody(configuration: Configuration) -> some View {
        CellBody(configuration: configuration, onNow: onNow, past: past, recording: recording)
    }

    private struct CellBody: View {
        let configuration: Configuration
        let onNow: Bool
        let past: Bool
        let recording: Bool
        @Environment(\.isFocused) private var isFocused

        var body: some View {
            configuration.label
                .foregroundStyle(isFocused ? Color.room : Color.screen)
                .background {
                    RoundedRectangle(cornerRadius: Radius.control, style: .continuous)
                        .fill(isFocused ? Color.screen : Color.seat.opacity(onNow ? 0.95 : 0.6))
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
                .scaleEffect(isFocused ? 1.04 : 1)
                .animation(.spring(response: 0.25, dampingFraction: 0.8), value: isFocused)
        }
    }
}
