package io.github.flexarr.ui.music

import io.github.flexarr.core.i18n.Words
import io.github.flexarr.core.i18n.t
import android.content.Context
import androidx.compose.runtime.Composable
import androidx.compose.ui.platform.LocalContext
import io.github.flexarr.appModel
import io.github.flexarr.core.FlexarrClient
import io.github.flexarr.core.ItemSummary
import io.github.flexarr.core.MediaKind
import io.github.flexarr.core.MusicActions
import io.github.flexarr.core.MusicMenu
import io.github.flexarr.core.PlaylistSummary
import io.github.flexarr.core.playlist
import io.github.flexarr.core.tracks
import io.github.flexarr.ui.Navigator
import io.github.flexarr.ui.components.LocalNavigator
import io.github.flexarr.ui.components.MenuAction
import io.github.flexarr.ui.theme.FlexarrIcons
import io.github.flexarr.usesTvInterface
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.launch

/** What a music menu acts on: an album, an artist or a track from the library, or one of the profile's playlists. */
sealed interface MusicMenuSubject {
    val id: String
    val title: String

    data class Item(val item: ItemSummary) : MusicMenuSubject {
        override val id: String get() = item.id
        override val title: String get() = item.title
    }

    data class Playlist(val playlist: PlaylistSummary) : MusicMenuSubject {
        override val id: String get() = playlist.id
        override val title: String get() = playlist.name
    }
}

/**
 * What pressing and holding a music card or a tracklist's row offers, in [MusicMenu]'s order: Play, Shuffle, Play next,
 * Add to queue, Add to playlist… (phones), Go to artist, Download (an album's or playlist's card, on phones). The
 * tracks are fetched when an action runs, not with the card. [onPlay] stands in for Play (a track plays as tapping it
 * does: its row from it); without it Play and Shuffle play everything it has and open Now Playing. [showsArtist]: Go to
 * artist is worth offering (not on that artist's own page). The TV ([platform] TV) keeps nothing and has no playlists
 * to add to, so it leaves those out. The web's ItemMenu for music; the iPhone's MusicMenu.
 */
@Composable
fun musicMenuActions(
    subject: MusicMenuSubject,
    place: MusicMenu.Place,
    platform: MusicActions.Platform = MusicActions.Platform.PHONE,
    showsArtist: Boolean = true,
    onPlay: (() -> Unit)? = null,
): List<MenuAction> = musicMenuActionsLater(subject, place, platform, showsArtist, onPlay)()

/** [musicMenuActions], built only when the menu opens or TalkBack asks, not each time the card is drawn. */
@Composable
fun musicMenuActionsLater(
    subject: MusicMenuSubject,
    place: MusicMenu.Place,
    platform: MusicActions.Platform = MusicActions.Platform.PHONE,
    showsArtist: Boolean = true,
    onPlay: (() -> Unit)? = null,
): () -> List<MenuAction> {
    val navigator = LocalNavigator.current
    val context = LocalContext.current
    return {
        val model = context.appModel
        val kind = subjectKind(subject)
        if (kind == null) emptyList() else {
            val artistId = (subject as? MusicMenuSubject.Item)?.item?.seriesId?.takeIf { showsArtist }
            val canDownload = platform == MusicActions.Platform.PHONE && model.downloads.collection(subject.id) == null
            musicActions(subject, MusicMenu.of(kind, place, platform, hasArtist = artistId != null, canDownload = canDownload), onPlay, navigator, context)
        }
    }
}

/**
 * An album's or artist's page ⋯: [MusicActions.menu]'s Play next, Add to queue and (phones) Add to playlist…, for
 * everything it has.
 */
@Composable
fun musicPageMenuActions(item: ItemSummary, menu: List<MusicActions.MenuItem>): List<MenuAction> {
    val navigator = LocalNavigator.current
    val context = LocalContext.current
    return musicActions(
        MusicMenuSubject.Item(item),
        menu.map {
            when (it) {
                MusicActions.MenuItem.PLAY_NEXT -> MusicMenu.Item.PLAY_NEXT
                MusicActions.MenuItem.ADD_TO_QUEUE -> MusicMenu.Item.ADD_TO_QUEUE
                MusicActions.MenuItem.ADD_TO_PLAYLIST -> MusicMenu.Item.ADD_TO_PLAYLIST
            }
        },
        onPlay = null,
        navigator,
        context,
    )
}

private fun subjectKind(subject: MusicMenuSubject): MusicMenu.Subject? = when (subject) {
    is MusicMenuSubject.Playlist -> MusicMenu.Subject.PLAYLIST
    is MusicMenuSubject.Item -> when (subject.item.kind) {
        MediaKind.Artist -> MusicMenu.Subject.ARTIST
        MediaKind.Album -> MusicMenu.Subject.ALBUM
        MediaKind.Track -> MusicMenu.Subject.TRACK
        else -> null
    }
}

/** [items] as actions on [subject]. */
private fun musicActions(subject: MusicMenuSubject, items: List<MusicMenu.Item>, onPlay: (() -> Unit)?, navigator: Navigator, context: Context): List<MenuAction> {
    val model = context.appModel
    val kind = subjectKind(subject) ?: return emptyList()
    val artistId = (subject as? MusicMenuSubject.Item)?.item?.seriesId

    /** Everything it has, in order: an album's or artist's tracks, a playlist's, or the one track. */
    suspend fun tracks(client: FlexarrClient): List<ItemSummary> = when (subject) {
        is MusicMenuSubject.Playlist -> client.playlist(subject.id).tracks.map { it.track }
        is MusicMenuSubject.Item -> if (kind == MusicMenu.Subject.TRACK) listOf(subject.item) else client.tracks(subject.id)
    }
    // An album lends its tracks its name for Now Playing.
    val albums = (subject as? MusicMenuSubject.Item)?.item?.takeIf { it.kind == MediaKind.Album }?.let(::listOf).orEmpty()

    /** Fetches the tracks and does [work] with them, saying [failure] if that didn't work. */
    fun withTracks(failure: String, work: suspend (List<ItemSummary>) -> Unit) {
        val client = model.client ?: return
        model.scope.launch {
            try {
                val tracks = tracks(client)
                if (tracks.isNotEmpty()) work(tracks)
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                navigator.tellFailure(failure, e)
            }
        }
    }

    fun play(shuffled: Boolean) = withTracks(t("Couldn’t play this")) { tracks ->
        model.music.play(tracks, shuffled = shuffled, albums = albums)
        // The TV shows Now Playing over everything when asked; the phone opens its sheet.
        if (context.usesTvInterface) model.nowPlayingRequests++ else model.showsMusicPlayer = true
    }

    return items.map { item ->
        when (item) {
            MusicMenu.Item.PLAY -> MenuAction(t(Words.play), FlexarrIcons.play) { onPlay?.invoke() ?: play(shuffled = false) }
            MusicMenu.Item.SHUFFLE -> MenuAction(t(Words.shuffle), FlexarrIcons.shuffle) { play(shuffled = true) }
            MusicMenu.Item.PLAY_NEXT -> MenuAction(t(Words.playNext), FlexarrIcons.playNext) {
                withTracks(t("Couldn’t add this to the queue")) { model.music.playNext(it, albums) }
            }
            MusicMenu.Item.ADD_TO_QUEUE -> MenuAction(t(Words.addToQueue), FlexarrIcons.addToQueue) {
                withTracks(t("Couldn’t add this to the queue")) { model.music.enqueue(it, albums) }
            }
            MusicMenu.Item.ADD_TO_PLAYLIST -> MenuAction(t("Add to playlist…"), FlexarrIcons.addToPlaylist) {
                navigator.addingToPlaylist = PlaylistAddition(subject.id, subject.title)
            }
            MusicMenu.Item.GO_TO_ARTIST -> MenuAction(t(Words.goToArtist), FlexarrIcons.goToArtist) { artistId?.let(navigator::openLibraryArtist) }
            MusicMenu.Item.DOWNLOAD -> MenuAction(t(Words.download), FlexarrIcons.download) {
                withTracks(t("Couldn’t download this")) { tracks ->
                    when (subject) {
                        is MusicMenuSubject.Playlist -> model.downloads.download(subject.playlist, tracks)
                        is MusicMenuSubject.Item -> model.downloads.download(subject.item, tracks)
                    }
                }
            }
        }
    }
}
