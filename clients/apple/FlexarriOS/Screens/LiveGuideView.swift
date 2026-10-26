import FlexarrKit
import SwiftUI

/// The guide on iPhone and iPad: the channels down the left, six hours across from the half hour before now, scrolled
/// with a finger. Tapping what's on now plays it; anything else shows what it is, with a way to watch that channel
/// now; holding a programme adds its channel to Favourites or takes it off.
struct LiveGuideView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.horizontalSizeClass) private var sizeClass
    let channels: [LiveChannel]
    let order: [LiveChannel]
    let onFavourite: (LiveChannel) -> Void

    @State private var guide: LiveGuide?
    @State private var windowStart = LiveGuideView.halfHour(before: Date())
    @State private var details: (programme: LiveProgramme, channel: LiveChannel)?
    @State private var now = Date()

    private static let hours: Double = 6
    private static let clock: DateFormatter = {
        let formatter = DateFormatter()
        formatter.timeStyle = .short
        return formatter
    }()

    private var perMinute: CGFloat { sizeClass == .regular ? 6 : 4 }
    private var channelWidth: CGFloat { sizeClass == .regular ? 120 : 84 }
    private var rowHeight: CGFloat { sizeClass == .regular ? 64 : 56 }
    private var width: CGFloat { CGFloat(Self.hours * 60) * perMinute }
    private var windowEnd: Date { windowStart.addingTimeInterval(Self.hours * 3600) }

    var body: some View {
        Group {
            if guide == nil {
                StatusView(state: .loading)
            } else {
                ScrollView(.vertical) {
                    HStack(alignment: .top, spacing: 0) {
                        // The channels stay put while the programmes scroll across.
                        VStack(spacing: 4) {
                            Color.clear.frame(height: 30)
                            ForEach(channels) { channelCell($0) }
                        }
                        .frame(width: channelWidth)
                        ScrollView(.horizontal, showsIndicators: false) {
                            VStack(alignment: .leading, spacing: 4) {
                                timeline
                                ForEach(channels) { row($0) }
                            }
                            .frame(width: width, alignment: .leading)
                            .overlay(alignment: .topLeading) { nowLine }
                            .padding(.trailing, Layout.edge)
                        }
                    }
                    .padding(.vertical, 8)
                }
            }
        }
        .task {
            while !Task.isCancelled {
                if let client = model.client,
                   let loaded = try? await client.liveGuide(from: Date().addingTimeInterval(-3600), hours: Int(Self.hours) + 2) {
                    guide = loaded
                }
                now = Date()
                try? await Task.sleep(for: .seconds(120))
            }
        }
        .alert(details?.programme.displayTitle ?? "", isPresented: Binding(get: { details != nil }, set: { if !$0 { details = nil } })) {
            if let channel = details?.channel {
                Button("Watch \(channel.name) now") { model.watch(channel, among: order) }
            }
            Button("Close", role: .cancel) {}
        } message: {
            if let programme = details?.programme { Text(describe(programme)) }
        }
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
            RoundedRectangle(cornerRadius: 8, style: .continuous).fill(Color.seat)
            RemoteImage(url: model.client?.imageURL(channel.logoUrl, width: 300), contentMode: .fit) {
                Text(channel.name.prefix(4)).font(.marquee(14, weight: .bold)).foregroundStyle(Color.usher)
            }
            .padding(8)
            if model.liveTv.startingChannel == channel.id {
                RoundedRectangle(cornerRadius: 8, style: .continuous).fill(.black.opacity(0.45))
                ProgressView()
            }
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

    private func row(_ channel: LiveChannel) -> some View {
        let programmes = (guide?.channels.first { $0.id == channel.id }?.programmes ?? [])
            .filter { $0.end > windowStart && $0.start < windowEnd }
        return ZStack(alignment: .leading) {
            if programmes.isEmpty {
                Text("No listings").font(.footnote).foregroundStyle(Color.usher).padding(.leading, 12)
            }
            ForEach(programmes) { programme in
                let start = max(programme.start, windowStart)
                let end = min(programme.end, windowEnd)
                let cellWidth = max(8, CGFloat(end.timeIntervalSince(start) / 60) * perMinute - 3)
                let onNow = programme.start <= now && programme.end > now
                cell(programme, onNow: onNow, past: programme.end <= now, narrow: cellWidth < 56)
                    .frame(width: cellWidth, height: rowHeight)
                    .offset(x: CGFloat(start.timeIntervalSince(windowStart) / 60) * perMinute)
                    .contentShape(Rectangle())
                    .onTapGesture {
                        if onNow { model.watch(channel, among: order) } else { details = (programme, channel) }
                    }
                    .contextMenu {
                        if onNow {
                            Button { model.watch(channel, among: order) } label: { Label("Watch", systemImage: "play.fill") }
                        }
                        Button { onFavourite(channel) } label: {
                            Label(channel.isFavourite ? "Remove from Favourites" : "Add to Favourites",
                                  systemImage: channel.isFavourite ? "star.slash" : "star")
                        }
                    }
                    .accessibilityLabel("\(channel.name), \(programme.displayTitle), \(Self.clock.string(from: programme.start))")
                    .accessibilityAddTraits(.isButton)
            }
        }
        .frame(width: width, height: rowHeight, alignment: .leading)
    }

    private func cell(_ programme: LiveProgramme, onNow: Bool, past: Bool, narrow: Bool) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            if !narrow {
                Text(programme.displayTitle)
                    .font(.footnote.weight(.semibold))
                    .foregroundStyle(Color.screen)
                    .lineLimit(1)
                Text("\(Self.clock.string(from: programme.start)) – \(Self.clock.string(from: programme.end))")
                    .font(.caption2.monospacedDigit())
                    .foregroundStyle(Color.usher)
                    .lineLimit(1)
            }
        }
        .padding(.horizontal, 8)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .leading)
        .background {
            RoundedRectangle(cornerRadius: 8, style: .continuous).fill(Color.seat.opacity(onNow ? 1 : 0.55))
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
                .fill(Color(red: 0.94, green: 0.54, blue: 0.49))
                .frame(width: 2)
                .frame(maxHeight: .infinity)
                .offset(x: offset)
                .allowsHitTesting(false)
        }
    }

    private func describe(_ programme: LiveProgramme) -> String {
        var lines = ["\(Self.clock.string(from: programme.start)) – \(Self.clock.string(from: programme.end))"]
        if let season = programme.season { lines.append(programme.episode.map { "Series \(season), episode \($0)" } ?? "Series \(season)") }
        if programme.isNew { lines.append("New") }
        if let description = programme.description { lines.append(description) }
        return lines.joined(separator: "\n")
    }

    static func halfHour(before date: Date) -> Date {
        let seconds = date.timeIntervalSinceReferenceDate
        return Date(timeIntervalSinceReferenceDate: (seconds / 1800).rounded(.down) * 1800)
    }
}
