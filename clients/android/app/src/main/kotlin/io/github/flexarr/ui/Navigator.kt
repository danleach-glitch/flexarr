package io.github.flexarr.ui

import android.content.Context
import android.net.Uri
import androidx.navigation.NavHostController
import androidx.compose.material3.SnackbarHostState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import io.github.flexarr.ui.music.PlaylistAddition
import io.github.flexarr.AppModel
import io.github.flexarr.core.FlexarrClient
import io.github.flexarr.core.ItemSummary
import io.github.flexarr.core.Library
import io.github.flexarr.core.LibrarySort
import io.github.flexarr.core.LiveChannel
import io.github.flexarr.player.LivePlayerActivity
import io.github.flexarr.core.LibraryType
import io.github.flexarr.core.MediaKind
import io.github.flexarr.core.MusicExternal
import io.github.flexarr.core.asPodcastEpisode
import io.github.flexarr.core.albumStart
import io.github.flexarr.core.genreTracks
import io.github.flexarr.core.mix
import io.github.flexarr.core.radio
import io.github.flexarr.core.tracks
import io.github.flexarr.core.setBookFinished
import io.github.flexarr.usesTvInterface
import io.github.flexarr.player.PlayerActivity
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.launch

/**
 * What any screen or card can do: open a title or library, play, and change what's watched. The phone's version of
 * the iPhone app's router and the playing and watched parts of its model.
 */
class Navigator(private val nav: NavHostController, private val model: AppModel, private val context: Context) {
    fun openItem(id: String) = nav.navigate("item/$id")

    /**
     * A title as a card knows it: a podcast opens its own page, an artist or album theirs (a track its album), and
     * everything else the title page.
     */
    fun open(item: ItemSummary) = when (item.kind) {
        MediaKind.Podcast -> openPodcast(item.id)
        MediaKind.Artist -> nav.navigate("artist/${item.id}")
        MediaKind.Album -> nav.navigate("album/${item.id}")
        MediaKind.Track -> item.parentId?.let { nav.navigate("album/$it") } ?: Unit
        // The TV has no book screens yet: its title page says so rather than showing a film.
        MediaKind.Audiobook -> if (context.usesTvInterface) openItem(item.id) else openBook(item.id)
        else -> openItem(item.id)
    }

    /** A library artist's page, by its id here (an outside artist's In your library). */
    fun openLibraryArtist(id: String) = nav.navigate("artist/$id")

    /** An artist from outside the library, by Deezer id. */
    fun openMusicArtist(deezerId: Long) = nav.navigate("music-artist/$deezerId")

    /** An album from outside the library, by Deezer id. */
    fun openMusicAlbum(deezerId: Long) = nav.navigate("music-album/$deezerId")

    /**
     * An artist or album from outside the library: one the household has opens as itself, the rest their outside
     * page. An album only Lidarr knows (no Deezer id) has no page to open.
     */
    fun openMusic(item: MusicExternal) {
        val id = item.libraryItemId
        when {
            id != null -> nav.navigate(if (item.isArtist) "artist/$id" else "album/$id")
            item.deezerId == 0L -> Unit
            item.isArtist -> openMusicArtist(item.deezerId)
            else -> openMusicAlbum(item.deezerId)
        }
    }

    fun openBook(id: String) = nav.navigate("book/$id")

    fun openBookSeries(id: String) = nav.navigate("book-series/$id")

    /**
     * Plays a book from where this profile left it, or from [from] on its timeline (a chapter, a bookmark), and opens
     * its player.
     */
    fun playBook(item: ItemSummary, from: Double? = null) {
        model.books.play(item, from)
        model.showsBookPlayer = true
    }

    /** Plays a podcast episode (a title as Home's Continue watching lists it) from where it was left, and opens Now Playing. */
    fun playEpisode(item: ItemSummary) {
        model.podcasts.play(item.asPodcastEpisode())
        model.showsNowPlaying = true
    }

    /**
     * Plays one of today's music mixes from its first track and opens Now Playing. A mix that can't be fetched (or is
     * yesterday's, which the server no longer has) says so instead.
     */
    fun playMix(id: String) {
        val client = model.client ?: return
        model.scope.launch {
            try {
                val tracks = client.mix(id).tracks
                if (tracks.isEmpty()) throw IllegalStateException("There's nothing in it today.")
                model.music.play(tracks)
                // The TV shows Now Playing over everything when asked; the phone opens its sheet.
                if (context.usesTvInterface) model.nowPlayingRequests++ else model.showsMusicPlayer = true
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                tell("Couldn’t play this mix", e)
            }
        }
    }

    /**
     * An album from a music library's hero: from [resume] (the track it carries on from, part way in when that was left
     * half way), or from the start.
     */
    fun playAlbum(album: ItemSummary, resume: ItemSummary?) = playMusic("Couldn’t play this album") { client ->
        val tracks = client.tracks(album.id)
        val from = albumStart(tracks, resume)
        model.music.play(tracks, start = from.index, albums = listOf(album), at = from.position)
        tracks
    }

    /** An artist's whole catalogue shuffled, or their [radio] (them and artists like them). */
    fun playArtist(artist: ItemSummary, radio: Boolean) = playMusic(if (radio) "Couldn’t start this radio" else "Couldn’t play this artist") { client ->
        val tracks = if (radio) client.radio(artist.id) else client.tracks(artist.id)
        model.music.play(tracks, shuffled = !radio)
        tracks
    }

    /** A genre's tracks in a music library, in order or shuffled (the server shuffles them). */
    fun playGenre(libraryId: String, genre: String, shuffled: Boolean) = playMusic("Couldn’t play this genre") { client ->
        client.genreTracks(libraryId, genre, shuffled).also { model.music.play(it) }
    }

    /** Starts music fetched by [start] (which hands back the tracks) and opens Now Playing; nothing to play does nothing. */
    private fun playMusic(failure: String, start: suspend (FlexarrClient) -> List<ItemSummary>) {
        val client = model.client ?: return
        model.scope.launch {
            try {
                if (start(client).isEmpty()) return@launch
                // The TV shows Now Playing over everything when asked; the phone opens its sheet.
                if (context.usesTvInterface) model.nowPlayingRequests++ else model.showsMusicPlayer = true
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                tell(failure, e)
            }
        }
    }

    /** Marks a book finished, or unread (no place, not finished); Continue listening follows. */
    fun setBookFinished(item: ItemSummary, finished: Boolean) =
        changeWatchState(if (finished) "Couldn’t mark this as finished" else "Couldn’t mark this as unread") { it.setBookFinished(item.id, finished) }

    /** Downloads a whole book, saying so if it didn't work. */
    fun downloadBook(item: ItemSummary) {
        model.scope.launch {
            try {
                model.bookDownloads.download(item)
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                tell("Couldn’t download this", e)
            }
        }
    }

    /**
     * A library from the Libraries tab: music has its own screen (artists, new albums, playlists), the rest a poster
     * grid. A type this app doesn't know opens nothing (it isn't listed either).
     */
    fun openLibrary(library: Library) = when (library.type) {
        LibraryType.Music -> nav.navigate("music/${library.id}")
        LibraryType.Audiobooks -> nav.navigate("books/${library.id}?view=")
        LibraryType.Unknown -> Unit
        else -> openLibrary(library.id)
    }

    /** An audiobooks library's books in one genre, on a screen of their own (Back goes to the genres). */
    fun openBookGenre(libraryId: String, genre: String) = nav.navigate("books/$libraryId?view=&genre=${Uri.encode(genre)}")

    fun openPlaylist(id: String) = nav.navigate("playlist/$id")

    /** A music library's albums in one genre, with Play and Shuffle for its tracks, on top (Back goes to the genres). */
    fun openMusicGenre(libraryId: String, genre: String) =
        if (context.usesTvInterface) openLibrary(libraryId, LibrarySort.Title, genre) else nav.navigate("music/$libraryId?genre=${Uri.encode(genre)}")

    /**
     * A library's poster grid; with [genre], just its titles in that genre (from a Genres tile or a genre row). On the
     * TV, a music library's is its artists, or its albums with [albums] or a genre.
     */
    fun openLibrary(id: String, sort: LibrarySort = LibrarySort.Title, genre: String? = null, albums: Boolean = false) =
        nav.navigate("library/$id?sort=${sort.raw}" + genre?.let { "&genre=${Uri.encode(it)}" }.orEmpty() + (if (albums) "&view=albums" else ""))

    /** A music library's artists, or its [albums], as a grid on a screen of its own (Back goes to where it was opened). */
    fun openMusicGrid(libraryId: String, albums: Boolean, sort: LibrarySort = LibrarySort.Title) =
        if (context.usesTvInterface) {
            openLibrary(libraryId, sort, albums = albums)
        } else {
            nav.navigate("music/$libraryId?view=${if (albums) "albums" else "artists"}&sort=${sort.raw}")
        }

    fun openPerson(id: String, name: String) = nav.navigate("person/$id?name=${Uri.encode(name)}")

    fun openPodcasts() = nav.navigate("podcasts")

    fun openPodcast(id: String) = nav.navigate("podcast/$id")

    fun openPodcastBrowse() = nav.navigate("podcasts/browse")

    fun openLiveTv() = nav.navigate("livetv")

    fun openWatchlist() = nav.navigate("watchlist")

    fun openComingSoon() = nav.navigate("coming-soon")

    fun openRecordings() = nav.navigate("livetv/recordings")

    /** Watches a live channel; Previous and Next go through [order], as the screen listed them. */
    fun watch(channel: LiveChannel, order: List<LiveChannel>) {
        context.startActivity(LivePlayerActivity.intent(context, channel, order))
    }

    fun back() = nav.popBackStack()

    /** Plays a film or episode from where it was left (or from the beginning). */
    /** [from]: the page Play was pressed on ([io.github.flexarr.player.LocalPlayFrom]), so it can move on afterwards. */
    fun play(item: ItemSummary, fromBeginning: Boolean = false, from: String? = null) = play(item.id, fromBeginning, from = from)

    fun play(itemId: String, fromBeginning: Boolean = false, shuffle: Boolean = false, startAt: Int? = null, from: String? = null) {
        context.startActivity(PlayerActivity.intent(context, itemId, fromBeginning, shuffle, startAt, from))
    }

    /** A film plays; a show or season plays the episode it's up to, or opens when everything's watched. */
    fun playBest(item: ItemSummary) {
        if (item.kind == MediaKind.Audiobook) return if (context.usesTvInterface) openItem(item.id) else playBook(item)
        if (item.kind.isPlayable) return play(item)
        val client = model.client ?: return
        model.scope.launch {
            val next = runCatching { client.nextUp(item.id) }.getOrNull()
            if (next != null) play(next) else openItem(item.id)
        }
    }

    /** A random episode of the show, then random ones after it. */
    fun shuffle(item: ItemSummary) = play(item.id, shuffle = true)

    /**
     * Something asked for from a card's menu that didn't work, said in a bar along the bottom (MainScreen shows it)
     * rather than nothing happening.
     */
    val notices = SnackbarHostState()

    /** What the Add to playlist sheet is open for (phones only), shown over every screen. */
    var addingToPlaylist by mutableStateOf<PlaylistAddition?>(null)

    fun setWatched(item: ItemSummary, watched: Boolean) =
        changeWatchState(if (watched) "Couldn’t mark this as watched" else "Couldn’t mark this as unwatched") {
            it.setWatched(item.id, watched)
            if (watched) model.downloads.markedPlayed(item.id)
        }

    /** The show of an episode, all of it. */
    fun setShowWatched(episode: ItemSummary) {
        val show = episode.seriesId ?: return
        changeWatchState("Couldn’t mark the show as watched") { it.setWatched(show, true) }
    }

    /**
     * Takes a title out of Continue watching. A book or episode loaded in its player stops there first, without
     * sending its place, so a later report can't put it straight back.
     */
    fun removeFromContinueWatching(item: ItemSummary) {
        if (model.books.current?.id == item.id) model.books.stop(reportsPlace = false)
        if (model.podcasts.current?.id == item.id) model.podcasts.stop(reportsPlace = false)
        changeWatchState("Couldn’t remove this from Continue watching") { it.removeFromContinueWatching(item.id) }
    }

    /** A film or episode downloaded at Settings' size. */
    fun download(item: ItemSummary) {
        model.scope.launch {
            try {
                model.downloads.download(item)
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                tell("Couldn’t download this", e)
            }
        }
    }

    /**
     * Puts a film or show on the watchlist, or takes it off, saying so if that didn't work ([onFailed] puts back what
     * the screen showed).
     */
    fun setOnWatchlist(item: ItemSummary, on: Boolean, onFailed: () -> Unit = {}) {
        model.scope.launch {
            try {
                model.setOnWatchlist(item.id, on)
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                onFailed()
                tell(if (on) "Couldn’t add this to your watchlist" else "Couldn’t remove this from your watchlist", e)
            }
        }
    }

    /** Runs a change from the Watchlist screen, saying [failure] if it didn't work. */
    fun watchlistChange(failure: String, change: suspend () -> Unit) {
        model.scope.launch {
            try {
                change()
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                tell(failure, e)
            }
        }
    }

    private fun changeWatchState(failure: String, change: suspend (FlexarrClient) -> Unit) {
        val client = model.client ?: return
        model.scope.launch {
            try {
                change(client)
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                tell(failure, e)
            }
            model.watchStateChanged()
        }
    }

    /** Says [failure] in a snackbar, with the reason when there is one. */
    fun tellFailure(failure: String, error: Exception) = tell(failure, error)

    /** "Couldn’t download this. The server can’t be reached." */
    private fun tell(failure: String, error: Exception) {
        val reason = error.message?.takeIf { it.isNotBlank() }
        model.scope.launch { notices.showSnackbar(if (reason != null) "$failure. $reason" else "$failure.") }
    }

    /**
     * A Home row's link, "/library/{id}?sort=added" or "/watchlist", as a place to open; null for anything else. A music
     * library's ([music]: the row is of albums, artists or tracks) opens its albums with "view=albums" or a genre, and
     * its artists otherwise. The TV's library screen tells music apart itself.
     */
    fun libraryLink(link: String?, music: Boolean = false): (() -> Unit)? {
        val uri = link?.let(Uri::parse) ?: return null
        val segments = uri.pathSegments
        if (segments == listOf("watchlist")) return { openWatchlist() }
        if (segments.size != 2 || segments[0] != "library") return null
        // An audiobooks library's rows open its own screen: "?view=all&sort=added" its every book, newest first.
        if (model.audiobookLibraries.any { it.id == segments[1] }) {
            val all = uri.getQueryParameter("view") == "all"
            return { nav.navigate("books/${segments[1]}?view=${if (all) uri.getQueryParameter("sort").orEmpty().ifEmpty { "title" } else ""}") }
        }
        val (id, sort, genre, albums) = LibraryLink.parse(uri) ?: return null
        if (context.usesTvInterface) return { openLibrary(id, sort, genre, albums) }
        if (!music && !albums) return { openLibrary(id, sort, genre) }
        return if (genre != null) ({ openMusicGenre(id, genre) }) else ({ openMusicGrid(id, albums, sort) })
    }
}

/**
 * A row's link into a library: "/library/{id}" itself, or "?sort=added" (Home's rows) and "?view=all&sort=added" or
 * "&genre=Drama" (a library's Browse rows), which open its titles that way rather than its Browse. A music library's
 * "?view=albums&sort=added" ([albums]) is its albums, newest added first.
 */
internal data class LibraryLink(val id: String, val sort: LibrarySort, val genre: String?, val albums: Boolean = false) {
    companion object {
        fun parse(uri: Uri): LibraryLink? {
            val segments = uri.pathSegments
            if (segments.size != 2 || segments[0] != "library") return null
            val sort = LibrarySort.entries.firstOrNull { it.raw == uri.getQueryParameter("sort") } ?: LibrarySort.Title
            return LibraryLink(segments[1], sort, uri.getQueryParameter("genre")?.takeIf { it.isNotEmpty() }, albums = uri.getQueryParameter("view") == "albums")
        }
    }
}
