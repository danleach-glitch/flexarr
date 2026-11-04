import AppKit
import FlexarrKit
import Observation

/// For the copy downloaded from flexarr.io (Info.plist's FlexarrDistribution is "direct"): whether a newer one is out,
/// asked of GitHub's latest release at launch and then daily. The App Store's copy updates itself and never asks.
/// What's found shows as Update at the foot of the sidebar and in the Flexarr menu; both download the newest disk image.
@MainActor @Observable
final class MacUpdates {
    static let shared = MacUpdates()

    /// The newer version, once one is out.
    private(set) var newer: String?

    let isDirectDownload = Bundle.main.object(forInfoDictionaryKey: "FlexarrDistribution") as? String == "direct"
    @ObservationIgnored private var checking: Task<Void, Never>?

    func start() {
        guard isDirectDownload, checking == nil else { return }
        checking = Task { [weak self] in
            while !Task.isCancelled {
                if let release = await AppRelease.latest(), release.isNewer(than: Bundle.main.shortVersion) {
                    self?.newer = release.version
                }
                try? await Task.sleep(for: .seconds(24 * 60 * 60))
            }
        }
    }

    func download() {
        NSWorkspace.shared.open(AppRelease.macDownloadURL)
    }
}
