import FlexarrKit
import SwiftUI

/// The guide on iPhone and iPad: the channels down the left, six hours across from the half hour before now, scrolled
/// with a finger. Tapping what's on now plays it; anything else shows what it is, with a way to watch that channel
/// now; holding a programme adds its channel to Favourites or takes it off.
struct LiveGuideView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.scenePhase) private var scenePhase
    @Environment(\.horizontalSizeClass) private var sizeClass
    let channels: [LiveChannel]
    /// The channels are the favourites: the guide is asked for only theirs.
    var favouritesOnly = false
    let order: [LiveChannel]
    /// Changes when a refresh of the guide has finished: fetched again, whatever is kept.
    var refreshed = 0
    let onFavourite: (LiveChannel) -> Void

    /// The guide as fetched: kept a while (`LiveGuide.keptFor`) rather than downloaded again at every look, and asked
    /// for (`LiveGuidePlan`) for the window shown and an hour more, and for only the favourites while they're shown.
    @State private var kept: KeptLiveGuide?
    /// The guide being asked for, so the same isn't asked twice at once.
    @State private var asking: LiveGuideRequest?
    @State private var windowStart = LiveGuideView.halfHour(before: Date())
    @State private var details: (programme: LiveProgramme, channel: LiveChannel)?
    @State private var now = Date()
    @State private var notice: String?
    /// The channel rows built: those near the screen (`GuideRowWindow`), kept up to date as the guide scrolls.
    @State private var shownRows = 0..<40

    private static let hours: Double = 6
    /// Kept while the language shown stays the same: every programme in the grid reads it three times.
    private static var clockFormatter: DateFormatter?
    private static var clock: DateFormatter {
        let locale = Localization.locale
        if let kept = clockFormatter, kept.locale == locale { return kept }
        let formatter = DateFormatter()
        formatter.locale = locale
        formatter.timeStyle = .short
        clockFormatter = formatter
        return formatter
    }

    private var perMinute: CGFloat { sizeClass == .regular ? 6 : 4 }
    private var channelWidth: CGFloat { sizeClass == .regular ? 120 : 84 }
    private var rowHeight: CGFloat { sizeClass == .regular ? 64 : 56 }
    private var width: CGFloat { CGFloat(Self.hours * 60) * perMinute }
    private var windowEnd: Date { windowStart.addingTimeInterval(Self.hours * 3600) }
    /// The times along the top and every channel's row below, as tall as if all the rows were built.
    private var columnHeight: CGFloat {
        channels.isEmpty ? 30 : 30 + CGFloat(channels.count) * (rowHeight + 4)
    }

    var body: some View {
        Group {
            if let guide = kept?.guide {
                // Each channel's programmes, found once rather than searched for in every row.
                let listings = Dictionary(guide.channels.map { ($0.id, $0.programmes) }, uniquingKeysWith: { first, _ in first })
                // Only the rows near the screen are built (`GuideRowWindow`), the same in both columns, each column
                // its full height so the rows line up and the scroll bar is right: a lazy stack in the programmes'
                // sideways scroll view would still build every row.
                let built = shownRows.clamped(to: 0..<channels.count)
                let skipped = CGFloat(built.lowerBound) * (rowHeight + 4)
                ScrollView(.vertical) {
                    HStack(alignment: .top, spacing: 0) {
                        // The channels stay put while the programmes scroll across.
                        VStack(spacing: 4) {
                            Color.clear.frame(height: 30)
                            VStack(spacing: 4) {
                                ForEach(channels[built]) { channelCell($0) }
                            }
                            .padding(.top, skipped)
                        }
                        .frame(width: channelWidth, height: columnHeight, alignment: .top)
                        ScrollView(.horizontal, showsIndicators: false) {
                            VStack(alignment: .leading, spacing: 4) {
                                timeline
                                VStack(alignment: .leading, spacing: 4) {
                                    ForEach(channels[built]) { row($0, listings[$0.id] ?? []) }
                                }
                                .padding(.top, skipped)
                            }
                            .frame(width: width, height: columnHeight, alignment: .topLeading)
                            .overlay(alignment: .topLeading) { nowLine }
                            .padding(.trailing, Layout.edge)
                        }
                    }
                    .padding(.vertical, 8)
                }
                .onScrollGeometryChange(for: Range<Int>.self) { geometry in
                    GuideRowWindow.rows(visibleTop: geometry.visibleRect.minY, visibleHeight: geometry.visibleRect.height,
                                        firstRowAt: 8 + 30 + 4, pitch: rowHeight + 4)
                } action: { _, rows in
                    shownRows = rows
                }
            } else {
                StatusView(state: .loading)
            }
        }
        // Only while the app is in front: with music keeping it alive in the background, it would go on asking.
        .task(id: scenePhase == .active) {
            guard scenePhase == .active else { return }
            // The line for now moves on every couple of minutes; the listings are fetched again only once they're old,
            // a recording has started or ended since (its mark changes), or they weren't had: a fetch that failed is
            // tried again at the next look.
            while !Task.isCancelled {
                await load()
                now = Date()
                try? await Task.sleep(for: .seconds(120))
            }
        }
        // Favourites has only its own channels' guide: another list may want more.
        .onChange(of: favouritesOnly) { Task { await load() } }
        .onChange(of: refreshed) { Task { await load(force: true) } }
        // A favourite starred on another device, or another list: its channels may not be in the guide kept.
        .onChange(of: channels.map(\.id)) { Task { await load() } }
        .alert(details?.programme.displayTitle ?? "", isPresented: Binding(get: { details != nil }, set: { if !$0 { details = nil } })) {
            if let channel = details?.channel, let programme = details?.programme {
                Button(t("Watch {channel} now", ["channel": channel.name])) { model.watch(channel, among: order) }
                if !model.hasAgeLimit, programme.end > now {
                    Button(DvrActions.recordLabel(programme)) { act { notice = try await DvrActions.toggleRecording(programme, on: channel.id, client: $0) } }
                    Button(DvrActions.seriesLabel(programme)) { act { try await DvrActions.toggleSeries(programme, on: channel.id, client: $0) } }
                }
            }
            Button(t("Close"), role: .cancel) {}
        } message: {
            if let programme = details?.programme { Text(describe(programme)) }
        }
        // The guide comes without descriptions: the one shown is fetched as its details open.
        .task(id: details?.programme.id) {
            guard let shown = details, shown.programme.description == nil, let client = model.client else { return }
            let full = await client.programmeWithDescription(shown.programme)
            if !Task.isCancelled, details?.programme.id == full.id { details = (full, shown.channel) }
        }
        .banner($notice, systemImage: "record.circle")
    }

    private var timeline: some View {
        ZStack(alignment: .leading) {
            ForEach(0..<Int(Self.hours * 2), id: \.self) { slot in
                Text(Self.clock.string(from: windowStart.addingTimeInterval(Double(slot) * 1800)))
                    .font(.caption.weight(.semibold).monospacedDigit())
                    .foregroundStyle(Color.usher)
                    .offset(x: CGFloat(slot * 30) * perMinute + 6)
            }
        }
        .frame(width: width, height: 30, alignment: .leading)
    }

    private func channelCell(_ channel: LiveChannel) -> some View {
        ZStack {
            RoundedRectangle(cornerRadius: Radius.art, style: .continuous).fill(Color.seat)
            RemoteImage(url: model.client?.imageURL(channel.logoUrl, width: 300), contentMode: .fit) {
                Text(channel.name.prefix(4)).font(.marquee(14, weight: .bold, scales: false)).foregroundStyle(Color.usher)
            }
            .padding(8)
            StartingMark(channelId: channel.id)
        }
        .overlay(alignment: .topTrailing) {
            if channel.isFavourite {
                Image(systemName: "star.fill").font(.system(size: 9)).foregroundStyle(LiveTvView.gold).padding(4)
            }
        }
        .padding(.horizontal, 6)
        .frame(height: rowHeight)
        .contentShape(Rectangle())
        .onTapGesture { model.watch(channel, among: order) }
        .accessibilityLabel("\(channel.numberText) \(channel.name)")
        .accessibilityAddTraits(.isButton)
    }

    private func row(_ channel: LiveChannel, _ listed: [LiveProgramme]) -> some View {
        let programmes = listed.filter { $0.end > windowStart && $0.start < windowEnd }
        return ZStack(alignment: .leading) {
            if programmes.isEmpty {
                Text(t("No listings")).font(.footnote).foregroundStyle(Color.usher).padding(.leading, 12)
            }
            ForEach(programmes) { programme in
                let start = max(programme.start, windowStart)
                let end = min(programme.end, windowEnd)
                let cellWidth = max(8, CGFloat(end.timeIntervalSince(start) / 60) * perMinute - 3)
                let onNow = programme.start <= now && programme.end > now
                cell(programme, onNow: onNow, past: programme.end <= now, narrow: cellWidth < 56)
                    .frame(width: cellWidth, height: rowHeight)
                    .contentShape(Rectangle())
                    .onTapGesture {
                        if onNow { model.watch(channel, among: order) } else { details = (programme, channel) }
                    }
                    .contextMenu {
                        if onNow {
                            Button { model.watch(channel, among: order) } label: { Label(t("Watch"), systemImage: "play.fill") }
                        }
                        if !model.hasAgeLimit, programme.end > now {
                            Button { act { notice = try await DvrActions.toggleRecording(programme, on: channel.id, client: $0) } } label: {
                                Label(DvrActions.recordLabel(programme), systemImage: programme.isSetToRecord ? "record.circle.fill" : "record.circle")
                            }
                            Button { act { try await DvrActions.toggleSeries(programme, on: channel.id, client: $0) } } label: {
                                Label(DvrActions.seriesLabel(programme), systemImage: "square.stack")
                            }
                        }
                        Button { onFavourite(channel) } label: {
                            Label(channel.isFavourite ? t("Remove from favourites") : t("Add to favourites"),
                                  systemImage: channel.isFavourite ? "star.slash" : "star")
                        }
                    }
                    .accessibilityLabel(programme.isSetToRecord
                        ? t("{channel}, {programme}, {time}, set to record", ["channel": channel.name, "programme": programme.displayTitle, "time": Self.clock.string(from: programme.start)])
                        : "\(channel.name), \(programme.displayTitle), \(Self.clock.string(from: programme.start))")
                    .accessibilityAddTraits(.isButton)
                    // Moved into its slot last, so where it's tapped moves with it (an offset before the tap area left
                    // every programme's tap at the start of the row, where the last one won).
                    .offset(x: CGFloat(start.timeIntervalSince(windowStart) / 60) * perMinute)
            }
        }
        .frame(width: width, height: rowHeight, alignment: .leading)
    }

    private func cell(_ programme: LiveProgramme, onNow: Bool, past: Bool, narrow: Bool) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            if !narrow {
                HStack(spacing: 4) {
                    if programme.isSetToRecord {
                        RecordMark(recordingNow: programme.isRecordingNow)
                    } else if programme.series == true {
                        Image(systemName: "square.stack.fill").font(.system(size: 9)).foregroundStyle(Self.recordRed)
                    }
                    Text(programme.displayTitle)
                        .font(.footnote.weight(.semibold))
                        .foregroundStyle(Color.screen)
                        .lineLimit(1)
                }
                Text("\(Self.clock.string(from: programme.start)) – \(Self.clock.string(from: programme.end))")
                    .font(.caption2.monospacedDigit())
                    .foregroundStyle(Color.usher)
                    .lineLimit(1)
            }
        }
        .padding(.horizontal, 8)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .leading)
        .background {
            RoundedRectangle(cornerRadius: Radius.control, style: .continuous).fill(Color.seat.opacity(onNow ? 1 : 0.55))
        }
        // Set to record: outlined in red, so it stands out across the grid.
        .overlay {
            if programme.isSetToRecord {
                RoundedRectangle(cornerRadius: Radius.control, style: .continuous).strokeBorder(Self.recordRed, lineWidth: 2)
            }
        }
        .overlay(alignment: .leading) {
            if onNow {
                RoundedRectangle(cornerRadius: 2).fill(Color.silver).frame(width: 3).padding(.vertical, 10)
            }
        }
        .opacity(past ? 0.55 : 1)
    }

    @ViewBuilder
    private var nowLine: some View {
        let offset = CGFloat(now.timeIntervalSince(windowStart) / 60) * perMinute
        if offset >= 0, now < windowEnd {
            Rectangle()
                .fill(Color.record)
                .frame(width: 2)
                .frame(maxHeight: .infinity)
                .offset(x: offset)
                .allowsHitTesting(false)
        }
    }

    private static let recordRed = Color.record

    /// The guide when `LiveGuidePlan` says the one kept won't do, or always after a recording was set or taken off
    /// (`force`, for its marks).
    private func load(force: Bool = false) async {
        guard let client = model.client,
              let request = LiveGuidePlan.next(kept: kept, scope: favouritesOnly ? .favourites : .all, shown: channels.map(\.id),
                                               showingUntil: windowEnd, keepAhead: 0, askAhead: 3600, force: force),
              request != asking else { return }
        asking = request
        defer { if asking == request { asking = nil } }
        if let loaded = try? await client.liveGuide(request, kept: kept) { kept = loaded }
    }

    /// Does a recording action, then fetches the guide again for its marks; says why if it couldn't be done.
    private func act(_ action: @escaping (FlexarrClient) async throws -> Void) {
        guard let client = model.client else { return }
        Task {
            do {
                try await action(client)
            } catch {
                notice = error.localizedDescription
            }
            await load(force: true)
        }
    }

    private func describe(_ programme: LiveProgramme) -> String {
        var lines = ["\(Self.clock.string(from: programme.start)) – \(Self.clock.string(from: programme.end))"]
        if let season = programme.season {
            lines.append(programme.episode.map { t("Series {season}, episode {episode}", ["season": season, "episode": $0]) } ?? t("Series {season}", ["season": season]))
        }
        if programme.isNew { lines.append(t("New")) }
        if let description = programme.description { lines.append(description) }
        return lines.joined(separator: "\n")
    }

    static func halfHour(before date: Date) -> Date {
        let seconds = date.timeIntervalSinceReferenceDate
        return Date(timeIntervalSinceReferenceDate: (seconds / 1800).rounded(.down) * 1800)
    }
}

/// The spinner over a channel's logo while it starts. Its own view, so a tap on a logo redraws that and not every
/// programme in the grid.
private struct StartingMark: View {
    @Environment(AppModel.self) private var model
    let channelId: String

    var body: some View {
        if model.liveTv.startingChannel == channelId {
            ZStack {
                RoundedRectangle(cornerRadius: Radius.art, style: .continuous).fill(.black.opacity(0.45))
                ProgressView()
            }
        }
    }
}
