import FlexarrKit
import Foundation
import Observation

/// "Join someone’s Flexarr": this device shows a code to read (or text) to whoever invited it, who types it into
/// Settings › Remote access. The invite comes back through Flexarr's relay, sealed so only this device can open it,
/// and the guest says Join (see `GuestTvCode`, docs/remote-guests.md). A code lasts ten minutes; a new one replaces
/// it, up to three, then the screen waits for the guest to ask for another. Shared by the iPhone's and the Apple TV's
/// code screens, which run `run` while the app is in front (and again with `round` when the guest asks).
@MainActor
@Observable
final class GuestCodeWaiter {
    /// The code showing, while it's good.
    private(set) var code: GuestTvCode?
    /// An invite came for the code: "Join Dan’s Flexarr?" (anyone who heard the code could have sent one).
    private(set) var offer: GuestInvite?
    private(set) var joining: GuestInvite?
    /// Joining didn't work: why, with New code (and Try again when `canTryAgain`).
    private(set) var problem: String?
    private(set) var canTryAgain = false
    /// No code could be had (the relay can't be reached): just the problem and Try again.
    private(set) var noCode = false
    /// Three codes ran out with nothing arriving: Show a new code.
    private(set) var stopped = false
    /// Bumped by the guest's choices, so the screen runs `run` again.
    private(set) var round = 0

    /// When the code showing was made, and how many have been shown since the guest last asked.
    private var codeMade = Date.distantPast
    private var codesShown = 0
    private var failedInvite: GuestInvite?

    private static let codeLife: TimeInterval = 10 * 60
    private static let codesBeforeStopping = 3

    /// Flexarr's relay; `-FlexarrPairRelay <url>` points a debug build at a local one (relay/api/pair.js).
    private var relay: URL {
        #if DEBUG
        if let url = UserDefaults.standard.string(forKey: "FlexarrPairRelay").flatMap(URL.init(string:)) { return url }
        #endif
        return GuestTvCode.relay
    }

    /// Shows a code (the one showing, if it's still good) and waits for its invite, until one arrives, the codes run
    /// out, or the task is cancelled (the app going to the back cancels it; coming back runs it again).
    func run() async {
        while !Task.isCancelled, offer == nil, joining == nil, problem == nil, !noCode, !stopped {
            if code == nil || Date().timeIntervalSince(codeMade) >= Self.codeLife {
                code = nil
                guard codesShown < Self.codesBeforeStopping else {
                    stopped = true
                    return
                }
                let tv = GuestTvCode()
                do {
                    try await tv.register(relay: relay)
                } catch GuestTvCode.Failure.codeTaken {
                    continue
                } catch {
                    if Task.isCancelled { return }
                    problem = "Can’t get a code just now."
                    noCode = true
                    return
                }
                code = tv
                codeMade = Date()
                codesShown += 1
            }
            guard let tv = code else { continue }
            // Every 3 seconds while they're likely typing it, then every 10.
            let age = Date().timeIntervalSince(codeMade)
            guard (try? await Task.sleep(for: .seconds(age < 120 ? 3 : 10))) != nil else { return }
            switch try? await tv.poll(relay: relay) {
            case .waiting?, nil:
                continue
            case .expired?:
                code = nil
            case .invite(let invite)?:
                code = nil
                offer = invite
            }
        }
    }

    /// Join, after "Join Dan’s Flexarr?" (or Try again after it didn't work).
    func join(model: AppModel) async {
        guard let invite = offer ?? failedInvite else { return }
        offer = nil
        failedInvite = nil
        problem = nil
        joining = invite
        do {
            try await model.join(invite)
        } catch {
            joining = nil
            problem = error.localizedDescription
            canTryAgain = (error as? AppModel.JoinError)?.canRetry == true
            if canTryAgain { failedInvite = invite }
        }
    }

    /// Not now, New code, Show a new code, or Try again with no code: back to a fresh code.
    func newCode() {
        code = nil
        offer = nil
        failedInvite = nil
        problem = nil
        canTryAgain = false
        noCode = false
        stopped = false
        codesShown = 0
        round += 1
    }
}
