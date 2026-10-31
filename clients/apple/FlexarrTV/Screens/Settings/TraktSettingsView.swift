import FlexarrKit
import SwiftUI

/// This profile's Trakt account on the Apple TV: connect with a code and a QR code (nothing to type on the TV), how
/// the sync is going, the first history import (previewed first), and disconnecting.
struct TraktSettingsView: View {
    @Environment(AppModel.self) private var model
    @State private var status: TraktStatus?
    @State private var preview: TraktImportPreview?
    @State private var working = false
    @State private var problem: String?
    @State private var confirmDisconnect = false

    private var waiting: Bool { status?.pending?.state == .waiting || status?.importing == true }

    var body: some View {
        SettingsPage(title: "Trakt", summary: "Keep what you watch, your place and your watchlist in step with Trakt.") {
            if let status {
                if status.state == .connected {
                    connected(status)
                } else {
                    connect(status)
                }
            }
            if let problem {
                Text(problem).font(.system(size: 28)).foregroundStyle(.red).padding(.top, 20)
            }
        }
        .task { await load() }
        // While a code waits, or the import runs, keep looking.
        .task(id: waiting) {
            while waiting, !Task.isCancelled {
                try? await Task.sleep(for: .seconds(2))
                await load()
            }
        }
        .confirmationDialog("Disconnect Trakt?", isPresented: $confirmDisconnect, titleVisibility: .visible) {
            Button("Disconnect", role: .destructive) { run { try await $0.disconnectTrakt() } }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("What you've watched stays as it is, here and on Trakt.")
        }
    }

    @ViewBuilder private func connect(_ status: TraktStatus) -> some View {
        if let pending = status.pending, pending.state == .waiting {
            HStack(alignment: .center, spacing: 50) {
                QRCode(text: pending.qrUrl)
                    .frame(width: 260, height: 260)
                VStack(alignment: .leading, spacing: 16) {
                    Text("On your phone, go to")
                        .font(.system(size: 30)).foregroundStyle(Color.usher)
                    Text(pending.verificationUrl.replacingOccurrences(of: "https://", with: ""))
                        .font(.system(size: 34, weight: .semibold)).foregroundStyle(Color.screen)
                    Text("and enter")
                        .font(.system(size: 30)).foregroundStyle(Color.usher)
                    Text(pending.userCode)
                        .font(.system(size: 64, weight: .heavy, design: .monospaced))
                        .tracking(6)
                        .foregroundStyle(Color.screen)
                }
            }
            .padding(.bottom, 30)
            .accessibilityElement(children: .combine)
            SettingsRow(title: "Cancel", symbol: "xmark", help: "Flexarr waits for the code for 10 minutes.") {
                run { try await $0.cancelTraktConnect() }
            }
        } else {
            SettingsRow(title: status.state == .needsSignIn ? "Connect \(status.username ?? "Trakt") again" : "Connect Trakt", symbol: "link",
                        help: status.state == .needsSignIn ? "Trakt signed Flexarr out." : status.pending?.state.problem ?? "Each profile connects its own account.") {
                run { _ = try await $0.connectTrakt() }
            }
        }
    }

    @ViewBuilder private func connected(_ status: TraktStatus) -> some View {
        SettingsFact("Account", status.username ?? "Connected")
        SettingsFact("Synced", status.lastSyncAt.map { $0.formatted(.relative(presentation: .named)) } ?? "Not yet")
        if let error = status.lastError {
            SettingsFact("Problem", error)
        } else if status.watchlistFull {
            SettingsFact("Watchlist", "Full on Trakt")
        }
        if status.importedAt == nil {
            if status.importing {
                SettingsFact("History", "Bringing it across…")
            } else if let preview {
                SettingsRow(title: "Bring across", symbol: "arrow.down.circle", value: preview.summary,
                            help: preview.details + " Nothing is marked unwatched, on either side.") {
                    run { try await $0.confirmTraktImport() }
                }
                .padding(.top, 36)
            } else {
                SettingsRow(title: "Bring your history across", symbol: "clock.arrow.circlepath", help: "See what changes first.") {
                    run { client in preview = try await client.previewTraktImport() }
                }
                .padding(.top, 36)
            }
        }
        SettingsRow(title: "Sync now", symbol: "arrow.triangle.2.circlepath", help: "Flexarr syncs every few minutes by itself.") {
            run { try await $0.syncTraktNow() }
        }
        .padding(.top, status.importedAt == nil ? 0 : 36)
        SettingsRow(title: "Disconnect", symbol: "link.badge.plus", help: "Stop syncing this profile with Trakt.", destructive: true) {
            confirmDisconnect = true
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
