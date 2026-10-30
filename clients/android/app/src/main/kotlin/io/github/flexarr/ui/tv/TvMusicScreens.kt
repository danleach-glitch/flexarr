package io.github.flexarr.ui.tv

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyListScope
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Shuffle
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.tv.material3.Text
import io.github.flexarr.AppModel
import io.github.flexarr.core.Format
import io.github.flexarr.core.ItemSummary
import io.github.flexarr.core.Library
import io.github.flexarr.core.LibrarySort
import io.github.flexarr.core.MediaKind
import io.github.flexarr.core.PlaylistSummary
import io.github.flexarr.core.artistName
import io.github.flexarr.core.discs
import io.github.flexarr.core.facts
import io.github.flexarr.core.playlist
import io.github.flexarr.core.playlists
import io.github.flexarr.core.releaseYear
import io.github.flexarr.core.totalRuntime
import io.github.flexarr.core.tracks
import io.github.flexarr.ui.components.Load
import io.github.flexarr.ui.components.LocalNavigator
import io.github.flexarr.ui.components.RefreshWhenStale
import io.github.flexarr.ui.components.rememberLoader
import io.github.flexarr.ui.podcasts.Tabular
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.RoomBackground
import io.github.flexarr.ui.theme.glowColor
import io.github.flexarr.ui.theme.marquee
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

// The music screens, as the Apple TV's: a library (playlists, new albums, every artist), an artist, an album and a
// playlist. Each title page has Play and Shuffle, which also bring up Now Playing over everything; pressing a track plays
// from it, and pressing the one playing pauses it.

private const val PAGE_SIZE = 60
/**
 * How many of the newest artists are opened to find the newest albums. The server lists albums only under their
 * artist, so this bounds the requests a visit makes (as the phone's library does).
 */
private const val RECENT_ARTISTS = 8
private const val RECENT_ALBUM_COUNT = 12
/** Six across, as the Apple TV's grids. */
private const val ACROSS = 6
/** The gap between six posters on the shared edges. */
private val GridSpacing = (960.dp - TvLayout.Edge * 2 - TvLayout.PosterWidth * ACROSS) / (ACROSS - 1)

/** Play or Shuffle started music: Now Playing over everything (TvMainScreen watches the count). */
private fun AppModel.showNowPlaying() {
    if (music.current != null) nowPlayingRequests++
}

/** Asks for focus until the item is placed (it can come a frame or two after its screen). */
private suspend fun FocusRequester.takeFocusSoon() {
    repeat(10) {
        if (runCatching { requestFocus() }.isSuccess) return
        delay(50)
    }
}

/** What a music library opens to, besides its artists: the profile's playlists and the albums that arrived last. */
private class MusicBrowse(val playlists: List<PlaylistSummary>, val recentAlbums: List<ItemSummary>)

/**
 * A music library's tab, as the Apple TV's MusicLibraryView: the profile's playlists, the albums that arrived most
 * recently, then every artist as a round picture. [takeFocus]: arriving from the sidebar, the first card takes focus
 * once there is one, then [onFocusTaken].
 */
@Composable
fun TvMusicLibraryScreen(model: AppModel, library: Library, takeFocus: Boolean, onFocusTaken: () -> Unit) {
    val client = model.client ?: return
    val navigator = LocalNavigator.current
    val scope = rememberCoroutineScope()
    val libraryId = library.id
    var artists by remember(libraryId) { mutableStateOf(emptyList<ItemSummary>()) }
    var total by remember(libraryId) { mutableIntStateOf(0) }
    var loading by remember(libraryId) { mutableStateOf(true) }
    var error by remember(libraryId) { mutableStateOf<String?>(null) }
    var attempt by remember { mutableIntStateOf(0) }
    var loadedAt by remember(libraryId) { mutableStateOf<Long?>(null) }
    var browse by remember(libraryId) { mutableStateOf(MusicBrowse(emptyList(), emptyList())) }
    val first = remember { FocusRequester() }
    // Coming back from an artist or another tab keeps what's loaded unless it's getting old.
    RefreshWhenStale(loadedAt) { attempt++ }

    LaunchedEffect(libraryId, attempt) {
        loading = true
        try {
            coroutineScope {
                val lists = async { runCatching { client.playlists() }.getOrDefault(emptyList()) }
                val newest = async { runCatching { client.libraryItems(libraryId, LibrarySort.Added, take = RECENT_ARTISTS).items }.getOrDefault(emptyList()) }
                val page = client.libraryItems(libraryId, LibrarySort.Title, take = PAGE_SIZE)
                artists = page.items.filter { it.kind == MediaKind.Artist || it.kind == MediaKind.Unknown }
                total = page.total
                error = null
                loadedAt = System.currentTimeMillis()
                // The newest albums among the newest artists', newest first.
                val albums = newest.await().map { artist ->
                    async { runCatching { client.item(artist.id).children.filter { it.kind == MediaKind.Album } }.getOrDefault(emptyList()) }
                }.awaitAll().flatten().sortedByDescending { it.addedAt.orEmpty() }.take(RECENT_ALBUM_COUNT)
                browse = MusicBrowse(lists.await(), albums)
            }
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            if (artists.isEmpty()) error = e.message
        }
        loading = false
    }

    fun loadMoreIfNeeded(index: Int) {
        if (loading || artists.size >= total || index < artists.size - 18) return
        loading = true
        val skip = artists.size
        scope.launch {
            try {
                val page = client.libraryItems(libraryId, LibrarySort.Title, skip = skip, take = PAGE_SIZE)
                if (skip == artists.size) {
                    val known = artists.mapTo(HashSet()) { it.id }
                    artists = artists + page.items.filter { it.id !in known }
                    total = page.total
                }
            } catch (e: CancellationException) {
                throw e
            } catch (_: Exception) {
                // Tried again as the next row comes into view.
            } finally {
                loading = false
            }
        }
    }

    // Arriving from the sidebar: the first card, once there is one (or nothing to take it, said at once).
    val hasCards = artists.isNotEmpty() || browse.playlists.isNotEmpty() || browse.recentAlbums.isNotEmpty()
    LaunchedEffect(takeFocus, hasCards, loading) {
        if (!takeFocus) return@LaunchedEffect
        if (hasCards) first.takeFocusSoon() else if (loading) return@LaunchedEffect
        onFocusTaken()
    }

    val firstSection = when {
        browse.playlists.isNotEmpty() -> 0
        browse.recentAlbums.isNotEmpty() -> 1
        else -> 2
    }
    RoomBackground(ambient = (browse.recentAlbums.firstOrNull() ?: artists.firstOrNull())?.let { glowColor(it.accentColor) }, intensity = 0.18f) {
        val failed = error
        when {
            failed != null && artists.isEmpty() -> TvStatusView(failed, retry = { attempt++ })
            artists.isEmpty() && !loading -> TvStatusView("Nothing in this library yet.", empty = true)
            artists.isEmpty() -> TvStatusView()
            else -> LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(top = 10.dp, bottom = 40.dp), verticalArrangement = Arrangement.spacedBy(22.dp)) {
                item(key = "count") {
                    Text(
                        if (total == 1) "1 artist" else "${Format.count(total)} artists",
                        fontSize = 12.sp, fontWeight = FontWeight.Medium, color = Flexarr.usher, style = Tabular,
                        modifier = Modifier.padding(start = TvLayout.Edge),
                    )
                }
                if (browse.playlists.isNotEmpty()) {
                    item(key = "playlists") {
                        Shelf("Your playlists") {
                            itemsIndexed(browse.playlists, key = { _, it -> it.id }) { index, playlist ->
                                Box(if (index == 0 && firstSection == 0) Modifier.focusRequester(first) else Modifier) {
                                    TvPlaylistCard(playlist, onClick = { navigator.openPlaylist(playlist.id) })
                                }
                            }
                        }
                    }
                }
                if (browse.recentAlbums.isNotEmpty()) {
                    item(key = "recent") {
                        Shelf("Recently added") {
                            itemsIndexed(browse.recentAlbums, key = { _, it -> it.id }) { index, album ->
                                TvAlbumCard(album, onClick = { navigator.open(album) }, showsArtist = true, modifier = if (index == 0 && firstSection == 1) Modifier.focusRequester(first) else Modifier)
                            }
                        }
                    }
                }
                item(key = "artists-heading") { TvRowHeading("Artists") }
                gridRows(artists, key = "artists", onRow = { lastIndex -> loadMoreIfNeeded(lastIndex) }) { index, artist ->
                    TvArtistCard(artist, onClick = { navigator.open(artist) }, modifier = if (index == 0 && firstSection == 2) Modifier.focusRequester(first) else Modifier)
                }
                if (loading) {
                    item(key = "loading") {
                        Box(Modifier.fillMaxWidth().padding(20.dp), contentAlignment = Alignment.Center) { CircularProgressIndicator(color = Flexarr.screen) }
                    }
                }
            }
        }
    }
}

/** A heading with a row of cards that scrolls sideways under it. */
@Composable
private fun Shelf(title: String, content: LazyListScope.() -> Unit) {
    Column {
        TvRowHeading(title)
        LazyRow(
            contentPadding = PaddingValues(start = TvLayout.Edge, end = TvLayout.Edge, top = 14.dp, bottom = 20.dp),
            horizontalArrangement = Arrangement.spacedBy(TvLayout.RowSpacing),
            verticalAlignment = Alignment.Top,
            content = content,
        )
    }
}

/**
 * [items] six across in rows of the page's list, on the shared edges. [onRow] hears each row's last index as it comes
 * into view (to load more).
 */
private fun LazyListScope.gridRows(
    items: List<ItemSummary>,
    key: String,
    onRow: (Int) -> Unit = {},
    card: @Composable (Int, ItemSummary) -> Unit,
) {
    val rows = items.chunked(ACROSS)
    itemsIndexed(rows, key = { _, row -> "$key-${row.first().id}" }) { rowIndex, row ->
        val start = rowIndex * ACROSS
        LaunchedEffect(start, items.size) { onRow(start + row.size - 1) }
        Row(Modifier.padding(start = TvLayout.Edge, end = TvLayout.Edge, bottom = 6.dp), horizontalArrangement = Arrangement.spacedBy(GridSpacing), verticalAlignment = Alignment.Top) {
            row.forEachIndexed { offset, item -> card(start + offset, item) }
        }
    }
}

/**
 * A title page's header, as the Apple TV's artist, album and playlist pages: the art at the left, then the name, what's
 * under it, and Play and Shuffle. Play takes focus as the page opens, so OK plays at once.
 */
@Composable
private fun MusicHeader(
    title: String,
    titleSize: Int,
    ambient: Color,
    canPlay: Boolean,
    canShuffle: Boolean,
    onPlay: () -> Unit,
    onShuffle: () -> Unit,
    busy: Boolean = false,
    alignment: Alignment.Vertical = Alignment.Bottom,
    art: @Composable () -> Unit,
    below: @Composable () -> Unit,
) {
    val play = remember { FocusRequester() }
    LaunchedEffect(canPlay) { if (canPlay) play.takeFocusSoon() }
    Row(Modifier.padding(horizontal = TvLayout.Edge), horizontalArrangement = Arrangement.spacedBy(30.dp), verticalAlignment = alignment) {
        art()
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            Text(title, style = marquee(titleSize).copy(fontWeight = FontWeight.ExtraBold), maxLines = 2, overflow = TextOverflow.Ellipsis)
            below()
            Row(Modifier.padding(top = 4.dp), horizontalArrangement = Arrangement.spacedBy(13.dp), verticalAlignment = Alignment.CenterVertically) {
                TvActionButton("Play", onClick = onPlay, icon = Icons.Filled.PlayArrow, prominent = true, ambient = ambient, enabled = canPlay && !busy, modifier = Modifier.focusRequester(play))
                TvActionButton("Shuffle", onClick = onShuffle, icon = Icons.Filled.Shuffle, ambient = ambient, enabled = canShuffle && !busy)
                if (busy) CircularProgressIndicator(Modifier.size(20.dp), color = Flexarr.screen, strokeWidth = 2.dp)
            }
        }
    }
}

/** An artist: their picture and name, Play and Shuffle for everything they have, and their albums newest first. */
@Composable
fun TvArtistScreen(model: AppModel, id: String) {
    val client = model.client ?: return
    val navigator = LocalNavigator.current
    val scope = rememberCoroutineScope()
    val page = rememberLoader(id, model.watchStateVersion) { client.item(id) }
    var queueing by remember { mutableStateOf(false) }
    val detail = (page.state as? Load.Loaded)?.value
    val ambient = detail?.item?.let { glowColor(it.accentColor) } ?: Flexarr.silver

    RoomBackground(ambient = ambient, intensity = 0.4f) {
        when (val state = page.state) {
            is Load.Loaded -> {
                val item = state.value.item
                val albums = state.value.children.filter { it.kind == MediaKind.Album }

                /** Everything they have, fetched when asked rather than with the page, then Now Playing. */
                fun playAll(shuffled: Boolean) {
                    queueing = true
                    scope.launch {
                        val tracks = runCatching { client.tracks(id) }.getOrDefault(emptyList())
                        queueing = false
                        if (tracks.isEmpty()) return@launch
                        model.music.play(tracks, shuffled = shuffled, albums = albums)
                        model.showNowPlaying()
                    }
                }

                LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(top = 65.dp, bottom = 45.dp), verticalArrangement = Arrangement.spacedBy(14.dp)) {
                    item(key = "header") {
                        MusicHeader(
                            title = item.title,
                            titleSize = 42,
                            ambient = ambient,
                            canPlay = albums.isNotEmpty(),
                            canShuffle = albums.isNotEmpty(),
                            onPlay = { playAll(shuffled = false) },
                            onShuffle = { playAll(shuffled = true) },
                            busy = queueing,
                            alignment = Alignment.CenterVertically,
                            art = { Box(Modifier.shadow(15.dp, CircleShape)) { TvSquareArtwork(item, 180.dp, circular = true) } },
                            below = {
                                Row(horizontalArrangement = Arrangement.spacedBy(11.dp), verticalAlignment = Alignment.CenterVertically) {
                                    TvFactsLine(item.facts)
                                    if (state.value.genres.isNotEmpty()) {
                                        Text(state.value.genres.take(3).joinToString(", "), fontSize = 13.sp, fontWeight = FontWeight.Medium, color = Flexarr.usher, maxLines = 1)
                                    }
                                }
                                state.value.overview?.takeIf { it.isNotEmpty() }?.let {
                                    Text(it, fontSize = 13.sp, lineHeight = 18.sp, color = Flexarr.screen.copy(alpha = 0.88f), maxLines = 3, overflow = TextOverflow.Ellipsis, modifier = Modifier.widthIn(max = 500.dp))
                                }
                            },
                        )
                    }
                    if (albums.isNotEmpty()) {
                        item(key = "albums-heading") { TvRowHeading("Albums", Modifier.padding(top = 14.dp)) }
                        gridRows(albums, key = "albums") { _, album -> TvAlbumCard(album, onClick = { navigator.open(album) }) }
                    } else {
                        item(key = "empty") { Box(Modifier.height(100.dp)) { TvStatusView("No albums yet.", empty = true) } }
                    }
                }
            }
            is Load.Failed -> TvStatusView(state.message, retry = page.retry)
            Load.Loading -> TvStatusView()
        }
    }
}

/**
 * An album: cover, title, artist and year, Play and Shuffle, and the tracklist (split by disc when there's more than
 * one) with the playing track marked.
 */
@Composable
fun TvAlbumScreen(model: AppModel, id: String) {
    val client = model.client ?: return
    val navigator = LocalNavigator.current
    val music = model.music
    val page = rememberLoader(id, model.watchStateVersion) { client.item(id) }
    val detail = (page.state as? Load.Loaded)?.value
    val ambient = detail?.item?.let { glowColor(it.accentColor) } ?: Flexarr.silver

    RoomBackground(ambient = ambient, intensity = 0.4f) {
        when (val state = page.state) {
            is Load.Loaded -> {
                val item = state.value.item
                val tracks = state.value.children.filter { it.kind == MediaKind.Track }
                val artist = state.value.ancestors.lastOrNull { it.kind == MediaKind.Artist }
                LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(top = 65.dp, bottom = 45.dp), verticalArrangement = Arrangement.spacedBy(3.dp)) {
                    item(key = "header") {
                        MusicHeader(
                            title = item.title,
                            titleSize = 36,
                            ambient = ambient,
                            canPlay = tracks.isNotEmpty(),
                            canShuffle = tracks.size >= 2,
                            onPlay = {
                                music.play(tracks, albums = listOf(item))
                                model.showNowPlaying()
                            },
                            onShuffle = {
                                music.play(tracks, shuffled = true, albums = listOf(item))
                                model.showNowPlaying()
                            },
                            art = { Box(Modifier.shadow(15.dp, RoundedCornerShape(5.dp))) { TvSquareArtwork(item, 210.dp) } },
                            below = {
                                if (artist != null) {
                                    TvPill(artist.title, selected = false, onClick = { navigator.open(artist) }, ambient = ambient)
                                } else {
                                    item.artistName?.let { Text(it, fontSize = 15.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen.copy(alpha = 0.9f)) }
                                }
                                TvFactsLine(albumFacts(item, tracks))
                            },
                        )
                    }
                    item(key = "gap") { Spacer(Modifier.height(21.dp)) }
                    if (tracks.isEmpty()) {
                        item(key = "empty") { Box(Modifier.height(100.dp)) { TvStatusView("No tracks yet.", empty = true) } }
                    } else {
                        val discs = discs(tracks)
                        discs.forEach { (disc, discTracks) ->
                            if (discs.size > 1) {
                                item(key = "disc-$disc") {
                                    Text(
                                        "Disc $disc",
                                        style = marquee(17, Flexarr.usher).copy(fontWeight = FontWeight.Bold),
                                        modifier = Modifier.padding(start = TvLayout.Edge + 15.dp, top = if (disc == discs.first().first) 0.dp else 15.dp, bottom = 5.dp),
                                    )
                                }
                            }
                            itemsIndexed(discTracks, key = { _, track -> track.id }) { offset, track ->
                                TvTrackRow(
                                    track,
                                    number = track.trackNumber ?: (offset + 1),
                                    isCurrent = music.current?.id == track.id,
                                    isPlaying = music.isPlaying,
                                    subtitle = track.trackArtist,
                                    onClick = {
                                        if (music.current?.id == track.id) music.togglePlayPause()
                                        else music.play(tracks, start = tracks.indexOf(track), albums = listOf(item))
                                    },
                                    modifier = Modifier.padding(horizontal = TvLayout.Edge).widthIn(max = 700.dp).fillMaxWidth(),
                                )
                            }
                        }
                    }
                }
            }
            is Load.Failed -> TvStatusView(state.message, retry = page.retry)
            Load.Loading -> TvStatusView()
        }
    }
}

/** "1991   12 tracks   46m". */
private fun albumFacts(item: ItemSummary, tracks: List<ItemSummary>): List<String> = buildList {
    item.releaseYear?.let { add(it.toString()) }
    val count = if (tracks.isEmpty()) item.childCount else tracks.size
    if (count > 0) add(if (count == 1) "1 track" else "$count tracks")
    totalRuntime(tracks)?.let(::add)
}

/** A playlist: its picture, name and length, Play and Shuffle, and its tracks in order with the playing one marked. */
@Composable
fun TvPlaylistScreen(model: AppModel, id: String) {
    val client = model.client ?: return
    val music = model.music
    val loader = rememberLoader(id, model.watchStateVersion) { client.playlist(id) }
    val tracks = (loader.state as? Load.Loaded)?.value?.tracks?.map { it.track }.orEmpty()

    RoomBackground(ambient = tracks.firstOrNull()?.let { glowColor(it.accentColor) }, intensity = 0.35f) {
        when (val state = loader.state) {
            is Load.Loaded -> {
                val summary = state.value.summary
                LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(top = 65.dp, bottom = 45.dp), verticalArrangement = Arrangement.spacedBy(3.dp)) {
                    item(key = "header") {
                        MusicHeader(
                            title = summary.name,
                            titleSize = 36,
                            ambient = Flexarr.silver,
                            canPlay = tracks.isNotEmpty(),
                            canShuffle = tracks.size >= 2,
                            onPlay = {
                                music.play(tracks)
                                model.showNowPlaying()
                            },
                            onShuffle = {
                                music.play(tracks, shuffled = true)
                                model.showNowPlaying()
                            },
                            art = { Box(Modifier.shadow(15.dp, RoundedCornerShape(5.dp))) { TvPlaylistArtwork(summary, 210.dp) } },
                            below = { TvFactsLine(listOfNotNull(summary.trackCountLabel, Format.runtime(summary.durationSeconds))) },
                        )
                    }
                    item(key = "gap") { Spacer(Modifier.height(21.dp)) }
                    if (tracks.isEmpty()) {
                        item(key = "empty") { Box(Modifier.height(100.dp)) { TvStatusView("Nothing in this playlist yet.", empty = true) } }
                    }
                    // A track can be in a playlist twice: each place is its own row.
                    itemsIndexed(state.value.tracks, key = { _, entry -> entry.entryId }) { index, entry ->
                        TvTrackRow(
                            entry.track,
                            number = index + 1,
                            isCurrent = music.current?.id == entry.track.id,
                            isPlaying = music.isPlaying,
                            onClick = { if (music.current?.id == entry.track.id) music.togglePlayPause() else music.play(tracks, start = index) },
                            modifier = Modifier.padding(horizontal = TvLayout.Edge).widthIn(max = 700.dp).fillMaxWidth(),
                        )
                    }
                }
            }
            is Load.Failed -> TvStatusView(state.message, retry = loader.retry)
            Load.Loading -> TvStatusView()
        }
    }
}
