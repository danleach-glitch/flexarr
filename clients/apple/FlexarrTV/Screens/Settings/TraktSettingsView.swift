import FlexarrKit
import SwiftUI

/// This profile's Trakt account on the Apple TV: connect with a code and a QR code (nothing to type on the TV), how
/// the sync is going, the first history import (previewed first), the lists brought across, and disconnecting.
struct TraktSettingsView: View {
    @Environment(AppModel.self) private var model
    @State private var status: TraktStatus?
    @State private var loadFailed = false
    @State private var unavailable = false
    @State private var preview: TraktImportPreview?
    @State private var lists: [TraktList]?
    @State private var chosen: Set<String> = []
    @State private var working = false
    @State private var syncing = false
    /// List choices save on their own, whatever else is running.
    @State private var savingLists = false
    @State private var problem: String?
    @State private var confirmDisconnect = false

    private var waiting: Bool { status?.pending?.state == .waiting || status?.importing == true }

    var body: some View {
        SettingsPage(title: "Trakt", summary: "Keep what you watch, your place and your watchlist in step with Trakt.") {
            if unavailable || status?.available == false {
                Text(TraktWords.unavailable).font(.system(size: 30)).foregroundStyle(Color.usher)
            } else if let status {
                if status.state == .connected {
                    connected(status)
                } else {
                    connect(status)
                }
            } else if loadFailed {
                SettingsRow(title: "Try again", symbol: "arrow.clockwise", help: TraktWords.unreachable) {
                    Task { await load() }
                }
            } else {
                ProgressView().padding(.top, 20)
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
        .confirmationDialog(TraktWords.disconnectTitle, isPresented: $confirmDisconnect, titleVisibility: .visible) {
            Button("Disconnect", role: .destructive) { run { try await $0.disconnectTrakt() } }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text(TraktWords.disconnectMessage)
        }
    }

    @ViewBuilder private func connect(_ status: TraktStatus) -> some View {
        if let pending = status.pending, pending.state == .waiting {
            HStack(alignment: .center, spacing: 50) {
                QRCode(text: pending.qrUrl)
                    .frame(width: 260, height: 260)
                    .accessibilityLabel("QR code for \(pending.verificationUrl.replacingOccurrences(of: "https://", with: ""))")
                VStack(alignment: .leading, spacing: 16) {
                    Text(TraktWords.enterCode(at: pending.verificationUrl))
                        .font(.system(size: 30)).foregroundStyle(Color.usher)
                    Text(pending.userCode)
                        .font(.system(size: 64, weight: .heavy, design: .monospaced))
                        .tracking(6)
                        .lineLimit(1)
                        .minimumScaleFactor(0.5)
                        .foregroundStyle(Color.screen)
                        .accessibilityLabel(pending.userCode.map(String.init).joined(separator: " "))
                }
            }
            .padding(.bottom, 30)
            SettingsRow(title: "Cancel", symbol: "xmark", help: TraktWords.waiting) {
                run { try await $0.cancelTraktConnect() }
            }
        } else {
            SettingsRow(title: status.state == .needsSignIn ? "Connect \(status.username ?? "Trakt") again" : "Connect Trakt", symbol: "link",
                        help: status.state == .needsSignIn ? TraktWords.signedOut
                            : status.pending.flatMap { $0.error ?? $0.state.problem } ?? "Each profile connects its own account.") {
                run { _ = try await $0.connectTrakt() }
            }
        }
    }

    @ViewBuilder private func connected(_ status: TraktStatus) -> some View {
        SettingsFact("Account", status.username ?? "Connected")
        Text(TraktWords.synced(status.lastSyncAt)).font(.system(size: 30)).foregroundStyle(Color.usher).padding(.horizontal, 32)
        if status.queued > 0 {
            SettingsFact("Waiting to send", status.queued.formatted())
        }
        if status.watchlistFull {
            Text(TraktWords.watchlistFull).font(.system(size: 28)).foregroundStyle(Color.usher).padding(.horizontal, 32)
        }

        // One row whatever stage the import is at, so focus stays on it as it changes.
        importRow(status).padding(.top, 36)
        SettingsRow(title: syncing ? "Syncing…" : "Sync now", symbol: "arrow.triangle.2.circlepath",
                    help: status.lastError ?? "Flexarr syncs every few minutes by itself.") {
            guard !working else { return }
            syncing = true
            run { try await $0.syncTraktNow() }
        }
        SettingsRow(title: "Disconnect", symbol: "xmark.circle", help: "Stop syncing this profile with Trakt.", destructive: true) {
            confirmDisconnect = true
        }
        // Beside the actions it's about, not below the lists.
        if let problem {
            Text(problem).font(.system(size: 28)).foregroundStyle(Color.danger).padding(.horizontal, 32)
        }

        if let lists, !lists.isEmpty {
            SettingsGroupLabel("Lists").padding(.top, 36)
            Text(TraktWords.listsNote).font(.system(size: 26)).foregroundStyle(Color.usher).padding(.horizontal, 32)
            ForEach(lists) { list in
                SettingsRow.toggle(list.name, isOn: chosen.contains(list.id), symbol: "list.bullet", help: list.detail) {
                    choose(list.id)
                }
            }
        }
    }

    private func importRow(_ status: TraktStatus) -> SettingsRow {
        if status.importing {
            return SettingsRow(title: TraktWords.importing, symbol: "clock.arrow.circlepath") {}
        }
        if let importedAt = status.importedAt {
            return SettingsRow(title: TraktWords.imported, symbol: "checkmark.circle", value: importedAt.formatted(.relative(presentation: .named))) {}
        }
        if let preview {
            let counts = "\(preview.traktFilms.formatted()) movies and \(preview.traktEpisodes.formatted()) episodes"
            return SettingsRow(title: TraktWords.importAction, symbol: "arrow.down.circle", value: counts,
                               help: "\(preview.summary). \(preview.details) Nothing is marked unwatched, on either side.") {
                run { try await $0.confirmTraktImport() }
            }
        }
        return SettingsRow(title: TraktWords.importEntry, symbol: "clock.arrow.circlepath", help: status.importError ?? "See what changes first.") {
            run { client in preview = try await client.previewTraktImport() }
        }
    }

    private func choose(_ id: String) {
        guard let client = model.client, !savingLists else { return }
        let before = chosen
        if chosen.contains(id) { chosen.remove(id) } else { chosen.insert(id) }
        let ids = lists?.map(\.id).filter(chosen.contains) ?? []
        savingLists = true
        problem = nil
        Task {
            do {
                try await client.saveTraktLists(ids)
            } catch {
                chosen = before
                problem = TraktWords.problem(error)
            }
            savingLists = false
        }
    }

    private func load() async {
        guard let client = model.client else { return }
        do {
            let loaded = try await client.traktStatus()
            loadFailed = false
            unavailable = loaded == nil
            status = loaded
            if loaded?.state == .connected, lists == nil, let found = try? await client.traktLists() {
                lists = found
                chosen = Set(found.filter(\.selected).map(\.id))
            }
        } catch {
            if status == nil { loadFailed = true }
        }
    }

    private func run(reload: Bool = true, _ work: @escaping (FlexarrClient) async throws -> Void) {
        guard let client = model.client, !working else { return }
        working = true
        problem = nil
        Task {
            do {
                try await work(client)
            } catch {
                problem = TraktWords.problem(error)
            }
            if reload { await load() }
            working = false
            syncing = false
        }
    }
}
