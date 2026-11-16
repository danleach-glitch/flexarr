import Foundation

/// The stream being replaced (another quality, audio track, moved sound, a recovery): paused and silent, its last
/// picture kept on screen over the new one until that plays, so a switch shows no black gap (docs/remote-streaming.md).
@MainActor
final class HeldPicture {
    /// The longest the old picture stays if the new stream never plays.
    static let longest: Duration = .seconds(20)

    private var held: PlayerTransport?
    private var timeout: Task<Void, Never>?

    /// Keeps `transport`'s picture up, paused and silent, until `release` (or `longest`).
    func hold(_ transport: PlayerTransport) {
        release()
        transport.pause()
        transport.volume = 0
        held = transport
        timeout = Task { [weak self] in
            guard (try? await Task.sleep(for: Self.longest)) != nil else { return }
            self?.release()
        }
    }

    /// Whether `transport` is the one held (its view stays up when it's swapped out).
    func holds(_ transport: PlayerTransport?) -> Bool {
        guard let transport, let held else { return false }
        return transport === held
    }

    /// The new stream plays (or the player closes): the old one goes.
    func release() {
        timeout?.cancel()
        timeout = nil
        guard let held else { return }
        self.held = nil
        held.stop()
        held.view.removeFromSuperview()
    }
}
