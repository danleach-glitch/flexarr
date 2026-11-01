import FlexarrKit
import SwiftUI

/// Coming soon: what Sonarr, Radarr and Readarr expect over the next few weeks, a day at a time, as on the web. One in
/// the library opens; anything else opens what's known about it.
struct ComingSoonView: View {
    @Environment(AppModel.self) private var model
    @State private var days: [UpcomingDay.Day]?
    @State private var error: String?

    var body: some View {
        Group {
            if let days, days.isEmpty {
                StatusView(state: .empty("Nothing is scheduled. Monitored shows and movies appear here as their dates get close."))
            } else if let days {
                List {
                    ForEach(days) { day in
                        Section {
                            ForEach(day.entries) { ComingSoonEntryRow(entry: $0) }
                        } header: {
                            Text(UpcomingDay.heading(day.date))
                                .font(.marquee(22, weight: .bold))
                                .foregroundStyle(Color.screen)
                                .textCase(nil)
                        }
                    }
                }
                .listStyle(.plain)
                .scrollContentBackground(.hidden)
                .refreshable { await load() }
            } else if let error {
                StatusView(state: .failed(error) { Task { await load() } })
            } else {
                StatusView(state: .loading)
            }
        }
        .background(RoomBackground(ambient: nil))
        .navigationTitle("Coming soon")
        .task { if days == nil { await load() } }
    }

    private func load() async {
        guard let client = model.client else { return }
        do {
            // Yesterday's (what just came) and four weeks ahead.
            let items = try await client.upcoming(days: 30)
            let yesterday = Calendar.current.date(byAdding: .day, value: -1, to: Calendar.current.startOfDay(for: .now))!
            days = UpcomingDay.days(items.filter { $0.date >= yesterday })
            error = nil
        } catch {
            if days == nil { self.error = ConnectionError(error).message }
        }
    }
}

/// One title on a day: its poster, what's coming, and where it's got to.
private struct ComingSoonEntryRow: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    let entry: UpcomingDay.Entry
    @State private var showingAbout = false

    var body: some View {
        Button {
            if let id = entry.first.libraryItemId { router.push(.itemID(id)) } else { showingAbout = true }
        } label: {
            HStack(spacing: 12) {
                Color.seat
                    .frame(width: 46, height: 69)
                    .overlay {
                        RemoteImage(url: model.client?.imageURL(entry.first.poster ?? entry.first.still, width: 140)) {
                            Image(systemName: entry.first.kind == .movie ? "film" : "tv").foregroundStyle(Color.usher)
                        }
                    }
                    .clipShape(RoundedRectangle(cornerRadius: Radius.art, style: .continuous))
                VStack(alignment: .leading, spacing: 2) {
                    Text(entry.title)
                        .font(.subheadline.weight(.semibold))
                        .foregroundStyle(Color.screen)
                    Text(entry.detail)
                        .font(.footnote)
                        .foregroundStyle(Color.screen.opacity(0.8))
                    if let status = entry.status {
                        Text(status).font(.footnote.weight(.medium)).foregroundStyle(entry.first.available == true ? Color.go : Color.usher)
                    } else if entry.first.kind == .episode {
                        Text(entry.first.date.formatted(date: .omitted, time: .shortened)).font(.footnote).foregroundStyle(Color.usher)
                    }
                }
                .lineLimit(2)
                Spacer(minLength: 0)
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(CardButtonStyle())
        .listRowBackground(Color.clear)
        .listRowSeparatorTint(Color.aisle)
        .accessibilityElement(children: .combine)
        .accessibilityAddTraits(.isButton)
        .sheet(isPresented: $showingAbout) {
            UpcomingAboutSheet(item: entry.first, detail: entry.detail)
                .presentationDetents([.medium, .large])
                .presentationDragIndicator(.visible)
        }
    }
}

/// What's known about something coming that isn't in the library yet: when, and its overview, with its page in
/// Sonarr or Radarr for the main profile.
struct UpcomingAboutSheet: View {
    @Environment(AppModel.self) private var model
    @Environment(\.openURL) private var openURL
    let item: UpcomingItem
    let detail: String

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 14) {
                HStack(alignment: .top, spacing: 16) {
                    Color.seat
                        .frame(width: 92, height: 138)
                        .overlay { RemoteImage(url: model.client?.imageURL(item.poster ?? item.still, width: 280)) { Color.seat } }
                        .clipShape(RoundedRectangle(cornerRadius: Radius.art, style: .continuous))
                    VStack(alignment: .leading, spacing: 4) {
                        Text(UpcomingDay.title(item))
                            .font(.marquee(28, weight: .bold))
                            .foregroundStyle(Color.screen)
                        Text(detail).font(.subheadline).foregroundStyle(Color.screen.opacity(0.85))
                        Text("\(item.when) \(item.date.formatted(date: .complete, time: item.kind == .episode ? .shortened : .omitted))")
                            .font(.footnote)
                            .foregroundStyle(Color.usher)
                        if let status = UpcomingDay.status(item) {
                            Text(status).font(.footnote.weight(.medium)).foregroundStyle(Color.go)
                        }
                    }
                }
                if let overview = item.overview, !overview.isEmpty {
                    Text(overview).font(.subheadline).foregroundStyle(Color.screen.opacity(0.88)).lineSpacing(3)
                }
                if model.currentProfile?.isMain == true, let url = manageURL {
                    Button { openURL(url) } label: {
                        Label("Open in \(item.source ?? (item.kind == .episode ? "Sonarr" : "Radarr"))", systemImage: "arrow.up.forward.app")
                    }
                    .buttonStyle(ActionButtonStyle())
                }
            }
            .padding(Layout.edge)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .background(Color.room)
    }

    /// An ordinary web address only (it comes from another app's answer).
    private var manageURL: URL? {
        guard let text = item.manageUrl, let url = URL(string: text), url.scheme == "http" || url.scheme == "https" else { return nil }
        return url
    }
}
