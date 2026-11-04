import FlexarrKit
import SwiftUI

/// Pick one of the profile's playlists for a track, album or artist, or start a new one with it. Tracks a playlist
/// already has aren't added twice. The web's Add to playlist dialog, as a sheet.
struct AddToPlaylistSheet: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    let request: PlaylistAdditionRequest

    @State private var playlists: [PlaylistSummary]?
    @State private var name = ""
    /// What's being added to: a playlist's id, or "new".
    @State private var busy: String?
    @State private var message: String?
    @State private var error: String?
    @FocusState private var naming: Bool

    var body: some View {
        NavigationStack {
            List {
                if let playlists, !playlists.isEmpty {
                    Section {
                        ForEach(playlists) { playlist in
                            Button { Task { await add(to: playlist) } } label: { row(playlist) }
                                .disabled(busy != nil)
                        }
                    }
                }
                Section {
                    HStack {
                        TextField(playlists?.isEmpty == true ? t("Name your first playlist") : t("New playlist"), text: $name)
                            .focused($naming)
                            .submitLabel(.done)
                            .onSubmit { Task { await create() } }
                        Button(t("Create")) { Task { await create() } }
                            .disabled(busy != nil || name.trimmingCharacters(in: .whitespaces).isEmpty)
                            .overlay { if busy == "new" { ProgressView() } }
                    }
                } footer: {
                    if let error {
                        Text(error).foregroundStyle(Color.danger)
                    } else if let message {
                        Text(message)
                    }
                }
            }
            .overlay { if playlists == nil && error == nil { ProgressView() } }
            .scrollContentBackground(.hidden)
            .navigationTitle(t(Words.addToPlaylist))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .principal) {
                    VStack(spacing: 0) {
                        Text(t(Words.addToPlaylist)).font(.headline)
                        Text(request.title).font(.caption).foregroundStyle(Color.usher).lineLimit(1)
                    }
                }
                ToolbarItem(placement: .confirmationAction) { Button(t(Words.done)) { dismiss() } }
            }
        }
        .presentationDetents([.medium, .large])
        .presentationBackground(Color.room)
        .sensoryFeedback(.success, trigger: message)
        .task { await load() }
    }

    private func row(_ playlist: PlaylistSummary) -> some View {
        HStack(spacing: 12) {
            PlaylistArtwork(playlist: playlist)
                .frame(width: 46, height: 46)
            VStack(alignment: .leading, spacing: 2) {
                Text(playlist.name).foregroundStyle(Color.screen)
                Text(playlist.trackCountLabel).font(.caption).foregroundStyle(Color.usher)
            }
            .lineLimit(1)
            Spacer()
            if busy == playlist.id { ProgressView() }
        }
        .contentShape(Rectangle())
    }

    private func load() async {
        guard let client = model.client else { return }
        do {
            let found = try await client.playlists()
            playlists = found
            if found.isEmpty { naming = true }
        } catch {
            self.error = ConnectionError(error).message
        }
    }

    private func add(to playlist: PlaylistSummary) async {
        await run(playlist.id) { client in
            let added = try await client.addToPlaylist(playlist.id, itemIds: [request.itemId])
            return added == 0 ? t("Already in {playlist}.", ["playlist": playlist.name]) : t("Added to {playlist}.", ["playlist": playlist.name])
        }
    }

    private func create() async {
        let trimmed = name.trimmingCharacters(in: .whitespaces)
        guard !trimmed.isEmpty, busy == nil else { return }
        await run("new") { client in
            let made = try await client.createPlaylist(name: trimmed, itemIds: [request.itemId])
            name = ""
            return t("Made {playlist} with it.", ["playlist": made.name])
        }
    }

    private func run(_ target: String, _ work: (FlexarrClient) async throws -> String) async {
        guard let client = model.client else { return }
        busy = target
        error = nil
        defer { busy = nil }
        do {
            message = try await work(client)
            playlists = try? await client.playlists()
            // Home and the library's Playlists row show the change.
            model.invalidate()
        } catch {
            self.error = ConnectionError(error).message
        }
    }
}
