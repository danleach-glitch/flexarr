package io.github.flexarr.ui.music

import io.github.flexarr.core.i18n.t
import androidx.compose.runtime.setValue
import androidx.compose.runtime.getValue
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyGridState
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.itemsIndexed
import androidx.compose.foundation.lazy.grid.rememberLazyGridState
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.FilterList
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.Text
import androidx.compose.material3.pulltorefresh.PullToRefreshBox
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewmodel.compose.viewModel
import io.github.flexarr.AppModel
import io.github.flexarr.core.Home
import io.github.flexarr.core.ItemSummary
import io.github.flexarr.core.LibrarySort
import io.github.flexarr.core.MediaKind
import io.github.flexarr.core.MusicView
import io.github.flexarr.core.Paged
import io.github.flexarr.core.albumCount
import io.github.flexarr.core.artistCount
import io.github.flexarr.core.artistName
import io.github.flexarr.core.facts
import io.github.flexarr.core.libraryAlbumGenres
import io.github.flexarr.core.libraryAlbums
import io.github.flexarr.core.musicBrowse
import io.github.flexarr.ui.components.ActionButton
import io.github.flexarr.ui.components.Edge
import io.github.flexarr.ui.components.FactsLine
import io.github.flexarr.ui.components.GridMinimum
import io.github.flexarr.ui.components.Load
import io.github.flexarr.ui.components.LocalNavigator
import io.github.flexarr.ui.components.PageTitle
import io.github.flexarr.ui.components.PosterWidth
import io.github.flexarr.ui.components.RemoteImage
import io.github.flexarr.ui.components.StatusView
import io.github.flexarr.ui.components.TitleArt
import io.github.flexarr.ui.components.pressable
import io.github.flexarr.ui.components.rememberLoader
import io.github.flexarr.ui.home.HomeHero
import io.github.flexarr.ui.home.fadeOut
import io.github.flexarr.ui.library.LibraryGenreGrid
import io.github.flexarr.ui.library.ShowingSwitch
import io.github.flexarr.ui.podcasts.Tabular
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.FlexarrIcons
import io.github.flexarr.ui.theme.RoomBackground
import io.github.flexarr.ui.theme.glowColor
import io.github.flexarr.ui.theme.marquee
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.async
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.launch

// A music library, as a film or TV library and the Apple TV's: Browse (a hero, the server's rows, Your artists),
// Artists, Albums and Genres, switched at the top. Albums and Genres need a server from 0.59.

/** How many artists Browse's Your artists row shows before its See all. */
private const val ARTISTS_IN_ROW = 20

private const val PAGE_SIZE = 60

/** Browse's hero and rows, with the first artists for Your artists. */
private class MusicBrowse(val home: Home, val artists: List<ItemSummary>, val artistTotal: Int)

/**
 * A music library. With [genre], that genre's albums alone under its name, with Play and Shuffle for its tracks and no
 * views to switch between: Back goes to the genres. With [grid] (Artists or Albums), that grid alone under the
 * library's name, sorted by [sort]: Your artists' See all, a row's link. Each view keeps its place while another is shown.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun MusicLibraryScreen(model: AppModel, libraryId: String, genre: String? = null, grid: MusicView? = null, sort: LibrarySort = LibrarySort.Title) {
    if (genre != null) return MusicGenreScreen(model, libraryId, genre)
    if (grid == MusicView.Artists || grid == MusicView.Albums) return MusicGridScreen(model, libraryId, grid, sort)
    val client = model.client ?: return
    val navigator = LocalNavigator.current
    val library = rememberLoader(libraryId) { client.libraries().firstOrNull { it.id == libraryId } }
    var view by rememberSaveable { mutableStateOf(MusicView.Browse) }
    var artistSort by rememberSaveable { mutableStateOf(LibrarySort.Title) }
    var albumSort by rememberSaveable { mutableStateOf(LibrarySort.Title) }
    // Albums and Genres need a server that lists albums (0.59 on): offered once that's known, never before.
    val albumsOffered = rememberLoader(libraryId, model.credentials) { model.offersMusicAlbums(libraryId) }
    val offersAlbums = (albumsOffered.state as? Load.Loaded)?.value == true
    val shown = if (view.needsAlbums && !offersAlbums) MusicView.Browse else view
    val browse = rememberLoader(libraryId, model.watchStateVersion, model.musicArtistsVersion) {
        coroutineScope {
            val first = async { client.libraryItems(libraryId, LibrarySort.Title, take = ARTISTS_IN_ROW) }
            val home = client.musicBrowse(libraryId)
            val artists = first.await()
            MusicBrowse(home.copy(rows = home.rows.filter { it.showsInMusic }), artists.items.filter { it.kind == MediaKind.Artist || it.kind == MediaKind.Unknown }, artists.total)
        }
    }
    // Each grid's pages and place, kept while another view is shown and while an album or artist opened from it is
    // (the pages in this screen's own ViewModel, the place saved with it), so Back returns to where it was.
    // Again when an artist has left the library from here (a placeholder unfollowed), so they don't stay listed.
    val pagers = viewModel { MusicPagers() }
    val artists = remember(libraryId, artistSort, model.musicArtistsVersion) {
        pagers.pager("artists", client, libraryId, artistSort, model.musicArtistsVersion) { skip, take -> client.libraryItems(libraryId, artistSort, skip = skip, take = take) }
    }
    val albums = remember(libraryId, albumSort) {
        pagers.pager("albums", client, libraryId, albumSort) { skip, take -> client.libraryAlbums(libraryId, albumSort, skip = skip, take = take) ?: Paged(0, emptyList()) }
    }
    val artistGrid = rememberSaveable(artistSort, saver = LazyGridState.Saver) { LazyGridState() }
    val albumGrid = rememberSaveable(albumSort, saver = LazyGridState.Saver) { LazyGridState() }
    val browseGrid = rememberLazyGridState()
    // Loaded the first time Genres is shown, then kept.
    var genresOpened by rememberSaveable { mutableStateOf(false) }
    LaunchedEffect(shown) { if (shown == MusicView.Genres) genresOpened = true }
    val genres = rememberLoader(libraryId, genresOpened) { if (genresOpened) client.libraryAlbumGenres(libraryId).orEmpty() else null }
    var featuredIndex by remember { mutableIntStateOf(0) }

    val loaded = (browse.state as? Load.Loaded)?.value
    val featured = loaded?.home?.featured.orEmpty()
    val ambient = when (shown) {
        MusicView.Browse -> featured.takeIf { it.isNotEmpty() }?.let { it[featuredIndex % it.size] }
            ?: loaded?.home?.rows?.firstNotNullOfOrNull { it.items.firstOrNull() } ?: loaded?.artists?.firstOrNull()
        MusicView.Artists -> artists.items.firstOrNull()
        MusicView.Albums -> albums.items.firstOrNull()
        MusicView.Genres -> null
    }
    RoomBackground(ambient = ambient?.let { glowColor(it.accentColor) }, intensity = if (shown == MusicView.Browse && featured.isNotEmpty()) 0.28f else 0.18f) {
        Column {
            PageTitle((library.state as? Load.Loaded)?.value?.name.orEmpty(), onBack = { navigator.back() }) {
                when (shown) {
                    MusicView.Artists -> SortMenu(shown.sorts, artistSort) { artistSort = it }
                    MusicView.Albums -> SortMenu(shown.sorts, albumSort) { albumSort = it }
                    else -> Unit
                }
            }
            ShowingSwitch(MusicView.offered(offersAlbums).map { it.label to it }, shown) { view = it }
            when (shown) {
                MusicView.Browse -> when (val state = browse.state) {
                    is Load.Loaded -> PullToRefreshBox(isRefreshing = browse.refreshing, onRefresh = browse.retry) {
                        BrowseView(model, state.value, browseGrid, onFeatured = { featuredIndex = it }, onSeeAllArtists = { navigator.openMusicGrid(libraryId, albums = false) })
                    }
                    is Load.Failed -> StatusView(state.message, browse.retry)
                    Load.Loading -> StatusView()
                }
                MusicView.Artists -> MusicGrid(artists, artistGrid, emptyMessage = "Nothing in this library yet.", count = ::artistCount) { ArtistCard(it) }
                MusicView.Albums -> MusicGrid(albums, albumGrid, emptyMessage = "No albums yet.", count = ::albumCount) { AlbumCard(it, showsArtist = true) }
                MusicView.Genres -> when (val state = genres.state) {
                    is Load.Loaded -> state.value?.let { list -> LibraryGenreGrid(list, caption = { albumCount(it.count) }, music = true) { navigator.openMusicGenre(libraryId, it.name) } } ?: StatusView()
                    is Load.Failed -> StatusView(state.message, genres.retry)
                    Load.Loading -> StatusView()
                }
            }
        }
    }
}

/** Browse: the hero (servers from 0.59), the server's rows in the profile's order, then Your artists. */
@Composable
private fun BrowseView(model: AppModel, browse: MusicBrowse, state: LazyGridState, onFeatured: (Int) -> Unit, onSeeAllArtists: () -> Unit) {
    val home = browse.home
    if (home.featured.isEmpty() && home.rows.isEmpty() && browse.artists.isEmpty()) return StatusView("Nothing in this library yet.", empty = true)
    LazyVerticalGrid(
        columns = GridCells.Fixed(1),
        state = state,
        contentPadding = PaddingValues(start = Edge, end = Edge, bottom = 24.dp),
        verticalArrangement = Arrangement.spacedBy(28.dp),
        modifier = Modifier.fillMaxSize(),
    ) {
        if (home.featured.isNotEmpty()) {
            fullWidth(key = "hero") {
                Box(Modifier.bleed()) { HomeHero(home, onIndex = onFeatured) { item, height -> MusicHeroSlide(home, item, height) } }
            }
        }
        // As the server lists them: the profile chooses their order and which show.
        home.rows.forEach { row -> fullWidth(key = "row-${row.id}") { MusicShelf(model, row) } }
        if (browse.artists.isNotEmpty()) {
            fullWidth(key = "your-artists") {
                val width = PosterWidth * 1.15f
                Shelf("Your artists", onSeeAll = onSeeAllArtists.takeIf { browse.artistTotal > browse.artists.size }, seeAllLabel = "See all artists") {
                    items(browse.artists, key = { it.id }) { ArtistCard(it, Modifier.width(width)) }
                }
            }
        }
    }
}

/**
 * A slide of a music library's hero: an album (its artist above its title, since the backdrop is theirs; Resume or Play
 * from where it was left) or an artist (Play shuffles them; Radio plays them and artists like them). No scores.
 */
@Composable
private fun MusicHeroSlide(home: Home, item: ItemSummary, height: Dp) {
    val navigator = LocalNavigator.current
    val glow = glowColor(item.accentColor)
    Box(Modifier.fillMaxWidth().heightIn(min = height).pressable({ navigator.open(item) }, onClickLabel = "Open")) {
        RemoteImage(item.artwork.backdrop, 1000, Modifier.matchParentSize().fadeOut()) {
            Box(Modifier.fillMaxSize().background(Brush.verticalGradient(listOf(glow.copy(alpha = 0.4f), Flexarr.room))))
        }
        Box(Modifier.matchParentSize().background(Brush.verticalGradient(0.25f to Color.Transparent, 1f to Flexarr.room.copy(alpha = 0.7f))))
        Column(Modifier.align(Alignment.BottomStart).padding(start = Edge, end = Edge, bottom = 44.dp, top = 160.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            if (item.kind == MediaKind.Album) {
                Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
                    item.artistName?.let { Text(it, fontSize = 17.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen.copy(alpha = 0.85f), maxLines = 1, overflow = TextOverflow.Ellipsis) }
                    Text(item.title, style = marquee(40).copy(fontWeight = FontWeight.Black), maxLines = 2, overflow = TextOverflow.Ellipsis, modifier = Modifier.widthIn(max = 520.dp))
                }
            } else {
                TitleArt(item, maxWidth = 280.dp, maxHeight = 110.dp, fontSize = 52)
            }
            FactsLine(item.facts(scores = false))
            home.overviews[item.id]?.let {
                Text(it, fontSize = 15.sp, color = Flexarr.screen.copy(alpha = 0.85f), maxLines = 2, overflow = TextOverflow.Ellipsis, modifier = Modifier.widthIn(max = 520.dp))
            }
            Row(horizontalArrangement = Arrangement.spacedBy(10.dp), modifier = Modifier.padding(top = 4.dp)) {
                if (item.kind == MediaKind.Album) {
                    val resume = home.resuming(item)
                    ActionButton(if (resume != null) "Resume" else "Play", onClick = { navigator.playAlbum(item, resume) }, icon = FlexarrIcons.play, prominent = true, ambient = glow)
                } else {
                    ActionButton("Play", onClick = { navigator.playArtist(item, radio = false) }, icon = FlexarrIcons.play, prominent = true, ambient = glow)
                    ActionButton("Radio", onClick = { navigator.playArtist(item, radio = true) }, icon = FlexarrIcons.radio, ambient = glow)
                }
                ActionButton("More info", onClick = { navigator.open(item) }, icon = FlexarrIcons.details, ambient = glow)
            }
        }
    }
}

/** A music library's artists or albums ([view]) alone, under its name, opened on top of its page. */
@Composable
private fun MusicGridScreen(model: AppModel, libraryId: String, view: MusicView, initialSort: LibrarySort) {
    val client = model.client ?: return
    val navigator = LocalNavigator.current
    val library = rememberLoader(libraryId) { client.libraries().firstOrNull { it.id == libraryId } }
    val albums = view == MusicView.Albums
    var sort by rememberSaveable { mutableStateOf(initialSort.takeIf { it in view.sorts } ?: LibrarySort.Title) }
    val pagers = viewModel { MusicPagers() }
    val version = if (albums) 0 else model.musicArtistsVersion
    val pager = remember(libraryId, albums, sort, version) {
        pagers.pager("grid", client, libraryId, albums, sort, version) { skip, take ->
            if (albums) client.libraryAlbums(libraryId, sort, skip = skip, take = take) ?: Paged(0, emptyList())
            else client.libraryItems(libraryId, sort, skip = skip, take = take)
        }
    }
    val grid = rememberSaveable(sort, saver = LazyGridState.Saver) { LazyGridState() }
    RoomBackground(ambient = pager.items.firstOrNull()?.let { glowColor(it.accentColor) }, intensity = 0.18f) {
        Column {
            PageTitle((library.state as? Load.Loaded)?.value?.name.orEmpty(), onBack = { navigator.back() }) { SortMenu(view.sorts, sort) { sort = it } }
            if (albums) {
                MusicGrid(pager, grid, emptyMessage = "No albums yet.", count = ::albumCount) { AlbumCard(it, showsArtist = true) }
            } else {
                MusicGrid(pager, grid, emptyMessage = "Nothing in this library yet.", count = ::artistCount) { ArtistCard(it) }
            }
        }
    }
}

/** A genre's albums, under its name, with Play and Shuffle for its tracks (up to 200, as the server picks them). */
@Composable
private fun MusicGenreScreen(model: AppModel, libraryId: String, genre: String) {
    val client = model.client ?: return
    val navigator = LocalNavigator.current
    var sort by rememberSaveable { mutableStateOf(LibrarySort.Title) }
    val pagers = viewModel { MusicPagers() }
    val albums = remember(libraryId, genre, sort) {
        pagers.pager("genre", client, libraryId, genre, sort) { skip, take -> client.libraryAlbums(libraryId, sort, genre, skip = skip, take = take) ?: Paged(0, emptyList()) }
    }
    val grid = rememberSaveable(sort, saver = LazyGridState.Saver) { LazyGridState() }
    val ambient = albums.items.firstOrNull()?.let { glowColor(it.accentColor) }
    RoomBackground(ambient = ambient, intensity = 0.18f) {
        Column {
            PageTitle(genre, onBack = { navigator.back() }) { SortMenu(MusicView.Albums.sorts, sort) { sort = it } }
            MusicGrid(albums, grid, emptyMessage = "No $genre albums here.", count = ::albumCount, header = {
                Row(Modifier.padding(bottom = 4.dp), horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                    val glow = ambient ?: Flexarr.silver
                    ActionButton("Play", onClick = { navigator.playGenre(libraryId, genre, shuffled = false) }, icon = FlexarrIcons.play, prominent = true, ambient = glow)
                    ActionButton("Shuffle", onClick = { navigator.playGenre(libraryId, genre, shuffled = true) }, icon = FlexarrIcons.shuffle, ambient = glow)
                }
            }) { AlbumCard(it, showsArtist = true) }
        }
    }
}

/**
 * A music screen's pagers, kept with its place in the back stack (Navigation drops what's only remembered while
 * another screen is on top), one for each [slot] and replaced when what it lists ([keys]) changes.
 */
internal class MusicPagers : ViewModel() {
    private val slots = HashMap<String, Pair<List<Any?>, MusicPager>>()

    fun pager(slot: String, vararg keys: Any?, page: suspend (skip: Int, take: Int) -> Paged<ItemSummary>): MusicPager {
        val listed = keys.toList()
        slots[slot]?.takeIf { it.first == listed }?.let { return it.second }
        return MusicPager(page).also { slots[slot] = listed to it }
    }
}

/** Pages of a music library's artists or albums, loaded as the grid nears the end of what's in. */
internal class MusicPager(private val page: suspend (skip: Int, take: Int) -> Paged<ItemSummary>) {
    val items = mutableStateListOf<ItemSummary>()
    var total by mutableIntStateOf(0)
    var loading by mutableStateOf(false)
    var loaded by mutableStateOf(false)
    var error by mutableStateOf<String?>(null)

    suspend fun reload() {
        loading = true
        try {
            val first = page(0, PAGE_SIZE)
            items.clear()
            items.addAll(first.items)
            total = first.total
            error = null
            loaded = true
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            if (items.isEmpty()) error = e.message ?: "Something went wrong."
        } finally {
            loading = false
        }
    }

    suspend fun more(index: Int) {
        if (loading || items.size >= total || index < items.size - 18) return
        loading = true
        try {
            val skip = items.size
            val next = page(skip, PAGE_SIZE)
            // Dropped if the list changed while it was out.
            if (skip == items.size) {
                val known = items.mapTo(HashSet()) { it.id }
                items.addAll(next.items.filter { it.id !in known })
                total = next.total
            }
        } catch (e: CancellationException) {
            throw e
        } catch (_: Exception) {
            // Tried again as the next card comes into view.
        } finally {
            loading = false
        }
    }
}

/** Artists or albums in a grid with how many there are at the top (after [header]), a page at a time as it's scrolled. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun MusicGrid(
    pager: MusicPager,
    state: LazyGridState,
    emptyMessage: String,
    count: (Int) -> String,
    header: (@Composable () -> Unit)? = null,
    cell: @Composable (ItemSummary) -> Unit,
) {
    val scope = rememberCoroutineScope()
    var refreshing by remember { mutableStateOf(false) }
    // The first page once, as the view is first shown; it then keeps what it has.
    LaunchedEffect(pager) { if (!pager.loaded) pager.reload() }
    val failed = pager.error
    when {
        failed != null -> StatusView(failed, retry = { scope.launch { pager.reload() } })
        pager.items.isEmpty() && !pager.loaded -> StatusView()
        pager.items.isEmpty() && header == null -> StatusView(emptyMessage, empty = true)
        else -> PullToRefreshBox(isRefreshing = refreshing, onRefresh = {
            refreshing = true
            scope.launch {
                pager.reload()
                refreshing = false
            }
        }) {
            LazyVerticalGrid(
                columns = GridCells.Adaptive(GridMinimum),
                state = state,
                contentPadding = PaddingValues(start = Edge, end = Edge, bottom = 24.dp),
                horizontalArrangement = Arrangement.spacedBy(12.dp),
                verticalArrangement = Arrangement.spacedBy(20.dp),
                modifier = Modifier.fillMaxSize(),
            ) {
                header?.let { fullWidth(key = "header") { it() } }
                fullWidth(key = "count") {
                    Text(if (pager.items.isEmpty()) emptyMessage else count(pager.total), fontSize = 15.sp, fontWeight = FontWeight.Medium, color = Flexarr.usher, style = Tabular)
                }
                itemsIndexed(pager.items, key = { _, item -> item.id }) { index, item ->
                    LaunchedEffect(index, pager.items.size) { pager.more(index) }
                    cell(item)
                }
                if (pager.loading && pager.items.isNotEmpty()) {
                    fullWidth(key = "loading") { Box(Modifier.fillMaxWidth().padding(20.dp), contentAlignment = Alignment.Center) { CircularProgressIndicator(color = Flexarr.usher) } }
                }
            }
        }
    }
}

/** Sort by, for a music library's artists or albums. */
@Composable
private fun SortMenu(sorts: List<LibrarySort>, sort: LibrarySort, onSort: (LibrarySort) -> Unit) {
    var open by remember { mutableStateOf(false) }
    Box {
        IconButton(onClick = { open = true }) { Icon(Icons.Filled.FilterList, contentDescription = t("Sort"), tint = Flexarr.screen) }
        DropdownMenu(expanded = open, onDismissRequest = { open = false }) {
            Text(t("Sort by"), fontSize = 13.sp, color = Flexarr.usher, modifier = Modifier.padding(horizontal = 16.dp, vertical = 6.dp))
            sorts.forEach { option ->
                DropdownMenuItem(
                    text = { Text(option.label, fontWeight = if (option == sort) FontWeight.Bold else FontWeight.Normal) },
                    trailingIcon = { if (option == sort) Icon(Icons.Filled.Check, contentDescription = null, modifier = Modifier.size(18.dp)) },
                    onClick = {
                        open = false
                        onSort(option)
                    },
                )
            }
        }
    }
}
