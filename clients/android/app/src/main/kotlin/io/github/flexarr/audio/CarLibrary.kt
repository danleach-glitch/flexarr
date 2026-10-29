package io.github.flexarr.audio

import android.net.Uri
import android.os.Bundle
import android.os.Process
import androidx.media3.common.ForwardingPlayer
import androidx.media3.common.MediaItem
import androidx.media3.common.MediaMetadata
import androidx.media3.common.Player
import androidx.media3.session.LibraryResult
import androidx.media3.session.MediaLibraryService.LibraryParams
import androidx.media3.session.MediaLibraryService.MediaLibrarySession
import androidx.media3.session.MediaSession
import androidx.media3.session.SessionCommands
import com.google.common.collect.ImmutableList
import com.google.common.util.concurrent.Futures
import com.google.common.util.concurrent.ListenableFuture
import io.github.flexarr.AppModel
import io.github.flexarr.core.FlexarrClient
import io.github.flexarr.core.ItemSummary
import io.github.flexarr.core.LibrarySort
import io.github.flexarr.core.LibraryType
import io.github.flexarr.core.PodcastEpisode
import io.github.flexarr.core.PodcastEpisodeSort
import io.github.flexarr.core.artistName
import io.github.flexarr.core.newPodcastEpisodes
import io.github.flexarr.core.playlist
import io.github.flexarr.core.playlists
import io.github.flexarr.core.podcast
import io.github.flexarr.core.podcastEpisode
import io.github.flexarr.core.podcastEpisodes
import io.github.flexarr.core.podcasts
import io.github.flexarr.core.tracks
import io.github.flexarr.core.upNext
import io.github.flexarr.logging.FlexarrLog
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.guava.future
import kotlinx.coroutines.launch
import java.util.concurrent.ConcurrentHashMap

private val log = FlexarrLog("car")

/**
 * Music and podcasts in the car (Android Auto), as CarPlay does on iPhone: tabs for Up Next, New episodes, Podcasts
 * (the shows followed, each opening its episodes) and Music (playlists, then the newest artists). What's chosen plays through the app's own players, so
 * the phone and the car agree.
 *
 * Ids: `upnext`, `new`, `podcasts`, `music` (the tabs); `podcast:<id>`, `list:playlist-<id>`, `list:artist-<id>`
 * (opened); `episode:<id>` and `track:<list>:<index>` (played).
 */
class CarLibrary(private val model: AppModel) : MediaLibrarySession.Callback {
    private val scope = CoroutineScope(Dispatchers.Main.immediate)

    /** What the car was last shown, so playing one doesn't ask the server again. */
    private val episodes = ConcurrentHashMap<String, PodcastEpisode>()
    private val trackLists = ConcurrentHashMap<String, List<ItemSummary>>()

    /** A playlist's tracks in order (`playlist-<id>`), or an artist's, albums oldest first (`artist-<id>`). */
    private suspend fun tracks(client: FlexarrClient, list: String): List<ItemSummary> {
        val id = list.substringAfter('-')
        val tracks = if (list.startsWith("playlist-")) client.playlist(id).tracks.map { it.track } else client.tracks(id)
        trackLists[list] = tracks
        return tracks
    }

    /**
     * Who may use the session. The service is open to every app (Android Auto must find it), so only this app, Android
     * Auto, the Assistant and the system (the lock screen, Bluetooth, anything Android trusts with media) may browse
     * and choose what plays. Any other app gets the transport controls alone: play, pause, skip and seek what's
     * already playing, never the library or something new to play.
     */
    override fun onConnect(session: MediaSession, controller: MediaSession.ControllerInfo): MediaSession.ConnectionResult {
        if (controller.uid == Process.myUid() || controller.isTrusted || controller.packageName in trustedPackages) {
            return super.onConnect(session, controller)
        }
        log.info("Transport controls only for ${controller.packageName}")
        return MediaSession.ConnectionResult.AcceptedResultBuilder(session)
            .setAvailableSessionCommands(SessionCommands.EMPTY)
            .setAvailablePlayerCommands(transportOnly)
            .build()
    }

    override fun onGetLibraryRoot(session: MediaLibrarySession, browser: MediaSession.ControllerInfo, params: LibraryParams?): ListenableFuture<LibraryResult<MediaItem>> =
        Futures.immediateFuture(LibraryResult.ofItem(folder(ROOT, "Flexarr"), LibraryParams.Builder().setExtras(rootHints()).build()))

    override fun onGetItem(session: MediaLibrarySession, browser: MediaSession.ControllerInfo, mediaId: String): ListenableFuture<LibraryResult<MediaItem>> =
        scope.future {
            val client = model.client ?: return@future LibraryResult.ofError(LibraryResult.RESULT_ERROR_SESSION_AUTHENTICATION_EXPIRED)
            val id = mediaId.substringAfter(':')
            runCatching {
                when {
                    mediaId.startsWith("episode:") -> episode(client, episodes[id] ?: client.podcastEpisode(id), showsPodcast = true)
                    mediaId.startsWith("podcast:") -> client.podcast(id).let { folder(mediaId, it.title, artwork = client.imageUrl(it.artwork.poster, 400)) }
                    else -> folder(mediaId, mediaId)
                }
            }.fold({ LibraryResult.ofItem(it, null) }, { LibraryResult.ofError(LibraryResult.RESULT_ERROR_IO) })
        }

    override fun onGetChildren(
        session: MediaLibrarySession,
        browser: MediaSession.ControllerInfo,
        parentId: String,
        page: Int,
        pageSize: Int,
        params: LibraryParams?,
    ): ListenableFuture<LibraryResult<ImmutableList<MediaItem>>> = scope.future {
        val client = model.client ?: return@future LibraryResult.ofError(LibraryResult.RESULT_ERROR_SESSION_AUTHENTICATION_EXPIRED)
        runCatching { children(client, parentId) }
            .onFailure { log.warning("Couldn't list $parentId for the car", it) }
            .fold({ LibraryResult.ofItemList(ImmutableList.copyOf(it), params) }, { LibraryResult.ofError(LibraryResult.RESULT_ERROR_IO) })
    }

    private suspend fun children(client: FlexarrClient, parentId: String): List<MediaItem> = when {
        parentId == ROOT -> listOf(
            folder(UP_NEXT, "Up Next", list = true),
            folder(NEW, "New episodes", list = true),
            folder(PODCASTS, "Podcasts"),
            folder(MUSIC, "Music"),
        )
        parentId == UP_NEXT -> client.upNext().map { episode(client, it, showsPodcast = true) }
        parentId == NEW -> client.newPodcastEpisodes(unplayed = true).map { episode(client, it, showsPodcast = true) }
        parentId == PODCASTS -> client.podcasts().map { folder("podcast:${it.id}", it.title, subtitle = it.author, artwork = client.imageUrl(it.artwork.poster, 400), list = true) }
        parentId.startsWith("podcast:") -> {
            // Its unplayed episodes, newest first (or oldest, as it's listened to).
            val podcast = client.podcast(parentId.substringAfter(':'))
            val sort = if (podcast.subscription?.oldestFirst == true) PodcastEpisodeSort.Oldest else PodcastEpisodeSort.Newest
            client.podcastEpisodes(podcast.id, sort, unplayed = true).items.map { episode(client, it, showsPodcast = false) }
        }
        // Playlists first, then the artists added most recently, each opening its tracks.
        parentId == MUSIC -> {
            val playlists = client.playlists().map { folder("list:playlist-${it.id}", it.name, subtitle = "${it.trackCount} songs", artwork = client.imageUrl(it.cover ?: it.covers.firstOrNull(), 400), list = true) }
            val artists = client.libraries().filter { it.type == LibraryType.Music }.flatMap { library ->
                client.libraryItems(library.id, LibrarySort.Added, take = 30).items.map { folder("list:artist-${it.id}", it.title, artwork = client.imageUrl(it.artwork.poster, 400), list = true) }
            }
            playlists + artists
        }
        parentId.startsWith("list:") -> {
            val list = parentId.removePrefix("list:")
            tracks(client, list).mapIndexed { index, track ->
                playable("track:$list:$index", track.title, track.artistName, client.imageUrl(track.artwork.poster, 400), MediaMetadata.MEDIA_TYPE_MUSIC)
            }
        }
        else -> emptyList()
    }

    /** The car's choice arrives as items with only their ids: they're kept as they are and played by [Router]. */
    override fun onAddMediaItems(mediaSession: MediaSession, controller: MediaSession.ControllerInfo, mediaItems: MutableList<MediaItem>): ListenableFuture<MutableList<MediaItem>> =
        Futures.immediateFuture(mediaItems)

    /** Plays what the car chose through the app's own players. */
    fun play(mediaId: String) {
        val client = model.client ?: return
        scope.launch {
            runCatching {
                when {
                    mediaId.startsWith("episode:") -> {
                        val id = mediaId.substringAfter(':')
                        model.podcasts.play(episodes[id] ?: client.podcastEpisode(id))
                    }
                    mediaId.startsWith("track:") -> {
                        val (list, index) = mediaId.removePrefix("track:").split(':').let { it[0] to it[1].toInt() }
                        model.music.play(trackLists[list] ?: tracks(client, list), start = index)
                    }
                }
            }.onFailure { log.warning("Couldn't play $mediaId from the car", it) }
        }
    }

    private fun episode(client: FlexarrClient, episode: PodcastEpisode, showsPodcast: Boolean): MediaItem {
        episodes[episode.id] = episode
        return playable(
            "episode:${episode.id}",
            episode.title,
            if (showsPodcast) episode.podcastTitle else null,
            client.imageUrl(episode.artwork.poster, 400),
            MediaMetadata.MEDIA_TYPE_PODCAST_EPISODE,
            progress = episode.progress,
        )
    }

    /**
     * Wraps whichever player is publishing, so a choice made in the car (a controller setting media items by id) goes
     * to [play] rather than into the player as an item it can't load.
     */
    inner class Router(player: Player) : ForwardingPlayer(player) {
        val wrapped: Player = player

        override fun getAvailableCommands(): Player.Commands =
            super.getAvailableCommands().buildUpon().addAll(Player.COMMAND_SET_MEDIA_ITEM, Player.COMMAND_CHANGE_MEDIA_ITEMS).build()

        override fun isCommandAvailable(command: Int): Boolean =
            command == Player.COMMAND_SET_MEDIA_ITEM || command == Player.COMMAND_CHANGE_MEDIA_ITEMS || super.isCommandAvailable(command)

        override fun setMediaItem(mediaItem: MediaItem) = play(mediaItem.mediaId)
        override fun setMediaItem(mediaItem: MediaItem, startPositionMs: Long) = play(mediaItem.mediaId)
        override fun setMediaItem(mediaItem: MediaItem, resetPosition: Boolean) = play(mediaItem.mediaId)
        override fun setMediaItems(mediaItems: MutableList<MediaItem>) = mediaItems.firstOrNull()?.let { play(it.mediaId) } ?: Unit
        override fun setMediaItems(mediaItems: MutableList<MediaItem>, resetPosition: Boolean) = mediaItems.firstOrNull()?.let { play(it.mediaId) } ?: Unit
        override fun setMediaItems(mediaItems: MutableList<MediaItem>, startIndex: Int, startPositionMs: Long) =
            mediaItems.getOrNull(startIndex)?.let { play(it.mediaId) } ?: Unit
        // The player starts by itself once the app's own player has the item.
        override fun prepare() {}
    }

    companion object {
        /** Android Auto, the Assistant (in the car and on the phone) and the system UI's media controls. */
        private val trustedPackages = setOf(
            "com.google.android.projection.gearhead",
            "com.google.android.googlequicksearchbox",
            "com.google.android.carassistant",
            "com.android.systemui",
        )

        private val transportOnly = Player.Commands.Builder().addAll(
            Player.COMMAND_PLAY_PAUSE,
            Player.COMMAND_STOP,
            Player.COMMAND_SEEK_IN_CURRENT_MEDIA_ITEM,
            Player.COMMAND_SEEK_BACK,
            Player.COMMAND_SEEK_FORWARD,
            Player.COMMAND_SEEK_TO_PREVIOUS,
            Player.COMMAND_SEEK_TO_NEXT,
            Player.COMMAND_GET_CURRENT_MEDIA_ITEM,
            Player.COMMAND_GET_METADATA,
            Player.COMMAND_GET_TIMELINE,
        ).build()

        const val ROOT = "root"
        const val UP_NEXT = "upnext"
        const val NEW = "new"
        const val PODCASTS = "podcasts"
        const val MUSIC = "music"

        // Android Auto's content-style hints: lists for episodes, a grid for shows and playlists.
        private const val BROWSABLE_HINT = "android.media.browse.CONTENT_STYLE_BROWSABLE_HINT"
        private const val PLAYABLE_HINT = "android.media.browse.CONTENT_STYLE_PLAYABLE_HINT"
        private const val STYLE_LIST = 1
        private const val STYLE_GRID = 2
        private const val COMPLETION_STATUS = "android.media.extra.PLAYBACK_STATUS"
        private const val COMPLETION_PERCENTAGE = "androidx.media.MediaItem.Extras.COMPLETION_PERCENTAGE"
        private const val STATUS_PARTIALLY_PLAYED = 1

        private fun rootHints() = Bundle().apply {
            putInt(BROWSABLE_HINT, STYLE_GRID)
            putInt(PLAYABLE_HINT, STYLE_LIST)
        }

        private fun folder(id: String, title: String, subtitle: String? = null, artwork: String? = null, list: Boolean = false): MediaItem =
            MediaItem.Builder().setMediaId(id).setMediaMetadata(
                MediaMetadata.Builder()
                    .setTitle(title)
                    .setSubtitle(subtitle)
                    .setArtworkUri(artwork?.let(Uri::parse))
                    .setIsBrowsable(true)
                    .setIsPlayable(false)
                    .setMediaType(MediaMetadata.MEDIA_TYPE_FOLDER_MIXED)
                    .setExtras(Bundle().apply { if (list) putInt(BROWSABLE_HINT, STYLE_LIST) })
                    .build(),
            ).build()

        private fun playable(id: String, title: String, subtitle: String?, artwork: String?, type: Int, progress: Double? = null): MediaItem =
            MediaItem.Builder().setMediaId(id).setMediaMetadata(
                MediaMetadata.Builder()
                    .setTitle(title)
                    .setArtist(subtitle)
                    .setSubtitle(subtitle)
                    .setArtworkUri(artwork?.let(Uri::parse))
                    .setIsBrowsable(false)
                    .setIsPlayable(true)
                    .setMediaType(type)
                    .setExtras(Bundle().apply {
                        progress?.let {
                            putInt(COMPLETION_STATUS, STATUS_PARTIALLY_PLAYED)
                            putDouble(COMPLETION_PERCENTAGE, it)
                        }
                    })
                    .build(),
            ).build()
    }
}
