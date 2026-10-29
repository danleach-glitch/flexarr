package io.github.flexarr.ui

import android.content.Context
import android.net.Uri
import androidx.navigation.NavHostController
import io.github.flexarr.AppModel
import io.github.flexarr.core.ItemSummary
import io.github.flexarr.core.Library
import io.github.flexarr.core.LibrarySort
import io.github.flexarr.core.LibraryType
import io.github.flexarr.core.MediaKind
import io.github.flexarr.player.PlayerActivity
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
        else -> openItem(item.id)
    }

    /** A library from the Libraries tab: music has its own screen (artists, new albums, playlists), the rest a poster grid. */
    fun openLibrary(library: Library) = if (library.type == LibraryType.Music) nav.navigate("music/${library.id}") else openLibrary(library.id)

    fun openPlaylist(id: String) = nav.navigate("playlist/$id")

    fun openLibrary(id: String, sort: LibrarySort = LibrarySort.Title) = nav.navigate("library/$id?sort=${sort.raw}")

    fun openPerson(id: String, name: String) = nav.navigate("person/$id?name=${Uri.encode(name)}")

    fun openPodcasts() = nav.navigate("podcasts")

    fun openPodcast(id: String) = nav.navigate("podcast/$id")

    fun openPodcastBrowse() = nav.navigate("podcasts/browse")

    fun back() = nav.popBackStack()

    /** Plays a film or episode from where it was left (or from the beginning). */
    fun play(item: ItemSummary, fromBeginning: Boolean = false) = play(item.id, fromBeginning)

    fun play(itemId: String, fromBeginning: Boolean = false, shuffle: Boolean = false) {
        context.startActivity(PlayerActivity.intent(context, itemId, fromBeginning, shuffle))
    }

    /** A film plays; a show or season plays the episode it's up to, or opens when everything's watched. */
    fun playBest(item: ItemSummary) {
        if (item.kind.isPlayable) return play(item)
        val client = model.client ?: return
        model.scope.launch {
            val next = runCatching { client.nextUp(item.id) }.getOrNull()
            if (next != null) play(next) else openItem(item.id)
        }
    }

    /** A random episode of the show, then random ones after it. */
    fun shuffle(item: ItemSummary) = play(item.id, shuffle = true)

    fun setWatched(item: ItemSummary, watched: Boolean) {
        val client = model.client ?: return
        model.scope.launch {
            runCatching { client.setWatched(item.id, watched) }
            model.watchStateChanged()
        }
    }

    /** The show of an episode, all of it. */
    fun setShowWatched(episode: ItemSummary) {
        val show = episode.seriesId ?: return
        val client = model.client ?: return
        model.scope.launch {
            runCatching { client.setWatched(show, true) }
            model.watchStateChanged()
        }
    }

    fun removeFromContinueWatching(item: ItemSummary) {
        val client = model.client ?: return
        model.scope.launch {
            runCatching { client.removeFromContinueWatching(item.id) }
            model.watchStateChanged()
        }
    }

    /** A Home row's link, "/library/{id}?sort=added", as a library to open; null for anything else. */
    fun libraryLink(link: String?): (() -> Unit)? {
        val uri = link?.let(Uri::parse) ?: return null
        val segments = uri.pathSegments
        if (segments.size != 2 || segments[0] != "library") return null
        val sort = LibrarySort.entries.firstOrNull { it.raw == uri.getQueryParameter("sort") } ?: LibrarySort.Title
        return { openLibrary(segments[1], sort) }
    }
}
