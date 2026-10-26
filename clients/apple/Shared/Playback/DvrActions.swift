import FlexarrKit

/// Recording from the guide, for both apps: what the guide shows on a programme says which way each action goes.
enum DvrActions {
    /// Sets the showing to record, or takes it off the list (stopping it, if it's recording now). Returns something to
    /// tell the viewer, if anything: that it clashes.
    static func toggleRecording(_ programme: LiveProgramme, on channelId: String, client: FlexarrClient) async throws -> String? {
        if programme.isSetToRecord {
            if let recording = try await client.recordings().recording(of: programme, on: channelId) {
                try await client.removeRecording(recording.id)
            }
            return nil
        }
        let recording = try await client.record(programme: programme.id)
        return recording.clashes
            ? "\(programme.title) clashes with recordings already on the list: every stream is wanted then, so it won't record unless one of them comes off."
            : nil
    }

    /// Records every showing of the title on this channel, or stops the series covering it.
    static func toggleSeries(_ programme: LiveProgramme, on channelId: String, client: FlexarrClient) async throws {
        if programme.series == true {
            if let series = try await client.recordings().series.first(where: { $0.covers(programme, on: channelId) }) {
                try await client.removeSeries(series.id)
            }
            return
        }
        try await client.recordSeries(programme: programme.id)
    }

    static func recordLabel(_ programme: LiveProgramme) -> String {
        programme.isRecordingNow ? "Stop recording" : programme.isSetToRecord ? "Don't record" : "Record"
    }

    static func seriesLabel(_ programme: LiveProgramme) -> String {
        programme.series == true ? "Stop recording the series" : "Record series"
    }
}
