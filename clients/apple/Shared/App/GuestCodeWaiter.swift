import FlexarrKit
import Foundation
import Observation

/// "Join someone’s Flexarr": this device shows a code to read (or text) to whoever invited it, who types it into
/// Settings › Remote access. The invite comes back through Flexarr's relay, sealed so only this device can open it, and it
/// joins (see `GuestTvCode`, docs/remote-guests.md). A code lasts ten minutes; a new one replaces it. Shared by the
/// iPhone's and the Apple TV's code screens.
@MainActor
@Observable
final class GuestCodeWaiter {
    private(set) var code: GuestTvCode?
    private(set) var joining: GuestInvite?
    private(set) var problem: String?

    /// Flexarr's relay; `-FlexarrPairRelay <url>` points a debug build at a local one (relay/api/pair.js).
    private var relay: URL {
        #if DEBUG
        if let url = UserDefaults.standard.string(forKey: "FlexarrPairRelay").flatMap(URL.init(string:)) { return url }
        #endif
        return GuestTvCode.relay
    }

    /// Shows codes until an invite arrives and this device has joined with it (or the task is cancelled).
    func run(model: AppModel) async {
        while !Task.isCancelled {
            let tv = GuestTvCode()
            do {
                try await tv.register(relay: relay)
            } catch GuestTvCode.Failure.codeTaken {
                continue
            } catch {
                problem = "Can’t get a code just now. Try again in a minute."
                try? await Task.sleep(for: .seconds(10))
                continue
            }
            code = tv
            problem = nil
            // Collect the invite once it's there; a code that runs out gets a new one.
            polling: while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(3))
                switch try? await tv.poll(relay: relay) {
                case .waiting?, nil:
                    continue
                case .expired?:
                    break polling
                case .invite(let invite)?:
                    joining = invite
                    do {
                        try await model.join(invite)
                        return
                    } catch {
                        joining = nil
                        problem = error.localizedDescription
                        break polling
                    }
                }
            }
        }
    }
}
