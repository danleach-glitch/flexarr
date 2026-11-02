import FlexarrKit
import SwiftUI

/// Where an outside album stands for its button: ask for it, asked, or here already.
enum MusicAlbumState {
    case requestable
    case requested
    /// In the library: the button opens it.
    case inLibrary(String)
    /// Nothing to offer (a state from a newer server, or nothing to ask Lidarr by).
    case none
}

extension MusicRequestTarget {
    /// The key `AppModel.musicAsked` keeps it under: the same as the card's `MusicExternal.id`.
    var askedKey: String {
        switch self {
        case .album(let id): "album-\(id)"
        case .albumByMusicBrainz(let mbid): "album-\(mbid)"
        case .artist(let id): "artist-\(id)"
        case .libraryArtist(let id): "library-artist-\(id)"
        }
    }
}

extension AppModel {
    /// Requests an album or follows an artist. False, with `requestError` set for the alert, when it didn't go through.
    @discardableResult
    func requestMusic(_ target: MusicRequestTarget) async -> Bool {
        guard let failure = await sendMusicRequest(target) else { return true }
        requestError = failure
        return false
    }

    /// Requests an album or follows an artist, leaving the alert alone: nil when it went through, else why not, for
    /// the button to say beside itself.
    func sendMusicRequest(_ target: MusicRequestTarget) async -> String? {
        guard let client else { return ConnectionError(FlexarrClientError.unreachable).message }
        do {
            try await client.requestMusic(target)
            musicAsked.insert(target.askedKey)
            return nil
        } catch {
            return ConnectionError(error).message
        }
    }

    func albumState(_ album: MusicExternal) -> MusicAlbumState {
        if let id = album.libraryItemId, album.availability == .available || album.availability == .partlyAvailable {
            return .inLibrary(id)
        }
        if album.availability == .requested || musicAsked.contains(album.id) { return .requested }
        return album.isRequestable ? .requestable : .none
    }

    /// Whether Lidarr watches for the artist's new releases (or the profile just asked it to).
    func isFollowing(_ artist: MusicExternal, followed: Bool = false) -> Bool {
        followed || artist.availability == .requested || musicAsked.contains(artist.id)
    }

    func isFollowing(libraryArtist id: String, followed: Bool) -> Bool {
        followed || musicAsked.contains(MusicRequestTarget.libraryArtist(id).askedKey)
    }
}

extension EnvironmentValues {
    /// Lidarr isn't answering (`MusicRequestStatus.reachable` is false): requests and follows wait, their buttons off.
    @Entry var musicRequestsPaused = false
}

/// The one line said when Lidarr isn't answering.
enum MusicWords {
    static let lidarrNotAnswering = "Lidarr isn\u{2019}t answering."
}

extension MusicExternal {
    /// What VoiceOver reads for its card: the name, or the album and its artist.
    var accessibilityName: String {
        if isAlbum, let artist { return "\(title), \(artist)" }
        return title
    }

    /// Where tapping it goes: the library's own page when the household has it, else its outside page.
    var route: Route? {
        if let id = libraryItemId, availability == .available || availability == .partlyAvailable {
            return .musicItem(id: id, isArtist: isArtist)
        }
        guard hasPage else { return nil }
        return isArtist ? .musicArtist(self) : .musicAlbum(self)
    }

    /// An album's artist, as an outside artist to open.
    var artistPage: MusicExternal? {
        guard let id = artistDeezerId, id != 0, let artist else { return nil }
        return MusicExternal(kind: "artist", deezerId: id, title: artist)
    }
}
