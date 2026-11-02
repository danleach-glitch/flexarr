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
            musicUndone.remove(target.askedKey)
            return nil
        } catch {
            return ConnectionError(error).message
        }
    }

    func albumState(_ album: MusicExternal) -> MusicAlbumState {
        if let id = album.libraryItemId, album.availability == .available || album.availability == .partlyAvailable {
            return .inLibrary(id)
        }
        if musicUndone.contains(album.id) {
            return album.deezerId != 0 || album.musicBrainzId != nil ? .requestable : .none
        }
        if album.availability == .requested || musicAsked.contains(album.id) { return .requested }
        return album.isRequestable ? .requestable : .none
    }

    /// Whether Lidarr watches for the artist's new releases (or the profile just asked it to).
    func isFollowing(_ artist: MusicExternal, followed: Bool = false) -> Bool {
        if musicUndone.contains(artist.id) { return false }
        return followed || artist.availability == .requested || musicAsked.contains(artist.id)
    }

    func isFollowing(libraryArtist id: String, followed: Bool) -> Bool {
        let key = MusicRequestTarget.libraryArtist(id).askedKey
        if musicUndone.contains(key) { return false }
        return followed || musicAsked.contains(key)
    }

    /// Whether music requests are on here, for an artist's or album's page: off when there's no client or no answer.
    func musicRequestStatus() async -> MusicRequestStatus {
        guard let client, !isKids else { return .off }
        return (try? await client.musicRequestStatus()) ?? .off
    }

    /// Stops following an artist for the household, by whichever ids the page has (both are marked, so the library
    /// page and the outside page agree): nil when it went through, else why not.
    func unfollowArtist(libraryId: String?, deezerId: Int?) async -> String? {
        guard let client else { return ConnectionError(FlexarrClientError.unreachable).message }
        do {
            try await client.unfollowArtist(libraryArtistId: libraryId, deezerId: libraryId == nil ? deezerId : nil)
            var keys: [String] = []
            if let libraryId { keys.append(MusicRequestTarget.libraryArtist(libraryId).askedKey) }
            if let deezerId, deezerId != 0 { keys.append(MusicRequestTarget.artist(deezerId: deezerId).askedKey) }
            for key in keys {
                musicAsked.remove(key)
                musicUndone.insert(key)
            }
            return nil
        } catch {
            return ConnectionError(error).message
        }
    }

    /// Cancels an album asked for, for the household: nil when it went through, else why not.
    func unrequestAlbum(_ album: MusicExternal) async -> String? {
        guard let client else { return ConnectionError(FlexarrClientError.unreachable).message }
        do {
            try await client.unrequestAlbum(album)
            musicAsked.remove(album.id)
            musicUndone.insert(album.id)
            return nil
        } catch {
            return ConnectionError(error).message
        }
    }
}

extension EnvironmentValues {
    /// Lidarr isn't answering (`MusicRequestStatus.reachable` is false): requests and follows wait, their buttons off.
    @Entry var musicRequestsPaused = false
}

/// The one line said when Lidarr isn't answering.
enum MusicWords {
    static let lidarrNotAnswering = "Lidarr isn\u{2019}t answering."

    /// The questions asked before taking music back, the web's words.
    static func stopFollowing(_ artist: String) -> String { "Stop following \(artist)?" }
    static func unrequest(_ album: String) -> String { "Unrequest \(album)?" }
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
