package io.github.flexarr.ui.library

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
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.GridItemSpan
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.lazy.grid.itemsIndexed
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.material3.SegmentedButton
import androidx.compose.material3.SegmentedButtonDefaults
import androidx.compose.material3.SingleChoiceSegmentedButtonRow
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.clearAndSetSemantics
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.role
import androidx.compose.ui.text.style.TextOverflow
import io.github.flexarr.core.LIVE_TV_ORDER_ID
import io.github.flexarr.core.ServerVersion
import io.github.flexarr.core.sortedByLibraryOrder
import io.github.flexarr.ui.components.RemoteImage
import io.github.flexarr.ui.components.isWide
import io.github.flexarr.ui.theme.marquee
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.KeyboardArrowRight
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.FilterList
import androidx.compose.material.icons.filled.Folder
import androidx.compose.material.icons.filled.Mic
import androidx.compose.material.icons.filled.SettingsInputAntenna
import androidx.compose.material.icons.filled.Movie
import androidx.compose.material.icons.filled.MusicNote
import androidx.compose.material.icons.filled.Tv
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
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
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.github.flexarr.AppModel
import io.github.flexarr.core.GenreCount
import io.github.flexarr.core.ItemSummary
import io.github.flexarr.core.Library
import io.github.flexarr.core.LibrarySort
import io.github.flexarr.core.LibraryType
import io.github.flexarr.ui.components.GridMinimum
import io.github.flexarr.ui.components.Edge
import io.github.flexarr.ui.components.Load
import io.github.flexarr.ui.components.LocalNavigator
import io.github.flexarr.ui.components.Loaded
import io.github.flexarr.ui.components.PageTitle
import io.github.flexarr.ui.components.PosterCard
import io.github.flexarr.ui.components.RefreshWhenStale
import io.github.flexarr.ui.components.StatusView
import io.github.flexarr.ui.components.pressable
import io.github.flexarr.ui.components.rememberLoader
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.RoomBackground
import io.github.flexarr.ui.theme.glowColor
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.launch
import java.text.NumberFormat

/** The libraries this profile can see, each opening its poster grid. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun LibrariesScreen(model: AppModel) {
    val client = model.client ?: return
    val navigator = LocalNavigator.current
    val loader = rememberLoader(model.credentials, model.librariesVersion) { client.libraries() }
    RoomBackground(ambient = null) {
        Column {
            PageTitle("Libraries")
            Loaded(loader) { libraries ->
                PullToRefreshBox(isRefreshing = loader.refreshing, onRefresh = loader.retry) {
                    val entries = LibraryMenuEntry.ordered(model, libraries)
                    LazyColumn(contentPadding = PaddingValues(horizontal = Edge, vertical = 8.dp), modifier = Modifier.fillMaxSize()) {
                        item {
                            Column(Modifier.clip(RoundedCornerShape(12.dp)).background(Flexarr.seat)) {
                                entries.forEachIndexed { index, entry ->
                                    if (index > 0) HorizontalDivider(Modifier.padding(start = 62.dp), color = Flexarr.aisle.copy(alpha = 0.6f))
                                    LibraryRow(entry.title, entry.icon) {
                                        when (entry) {
                                            is LibraryMenuEntry.Shelf -> navigator.openLibrary(entry.library)
                                            is LibraryMenuEntry.Podcasts -> navigator.openPodcasts()
                                            LibraryMenuEntry.LiveTv -> navigator.openLiveTv()
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun LibraryRow(name: String, icon: ImageVector, onClick: () -> Unit) {
    Row(
        Modifier.fillMaxWidth().pressable(onClick).padding(horizontal = 16.dp, vertical = 14.dp),
        horizontalArrangement = Arrangement.spacedBy(14.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Icon(icon, contentDescription = null, tint = Flexarr.silver, modifier = Modifier.width(32.dp))
        Text(name, fontSize = 17.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen, modifier = Modifier.weight(1f))
        Icon(Icons.AutoMirrored.Filled.KeyboardArrowRight, contentDescription = null, tint = Flexarr.usher)
    }
}

/** The libraries the Libraries tab lists. Podcasts are one place of their own (see [LibraryMenuEntry.Podcasts]). */
internal val Library.inMenu get() = type != LibraryType.Podcasts

/** A place in the Libraries tab: a library, Podcasts or Live TV, in the order the profile chose. As on iPhone. */
internal sealed interface LibraryMenuEntry {
    /** Its place in the profile's library order. */
    val id: String
    val title: String
    val icon: ImageVector

    /** One of the server's libraries. */
    data class Shelf(val library: Library) : LibraryMenuEntry {
        override val id get() = library.id
        override val title get() = library.name
        override val icon get() = library.icon
    }

    /** Podcasts are one place however many libraries hold them; [id] is their library's, when there is one. */
    data class Podcasts(override val id: String) : LibraryMenuEntry {
        override val title get() = "Podcasts"
        override val icon get() = Icons.Filled.Mic
    }

    data object LiveTv : LibraryMenuEntry {
        override val id get() = LIVE_TV_ORDER_ID
        override val title get() = "Live TV"
        override val icon get() = Icons.Filled.SettingsInputAntenna
    }

    companion object {
        /**
         * The Libraries tab's places in the profile's order (Settings › Library order); any it doesn't mention yet,
         * such as a new library, after them. Podcasts once the server has them (it says how to set them up when no
         * library holds them), and Live TV once it has channels.
         */
        fun ordered(model: AppModel, libraries: List<Library>): List<LibraryMenuEntry> {
            val entries = buildList {
                libraries.filter { it.inMenu }.forEach { add(Shelf(it)) }
                if (model.podcastStatus != null) add(Podcasts(libraries.firstOrNull { it.type == LibraryType.Podcasts }?.id ?: "podcasts"))
                if (model.hasLiveTv) add(LiveTv)
            }
            return sortedByLibraryOrder(entries, model.currentProfile?.libraryOrder.orEmpty()) { it.id }
        }
    }
}

internal val Library.icon
    get() = when (type) {
        LibraryType.TvShows -> Icons.Filled.Tv
        LibraryType.Music -> Icons.Filled.MusicNote
        LibraryType.HomeVideos -> Icons.Filled.Folder
        else -> Icons.Filled.Movie
    }

private const val PAGE_SIZE = 60

private enum class Showing { All, Genres }

/**
 * Poster grid for one library, a page at a time as it's scrolled, with sort and genre in the corner. A film or TV
 * library also has Genres: a tile for each, opening its titles in that genre ([initialGenre], under the genre's name).
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun LibraryScreen(model: AppModel, libraryId: String, initialSort: LibrarySort, initialGenre: String? = null) {
    val client = model.client ?: return
    val navigator = LocalNavigator.current
    val scope = rememberCoroutineScope()
    var sort by rememberSaveable { mutableStateOf(initialSort) }
    var genre by rememberSaveable { mutableStateOf(initialGenre) }
    var showing by rememberSaveable { mutableStateOf(Showing.All) }
    val library = rememberLoader(libraryId) { client.libraries().firstOrNull { it.id == libraryId } }
    val genres = rememberLoader(libraryId) { runCatching { client.libraryGenres(libraryId) }.getOrDefault(emptyList()) }
    // Genre tiles have their pictures from servers of 0.54 on; older ones keep to the menu's genres.
    val tilesServer = rememberLoader(model.credentials) {
        runCatching { ServerVersion.atLeast(client.systemStatus().version, 0, 54) }.getOrDefault(false)
    }
    val items = remember(libraryId, sort, genre) { mutableStateListOf<ItemSummary>() }
    var total by remember(libraryId, sort, genre) { mutableIntStateOf(0) }
    var loading by remember(libraryId, sort, genre) { mutableStateOf(true) }
    var refreshing by remember { mutableStateOf(false) }
    var error by remember(libraryId, sort, genre) { mutableStateOf<String?>(null) }
    var attempt by remember { mutableIntStateOf(0) }
    var loadedAt by remember(libraryId, sort, genre) { mutableStateOf<Long?>(null) }
    var staleVersion by remember { mutableIntStateOf(0) }
    // Back to a list more than a minute old: what's on screen is fetched again, keeping the scroll position.
    RefreshWhenStale(loadedAt) { staleVersion++ }

    LaunchedEffect(libraryId, sort, genre, attempt) {
        loading = true
        try {
            val page = client.libraryItems(libraryId, sort, genre, skip = 0, take = PAGE_SIZE)
            items.clear()
            items.addAll(page.items)
            total = page.total
            error = null
            loadedAt = System.currentTimeMillis()
        } catch (e: CancellationException) {
            // A newer load (another sort, say) took over: it says when it's done.
            throw e
        } catch (e: Exception) {
            if (items.isEmpty()) error = e.message
        }
        loading = false
    }

    // After playback or watched changes, fetch what's on screen again so badges update, keeping the scroll position.
    LaunchedEffect(model.watchStateVersion, staleVersion) {
        if (items.isEmpty()) return@LaunchedEffect
        runCatching { client.libraryItems(libraryId, sort, genre, skip = 0, take = items.size.coerceIn(PAGE_SIZE, 500)) }
            .onSuccess { page ->
                val fresh = page.items.associateBy { it.id }
                items.replaceAll { fresh[it.id] ?: it }
                total = page.total
                loadedAt = System.currentTimeMillis()
            }
    }

    fun loadMoreIfNeeded(index: Int) {
        if (loading || items.size >= total || index < items.size - 18) return
        loading = true
        val skip = items.size
        scope.launch {
            try {
                val page = client.libraryItems(libraryId, sort, genre, skip = skip, take = PAGE_SIZE)
                // Dropped if the list changed while it was out.
                if (skip == items.size) {
                    val known = items.mapTo(HashSet()) { it.id }
                    items.addAll(page.items.filter { it.id !in known })
                    total = page.total
                }
            } catch (_: Exception) {
            } finally {
                loading = false
            }
        }
    }

    val loadedLibrary = (library.state as? Load.Loaded)?.value
    val genreList = (genres.state as? Load.Loaded)?.value.orEmpty()
    // Films and shows have genres to browse by; a genre's own page doesn't offer them again.
    val hasGenres = initialGenre == null && (tilesServer.state as? Load.Loaded)?.value == true &&
        (loadedLibrary?.type == LibraryType.Movies || loadedLibrary?.type == LibraryType.TvShows)
    val shows = if (hasGenres) showing else Showing.All
    RoomBackground(ambient = items.firstOrNull()?.let { glowColor(it.accentColor) }, intensity = 0.18f) {
        Column {
            PageTitle(initialGenre ?: loadedLibrary?.name.orEmpty(), onBack = { navigator.back() }) {
                if (shows == Showing.All) SortAndFilter(sort, { sort = it }, genre, { genre = it }, genreList)
            }
            if (hasGenres) {
                ShowingSwitch(if (loadedLibrary?.type == LibraryType.TvShows) "All shows" else "All films", shows) { showing = it }
            }
            val failed = error
            when {
                shows == Showing.Genres -> LibraryGenreGrid(loadedLibrary!!, genreList)
                failed != null -> StatusView(failed, retry = { attempt++ })
                items.isEmpty() && !loading -> StatusView(genre?.let { "No $it titles here." } ?: "Nothing in this library yet.", empty = true)
                else -> PullToRefreshBox(
                    isRefreshing = refreshing,
                    onRefresh = {
                        refreshing = true
                        scope.launch {
                            runCatching { client.libraryItems(libraryId, sort, genre, skip = 0, take = PAGE_SIZE) }.onSuccess { page ->
                                items.clear()
                                items.addAll(page.items)
                                total = page.total
                            }
                            refreshing = false
                        }
                    },
                ) {
                    LazyVerticalGrid(
                        columns = GridCells.Adaptive(GridMinimum),
                        contentPadding = PaddingValues(start = Edge, end = Edge, bottom = 32.dp),
                        horizontalArrangement = Arrangement.spacedBy(12.dp),
                        verticalArrangement = Arrangement.spacedBy(20.dp),
                        modifier = Modifier.fillMaxSize(),
                    ) {
                        item(span = { GridItemSpan(maxLineSpan) }) {
                            Text(
                                if (total == 1) "1 title" else "${NumberFormat.getIntegerInstance().format(total)} titles",
                                fontSize = 15.sp,
                                fontWeight = FontWeight.Medium,
                                color = Flexarr.usher,
                                modifier = Modifier.height(if (items.isEmpty()) 0.dp else 20.dp),
                            )
                        }
                        itemsIndexed(items, key = { _, item -> item.id }) { index, item ->
                            LaunchedEffect(index, items.size) { loadMoreIfNeeded(index) }
                            PosterCard(item, width = null)
                        }
                        if (loading) {
                            item(span = { GridItemSpan(maxLineSpan) }) {
                                Box(Modifier.fillMaxWidth().padding(24.dp), contentAlignment = Alignment.Center) { CircularProgressIndicator(color = Flexarr.screen) }
                            }
                        }
                    }
                }
            }
        }
    }
}

/** Sort by, and the library's genres to narrow it to. */
@Composable
private fun SortAndFilter(sort: LibrarySort, onSort: (LibrarySort) -> Unit, genre: String?, onGenre: (String?) -> Unit, genres: List<GenreCount>) {
    var open by remember { mutableStateOf(false) }
    Box {
        IconButton(onClick = { open = true }) {
            Icon(Icons.Filled.FilterList, contentDescription = "Sort and filter", tint = if (genre == null) Flexarr.screen else Flexarr.silver)
        }
        DropdownMenu(expanded = open, onDismissRequest = { open = false }) {
            Text("Sort by", fontSize = 13.sp, color = Flexarr.usher, modifier = Modifier.padding(horizontal = 16.dp, vertical = 6.dp))
            LibrarySort.entries.forEach { option ->
                DropdownMenuItem(
                    text = { Text(option.label, fontWeight = if (option == sort) FontWeight.Bold else FontWeight.Normal) },
                    trailingIcon = { if (option == sort) Icon(Icons.Filled.Check, contentDescription = null, modifier = Modifier.size(18.dp)) },
                    onClick = {
                        open = false
                        onSort(option)
                    },
                )
            }
            if (genres.isNotEmpty()) {
                HorizontalDivider(color = Flexarr.aisle)
                Text("Genre", fontSize = 13.sp, color = Flexarr.usher, modifier = Modifier.padding(horizontal = 16.dp, vertical = 6.dp))
                val choices = listOf<GenreCount?>(null) + genres
                choices.forEach { choice ->
                    val selected = choice?.name == genre
                    DropdownMenuItem(
                        text = { Text(choice?.let { "${it.name} (${it.count})" } ?: "All genres", fontWeight = if (selected) FontWeight.Bold else FontWeight.Normal) },
                        trailingIcon = { if (selected) Icon(Icons.Filled.Check, contentDescription = null, modifier = Modifier.size(18.dp)) },
                        onClick = {
                            open = false
                            onGenre(choice?.name)
                        },
                    )
                }
            }
        }
    }
}

/** "All films" (or "All shows") or Genres, at the top of a film or TV library. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun ShowingSwitch(allLabel: String, showing: Showing, onChange: (Showing) -> Unit) {
    SingleChoiceSegmentedButtonRow(Modifier.fillMaxWidth().padding(horizontal = Edge).padding(bottom = 12.dp)) {
        listOf(allLabel to Showing.All, "Genres" to Showing.Genres).forEachIndexed { index, (label, value) ->
            SegmentedButton(
                selected = showing == value,
                onClick = { onChange(value) },
                shape = SegmentedButtonDefaults.itemShape(index, 2),
                icon = {},
                colors = SegmentedButtonDefaults.colors(activeContainerColor = Flexarr.aisle, activeContentColor = Flexarr.screen, inactiveContainerColor = Flexarr.seat, inactiveContentColor = Flexarr.usher, activeBorderColor = Flexarr.aisle, inactiveBorderColor = Flexarr.aisle),
            ) { Text(label, fontSize = 13.sp) }
        }
    }
}

/** A film or TV library's genres as tiles: a backdrop from one of its titles, the name and how many there are. */
@Composable
private fun LibraryGenreGrid(library: Library, genres: List<GenreCount>) {
    val navigator = LocalNavigator.current
    if (genres.isEmpty()) {
        StatusView("No genres here yet. They come with each title’s details once a scan has found them.", empty = true)
        return
    }
    LazyVerticalGrid(
        columns = GridCells.Adaptive(if (isWide) 240.dp else 150.dp),
        contentPadding = PaddingValues(start = Edge, end = Edge, bottom = 32.dp),
        horizontalArrangement = Arrangement.spacedBy(12.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
        modifier = Modifier.fillMaxSize(),
    ) {
        items(genres, key = { it.name }) { genre ->
            val caption = genreCaption(library, genre)
            Box(
                Modifier.fillMaxWidth().aspectRatio(16f / 9f).clip(RoundedCornerShape(10.dp)).background(Flexarr.seat)
                    .pressable({ navigator.openLibrary(library.id, LibrarySort.Title, genre.name) })
                    .clearAndSetSemantics {
                        contentDescription = "${genre.name}, $caption"
                        role = Role.Button
                    },
            ) {
                RemoteImage(genre.backdrop, 640, Modifier.matchParentSize()) {
                    Box(Modifier.fillMaxSize().background(Brush.linearGradient(listOf(Flexarr.silver.copy(alpha = 0.3f), Flexarr.seat))))
                }
                Box(Modifier.matchParentSize().background(Brush.verticalGradient(0.5f to Flexarr.room.copy(alpha = 0.1f), 1f to Flexarr.room.copy(alpha = 0.92f))))
                Column(Modifier.align(Alignment.BottomStart).padding(10.dp), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                    Text(genre.name, style = marquee(20).copy(fontWeight = FontWeight.ExtraBold), maxLines = 1, overflow = TextOverflow.Ellipsis)
                    Text(caption, fontSize = 12.sp, fontWeight = FontWeight.Medium, color = Flexarr.usher, maxLines = 1)
                }
            }
        }
    }
}

/** "12 films", "1 show". */
private fun genreCaption(library: Library, genre: GenreCount): String {
    val noun = if (library.type == LibraryType.TvShows) (if (genre.count == 1) "show" else "shows") else (if (genre.count == 1) "film" else "films")
    return "${genre.count} $noun"
}
