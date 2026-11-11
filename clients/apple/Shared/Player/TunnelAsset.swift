import AVFoundation
import FlexarrKit

extension AVURLAsset {
    /// An asset for `url` that carries the guest tunnel's token when `url` goes through the tunnel (`GuestTunnelToken`),
    /// so a family guest's player gets past it. `AVURLAssetHTTPHeaderFieldsKey` isn't in Apple's headers, but it's what
    /// AVFoundation sends with every request the asset makes (an HLS stream's playlists and segments too).
    static func flexarr(_ url: URL, options: [String: Any] = [:]) -> AVURLAsset {
        var options = options
        let headers = GuestTunnelToken.headers(for: url)
        if !headers.isEmpty { options["AVURLAssetHTTPHeaderFieldsKey"] = headers }
        return AVURLAsset(url: url, options: options)
    }
}
