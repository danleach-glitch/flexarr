import FlexarrKit
import SwiftUI

/// Download on a book's page: the whole book, file by file; then how far it's come, then Downloaded (a tap removes it).
struct BookDownloadButton: View {
    @Environment(DownloadStore.self) private var downloads
    let item: ItemSummary
    @State private var confirmRemove = false
    @State private var failed: String?

    var body: some View {
        let entry = downloads.entry(for: item.id)
        Button {
            if let entry {
                if case .failed = entry.state { start() } else { confirmRemove = true }
            } else {
                start()
            }
        } label: {
            switch entry?.state {
            case .downloading(let fraction), .preparing(let fraction):
                Label("\(Int(fraction * 100))%", systemImage: "arrow.down.circle.dotted")
                    .monospacedDigit()
            case .downloaded:
                Label {
                    Text("Downloaded")
                } icon: {
                    // Green, as downloads are everywhere else.
                    Image(systemName: "checkmark.circle.fill").foregroundStyle(Color.downloaded)
                }
            case .failed:
                Label("Try again", systemImage: "exclamationmark.arrow.circlepath")
            case nil:
                Label("Download", systemImage: "arrow.down.circle")
            }
        }
        .confirmationDialog("Remove the download?", isPresented: $confirmRemove, titleVisibility: .visible) {
            Button("Remove", role: .destructive) { if let entry { downloads.remove(entry.id) } }
        }
        .alert(failed ?? "", isPresented: Binding(get: { failed != nil }, set: { if !$0 { failed = nil } })) {
            Button("OK", role: .cancel) {}
        }
    }

    private func start() {
        Task {
            do {
                try await downloads.download(book: item)
            } catch {
                failed = ConnectionError(error).message
            }
        }
    }
}
