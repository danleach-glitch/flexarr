import FlexarrKit
import SwiftUI

/// Subtitles Bazarr can find for a file: Best match first (Flexarr picks), then every result, best first. Choosing
/// one saves it beside the file and hands it to `onDownloaded` (the player opens again with it on; a title page says
/// it's saved). A panel down the right on Apple TV, a sheet on iPhone and iPad. Opened from the player or a title page,
/// so its words fit both.
struct SubtitleSearchView: View {
    let client: FlexarrClient
    let itemId: String
    let fileId: String
    let onDownloaded: (DownloadedSubtitles) -> Void
    let onClose: () -> Void

    private enum Search {
        case searching
        case failed(String)
        case found([SubtitleSearchResult])
    }

    @State private var search = Search.searching
    /// The row being downloaded (`bestMatch` for Best match), and the work, so closing calls it off.
    @State private var downloading: String?
    @State private var download: Task<Void, Never>?
    @State private var downloadError: String?
    @FocusState private var focused: String?

    private static let bestMatch = "best-match"

    var body: some View {
        #if os(tvOS)
        HStack(spacing: 0) {
            Spacer(minLength: 0)
            VStack(alignment: .leading, spacing: 18) {
                Text("Search for subtitles")
                    .font(.marquee(48, weight: .black))
                    .foregroundStyle(Color.screen)
                    .padding(.horizontal, 40)
                list
            }
            .padding(.top, 60)
            .frame(width: 900)
            .background(Color.room.opacity(0.94))
        }
        .ignoresSafeArea()
        .onExitCommand(perform: close)
        .task { await runSearch() }
        #else
        NavigationStack {
            list
                .navigationTitle("Search for subtitles")
                .navigationBarTitleDisplayMode(.inline)
                .toolbar { Button("Done", action: close) }
        }
        .task { await runSearch() }
        #endif
    }

    private var list: some View {
        ScrollView(.vertical) {
            LazyVStack(alignment: .leading, spacing: rowSpacing) {
                if let downloadError {
                    Text(downloadError)
                        .font(detailFont)
                        .foregroundStyle(Color.danger)
                        .padding(.horizontal, 14)
                        .padding(.bottom, 8)
                }
                Button { pick(nil) } label: {
                    row(id: Self.bestMatch, title: "Best match", detail: "Flexarr picks the closest one", provider: nil, matchesFile: false)
                }
                .buttonStyle(PickerRowStyle(current: false))
                .focused($focused, equals: Self.bestMatch)

                switch search {
                case .searching:
                    status { ProgressView(); Text("Searching…") }
                case .failed(let message):
                    status { Text(message).foregroundStyle(Color.danger) }
                case .found(let results) where results.isEmpty:
                    status { Text("No subtitles found") }
                case .found(let results):
                    ForEach(results) { result in
                        Button { pick(result.id) } label: {
                            row(id: result.id, title: result.languageLabel, detail: result.release, provider: result.provider,
                                matchesFile: result.matchesFile)
                        }
                        .buttonStyle(PickerRowStyle(current: false))
                        .focused($focused, equals: result.id)
                    }
                }
            }
            .padding(.horizontal, horizontalPadding)
            .padding(.vertical, 20)
        }
        .onAppear { focused = Self.bestMatch }
    }

    private func status<Content: View>(@ViewBuilder _ content: () -> Content) -> some View {
        HStack(spacing: 12) { content() }
            .font(detailFont)
            .foregroundStyle(Color.usher)
            .padding(.horizontal, 14)
            .padding(.top, 16)
    }

    private func row(id: String, title: String, detail: String, provider: String?, matchesFile: Bool) -> some View {
        HStack(spacing: 16) {
            VStack(alignment: .leading, spacing: 4) {
                Text(title)
                    .font(titleFont)
                    .foregroundStyle(Color.screen)
                    .lineLimit(1)
                Text(downloading == id ? "Downloading…" : detail)
                    .font(detailFont)
                    .foregroundStyle(Color.usher)
                    .lineLimit(2)
                if provider != nil || matchesFile {
                    HStack(spacing: 10) {
                        if let provider { Text(provider) }
                        if matchesFile {
                            Label("Matches your file", systemImage: "checkmark.seal.fill")
                                .foregroundStyle(Color.downloaded)
                        }
                    }
                    .font(detailFont)
                    .foregroundStyle(Color.usher)
                    .lineLimit(1)
                }
            }
            Spacer(minLength: 0)
            if downloading == id { ProgressView() }
        }
        .padding(.horizontal, 14)
        .padding(.vertical, rowPadding)
    }

    private func runSearch() async {
        search = .searching
        do {
            let results = try await client.searchSubtitles(itemId: itemId, fileId: fileId)
            search = .found(results)
        } catch is CancellationError {
        } catch let error as URLError where error.code == .cancelled {
        } catch {
            search = .failed(error.localizedDescription)
        }
    }

    /// Saves the result (nil: the best match) beside the file and hands the new track back.
    private func pick(_ resultId: String?) {
        guard downloading == nil else { return }
        downloading = resultId ?? Self.bestMatch
        downloadError = nil
        download = Task {
            do {
                let saved = try await client.downloadSubtitles(itemId: itemId, fileId: fileId, result: resultId)
                guard !Task.isCancelled else { return }
                onDownloaded(saved)
            } catch {
                guard !Task.isCancelled else { return }
                downloadError = error.localizedDescription
                downloading = nil
            }
        }
    }

    private func close() {
        download?.cancel()
        onClose()
    }

    #if os(tvOS)
    private let rowSpacing: CGFloat = 8, horizontalPadding: CGFloat = 30, rowPadding: CGFloat = 12
    private let titleFont = Font.system(size: 27, weight: .semibold), detailFont = Font.system(size: 21)
    #else
    private let rowSpacing: CGFloat = 2, horizontalPadding: CGFloat = 12, rowPadding: CGFloat = 8
    private let titleFont = Font.body.weight(.semibold), detailFont = Font.caption
    #endif
}

/// The file a title page searches subtitles for (its first, the copy playback picks), for presenting the search.
struct SubtitleSearchFile: Identifiable, Hashable {
    let id: String
}
