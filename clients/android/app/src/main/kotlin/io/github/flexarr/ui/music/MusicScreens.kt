package io.github.flexarr.ui.music

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.GridItemSpan
import androidx.compose.foundation.lazy.grid.LazyGridScope
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.itemsIndexed
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.itemsIndexed as rowItemsIndexed
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.ui.layout.layout
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Shuffle
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.Text
import androidx.compose.material3.pulltorefresh.PullToRefreshBox
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.github.flexarr.AppModel
import io.github.flexarr.core.Format
import io.github.flexarr.core.HomeRow
import io.github.flexarr.core.HomeRowStyle
import io.github.flexarr.core.ItemDetail
import io.github.flexarr.core.ItemSummary
import io.github.flexarr.core.LibrarySort
import io.github.flexarr.core.MediaKind
import io.github.flexarr.core.PlaylistDetail
import io.github.flexarr.core.artistName
import io.github.flexarr.core.discs
import io.github.flexarr.core.facts
import io.github.flexarr.core.playlist
import io.github.flexarr.core.musicRows
import io.github.flexarr.core.playlists
import io.github.flexarr.core.releaseYear
import io.github.flexarr.core.totalRuntime
import io.github.flexarr.core.tracks
import io.github.flexarr.ui.components.GridMinimum
import io.github.flexarr.ui.components.ActionButton
import io.github.flexarr.ui.components.Edge
import io.github.flexarr.ui.components.FactsLine
import io.github.flexarr.ui.components.Load
import io.github.flexarr.ui.components.LocalNavigator
import io.github.flexarr.ui.components.PageTitle
import io.github.flexarr.ui.components.PosterWidth
import io.github.flexarr.ui.components.RefreshWhenStale
import io.github.flexarr.ui.components.RowSpacing
import io.github.flexarr.ui.components.SectionHeading
import io.github.flexarr.ui.components.StatusView
import io.github.flexarr.ui.components.pressable
import io.github.flexarr.ui.components.rememberLoader
import io.github.flexarr.ui.downloads.MusicDownloadButton
import io.github.flexarr.ui.downloads.MusicDownloadSource
import io.github.flexarr.ui.podcasts.Tabular
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.RoomBackground
import io.github.flexarr.ui.theme.glowColor
import io.github.flexarr.ui.theme.marquee
import kotlinx.coroutines.async
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.launch

// The music screens, as the iPhone app's: a library (the server's rows, every artist), an artist, an album and a
// playlist. Each title page has Play and Shuffle; tapping a track plays from it, and tapping the one playing pauses it.

private const val PAGE_SIZE = 60

/**
 * A music library: the server's rows for it (playlists, mixes, what's been played, new albums), in the profile's order
 * and without the ones it switched off, then every artist as a round picture.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun MusicLibraryScreen(model: AppModel, libraryId: String) {
    val client = model.client ?: return
    val navigator = LocalNavigator.current
    val scope = rememberCoroutineScope()
    val library = rememberLoader(libraryId) { client.libraries().firstOrNull { it.id == libraryId } }
    val artists = remember(libraryId) { mutableStateListOf<ItemSummary>() }
    var total by remember(libraryId) { mutableIntStateOf(0) }
    var loading by remember(libraryId) { mutableStateOf(true) }
    var error by remember(libraryId) { mutableStateOf<String?>(null) }
    var attempt by remember { mutableIntStateOf(0) }
    var refreshing by remember { mutableStateOf(false) }
    var loadedAt by remember(libraryId) { mutableStateOf<Long?>(null) }
    var rows by remember(libraryId) { mutableStateOf(emptyList<HomeRow>()) }
    RefreshWhenStale(loadedAt) { attempt++ }

    LaunchedEffect(libraryId, attempt) {
        loading = true
        try {
            coroutineScope {
                // Rows that can't load leave the playlists (when those can) and the artists.
                val browse = async { client.musicRows(libraryId) }
                val page = client.libraryItems(libraryId, LibrarySort.Title, take = PAGE_SIZE)
                artists.clear()
                artists.addAll(page.items.filter { it.kind == MediaKind.Artist || it.kind == MediaKind.Unknown })
                total = page.total
                error = null
                loadedAt = System.currentTimeMillis()
                rows = browse.await().filter { it.showsInMusic }
            }
        } catch (e: CancellationException) {
            // A newer load took over: it says when it's done.
            throw e
        } catch (e: Exception) {
            if (artists.isEmpty()) error = e.message
        }
        loading = false
        refreshing = false
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
                    artists.addAll(page.items.filter { it.id !in known })
                    total = page.total
                }
            } catch (_: Exception) {
            } finally {
                loading = false
            }
        }
    }

    val name = (library.state as? Load.Loaded)?.value?.name.orEmpty()
    RoomBackground(ambient = (rows.firstNotNullOfOrNull { it.items.firstOrNull() } ?: artists.firstOrNull())?.let { glowColor(it.accentColor) }, intensity = 0.18f) {
        Column {
            PageTitle(name, onBack = { navigator.back() })
            val failed = error
            when {
                failed != null -> StatusView(failed, retry = { attempt++ })
                artists.isEmpty() && !loading -> StatusView("Nothing in this library yet.", empty = true)
                artists.isEmpty() -> StatusView()
                else -> PullToRefreshBox(isRefreshing = refreshing, onRefresh = { refreshing = true; attempt++ }) {
                    LazyVerticalGrid(
                        columns = GridCells.Adaptive(GridMinimum),
                        contentPadding = PaddingValues(start = Edge, end = Edge, bottom = 24.dp),
                        horizontalArrangement = Arrangement.spacedBy(12.dp),
                        verticalArrangement = Arrangement.spacedBy(20.dp),
                        modifier = Modifier.fillMaxSize(),
                    ) {
                        fullWidth {
                            Text(if (total == 1) "1 artist" else "${Format.count(total)} artists", fontSize = 15.sp, fontWeight = FontWeight.Medium, color = Flexarr.usher, style = Tabular)
                        }
                        // As the server lists them: the profile chooses their order and which show.
                        rows.forEach { row -> fullWidth { MusicShelf(model, row) } }
                        fullWidth { SectionHeading("Artists", Modifier.bleed()) }
                        itemsIndexed(artists, key = { _, artist -> artist.id }) { index, artist ->
                            LaunchedEffect(index, artists.size) { loadMoreIfNeeded(index) }
                            ArtistCard(artist)
                        }
                        if (loading) fullWidth { Box(Modifier.fillMaxWidth().padding(20.dp), contentAlignment = Alignment.Center) { CircularProgressIndicator(color = Flexarr.usher) } }
                    }
                }
            }
        }
    }
}

private fun LazyGridScope.fullWidth(content: @Composable () -> Unit) = item(span = { GridItemSpan(maxLineSpan) }) { content() }

/** One of the server's rows for a music library: playlists, tracks that play, or albums (and artists) that open. */
@Composable
private fun MusicShelf(model: AppModel, row: HomeRow) {
    val width = PosterWidth * 1.15f
    Shelf(row.title) {
        when (row.style) {
            HomeRowStyle.Playlists -> items(row.playlists.orEmpty(), key = { it.id }) { PlaylistCard(it, width) }
            // A row of tracks plays: the one tapped first, the rest of the row after it.
            HomeRowStyle.Tracks -> rowItemsIndexed(row.items, key = { _, item -> item.id }) { index, item ->
                TrackCard(model, item, width) { model.music.play(row.items, start = index) }
            }
            else -> items(row.items, key = { it.id }) { item ->
                if (item.kind == MediaKind.Artist) ArtistCard(item, Modifier.width(width)) else AlbumCard(item, width = width, showsArtist = true)
            }
        }
    }
}

/** A row this screen can draw, with something in it: not one of a kind it doesn't know (mixes, say), nor an empty one. */
private val HomeRow.showsInMusic: Boolean
    get() = when (style) {
        HomeRowStyle.Playlists -> !playlists.isNullOrEmpty()
        HomeRowStyle.Tracks, HomeRowStyle.Poster, HomeRowStyle.Wide -> items.isNotEmpty()
        else -> false
    }

/** A heading with a row of cards that scrolls sideways under it, reaching the screen's edges. */
@Composable
private fun Shelf(title: String, content: androidx.compose.foundation.lazy.LazyListScope.() -> Unit) {
    Column(Modifier.bleed(), verticalArrangement = Arrangement.spacedBy(10.dp)) {
        SectionHeading(title)
        LazyRow(contentPadding = PaddingValues(horizontal = Edge), horizontalArrangement = Arrangement.spacedBy(RowSpacing), verticalAlignment = Alignment.Top, content = content)
    }
}

/** Out past the grid's own edge to the screen's, for what draws its own edge (headings, rows that scroll sideways). */
private fun Modifier.bleed() = layout { measurable, constraints ->
    val edge = Edge.roundToPx()
    val placeable = measurable.measure(constraints.copy(minWidth = constraints.minWidth + 2 * edge, maxWidth = constraints.maxWidth + 2 * edge))
    layout(constraints.maxWidth, placeable.height) { placeable.place(-edge, 0) }
}

/** An artist's or an album's page, loaded once and again when it's getting old. */
@Composable
private fun rememberMusicPage(model: AppModel, id: String) = rememberLoader(id) { model.client!!.item(id) }

/** A title page's header: the art, the name and what's under it, and Play and Shuffle. */
@Composable
private fun MusicHeader(
    art: @Composable (Modifier) -> Unit,
    title: String,
    ambient: Color,
    below: @Composable () -> Unit,
    canPlay: Boolean,
    canShuffle: Boolean,
    busy: Boolean = false,
    onPlay: () -> Unit,
    onShuffle: () -> Unit,
    /** Beside Play and Shuffle: an album's or playlist's download button. */
    extra: @Composable () -> Unit = {},
) {
    Column(Modifier.fillMaxWidth().statusBarsPadding().padding(top = 12.dp, start = Edge, end = Edge), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(16.dp)) {
        art(Modifier)
        Column(horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(6.dp)) {
            Text(title, style = marquee(30), textAlign = TextAlign.Center, maxLines = 2, overflow = TextOverflow.Ellipsis)
            below()
        }
        Row(horizontalArrangement = Arrangement.spacedBy(12.dp), verticalAlignment = Alignment.CenterVertically) {
            ActionButton("Play", onClick = onPlay, icon = Icons.Filled.PlayArrow, prominent = true, ambient = ambient, enabled = canPlay && !busy)
            ActionButton("Shuffle", onClick = onShuffle, icon = Icons.Filled.Shuffle, ambient = ambient, enabled = canShuffle && !busy)
            extra()
            if (busy) CircularProgressIndicator(Modifier.size(22.dp), color = Flexarr.usher, strokeWidth = 2.dp)
        }
    }
}

/** Back, on its own over the page, as on the title pages. */
@Composable
private fun BackButton() {
    val navigator = LocalNavigator.current
    IconButton(
        onClick = { navigator.back() },
        modifier = Modifier.statusBarsPadding().padding(8.dp).clip(CircleShape).background(Flexarr.room.copy(alpha = 0.55f)),
    ) { Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = "Back", tint = Flexarr.screen) }
}

/** An artist: their picture and name, Play and Shuffle for everything they have, and their albums newest first. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ArtistScreen(model: AppModel, id: String) {
    val client = model.client ?: return
    val scope = rememberCoroutineScope()
    val page = rememberMusicPage(model, id)
    var queueing by remember { mutableStateOf(false) }
    val detail = (page.state as? Load.Loaded)?.value
    val ambient = detail?.item?.let { glowColor(it.accentColor) } ?: Flexarr.silver

    /** Everything they have (fetched when asked, not with the page, so a visit to an artist with a hundred albums stays light). */
    fun playAll(detail: ItemDetail, shuffled: Boolean) {
        queueing = true
        scope.launch {
            val tracks = runCatching { client.tracks(id) }.getOrDefault(emptyList())
            queueing = false
            model.music.play(tracks, shuffled = shuffled, albums = detail.children.filter { it.kind == MediaKind.Album })
        }
    }

    RoomBackground(ambient = ambient, intensity = 0.3f) {
        when (val state = page.state) {
            is Load.Loaded -> PullToRefreshBox(isRefreshing = page.refreshing, onRefresh = page.retry) {
                val item = state.value.item
                val albums = state.value.children.filter { it.kind == MediaKind.Album }
                LazyVerticalGrid(
                    columns = GridCells.Adaptive(105.dp),
                    contentPadding = PaddingValues(start = Edge, end = Edge, bottom = 32.dp),
                    horizontalArrangement = Arrangement.spacedBy(12.dp),
                    verticalArrangement = Arrangement.spacedBy(20.dp),
                    modifier = Modifier.fillMaxSize(),
                ) {
                    fullWidth {
                        MusicHeader(
                            art = { SquareArtwork(item, it.width(180.dp).shadow(20.dp, CircleShape), circular = true) },
                            title = item.title,
                            ambient = ambient,
                            below = {
                                FactsLine(item.facts(scores = !model.isKids))
                                if (state.value.genres.isNotEmpty()) {
                                    Text(state.value.genres.take(3).joinToString(", "), fontSize = 15.sp, fontWeight = FontWeight.Medium, color = Flexarr.usher, maxLines = 1)
                                }
                                state.value.overview?.takeIf { it.isNotEmpty() }?.let {
                                    Text(it, fontSize = 15.sp, color = Flexarr.screen.copy(alpha = 0.88f), maxLines = 4, overflow = TextOverflow.Ellipsis, modifier = Modifier.padding(top = 6.dp).widthIn(max = 680.dp))
                                }
                            },
                            canPlay = albums.isNotEmpty(),
                            canShuffle = albums.isNotEmpty(),
                            busy = queueing,
                            onPlay = { playAll(state.value, shuffled = false) },
                            onShuffle = { playAll(state.value, shuffled = true) },
                        )
                    }
                    if (albums.isNotEmpty()) {
                        fullWidth { SectionHeading("Albums", Modifier.bleed()) }
                        itemsIndexed(albums, key = { _, album -> album.id }) { _, album -> AlbumCard(album) }
                    } else {
                        fullWidth { Box(Modifier.height(160.dp)) { StatusView("No albums yet.", empty = true) } }
                    }
                }
            }
            is Load.Failed -> StatusView(state.message, page.retry)
            Load.Loading -> StatusView()
        }
        BackButton()
    }
}

/** An album: cover, title, artist and year, Play and Shuffle, and the tracklist (split by disc when there's more than one). */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun AlbumScreen(model: AppModel, id: String) {
    val navigator = LocalNavigator.current
    val page = rememberMusicPage(model, id)
    val music = model.music
    val detail = (page.state as? Load.Loaded)?.value
    val ambient = detail?.item?.let { glowColor(it.accentColor) } ?: Flexarr.silver

    RoomBackground(ambient = ambient, intensity = 0.3f) {
        when (val state = page.state) {
            is Load.Loaded -> PullToRefreshBox(isRefreshing = page.refreshing, onRefresh = page.retry) {
                val item = state.value.item
                val tracks = state.value.children.filter { it.kind == MediaKind.Track }
                val artist = state.value.ancestors.lastOrNull { it.kind == MediaKind.Artist }
                androidx.compose.foundation.lazy.LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 32.dp)) {
                    item {
                        MusicHeader(
                            art = { SquareArtwork(item, it.width(240.dp).shadow(20.dp, RoundedCornerShape(6.dp)), imageWidth = 800) },
                            title = item.title,
                            ambient = ambient,
                            below = {
                                if (artist != null) {
                                    Text(artist.title, fontSize = 17.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.silver, modifier = Modifier.pressable({ navigator.open(artist) }))
                                } else {
                                    item.artistName?.let { Text(it, fontSize = 17.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen.copy(alpha = 0.9f)) }
                                }
                                FactsLine(albumFacts(item, tracks))
                            },
                            canPlay = tracks.isNotEmpty(),
                            canShuffle = tracks.size >= 2,
                            onPlay = { music.play(tracks, albums = listOf(item)) },
                            onShuffle = { music.play(tracks, shuffled = true, albums = listOf(item)) },
                            extra = { MusicDownloadButton(model, MusicDownloadSource.Album(item, tracks)) },
                        )
                    }
                    if (tracks.isEmpty()) {
                        item { Box(Modifier.height(160.dp)) { StatusView("No tracks yet.", empty = true) } }
                    } else {
                        val discs = discs(tracks)
                        discs.forEach { (disc, discTracks) ->
                            if (discs.size > 1) {
                                item(key = "disc-$disc") {
                                    Text("Disc $disc", style = marquee(20, Flexarr.usher).copy(fontWeight = FontWeight.Bold), modifier = Modifier.padding(start = Edge + 4.dp, top = if (disc == discs.first().first) 20.dp else 40.dp, bottom = 6.dp))
                                }
                            }
                            discTracks.forEachIndexed { offset, track ->
                                item(key = track.id) {
                                    Box(Modifier.padding(horizontal = Edge - 8.dp).padding(top = if (offset == 0 && discs.size == 1) 20.dp else 0.dp)) {
                                        TrackRow(
                                            track, track.trackNumber ?: (offset + 1),
                                            isCurrent = music.current?.id == track.id, isPlaying = music.isPlaying,
                                            subtitle = track.trackArtist,
                                            isDownloaded = model.downloads.isDownloaded(track.id),
                                        ) {
                                            if (music.current?.id == track.id) music.togglePlayPause()
                                            else music.play(tracks, start = tracks.indexOf(track), albums = listOf(item))
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            is Load.Failed -> StatusView(state.message, page.retry)
            Load.Loading -> StatusView()
        }
        BackButton()
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
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun PlaylistScreen(model: AppModel, id: String) {
    val client = model.client ?: return
    val music = model.music
    val loader = rememberLoader(id) { client.playlist(id) }

    val detail: PlaylistDetail? = (loader.state as? Load.Loaded)?.value
    val tracks = detail?.tracks?.map { it.track }.orEmpty()
    RoomBackground(ambient = tracks.firstOrNull()?.let { glowColor(it.accentColor) }, intensity = 0.25f) {
        when (val state = loader.state) {
            is Load.Loaded -> PullToRefreshBox(isRefreshing = loader.refreshing, onRefresh = loader.retry) {
                val summary = state.value.summary
                androidx.compose.foundation.lazy.LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 32.dp)) {
                    item {
                        MusicHeader(
                            art = { PlaylistArtwork(summary, it.width(240.dp).shadow(20.dp, RoundedCornerShape(6.dp))) },
                            title = summary.name,
                            ambient = Flexarr.silver,
                            below = { FactsLine(listOfNotNull(summary.trackCountLabel, Format.runtime(summary.durationSeconds))) },
                            canPlay = tracks.isNotEmpty(),
                            canShuffle = tracks.size >= 2,
                            onPlay = { music.play(tracks) },
                            onShuffle = { music.play(tracks, shuffled = true) },
                            extra = { MusicDownloadButton(model, MusicDownloadSource.Playlist(summary, tracks)) },
                        )
                    }
                    if (tracks.isEmpty()) {
                        item { Box(Modifier.height(160.dp)) { StatusView("Nothing in this playlist yet.", empty = true) } }
                    }
                    // A track can be in a playlist twice: each place is its own row.
                    state.value.tracks.forEachIndexed { index, entry ->
                        item(key = entry.entryId) {
                            Box(Modifier.padding(horizontal = Edge - 8.dp).padding(top = if (index == 0) 20.dp else 0.dp)) {
                                TrackRow(entry.track, index + 1, isCurrent = music.current?.id == entry.track.id, isPlaying = music.isPlaying, subtitle = entry.track.artistName, isDownloaded = model.downloads.isDownloaded(entry.track.id)) {
                                    if (music.current?.id == entry.track.id) music.togglePlayPause() else music.play(tracks, start = index)
                                }
                            }
                        }
                    }
                }
            }
            is Load.Failed -> StatusView(state.message, loader.retry)
            Load.Loading -> StatusView()
        }
        BackButton()
    }
}
