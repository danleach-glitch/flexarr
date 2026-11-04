import FlexarrKit
import SwiftUI
import WebKit

/// A title's trailer in a sheet: YouTube's privacy-enhanced player, playing straight away.
struct TrailerSheet: View {
    let trailer: YouTubeTrailer
    let title: String
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        NavigationStack {
            ZStack {
                Color.black.ignoresSafeArea()
                TrailerPlayer(trailer: trailer)
                    .aspectRatio(16 / 9, contentMode: .fit)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                    .accessibilityLabel(t("{title} trailer", ["title": title]))
            }
            .navigationTitle(title)
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button(t("Close")) { dismiss() }
                }
            }
        }
        .preferredColorScheme(.dark)
    }
}

#if os(macOS)
private struct TrailerPlayer: NSViewRepresentable {
    let trailer: YouTubeTrailer

    func makeNSView(context: Context) -> WKWebView {
        let configuration = WKWebViewConfiguration()
        configuration.mediaTypesRequiringUserActionForPlayback = []
        configuration.websiteDataStore = .nonPersistent()
        let view = WKWebView(frame: .zero, configuration: configuration)
        view.setValue(false, forKey: "drawsBackground")
        // YouTube refuses an embed that doesn't say which site it's on (error 153).
        view.loadHTMLString(trailer.embedPage, baseURL: YouTubeTrailer.embedOrigin)
        return view
    }

    func updateNSView(_ view: WKWebView, context: Context) {}

    static func dismantleNSView(_ view: WKWebView, coordinator: ()) {
        view.loadHTMLString("", baseURL: nil)
    }
}
#else
private struct TrailerPlayer: UIViewRepresentable {
    let trailer: YouTubeTrailer

    func makeUIView(context: Context) -> WKWebView {
        let configuration = WKWebViewConfiguration()
        configuration.allowsInlineMediaPlayback = true
        configuration.allowsPictureInPictureMediaPlayback = true
        configuration.mediaTypesRequiringUserActionForPlayback = []
        // Nothing kept between trailers: no cookies or storage left behind.
        configuration.websiteDataStore = .nonPersistent()
        let view = WKWebView(frame: .zero, configuration: configuration)
        view.isOpaque = false
        view.backgroundColor = .black
        view.scrollView.isScrollEnabled = false
        // YouTube refuses an embed that doesn't say which site it's on (error 153): the page's base URL is the
        // referrer the player's frame sends.
        view.loadHTMLString(trailer.embedPage, baseURL: YouTubeTrailer.embedOrigin)
        return view
    }

    func updateUIView(_ view: WKWebView, context: Context) {}

    static func dismantleUIView(_ view: WKWebView, coordinator: ()) {
        // Stop the sound with the sheet.
        view.loadHTMLString("", baseURL: nil)
    }
}
#endif
