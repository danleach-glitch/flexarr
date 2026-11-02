import FlexarrKit
import SwiftUI

/// Requesting films and shows through Seerr, and taking them back, as music's requests work.
enum TitleRequesting {
    /// The key `AppModel.titlesAsked`/`titlesUndone` keep a title (or some of a show's seasons) under.
    static func key(mediaType: String, tmdbId: Int, seasons: [Int]? = nil) -> String {
        let base = "\(mediaType)-\(tmdbId)"
        guard let seasons, !seasons.isEmpty else { return base }
        return base + "-s" + seasons.sorted().map(String.init).joined(separator: ",")
    }
}

/// How a request turned out, for the button to say beside itself.
enum TitleRequestOutcome: Equatable {
    case sent
    /// Seerr (or the server) can't take it: Can't request, with why. Asking again won't change that.
    case refused(String)
    /// It didn't go through this time: Try again.
    case failed(String)
}

extension AppModel {
    /// Where a title (or some of a show's seasons) stands for its button, with what was done here since.
    func titleState(mediaType: String, tmdbId: Int, availability: Availability, inLibrary: Bool = false, seasons: [Int]? = nil) -> TitleRequestState {
        let key = TitleRequesting.key(mediaType: mediaType, tmdbId: tmdbId, seasons: seasons)
        return .of(availability, inLibrary: inLibrary, asked: titlesAsked.contains(key), undone: titlesUndone.contains(key))
    }

    func titleState(_ title: ExternalTitle) -> TitleRequestState {
        titleState(mediaType: title.mediaType, tmdbId: title.tmdbId, availability: title.availability, inLibrary: title.libraryItemId != nil)
    }

    /// Requests a film or show (or just some of a show's seasons).
    func requestTitle(mediaType: String, tmdbId: Int, seasons: [Int]? = nil) async -> TitleRequestOutcome {
        guard let client else { return .failed(ConnectionError(FlexarrClientError.unreachable).message) }
        do {
            if let seasons, !seasons.isEmpty {
                try await client.requestSeasons(tmdbId: tmdbId, seasons: seasons)
            } else {
                try await client.request(mediaType: mediaType, tmdbId: tmdbId)
            }
            let key = TitleRequesting.key(mediaType: mediaType, tmdbId: tmdbId, seasons: seasons)
            titlesAsked.insert(key)
            titlesUndone.remove(key)
            return .sent
        } catch {
            let message = ConnectionError(error).message
            return TitleRequestState.isRefusal(error) ? .refused(message) : .failed(message)
        }
    }

    /// Takes a film or show request back (or just some of a show's seasons), for the household: nil when it went
    /// through, else why not.
    func unrequestTitle(mediaType: String, tmdbId: Int, seasons: [Int]? = nil) async -> String? {
        guard let client else { return ConnectionError(FlexarrClientError.unreachable).message }
        do {
            try await client.unrequest(mediaType: mediaType, tmdbId: tmdbId, seasons: seasons)
            let key = TitleRequesting.key(mediaType: mediaType, tmdbId: tmdbId, seasons: seasons)
            titlesAsked.remove(key)
            titlesUndone.insert(key)
            return nil
        } catch {
            return TitleRequestState.needsNewerServer(error) ? MusicWords.updateServerToUndo : ConnectionError(error).message
        }
    }

    /// Whether Seerr is set up and answering. An answer is kept for a minute, as music's is; Seerr not answering only
    /// ten seconds, so a page opened once it's back isn't still greyed.
    func titleRequestStatus(fresh: Bool = false) async -> RequestStatus {
        guard let client, !isKids else { return .off }
        if !fresh, let cached = titleStatusCache,
           cached.at.timeIntervalSinceNow > (cached.status.configured && !cached.status.reachable ? -10 : -60) {
            return cached.status
        }
        guard let status = try? await client.requestStatus() else { return .off }
        titleStatusCache = (status, Date())
        return status
    }
}

/// What film and show request buttons say, everywhere (buttons, menus, VoiceOver): music's words.
enum TitleWords {
    static let request = "Request"
    static let requested = MusicWords.requested
    static let unrequestAction = MusicWords.unrequestAction
    static let unrequestHint = MusicWords.unrequestHint
    static let keepIt = MusicWords.keepIt
    static let cantRequest = "Can\u{2019}t request"
    static let tryAgain = "Try again"
    static let seerrNotAnswering = "Seerr isn\u{2019}t answering."

    /// The question asked before taking a request back.
    static func unrequest(_ name: String) -> String { MusicWords.unrequest(name) }
}
