import FlexarrKit
import SwiftUI
import UIKit

/// This profile's Trakt account: connect by a code entered at trakt.tv/activate, how the sync is going, the first
/// history import (previewed first), and disconnecting.
struct TraktSettingsView: View {
    @Environment(AppModel.self) private var model
    @State private var status: TraktStatus?
    @State private var preview: TraktImportPreview?
    @State private var working = false
    @State private var problem: String?
    @State private var confirmDisconnect = false

    private var waiting: Bool { status?.pending?.state == .waiting || status?.importing == true }

    var body: some View {
        List {
            if let status {
                if status.state == .connected {
                    connected(status)
                    if status.importedAt == nil { importSection(status) }
                } else {
                    connect(status)
                }
            }
            if let problem {
                Section { Text(problem).foregroundStyle(.red).listRowBackground(Color.seat) }
            }
        }
        .settingsPage("Trakt")
        .task { await load() }
        .refreshable { await load() }
        // While a code waits, or the import runs, keep looking.
        .task(id: waiting) {
            while waiting, !Task.isCancelled {
                try? await Task.sleep(for: .seconds(2))
                await load()
            }
        }
        .confirmationDialog("Disconnect Trakt?", isPresented: $confirmDisconnect, titleVisibility: .visible) {
            Button("Disconnect", role: .destructive) { run { try await $0.disconnectTrakt() } }
        } message: {
            Text("What you've watched stays as it is, here and on Trakt.")
        }
    }

    @ViewBuilder private func connect(_ status: TraktStatus) -> some View {
        if let pending = status.pending, pending.state == .waiting {
            Section {
                Text(pending.userCode)
                    .font(.system(.largeTitle, design: .monospaced).weight(.heavy))
                    .tracking(4)
                    .frame(maxWidth: .infinity)
                    .padding(.vertical, 8)
                    .textSelection(.enabled)
                    .listRowBackground(Color.seat)
                if let url = URL(string: pending.verificationUrl) {
                    Button {
                        UIPasteboard.general.string = pending.userCode
                        UIApplication.shared.open(url)
                    } label: {
                        Label("Copy code and open Trakt", systemImage: "arrow.up.forward.app")
                    }
                    .listRowBackground(Color.seat)
                }
                Button("Cancel", role: .cancel) { run { try await $0.cancelTraktConnect() } }
                    .listRowBackground(Color.seat)
            } footer: {
                Text("Enter the code at \(pending.verificationUrl.replacingOccurrences(of: "https://", with: "")). Flexarr waits, so you can leave this.")
            }
        } else {
            Section {
                Button {
                    run { _ = try await $0.connectTrakt() }
                } label: {
                    Label(status.state == .needsSignIn ? "Connect \(status.username ?? "Trakt") again" : "Connect Trakt", systemImage: "link")
                }
                .disabled(working)
                .listRowBackground(Color.seat)
            } footer: {
                Text(status.state == .needsSignIn ? "Trakt signed Flexarr out." : status.pending?.state.problem ?? "Each profile connects its own account.")
            }
        }
    }

    @ViewBuilder private func connected(_ status: TraktStatus) -> some View {
        Section {
            LabeledContent("Account", value: status.username ?? "Connected").settingsRow()
            LabeledContent("Synced", value: status.lastSyncAt.map { $0.formatted(.relative(presentation: .named)) } ?? "Not yet").settingsRow()
            if status.queued > 0 {
                LabeledContent("Waiting to send", value: status.queued.formatted()).settingsRow()
            }
            Button("Sync now") { run { try await $0.syncTraktNow() } }
                .disabled(working)
                .listRowBackground(Color.seat)
            Button("Disconnect", role: .destructive) { confirmDisconnect = true }
                .listRowBackground(Color.seat)
        } footer: {
            if let error = status.lastError {
                Text(error)
            } else if status.watchlistFull {
                Text("Trakt's watchlist is full. New titles stay on Flexarr's list.")
            }
        }
    }

    @ViewBuilder private func importSection(_ status: TraktStatus) -> some View {
        Section {
            if status.importing {
                HStack {
                    ProgressView()
                    Text("Bringing your history across…").foregroundStyle(Color.usher)
                }
                .listRowBackground(Color.seat)
            } else if let preview {
                VStack(alignment: .leading, spacing: 4) {
                    Text(preview.summary).font(.body.weight(.semibold))
                    Text(preview.details).font(.footnote).foregroundStyle(Color.usher)
                }
                .listRowBackground(Color.seat)
                Button("Bring across") { run { try await $0.confirmTraktImport() } }
                    .disabled(working)
                    .listRowBackground(Color.seat)
            } else {
                Button("Bring your Trakt history across") {
                    run { client in preview = try await client.previewTraktImport() }
                }
                .disabled(working)
                .listRowBackground(Color.seat)
            }
        } header: {
            Text("Your history")
        } footer: {
            Text(preview == nil ? "See what changes first." : "Nothing is marked unwatched, on either side.")
        }
    }

    private func load() async {
        guard let client = model.client else { return }
        if let loaded = try? await client.traktStatus() { status = loaded }
    }

    private func run(_ work: @escaping (FlexarrClient) async throws -> Void) {
        guard let client = model.client, !working else { return }
        working = true
        problem = nil
        Task {
            do {
                try await work(client)
            } catch {
                problem = "That didn't work. Try again."
            }
            await load()
            working = false
        }
    }
}
