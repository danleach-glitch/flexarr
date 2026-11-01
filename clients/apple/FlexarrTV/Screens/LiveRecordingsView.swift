import FlexarrKit
import SwiftUI

/// The DVR on Apple TV: what's coming up (choose one to take it off, or stop it), the series being recorded, and what's
/// been recorded (choose one to play it). Programmes are set to record from the guide.
struct LiveRecordingsView: View {
    @Environment(AppModel.self) private var model
    let onChanged: () -> Void
    @Binding var upcoming: Int?

    @State private var recordings: Recordings?
    @State private var failed: String?
    @State private var confirming: Confirm?

    private enum Confirm: Identifiable {
        case recording(Recording)
        case series(RecordingSeries)

        var id: String {
            switch self {
            case .recording(let r): "r" + r.id
            case .series(let s): "s" + s.id
            }
        }
    }

    private static let day: DateFormatter = {
        let formatter = DateFormatter()
        formatter.setLocalizedDateFormatFromTemplate("EEE d MMM HH:mm")
        return formatter
    }()

    var body: some View {
        Group {
            if let recordings {
                ScrollView(.vertical) {
                    LazyVStack(alignment: .leading, spacing: 10) {
                        heading("Coming up")
                        if recordings.upcoming.isEmpty {
                            Text("Nothing set to record. In the guide, press and hold a programme and choose Record, or Record series.")
                                .font(.system(size: 26)).foregroundStyle(Color.usher)
                        }
                        ForEach(recordings.upcoming) { recording in
                            Button { confirming = .recording(recording) } label: { row(recording) }
                                .buttonStyle(RecordingRowStyle())
                        }
                        if !recordings.series.isEmpty {
                            heading("Series").padding(.top, 24)
                            ForEach(recordings.series) { series in
                                Button { confirming = .series(series) } label: { seriesRow(series) }
                                    .buttonStyle(RecordingRowStyle())
                            }
                        }
                        if !recordings.recent.isEmpty {
                            heading("Recorded").padding(.top, 24)
                            ForEach(recordings.recent) { recording in
                                Button {
                                    if let item = recording.itemId { Task { await model.playFromIntent(itemId: item) } }
                                } label: { row(recording) }
                                    .buttonStyle(RecordingRowStyle())
                                    .disabled(recording.itemId == nil)
                            }
                        }
                    }
                    .padding(.trailing, Layout.edge)
                    .padding(.vertical, 12)
                }
                .scrollClipDisabled()
            } else if let failed {
                StatusView(state: .failed(failed) { Task { await load() } })
            } else {
                StatusView(state: .loading)
            }
        }
        .task {
            while !Task.isCancelled {
                await load()
                try? await Task.sleep(for: .seconds(30))
            }
        }
        .alert(title, isPresented: Binding(get: { confirming != nil }, set: { if !$0 { confirming = nil } }), presenting: confirming) { confirm in
            switch confirm {
            case .recording(let recording):
                Button(recording.isRecordingNow ? "Stop recording" : "Don't record", role: .destructive) {
                    act { try await $0.removeRecording(recording.id) }
                }
            case .series(let series):
                Button("Stop recording the series", role: .destructive) { act { try await $0.removeSeries(series.id) } }
            }
            Button("Cancel", role: .cancel) {}
        } message: { confirm in
            switch confirm {
            case .recording(let recording):
                Text(recording.isRecordingNow ? "What's recorded so far is kept." : "\(recording.channelName), \(Self.day.string(from: recording.start))")
            case .series(let series):
                Text("Its scheduled showings come off the list. What it's recorded stays.")
            }
        }
    }

    private var title: String {
        switch confirming {
        case .recording(let r): r.title
        case .series(let s): s.title
        case nil: ""
        }
    }

    private func heading(_ text: String) -> some View {
        Text(text).font(.system(size: 30, weight: .bold)).foregroundStyle(Color.screen).padding(.bottom, 4)
    }

    private func row(_ recording: Recording) -> some View {
        HStack(spacing: 18) {
            Circle()
                .fill(recording.isRecordingNow ? Color.record : Color.clear)
                .frame(width: 16, height: 16)
            VStack(alignment: .leading, spacing: 4) {
                Text(recording.title).font(.system(size: 27, weight: .semibold)).lineLimit(1)
                Text(detail(recording)).font(.system(size: 21)).opacity(0.7).lineLimit(1)
                if recording.clashes {
                    Text("Won't record: every stream is wanted by recordings scheduled before it.")
                        .font(.system(size: 21)).foregroundStyle(Color.danger)
                } else if let reason = recording.reason, recording.state != "scheduled" {
                    Text(reason).font(.system(size: 21)).opacity(0.7).lineLimit(1)
                }
            }
            Spacer(minLength: 0)
        }
        .padding(.horizontal, 22)
        .padding(.vertical, 16)
    }

    private func seriesRow(_ series: RecordingSeries) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(series.title).font(.system(size: 27, weight: .semibold)).lineLimit(1)
            Text("\(series.channelName.map { "On \($0)" } ?? "On any channel"), \(series.upcoming) coming up, \(series.recorded) recorded"
                 + (series.newOnly ? ", new episodes only" : ""))
                .font(.system(size: 21)).opacity(0.7).lineLimit(1)
        }
        .padding(.horizontal, 22)
        .padding(.vertical, 16)
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private func detail(_ recording: Recording) -> String {
        var parts: [String] = []
        if let season = recording.season, let episode = recording.episode { parts.append("S\(season)E\(episode)") }
        if let subtitle = recording.subtitle { parts.append(subtitle) }
        parts.append(recording.channelName)
        parts.append(Self.day.string(from: recording.start))
        return parts.joined(separator: "   ")
    }

    private func load() async {
        guard let client = model.client else { return }
        do {
            let loaded = try await client.recordings()
            recordings = loaded
            upcoming = loaded.upcoming.count
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
            onChanged()
        }
    }
}

/// A recording's row: a quiet panel, white with dark text when focused, as tvOS does.
private struct RecordingRowStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        RowBody(configuration: configuration)
    }

    private struct RowBody: View {
        let configuration: Configuration
        @Environment(\.isFocused) private var isFocused

        var body: some View {
            configuration.label
                .foregroundStyle(isFocused ? Color.room : Color.screen)
                .background {
                    RoundedRectangle(cornerRadius: Radius.panel, style: .continuous).fill(isFocused ? Color.screen : Color.seat.opacity(0.7))
                }
                .scaleEffect(isFocused ? 1.02 : 1)
                .animation(.spring(response: 0.25, dampingFraction: 0.8), value: isFocused)
        }
    }
}
