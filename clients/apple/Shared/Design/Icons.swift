// Generated from design/tokens.json by `make tokens`. Don't edit; change the JSON.

/// The SF Symbol for each action and place, the same idea on every client (design/tokens.json › icons).
enum Symbol {
    /// Play or Resume.
    static let play = "play.fill"
    /// Start over.
    static let startOver = "arrow.counterclockwise"
    /// Shuffle a show.
    static let shuffle = "shuffle"
    /// Trailer.
    static let trailer = "film"
    /// Mark as watched.
    static let watched = "checkmark"
    /// Already watched (the Watched button pressed).
    static let isWatched = "checkmark.circle.fill"
    /// Mark as unwatched.
    static let markUnwatched = "eye.slash"
    /// Add to watchlist.
    static let watchlist = "bookmark"
    /// On the watchlist.
    static let onWatchlist = "bookmark.fill"
    /// Download.
    static let download = "arrow.down.circle"
    /// Search for subtitles.
    static let subtitles = "captions.bubble"
    /// Go to the show.
    static let goToShow = "rectangle.stack"
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
    static let all = [play, startOver, shuffle, trailer, watched, isWatched, markUnwatched, watchlist, onWatchlist, download, subtitles, goToShow, more, home, libraries, search, settings, downloads, discover, comingSoon, liveTv, collections, nowPlaying, movies, shows, music, podcasts, audiobooks, homeVideos]
}
