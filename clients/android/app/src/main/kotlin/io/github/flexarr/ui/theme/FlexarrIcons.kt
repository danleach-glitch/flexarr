// Generated from design/tokens.json by `make tokens`. Don't edit; change the JSON.

package io.github.flexarr.ui.theme

import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Bookmark
import androidx.compose.material.icons.filled.BookmarkBorder
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.filled.LocalMovies
import androidx.compose.material.icons.filled.MoreHoriz
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Replay
import androidx.compose.material.icons.filled.Shuffle
import androidx.compose.material.icons.outlined.ArrowCircleDown
import androidx.compose.material.icons.outlined.AutoAwesome
import androidx.compose.material.icons.outlined.CalendarMonth
import androidx.compose.material.icons.outlined.Folder
import androidx.compose.material.icons.outlined.GraphicEq
import androidx.compose.material.icons.outlined.GridView
import androidx.compose.material.icons.outlined.Home
import androidx.compose.material.icons.outlined.ImportContacts
import androidx.compose.material.icons.outlined.LocalMovies
import androidx.compose.material.icons.outlined.MicNone
import androidx.compose.material.icons.outlined.MusicNote
import androidx.compose.material.icons.outlined.Search
import androidx.compose.material.icons.outlined.Settings
import androidx.compose.material.icons.outlined.SettingsInputAntenna
import androidx.compose.material.icons.outlined.SpeakerNotes
import androidx.compose.material.icons.outlined.Tv
import androidx.compose.material.icons.outlined.VideoLibrary
import androidx.compose.material.icons.outlined.ViewDay
import androidx.compose.ui.graphics.vector.ImageVector

/** The icon for each action and place, the same idea on every client (design/tokens.json › icons). */
object FlexarrIcons {
    /** Play or Resume. */
    val play: ImageVector get() = Icons.Filled.PlayArrow
    /** Start over. */
    val startOver: ImageVector get() = Icons.Filled.Replay
    /** Shuffle a show. */
    val shuffle: ImageVector get() = Icons.Filled.Shuffle
    /** Trailer. */
    val trailer: ImageVector get() = Icons.Filled.LocalMovies
    /** Mark as watched. */
    val watched: ImageVector get() = Icons.Filled.Check
    /** Already watched (the Watched button pressed). */
    val isWatched: ImageVector get() = Icons.Filled.CheckCircle
    /** Mark as unwatched. */
    val markUnwatched: ImageVector get() = Icons.Filled.CheckCircle
    /** Add to watchlist. */
    val watchlist: ImageVector get() = Icons.Filled.BookmarkBorder
    /** On the watchlist. */
    val onWatchlist: ImageVector get() = Icons.Filled.Bookmark
    /** Download. */
    val download: ImageVector get() = Icons.Outlined.ArrowCircleDown
    /** Search for subtitles. */
    val subtitles: ImageVector get() = Icons.Outlined.SpeakerNotes
    /** Go to the show. */
    val goToShow: ImageVector get() = Icons.Outlined.VideoLibrary
    /** More actions. */
    val more: ImageVector get() = Icons.Filled.MoreHoriz
    /** Home. */
    val home: ImageVector get() = Icons.Outlined.Home
    /** Libraries. */
    val libraries: ImageVector get() = Icons.Outlined.GridView
    /** Search. */
    val search: ImageVector get() = Icons.Outlined.Search
    /** Settings. */
    val settings: ImageVector get() = Icons.Outlined.Settings
    /** Downloads. */
    val downloads: ImageVector get() = Icons.Outlined.ArrowCircleDown
    /** Discover. */
    val discover: ImageVector get() = Icons.Outlined.AutoAwesome
    /** Coming soon. */
    val comingSoon: ImageVector get() = Icons.Outlined.CalendarMonth
    /** Live TV. */
    val liveTv: ImageVector get() = Icons.Outlined.SettingsInputAntenna
    /** Collections. */
    val collections: ImageVector get() = Icons.Outlined.ViewDay
    /** Now playing. */
    val nowPlaying: ImageVector get() = Icons.Outlined.GraphicEq
    /** A movie library. */
    val movies: ImageVector get() = Icons.Outlined.LocalMovies
    /** A TV library. */
    val shows: ImageVector get() = Icons.Outlined.Tv
    /** A music library. */
    val music: ImageVector get() = Icons.Outlined.MusicNote
    /** Podcasts. */
    val podcasts: ImageVector get() = Icons.Outlined.MicNone
    /** An audiobooks library. */
    val audiobooks: ImageVector get() = Icons.Outlined.ImportContacts
    /** A home videos library. */
    val homeVideos: ImageVector get() = Icons.Outlined.Folder
}
