// Generated from design/tokens.json by `make tokens`. Don't edit; change the JSON.

/// The SF Symbol for each action and place, the same idea on every client (design/tokens.json › icons).
enum Symbol {
    /// Play or Resume.
    static let play = "play.fill"
    /// Pause.
    static let pause = "pause.fill"
    /// Start over.
    static let startOver = "arrow.counterclockwise"
    /// Shuffle a show, an artist or a genre.
    static let shuffle = "shuffle"
    /// Artist radio.
    static let radio = "dot.radiowaves.left.and.right"
    /// Trailer.
    static let trailer = "film"
    /// Mark as watched.
    static let watched = "checkmark"
    /// Already watched (the Watched button pressed).
    static let isWatched = "checkmark.circle.fill"
    /// Mark as unwatched.
    static let markUnwatched = "checkmark.circle.fill"
    /// Add to watchlist.
    static let watchlist = "bookmark"
    /// On the watchlist.
    static let onWatchlist = "bookmark.fill"
    /// Remove from the watchlist.
    static let offWatchlist = "bookmark.slash"
    /// Request a title or an album.
    static let request = "plus"
    /// Asked for, on its way.
    static let requested = "clock"
    /// Follow an artist's new releases.
    static let follow = "person.badge.plus"
    /// Following an artist.
    static let following = "checkmark"
    /// Go to the artist.
    static let goToArtist = "music.mic"
    /// Play next, after what's playing.
    static let playNext = "text.line.first.and.arrowtriangle.forward"
    /// Add to the end of the queue.
    static let addToQueue = "text.line.last.and.arrowtriangle.forward"
    /// Add to a playlist.
    static let addToPlaylist = "text.badge.plus"
    /// Download.
    static let download = "arrow.down.circle"
    /// Delete a title, album, artist or book.
    static let delete = "trash"
    /// Explicit (a badge on a track or episode).
    static let explicit = "e.square.fill"
    /// Search for subtitles.
    static let subtitles = "captions.bubble"
    /// Go to the show.
    static let goToShow = "play.rectangle.on.rectangle"
    /// Go to a title's page (details, episode, book, podcast).
    static let details = "info.circle"
    /// Take something out of Continue watching or Up next.
    static let removeFromRow = "minus.circle"
    /// Refresh the Live TV guide.
    static let refresh = "arrow.clockwise"
    /// More actions.
    static let more = "ellipsis"
    /// Home.
    static let home = "house"
    /// Libraries.
    static let libraries = "square.grid.2x2"
    /// Search.
    static let search = "magnifyingglass"
    /// Settings.
    static let settings = "gearshape"
    /// Downloads.
    static let downloads = "arrow.down.circle"
    /// Discover.
    static let discover = "sparkles"
    /// Coming soon.
    static let comingSoon = "calendar"
    /// Live TV.
    static let liveTv = "antenna.radiowaves.left.and.right"
    /// Collections.
    static let collections = "rectangle.stack"
    /// Now playing.
    static let nowPlaying = "waveform"
    /// A movie library.
    static let movies = "film"
    /// A TV library.
    static let shows = "tv"
    /// A music library.
    static let music = "music.note"
    /// Podcasts.
    static let podcasts = "mic"
    /// An audiobooks library.
    static let audiobooks = "book"
    /// A home videos library.
    static let homeVideos = "folder"

    /// Every name above, so a debug build can check each one exists on this system.
    static let all = [play, pause, startOver, shuffle, radio, trailer, watched, isWatched, markUnwatched, watchlist, onWatchlist, offWatchlist, request, requested, follow, following, goToArtist, playNext, addToQueue, addToPlaylist, download, delete, explicit, subtitles, goToShow, details, removeFromRow, refresh, more, home, libraries, search, settings, downloads, discover, comingSoon, liveTv, collections, nowPlaying, movies, shows, music, podcasts, audiobooks, homeVideos]
}
