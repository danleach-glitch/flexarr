import FlexarrKit
import SwiftUI
#if canImport(UIKit)
import UIKit
#else
import AppKit
#endif

/// This profile's Trakt account: connect by a code entered at trakt.tv/activate, how the sync is going, the first
/// history import (previewed first), the lists brought across, and disconnecting.
struct TraktSettingsView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.scenePhase) private var scenePhase
    @State private var status: TraktStatus?
    @State private var loadFailed = false
    /// The server no longer offers Trakt to this profile (an older one, or it's a kids profile now).
    @State private var unavailable = false
    @State private var preview: TraktImportPreview?
    @State private var lists: [TraktList]?
    @State private var chosen: Set<String> = []
    @State private var working: Part?
    /// Sync now under way (the account part runs other things too, like Disconnect).
    @State private var syncing = false
    /// List choices save on their own, whatever else is running.
    @State private var savingLists = false
    @State private var problems: [Part: String] = [:]
    @State private var confirmDisconnect = false

    /// Where on the page something happened, so its error shows beside it.
    private enum Part { case account, history, lists }

    private var waiting: Bool { status?.pending?.state == .waiting || status?.importing == true }

    var body: some View {
        List {
            if unavailable {
                Section { Text(TraktWords.unavailable).foregroundStyle(Color.usher).listRowBackground(Color.seat) }
            } else if let status {
                if !status.available {
                    Section { Text(TraktWords.unavailable).foregroundStyle(Color.usher).listRowBackground(Color.seat) }
                } else if status.state == .connected {
                    connected(status)
                    history(status)
                    listsSection
                } else {
                    connect(status)
                }
            } else if loadFailed {
                Section {
                    Text(TraktWords.unreachable).foregroundStyle(Color.danger).listRowBackground(Color.seat)
                    Button(t("Try again")) { Task { await load() } }.listRowBackground(Color.seat)
                }
            } else {
                Section { ProgressView().frame(maxWidth: .infinity).listRowBackground(Color.seat) }
            }
        }
        .settingsPage("Trakt")
        .task { await load() }
        .refreshable { await load() }
        // While a code waits, or the import runs, keep looking: only while Flexarr is on screen.
        .task(id: waiting && scenePhase == .active) {
            while waiting, scenePhase == .active, !Task.isCancelled {
                try? await Task.sleep(for: .seconds(2))
                await load()
            }
        }
        .confirmationDialog(TraktWords.disconnectTitle, isPresented: $confirmDisconnect, titleVisibility: .visible) {
            Button(t("Disconnect"), role: .destructive) { run(.account) { try await $0.disconnectTrakt() } }
        } message: {
            Text(TraktWords.disconnectMessage)
        }
    }

    @ViewBuilder private func problemRow(_ part: Part) -> some View {
        if let problem = problems[part] {
            Text(problem).font(.footnote).foregroundStyle(Color.danger).listRowBackground(Color.seat)
        }
    }

    @ViewBuilder private func connect(_ status: TraktStatus) -> some View {
        if let pending = status.pending, pending.state == .waiting {
            Section {
                Text(pending.userCode)
                    .font(.system(.largeTitle, design: .monospaced).weight(.heavy))
                    .tracking(4)
                    .lineLimit(1)
                    .minimumScaleFactor(0.4)
                    .frame(maxWidth: .infinity)
                    .padding(.vertical, 8)
                    .textSelection(.enabled)
                    .accessibilityLabel(pending.userCode.map(String.init).joined(separator: " "))
                    .listRowBackground(Color.seat)
                if let url = URL(string: pending.verificationUrl) {
                    Button {
                        #if os(macOS)
                        NSPasteboard.general.clearContents()
                        NSPasteboard.general.setString(pending.userCode, forType: .string)
                        NSWorkspace.shared.open(url)
                        #else
                        UIPasteboard.general.string = pending.userCode
                        UIApplication.shared.open(url)
                        #endif
                    } label: {
                        Label(t("Copy code and open Trakt"), systemImage: "arrow.up.forward.app")
                    }
                    .listRowBackground(Color.seat)
                }
                Button(t(Words.cancel), role: .cancel) { run(.account) { try await $0.cancelTraktConnect() } }
                    .listRowBackground(Color.seat)
                problemRow(.account)
            } footer: {
                Text("\(TraktWords.enterCode(at: pending.verificationUrl)) \(TraktWords.waiting)")
            }
        } else {
            Section {
                Button {
                    run(.account) { _ = try await $0.connectTrakt() }
                } label: {
                    Label(status.state == .needsSignIn ? t("Connect {account} again", ["account": status.username ?? "Trakt"]) : t("Connect Trakt"), systemImage: "link")
                }
                .disabled(working != nil)
                .listRowBackground(Color.seat)
                problemRow(.account)
            } footer: {
                Text(status.state == .needsSignIn ? TraktWords.signedOut
                     : status.pending.flatMap { $0.error ?? $0.state.problem } ?? t("Each profile connects its own account."))
            }
        }
    }

    @ViewBuilder private func connected(_ status: TraktStatus) -> some View {
        Section {
            LabeledContent(t("Account"), value: status.username ?? t("Connected")).settingsRow()
            Text(TraktWords.synced(status.lastSyncAt)).settingsRow()
            if status.queued > 0 {
                LabeledContent(t("Waiting to send"), value: status.queued.formatted(.number.locale(Localization.locale))).settingsRow()
            }
            Button {
                syncing = true
                run(.account) { try await $0.syncTraktNow() }
            } label: {
                HStack {
                    Text(syncing ? t("Syncing…") : t("Sync now"))
                    if syncing { Spacer(); ProgressView().id(UUID()) }
                }
            }
            .disabled(working != nil)
            .listRowBackground(Color.seat)
            Button(t("Disconnect"), role: .destructive) { confirmDisconnect = true }
                .foregroundStyle(Color.danger)
                .disabled(working != nil)
                .listRowBackground(Color.seat)
            problemRow(.account)
        } footer: {
            if let error = status.lastError {
                Text(error)
            } else if status.watchlistFull {
                Text(TraktWords.watchlistFull)
            }
        }
    }

    @ViewBuilder private func history(_ status: TraktStatus) -> some View {
        Section {
            if status.importing {
                HStack {
                    ProgressView().id(UUID())
                    Text(TraktWords.importing).foregroundStyle(Color.usher)
                }
                .listRowBackground(Color.seat)
            } else if let importedAt = status.importedAt {
                LabeledContent(TraktWords.imported, value: importedAt.formatted(.relative(presentation: .named).locale(Localization.locale))).settingsRow()
            } else if let preview {
                VStack(alignment: .leading, spacing: 4) {
                    Text(preview.summary).font(.body.weight(.semibold))
                    Text(preview.details).font(.footnote).foregroundStyle(Color.usher)
                }
                .listRowBackground(Color.seat)
                Button(TraktWords.importAction) { run(.history) { try await $0.confirmTraktImport() } }
                    .disabled(working != nil)
                    .listRowBackground(Color.seat)
            } else {
                Button(TraktWords.importEntry) {
                    run(.history) { client in preview = try await client.previewTraktImport() }
                }
                .disabled(working != nil)
                .listRowBackground(Color.seat)
            }
            problemRow(.history)
            if problems[.history] == nil, !status.importing, status.importedAt == nil, let failed = status.importError {
                Text(failed).font(.footnote).foregroundStyle(Color.danger).listRowBackground(Color.seat)
            }
        } header: {
            Text(t("Your history"))
        } footer: {
            if status.importedAt == nil, !status.importing {
                Text(preview == nil ? t("See what changes first.") : t("Nothing is marked unwatched, on either side."))
            }
        }
    }

    @ViewBuilder private var listsSection: some View {
        if let lists, !lists.isEmpty {
            Section {
                ForEach(lists) { list in
                    Toggle(isOn: Binding(get: { chosen.contains(list.id) }, set: { on in choose(list.id, on) })) {
                        VStack(alignment: .leading, spacing: 2) {
                            Text(list.name)
                            Text(list.detail).font(.footnote).foregroundStyle(Color.usher)
                        }
                    }
                    .disabled(savingLists)
                    .settingsRow()
                }
                problemRow(.lists)
            } header: {
                Text(t("Lists"))
            } footer: {
                Text(TraktWords.listsNote)
            }
        }
    }

    private func choose(_ id: String, _ on: Bool) {
        guard let client = model.client, !savingLists else { return }
        let before = chosen
        if on { chosen.insert(id) } else { chosen.remove(id) }
        let ids = lists?.map(\.id).filter(chosen.contains) ?? []
        savingLists = true
        problems[.lists] = nil
        Task {
            do {
                try await client.saveTraktLists(ids)
            } catch {
                chosen = before
                problems[.lists] = TraktWords.problem(error)
            }
            savingLists = false
        }
    }

    private func load() async {
        guard let client = model.client else { return }
        do {
            let loaded = try await client.traktStatus()
            loadFailed = false
            // Gone from the server: say so rather than show nothing.
            unavailable = loaded == nil
            status = loaded
            if status?.state == .connected, lists == nil, let loaded = try? await client.traktLists() {
                lists = loaded
                chosen = Set(loaded.filter(\.selected).map(\.id))
            }
        } catch {
            if status == nil { loadFailed = true }
        }
    }

    private func run(_ part: Part, reload: Bool = true, _ work: @escaping (FlexarrClient) async throws -> Void) {
        guard let client = model.client, working == nil else { return }
        working = part
        problems[part] = nil
        Task {
            do {
                try await work(client)
            } catch {
                problems[part] = TraktWords.problem(error)
            }
            if reload { await load() }
            working = nil
            syncing = false
        }
    }
}
