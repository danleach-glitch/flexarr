import FlexarrKit
import SwiftUI

/// The DVR on iPhone and iPad: what's coming up, the series being recorded, and what's been recorded. Swipe to take
/// one off (or stop it, or delete a recording); tap a recording to play it. Programmes are set to record from the guide.
struct LiveRecordingsView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.scenePhase) private var scenePhase
    @State private var recordings: Recordings?
    @State private var failed: String?

    /// Made each time, so it follows a change of language.
    private static var day: DateFormatter {
        let formatter = DateFormatter()
        formatter.locale = Localization.locale
        formatter.setLocalizedDateFormatFromTemplate("EEE d MMM HH:mm")
        return formatter
    }

    var body: some View {
        Group {
            if let recordings {
                List {
                    Section(t("Coming up")) {
                        if recordings.upcoming.isEmpty {
                            Text(t("Nothing set to record. In the Guide, tap a programme and choose Record, or Record series."))
                                .font(.footnote).foregroundStyle(Color.usher)
                        }
                        ForEach(recordings.upcoming) { recording in
                            row(recording)
                                .swipeActions {
                                    Button(recording.isRecordingNow ? t("Stop") : t("Don’t record"), role: .destructive) {
                                        act { try await $0.removeRecording(recording.id) }
                                    }
                                }
                        }
                    }
                    if !recordings.series.isEmpty {
                        Section(t(Words.series)) {
                            ForEach(recordings.series) { series in
                                VStack(alignment: .leading, spacing: 3) {
                                    Text(series.title).font(.body.weight(.semibold))
                                    Text(seriesLine(series))
                                        .font(.caption).foregroundStyle(Color.usher)
                                }
                                .swipeActions {
                                    Button(t("Stop series"), role: .destructive) { act { try await $0.removeSeries(series.id) } }
                                }
                            }
                        }
                    }
                    if !recordings.recent.isEmpty {
                        Section(t("Recorded")) {
                            ForEach(recordings.recent) { recording in
                                Button {
                                    if let item = recording.itemId { Task { await model.playFromIntent(itemId: item) } }
                                } label: { row(recording) }
                                    .disabled(recording.itemId == nil)
                                    .swipeActions {
                                        Button(recording.state == "completed" ? t(Words.delete) : t("Clear"), role: .destructive) {
                                            act { try await $0.removeRecording(recording.id) }
                                        }
                                    }
                            }
                        }
                    }
                }
                .scrollContentBackground(.hidden)
                .macReadable()
            } else if let failed {
                StatusView(state: .failed(failed) { Task { await load() } })
            } else {
                StatusView(state: .loading)
            }
        }
        .background(RoomBackground(ambient: nil))
        .navigationTitle(t("Recordings"))
        .refreshable { await load() }
        // Only while the app is in front: with music keeping it alive in the background, it would go on asking.
        .task(id: scenePhase == .active) {
            guard scenePhase == .active else { return }
            while !Task.isCancelled {
                await load()
                try? await Task.sleep(for: .seconds(30))
            }
        }
    }

    private func row(_ recording: Recording) -> some View {
        HStack(spacing: 10) {
            if recording.isRecordingNow {
                Circle().fill(Color.record).frame(width: 9, height: 9).accessibilityLabel(t("Recording now"))
            }
            VStack(alignment: .leading, spacing: 3) {
                Text(recording.title).font(.body.weight(.semibold)).foregroundStyle(Color.screen)
                Text(detail(recording)).font(.caption).foregroundStyle(Color.usher).lineLimit(1)
                if recording.clashes {
                    Text(t("Won’t record: every stream is wanted by recordings scheduled before it.")).font(.caption).foregroundStyle(Color.danger)
                } else if let reason = recording.reason, recording.state != "scheduled" {
                    Text(reason).font(.caption).foregroundStyle(Color.usher)
                }
            }
        }
    }

    /// "On BBC One, 2 coming up, 5 recorded".
    private func seriesLine(_ series: RecordingSeries) -> String {
        let channel = series.channelName.map { t("On {channel}", ["channel": $0]) } ?? t("On any channel")
        let values: [String: Any] = ["channel": channel, "upcoming": series.upcoming, "recorded": series.recorded]
        return series.newOnly
            ? t("{channel}, {upcoming} coming up, {recorded} recorded, new only", values)
            : t("{channel}, {upcoming} coming up, {recorded} recorded", values)
    }

    private func detail(_ recording: Recording) -> String {
        var parts: [String] = []
        if let season = recording.season, let episode = recording.episode { parts.append(t(Words.seasonEpisodeShort, ["season": season, "episode": episode])) }
        if let subtitle = recording.subtitle { parts.append(subtitle) }
        parts.append(recording.channelName)
        parts.append(Self.day.string(from: recording.start))
        if let size = recording.size { parts.append(formatBytes(size)) }
        return parts.joined(separator: "   ")
    }

    private func load() async {
        guard let client = model.client else { return }
        do {
            recordings = try await client.recordings()
            failed = nil
        } catch {
            if recordings == nil { failed = ConnectionError(error).message }
        }
    }

    private func act(_ action: @escaping (FlexarrClient) async throws -> Void) {
        guard let client = model.client else { return }
        Task {
            try? await action(client)
            await load()
        }
    }
}
