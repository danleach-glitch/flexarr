package io.github.flexarr.ui

import android.content.Context
import android.net.Uri
import androidx.navigation.NavHostController
import androidx.compose.material3.SnackbarHostState
import io.github.flexarr.AppModel
import io.github.flexarr.core.FlexarrClient
import io.github.flexarr.core.ItemSummary
import io.github.flexarr.core.Library
import io.github.flexarr.core.LibrarySort
import io.github.flexarr.core.LiveChannel
import io.github.flexarr.player.LivePlayerActivity
import io.github.flexarr.core.LibraryType
import io.github.flexarr.core.MediaKind
import io.github.flexarr.core.asPodcastEpisode
import io.github.flexarr.core.mix
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

    /** A library's poster grid; with [genre], just its titles in that genre (from a Genres tile or a genre row). */
    fun openLibrary(id: String, sort: LibrarySort = LibrarySort.Title, genre: String? = null) =
        nav.navigate("library/$id?sort=${sort.raw}" + genre?.let { "&genre=${Uri.encode(it)}" }.orEmpty())

    fun openPerson(id: String, name: String) = nav.navigate("person/$id?name=${Uri.encode(name)}")

    fun openPodcasts() = nav.navigate("podcasts")

    fun openPodcast(id: String) = nav.navigate("podcast/$id")

    fun openPodcastBrowse() = nav.navigate("podcasts/browse")

    fun openLiveTv() = nav.navigate("livetv")

    fun openWatchlist() = nav.navigate("watchlist")

    fun openRecordings() = nav.navigate("livetv/recordings")

    /** Watches a live channel; Previous and Next go through [order], as the screen listed them. */
    fun watch(channel: LiveChannel, order: List<LiveChannel>) {
        context.startActivity(LivePlayerActivity.intent(context, channel, order))
    }

    fun back() = nav.popBackStack()

    /** Plays a film or episode from where it was left (or from the beginning). */
    fun play(item: ItemSummary, fromBeginning: Boolean = false) = play(item.id, fromBeginning)

    fun play(itemId: String, fromBeginning: Boolean = false, shuffle: Boolean = false, startAt: Int? = null) {
        context.startActivity(PlayerActivity.intent(context, itemId, fromBeginning, shuffle, startAt))
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
        changeWatchState("Couldn’t remove this from Continue Watching") { it.removeFromContinueWatching(item.id) }
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

    /** A Home row's link, "/library/{id}?sort=added" or "/watchlist", as a place to open; null for anything else. */
    fun libraryLink(link: String?): (() -> Unit)? {
        val uri = link?.let(Uri::parse) ?: return null
        val segments = uri.pathSegments
        if (segments == listOf("watchlist")) return { openWatchlist() }
        if (segments.size != 2 || segments[0] != "library") return null
        // An audiobooks library's rows open its own screen: "?view=all&sort=added" its every book, newest first.
        if (model.audiobookLibraries.any { it.id == segments[1] }) {
            val all = uri.getQueryParameter("view") == "all"
            return { nav.navigate("books/${segments[1]}?view=${if (all) uri.getQueryParameter("sort").orEmpty().ifEmpty { "title" } else ""}") }
        }
        val sort = LibrarySort.entries.firstOrNull { it.raw == uri.getQueryParameter("sort") } ?: LibrarySort.Title
        val genre = uri.getQueryParameter("genre")?.takeIf { it.isNotEmpty() }
        return { openLibrary(segments[1], sort, genre) }
    }
}
