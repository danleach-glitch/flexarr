import FlexarrKit
import SwiftUI

/// The DVR on iPhone and iPad: what's coming up, the series being recorded, and what's been recorded. Swipe to take
/// one off (or stop it, or delete a recording); tap a recording to play it. Programmes are set to record from the guide.
struct LiveRecordingsView: View {
    @Environment(AppModel.self) private var model
    @State private var recordings: Recordings?
    @State private var failed: String?

    private static let day: DateFormatter = {
        let formatter = DateFormatter()
        formatter.setLocalizedDateFormatFromTemplate("EEE d MMM HH:mm")
        return formatter
    }()

    var body: some View {
        Group {
            if let recordings {
                List {
                    Section("Coming up") {
                        if recordings.upcoming.isEmpty {
                            Text("Nothing set to record. In the Guide, tap a programme and choose Record, or Record series.")
                                .font(.footnote).foregroundStyle(Color.usher)
                        }
                        ForEach(recordings.upcoming) { recording in
                            row(recording)
                                .swipeActions {
                                    Button(recording.isRecordingNow ? "Stop" : "Don't record", role: .destructive) {
                                        act { try await $0.removeRecording(recording.id) }
                                    }
                                }
                        }
                    }
                    if !recordings.series.isEmpty {
                        Section("Series") {
                            ForEach(recordings.series) { series in
                                VStack(alignment: .leading, spacing: 3) {
                                    Text(series.title).font(.body.weight(.semibold))
                                    Text("\(series.channelName.map { "On \($0)" } ?? "On any channel") · \(series.upcoming) coming up · \(series.recorded) recorded"
                                         + (series.newOnly ? " · New only" : ""))
                                        .font(.caption).foregroundStyle(Color.usher)
                                }
                                .swipeActions {
                                    Button("Stop series", role: .destructive) { act { try await $0.removeSeries(series.id) } }
                                }
                            }
                        }
                    }
                    if !recordings.recent.isEmpty {
                        Section("Recorded") {
                            ForEach(recordings.recent) { recording in
                                Button {
                                    if let item = recording.itemId { Task { await model.playFromIntent(itemId: item) } }
                                } label: { row(recording) }
                                    .disabled(recording.itemId == nil)
                                    .swipeActions {
                                        Button(recording.state == "completed" ? "Delete" : "Clear", role: .destructive) {
                                            act { try await $0.removeRecording(recording.id) }
                                        }
                                    }
                            }
                        }
                    }
                }
                .scrollContentBackground(.hidden)
            } else if let failed {
                StatusView(state: .failed(failed) { Task { await load() } })
            } else {
                StatusView(state: .loading)
            }
        }
        .background(RoomBackground(ambient: nil))
        .navigationTitle("Recordings")
        .refreshable { await load() }
        .task {
            while !Task.isCancelled {
                await load()
                try? await Task.sleep(for: .seconds(30))
            }
        }
    }

    private func row(_ recording: Recording) -> some View {
        HStack(spacing: 10) {
            if recording.isRecordingNow {
                Circle().fill(Color(red: 0.9, green: 0.28, blue: 0.3)).frame(width: 9, height: 9).accessibilityLabel("Recording now")
            }
            VStack(alignment: .leading, spacing: 3) {
                Text(recording.title).font(.body.weight(.semibold)).foregroundStyle(Color.screen)
                Text(detail(recording)).font(.caption).foregroundStyle(Color.usher).lineLimit(1)
                if recording.clashes {
                    Text("Won't record: every stream is wanted by recordings scheduled before it.").font(.caption).foregroundStyle(.red)
                } else if let reason = recording.reason, recording.state != "scheduled" {
                    Text(reason).font(.caption).foregroundStyle(Color.usher)
                }
            }
        }
    }

    private func detail(_ recording: Recording) -> String {
        var parts: [String] = []
        if let season = recording.season, let episode = recording.episode { parts.append("S\(season) E\(episode)") }
        if let subtitle = recording.subtitle { parts.append(subtitle) }
        parts.append(recording.channelName)
        parts.append(Self.day.string(from: recording.start))
        if let size = recording.size { parts.append(ByteCountFormatter.string(fromByteCount: size, countStyle: .file)) }
        return parts.joined(separator: " · ")
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
