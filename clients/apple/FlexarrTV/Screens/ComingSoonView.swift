import FlexarrKit
import SwiftUI

/// Coming soon: what Sonarr and Radarr expect over the next few weeks, a row a day, as on the web.
struct ComingSoonView: View {
    @Environment(AppModel.self) private var model
    @State private var days: [UpcomingDay.Day]?
    @State private var error: String?

    var body: some View {
        ScrollView(.vertical) {
            VStack(alignment: .leading, spacing: 30) {
                Text("Coming soon")
                    .font(.marquee(64, weight: .heavy))
                    .foregroundStyle(Color.screen)
                    .padding(.horizontal, Layout.edge)
                if let days, days.isEmpty {
                    StatusView(state: .empty("Nothing is scheduled. Monitored shows and movies appear here as their dates get close."))
                        .frame(height: 500)
                } else if let days {
                    ForEach(days) { day in
                        VStack(alignment: .leading, spacing: 0) {
                            RowHeading(title: UpcomingDay.heading(day.date))
                            ScrollView(.horizontal) {
                                LazyHStack(alignment: .top, spacing: Layout.rowSpacing) {
                                    ForEach(day.entries) { entry in
                                        UpcomingCard(item: entry.first, detail: entry.detail, status: entry.status)
                                    }
                                }
                                .padding(.horizontal, Layout.edge)
                                .padding(.top, 28)
                                .padding(.bottom, 40)
                            }
                            .scrollClipDisabled()
                            .focusSection()
                        }
                    }
                } else if let error {
                    StatusView(state: .failed(error) { Task { await load() } }).frame(height: 500)
                } else {
                    StatusView(state: .loading).frame(height: 500)
                }
            }
            .padding(.bottom, 80)
        }
        .scrollClipDisabled()
        .background(RoomBackground(ambient: nil))
        .task { if days == nil { await load() } }
    }

    private func load() async {
        guard let client = model.client else { return }
        do {
            let items = try await client.upcoming(days: 30)
            let yesterday = Calendar.current.date(byAdding: .day, value: -1, to: Calendar.current.startOfDay(for: .now))!
            days = UpcomingDay.days(items.filter { $0.date >= yesterday })
            error = nil
        } catch {
            if days == nil { self.error = ConnectionError(error).message }
        }
    }
}

/// Something coming, as a 16:9 card with its day on it. It takes focus only when pressing it does something: one in
/// the library opens, one with an overview shows it; pressing and holding offers the same. Anything else is just shown.
struct UpcomingCard: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    let item: UpcomingItem
    var detail: String?
    var status: String?
    @State private var showingAbout = false

    var body: some View {
        if UpcomingDay.hasAction(item) {
            ArtworkButton(ambient: .silver, accessibilityText: "\(UpcomingDay.title(item)), \(UpcomingDay.label(item.date))") {
                open()
            } artwork: {
                artwork
            } caption: { focused in
                caption(focused: focused)
            }
            .contextMenu {
                if let id = item.libraryItemId {
                    Button { router.push(.itemID(id)) } label: {
                        Label(item.kind == .movie ? "Go to movie" : item.kind == .audiobook ? "Go to book" : "Go to show", systemImage: "info.circle")
                    }
                }
                if !(item.overview ?? "").isEmpty {
                    Button { showingAbout = true } label: { Label("About", systemImage: "text.alignleft") }
                }
            }
            .fullScreenCover(isPresented: $showingAbout) {
                UpcomingAboutView(item: item, detail: detail ?? UpcomingDay.detail(item))
                    .presentationBackground(Color.black.opacity(0.6))
            }
        } else {
            VStack(alignment: .leading, spacing: 18) {
                artwork
                caption(focused: false)
            }
            .accessibilityElement(children: .combine)
        }
    }

    private func open() {
        if let id = item.libraryItemId { router.push(.itemID(id)) } else { showingAbout = true }
    }

    private var artwork: some View {
        RemoteImage(url: model.client?.imageURL(item.still ?? item.poster, width: 780)) {
            Text(UpcomingDay.title(item))
                .font(.system(size: 25, weight: .semibold))
                .foregroundStyle(Color.usher)
                .multilineTextAlignment(.center)
                .padding(20)
        }
        .frame(width: Layout.wideWidth, height: Layout.wideWidth * 9 / 16)
        .background(Color.seat)
        .overlay(alignment: .topLeading) {
            Text(UpcomingDay.label(item.date))
                .font(.system(size: 21, weight: .semibold))
                .foregroundStyle(Color.screen)
                .padding(.horizontal, 12)
                .padding(.vertical, 6)
                .background(Capsule().fill(.black.opacity(0.6)))
                .padding(14)
        }
        .overlay(alignment: .bottom) {
            if let progress = item.download?.progress {
                ProgressBar(value: progress).padding(16)
            }
        }
        .clipShape(RoundedRectangle(cornerRadius: Radius.art, style: .continuous))
    }

    private func caption(focused: Bool) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(UpcomingDay.title(item))
                .font(.system(size: 25, weight: .semibold))
                .foregroundStyle(focused ? Color.screen : Color.screen.opacity(0.85))
            Text(detail ?? UpcomingDay.detail(item))
                .font(.system(size: 21))
                .foregroundStyle(Color.usher)
            if let status {
                Text(status).font(.system(size: 21, weight: .medium)).foregroundStyle(item.available == true ? Color.go : Color.usher)
            }
        }
        .lineLimit(1)
        .frame(width: Layout.wideWidth, alignment: .leading)
    }
}

/// What's known about something coming: when, and its overview.
private struct UpcomingAboutView: View {
    @Environment(\.dismiss) private var dismiss
    let item: UpcomingItem
    let detail: String

    var body: some View {
        VStack(alignment: .leading, spacing: 20) {
            Text(UpcomingDay.title(item))
                .font(.marquee(64, weight: .heavy))
                .foregroundStyle(Color.screen)
            Text(detail).font(.system(size: 30, weight: .semibold)).foregroundStyle(Color.screen.opacity(0.85))
            Text("\(item.when) \(item.date.formatted(date: .complete, time: item.kind == .episode ? .shortened : .omitted))")
                .font(.system(size: 26))
                .foregroundStyle(Color.usher)
            if let overview = item.overview {
                Text(overview).font(.system(size: 29)).foregroundStyle(Color.screen.opacity(0.88)).lineSpacing(6)
            }
            Button("Close") { dismiss() }
                .buttonStyle(ActionButtonStyle())
                .padding(.top, 20)
        }
        .frame(maxWidth: 1100, alignment: .leading)
        .padding(80)
        .background(RoundedRectangle(cornerRadius: Radius.panel, style: .continuous).fill(Color.seat))
    }
}
