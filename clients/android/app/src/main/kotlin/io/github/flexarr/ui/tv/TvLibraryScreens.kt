package io.github.flexarr.ui.tv

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.GridItemSpan
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.itemsIndexed
import androidx.compose.foundation.lazy.grid.rememberLazyGridState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.Sort
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.runtime.snapshotFlow
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.tv.material3.ClickableSurfaceDefaults
import androidx.tv.material3.Surface
import androidx.tv.material3.Text
import io.github.flexarr.AppModel
import io.github.flexarr.core.GenreCount
import io.github.flexarr.core.ItemSummary
import io.github.flexarr.core.Library
import io.github.flexarr.core.LibrarySort
import io.github.flexarr.core.LibraryType
import io.github.flexarr.core.alphabetLetter
import io.github.flexarr.core.alphabetLetters
import io.github.flexarr.core.emptyAlphabetLetters
import io.github.flexarr.ui.components.Load
import io.github.flexarr.ui.components.LocalNavigator
import io.github.flexarr.ui.components.RemoteImage
import io.github.flexarr.ui.components.rememberLoader
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.RoomBackground
import io.github.flexarr.ui.theme.glowColor
import io.github.flexarr.ui.theme.marquee
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

/**
 * A film or TV library's tab, as the Apple TV's LibraryTab: Browse, All or Genres, switched with the words at the top.
 * It opens on Settings › Libraries' choice until one's picked here. [takeFocus]: arriving from the sidebar, the first
 * title (or the hero's Play) takes focus once there is one, then [onFocusTaken].
 */
@Composable
fun TvLibraryTab(model: AppModel, library: Library, takeFocus: Boolean, onFocusTaken: () -> Unit) {
    val saved = TvLibraryOpening.saved(LocalContext.current)
    var picked by rememberSaveable(library.id, saved) { mutableStateOf<TvLibraryOpening?>(null) }
    // Just switched: the view taking over gives its switch focus, as the word pressed has gone.
    var switching by remember { mutableStateOf(false) }
    val chosen = picked ?: saved
    val view = if (chosen == TvLibraryOpening.Genres && !TvLibraryOpening.hasGenres(library)) TvLibraryOpening.Browse else chosen
    val switch: @Composable () -> Unit = {
        LibraryViewSwitch(library, view, takesFocus = switching, onFocusTaken = { switching = false }) {
            switching = it != view
            picked = it
        }
    }
    when (view) {
        TvLibraryOpening.Browse -> LibraryBrowse(model, library, takeFocus, onFocusTaken, switch)
        TvLibraryOpening.All -> TvLibraryGrid(model, library, takeFocus = takeFocus, onFocusTaken = onFocusTaken, top = switch)
        TvLibraryOpening.Genres -> LibraryGenres(model, library, takeFocus, onFocusTaken, switch)
    }
}

/**
 * "Browse", "All films" (or "All shows") and "Genres" as quiet words at the top-left: bright and underlined for the view
 * shown, dim otherwise, lifted onto a capsule by focus. Plain words rather than pills, so it doesn't read as a second
 * row of tabs.
 */
@Composable
private fun LibraryViewSwitch(library: Library, selected: TvLibraryOpening, takesFocus: Boolean, onFocusTaken: () -> Unit, onSelect: (TvLibraryOpening) -> Unit) {
    val all = when (library.type) {
        LibraryType.TvShows -> "All shows"
        LibraryType.HomeVideos -> "All folders"
        else -> "All films"
    }
    val choices = listOf(TvLibraryOpening.Browse to "Browse", TvLibraryOpening.All to all) +
        (if (TvLibraryOpening.hasGenres(library)) listOf(TvLibraryOpening.Genres to "Genres") else emptyList())
    val requester = remember { FocusRequester() }
    LaunchedEffect(takesFocus) {
        if (!takesFocus) return@LaunchedEffect
        runCatching { requester.requestFocus() }
        onFocusTaken()
    }
    // The words line up with the edge below them; the room either side is for the capsule focus puts behind them.
    Row(Modifier.offset(x = (-11).dp), horizontalArrangement = Arrangement.spacedBy(3.dp)) {
        choices.forEach { (choice, title) ->
            val isSelected = choice == selected
            Surface(
                onClick = { onSelect(choice) },
                modifier = Modifier.height(28.dp).then(if (isSelected) Modifier.focusRequester(requester) else Modifier),
                shape = ClickableSurfaceDefaults.shape(CircleShape),
                colors = ClickableSurfaceDefaults.colors(
                    containerColor = Color.Transparent,
                    contentColor = if (isSelected) Flexarr.screen else Flexarr.screen.copy(alpha = 0.55f),
                    focusedContainerColor = Flexarr.screen,
                    focusedContentColor = Flexarr.room,
                ),
                scale = ClickableSurfaceDefaults.scale(focusedScale = 1.06f),
            ) {
                Column(Modifier.padding(horizontal = 11.dp).align(Alignment.Center), horizontalAlignment = Alignment.CenterHorizontally) {
                    Text(title, fontSize = 14.sp, fontWeight = if (isSelected) FontWeight.Bold else FontWeight.Medium)
                    if (isSelected) Box(Modifier.padding(top = 1.dp).width(16.dp).height(1.5.dp).background(Flexarr.screen.copy(alpha = 0.8f), CircleShape))
                }
            }
        }
    }
}

/** A library's own home page, as on the web: a hero of its titles, then its rows (Continue watching, the latest, its Top 10…). */
@Composable
private fun LibraryBrowse(model: AppModel, library: Library, takeFocus: Boolean, onFocusTaken: () -> Unit, switch: @Composable () -> Unit) {
    val client = model.client ?: return
    val loader = rememberLoader(library.id, model.watchStateVersion) { client.libraryBrowse(library.id) }
    when (val state = loader.state) {
        is Load.Loaded -> {
            val browse = state.value
            if (browse.featured.isEmpty() && browse.rows.none { it.items.isNotEmpty() }) {
                LaunchedEffect(Unit) { if (takeFocus) onFocusTaken() }
                RoomBackground(ambient = null) {
                    Column(Modifier.padding(start = TvLayout.Edge, top = 28.dp)) {
                        switch()
                        TvStatusView(if (library.scan == null) "Nothing in this library yet." else "Titles appear here as the scan finds them.", empty = true)
                    }
                }
            } else {
                TvHomeRows(browse, emptyMessage = null, takeFocus = takeFocus, onFocusTaken = onFocusTaken, top = switch)
            }
        }
        is Load.Failed -> RoomBackground(ambient = null) {
            Column(Modifier.padding(start = TvLayout.Edge, top = 28.dp)) {
                switch()
                TvStatusView(state.message, retry = loader.retry)
            }
        }
        Load.Loading -> RoomBackground(ambient = null) { TvStatusView() }
    }
}

/** A tile for each of the library's genres, biggest first, with a backdrop from one of its titles. */
@Composable
private fun LibraryGenres(model: AppModel, library: Library, takeFocus: Boolean, onFocusTaken: () -> Unit, switch: @Composable () -> Unit) {
    val client = model.client ?: return
    val navigator = LocalNavigator.current
    val loader = rememberLoader(library.id) { client.libraryGenres(library.id) }
    val first = remember { FocusRequester() }
    RoomBackground(ambient = null) {
        when (val state = loader.state) {
            is Load.Loaded -> {
                val genres = state.value
                LaunchedEffect(takeFocus, genres.isNotEmpty()) {
                    if (!takeFocus) return@LaunchedEffect
                    if (genres.isNotEmpty()) runCatching { first.requestFocus() }
                    onFocusTaken()
                }
                LazyVerticalGrid(
                    GridCells.Fixed(GENRES_ACROSS),
                    contentPadding = PaddingValues(start = TvLayout.Edge, end = TvLayout.Edge, top = 28.dp, bottom = 40.dp),
                    horizontalArrangement = Arrangement.spacedBy(GENRE_SPACING),
                    verticalArrangement = Arrangement.spacedBy(22.dp),
                ) {
                    item(span = { GridItemSpan(maxLineSpan) }) { Box(Modifier.padding(bottom = 18.dp)) { switch() } }
                    if (genres.isEmpty()) {
                        item(span = { GridItemSpan(maxLineSpan) }) {
                            Box(Modifier.height(300.dp)) { TvStatusView("No genres here yet. They come with each title’s details once a scan has found them.", empty = true) }
                        }
                    }
                    itemsIndexed(genres, key = { _, genre -> genre.name }) { index, genre ->
                        TvGenreTile(genre.name, genreCaption(library, genre), genre.backdrop, modifier = if (index == 0) Modifier.focusRequester(first) else Modifier) {
                            navigator.openLibrary(library.id, LibrarySort.Title, genre.name)
                        }
                    }
                }
            }
            is Load.Failed -> TvStatusView(state.message, retry = loader.retry)
            Load.Loading -> TvStatusView()
        }
    }
}

private fun genreCaption(library: Library, genre: GenreCount): String {
    val noun = if (library.type == LibraryType.TvShows) (if (genre.count == 1) "show" else "shows") else (if (genre.count == 1) "film" else "films")
    return "${genre.count} $noun"
}

/** Five genres across the screen: a genre is a way in, not something to look at, so smaller than a row's cards. */
private const val GENRES_ACROSS = 5
private val GENRE_SPACING = 20.dp
private val GENRE_WIDTH = (960.dp - TvLayout.Edge * 2 - GENRE_SPACING * (GENRES_ACROSS - 1)) / GENRES_ACROSS

/** A genre as a way in: a backdrop from one of its titles, its name and how many there are. */
@Composable
private fun TvGenreTile(name: String, caption: String, backdrop: String?, modifier: Modifier = Modifier, onClick: () -> Unit) {
    TvArtworkButton(
        ambient = Flexarr.silver,
        onClick = onClick,
        modifier = modifier.width(GENRE_WIDTH),
        artwork = {
            RemoteImage(backdrop, 640, Modifier.size(GENRE_WIDTH, GENRE_WIDTH * 9f / 16f)) {
                Box(Modifier.fillMaxSize().background(Brush.linearGradient(listOf(Flexarr.silver.copy(alpha = 0.3f), Flexarr.seat))))
            }
        },
        caption = { focused ->
            Column(verticalArrangement = Arrangement.spacedBy(2.dp)) {
                Text(name, fontSize = 12.sp, fontWeight = FontWeight.SemiBold, color = if (focused) Flexarr.screen else Flexarr.screen.copy(alpha = 0.85f), maxLines = 1, overflow = TextOverflow.Ellipsis)
                Text(caption, fontSize = 10.sp, color = Flexarr.usher, maxLines = 1)
            }
        },
    )
}

/**
 * Every title in a library as a poster grid, paged as it scrolls, with sort and genre at the top and an A–Z rail down
 * the right to jump through it when sorted by title. On a tab, [top] is the Browse and All switch; opened from a row's
 * "See all" or a genre, the library's name heads it instead. As the Apple TV's LibraryView.
 */
@Composable
fun TvLibraryGrid(
    model: AppModel,
    library: Library,
    initialSort: LibrarySort = LibrarySort.Title,
    initialGenre: String? = null,
    takeFocus: Boolean = false,
    onFocusTaken: () -> Unit = {},
    top: (@Composable () -> Unit)? = null,
) {
    val client = model.client ?: return
    val navigator = LocalNavigator.current
    val scope = rememberCoroutineScope()
    var sort by rememberSaveable(library.id) { mutableStateOf(initialSort) }
    var genre by rememberSaveable(library.id) { mutableStateOf(initialGenre) }
    var items by remember { mutableStateOf(emptyList<ItemSummary>()) }
    var total by remember { mutableIntStateOf(0) }
    var loading by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }
    var loadedOnce by remember { mutableStateOf(false) }
    // Bumped by each reload (a new sort or genre), so an answer for the old one is dropped rather than shown or added.
    var generation by remember { mutableIntStateOf(0) }
    var jumping by remember { mutableStateOf<String?>(null) }
    var attempt by remember { mutableIntStateOf(0) }
    var choosingGenre by remember { mutableStateOf(false) }
    var focusTarget by remember { mutableStateOf<String?>(null) }
    val targetRequester = remember { FocusRequester() }
    val firstRequester = remember { FocusRequester() }
    val grid = rememberLazyGridState()
    val genres = rememberLoader(library.id) { client.libraryGenres(library.id) }
    val showsLetters = sort == LibrarySort.Title && total > PAGE_SIZE

    // The first page, again whenever the sort or genre changes, and when what's watched may have changed (the list is
    // fetched again up to as far as it had got, so the place in it stays).
    LaunchedEffect(sort, genre, model.watchStateVersion, attempt) {
        val current = ++generation
        loading = true
        try {
            val wanted = items.size.coerceIn(PAGE_SIZE, MAX_PAGE_SIZE)
            val page = client.libraryItems(library.id, sort, genre, skip = 0, take = if (loadedOnce) wanted else PAGE_SIZE)
            if (current != generation) return@LaunchedEffect
            items = page.items
            total = page.total
            error = null
            loadedOnce = true
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            if (current == generation) error = e.message
        } finally {
            if (current == generation) loading = false
        }
    }
    // More as the grid nears the end of what's loaded.
    LaunchedEffect(grid, sort, genre) {
        snapshotFlow { grid.layoutInfo.visibleItemsInfo.lastOrNull()?.index ?: 0 }.collect { last ->
            if (loading || items.size >= total || last < items.size - 18) return@collect
            val current = generation
            val skip = items.size
            loading = true
            try {
                val page = client.libraryItems(library.id, sort, genre, skip = skip, take = PAGE_SIZE)
                if (current == generation && skip == items.size) {
                    val known = items.map { it.id }.toSet()
                    items = items + page.items.filter { it.id !in known }
                    total = page.total
                }
            } catch (e: CancellationException) {
                throw e
            } catch (_: Exception) {
                // Tried again on the next scroll.
            } finally {
                if (current == generation) loading = false
            }
        }
    }
    // Arriving from the sidebar: the first title, once it's there.
    LaunchedEffect(takeFocus, items.firstOrNull()?.id) {
        if (!takeFocus || items.isEmpty()) return@LaunchedEffect
        delay(50)
        runCatching { firstRequester.requestFocus() }
        onFocusTaken()
    }
    // A letter jumped to: its first title takes focus once the grid has brought it in.
    LaunchedEffect(focusTarget) {
        if (focusTarget == null) return@LaunchedEffect
        delay(150)
        runCatching { targetRequester.requestFocus() }
        focusTarget = null
    }

    /** Loads titles until the first under [letter] is in the grid, then scrolls to it and gives it focus. */
    fun jump(letter: String) {
        if (jumping != null) return
        val current = generation
        jumping = letter
        scope.launch {
            try {
                while (current == generation) {
                    val index = items.indexOfFirst { alphabetLetter(it.title) == letter }
                    if (index >= 0) {
                        // The header is the grid's first item.
                        grid.scrollToItem(index + 1)
                        focusTarget = items[index].id
                        return@launch
                    }
                    val empty = emptyAlphabetLetters(items.map { alphabetLetter(it.title) }, items.size >= total)
                    if (letter in empty || items.size >= total) return@launch
                    val page = runCatching { client.libraryItems(library.id, sort, genre, skip = items.size, take = MAX_PAGE_SIZE) }.getOrNull()
                    if (page == null || current != generation || page.items.isEmpty()) return@launch
                    val known = items.map { it.id }.toSet()
                    items = items + page.items.filter { it.id !in known }
                    total = page.total
                }
            } finally {
                jumping = null
            }
        }
    }

    val trailing = if (showsLetters) RAIL_GUTTER else TvLayout.Edge
    val spacing = (960.dp - TvLayout.Edge - trailing - TvLayout.PosterWidth * 6) / 5
    RoomBackground(ambient = items.firstOrNull()?.let { glowColor(it.accentColor) }, intensity = 0.18f) {
        LazyVerticalGrid(
            GridCells.Fixed(6),
            state = grid,
            contentPadding = PaddingValues(start = TvLayout.Edge, end = trailing, top = if (top == null) 20.dp else 28.dp, bottom = 40.dp),
            horizontalArrangement = Arrangement.spacedBy(spacing),
            verticalArrangement = Arrangement.spacedBy(28.dp),
        ) {
            item(span = { GridItemSpan(maxLineSpan) }) {
                Header(library, top, total, sort, onSort = { sort = it }, genre, hasGenres = (genres.state as? Load.Loaded)?.value?.isNotEmpty() == true, onGenre = { choosingGenre = true })
            }
            when {
                error != null && items.isEmpty() -> item(span = { GridItemSpan(maxLineSpan) }) {
                    Box(Modifier.height(300.dp)) { TvStatusView(error, retry = { attempt++ }) }
                }
                items.isEmpty() && loadedOnce && !loading -> item(span = { GridItemSpan(maxLineSpan) }) {
                    Box(Modifier.height(300.dp)) { TvStatusView(genre?.let { "No $it titles here." } ?: "Nothing in this library yet.", empty = true) }
                }
                else -> itemsIndexed(items, key = { _, item -> item.id }) { index, item ->
                    val modifier = when {
                        item.id == focusTarget -> Modifier.focusRequester(targetRequester)
                        index == 0 -> Modifier.focusRequester(firstRequester)
                        else -> Modifier
                    }
                    TvPosterCard(item, onClick = { navigator.open(item) }, modifier = modifier)
                }
            }
            if (loading && items.isNotEmpty()) {
                item(span = { GridItemSpan(maxLineSpan) }) {
                    Box(Modifier.fillMaxWidth().padding(20.dp), contentAlignment = Alignment.Center) { CircularProgressIndicator(color = Flexarr.screen) }
                }
            }
        }
        if (showsLetters) {
            val empty = emptyAlphabetLetters(items.map { alphabetLetter(it.title) }, items.size >= total)
            LetterRail(empty, jumping, ::jump, Modifier.align(Alignment.CenterEnd).padding(end = TvLayout.Edge - 10.dp))
        }
    }
    if (choosingGenre) {
        val all = (genres.state as? Load.Loaded)?.value.orEmpty()
        TvChoiceMenu(
            "Genre",
            listOf("All genres" to null) + all.map { "${it.name}  (${it.count})" to it.name },
            selected = genre,
            onChoose = {
                genre = it
                choosingGenre = false
            },
            onDismiss = { choosingGenre = false },
        )
    }
}

/** The library's name (or the view switch) and how many titles, then the sort pills and the genre at the right. */
@Composable
private fun Header(
    library: Library,
    top: (@Composable () -> Unit)?,
    total: Int,
    sort: LibrarySort,
    onSort: (LibrarySort) -> Unit,
    genre: String?,
    hasGenres: Boolean,
    onGenre: () -> Unit,
) {
    Row(Modifier.padding(bottom = 6.dp), verticalAlignment = Alignment.Bottom) {
        Column(verticalArrangement = Arrangement.spacedBy(if (top == null) 2.dp else 8.dp)) {
            if (top != null) top() else Text(library.name, style = marquee(38).copy(fontWeight = FontWeight.ExtraBold))
            Text(if (total == 1) "1 title" else "%,d titles".format(total), fontSize = 12.sp, fontWeight = FontWeight.Medium, color = Flexarr.usher)
        }
        Spacer(Modifier.weight(1f))
        Row(horizontalArrangement = Arrangement.spacedBy(9.dp)) {
            LibrarySort.entries.forEach { option -> TvPill(option.label, selected = sort == option, onClick = { onSort(option) }) }
            if (hasGenres) TvPill(genre ?: "All genres", selected = genre != null, onClick = onGenre)
        }
    }
}

/** The A–Z rail down the right of the grid. Right from the last column reaches it; a letter with nothing under it can't be picked. */
@Composable
private fun LetterRail(empty: Set<String>, jumping: String?, onJump: (String) -> Unit, modifier: Modifier) {
    Column(modifier) {
        alphabetLetters.forEach { letter ->
            val enabled = letter !in empty
            Surface(
                onClick = { onJump(letter) },
                enabled = enabled,
                modifier = Modifier.size(27.dp, 15.5.dp),
                shape = ClickableSurfaceDefaults.shape(CircleShape),
                colors = ClickableSurfaceDefaults.colors(
                    containerColor = Color.Transparent,
                    contentColor = Flexarr.screen.copy(alpha = 0.8f),
                    focusedContainerColor = Flexarr.screen,
                    focusedContentColor = Flexarr.room,
                    disabledContainerColor = Color.Transparent,
                    disabledContentColor = Flexarr.usher.copy(alpha = 0.35f),
                ),
                scale = ClickableSurfaceDefaults.scale(focusedScale = 1.3f),
            ) {
                Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                    if (jumping == letter) {
                        CircularProgressIndicator(Modifier.size(10.dp), strokeWidth = 1.5.dp, color = Flexarr.screen)
                    } else {
                        Text(letter, fontSize = 11.sp, fontWeight = FontWeight.SemiBold)
                    }
                }
            }
        }
    }
}

/** A short list to choose one from (a genre), with the current choice marked, as the Apple TV's menu. */
@Composable
fun <T> TvChoiceMenu(title: String, choices: List<Pair<String, T>>, selected: T, onChoose: (T) -> Unit, onDismiss: () -> Unit) {
    val current = remember { FocusRequester() }
    androidx.compose.ui.window.Dialog(onDismissRequest = onDismiss) {
        androidx.compose.foundation.lazy.LazyColumn(
            Modifier.width(320.dp).background(Flexarr.seat, RoundedCornerShape(14.dp)),
            contentPadding = PaddingValues(20.dp),
            verticalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            item { Text(title, fontSize = 15.sp, fontWeight = FontWeight.Bold, modifier = Modifier.padding(bottom = 4.dp)) }
            items(choices.size) { index ->
                val (label, value) = choices[index]
                TvActionButton(
                    if (value == selected) "✓  $label" else label,
                    onClick = { onChoose(value) },
                    modifier = Modifier.fillMaxWidth().then(if (value == selected) Modifier.focusRequester(current) else Modifier),
                )
            }
        }
    }
    LaunchedEffect(Unit) {
        delay(50)
        runCatching { current.requestFocus() }
    }
}

private const val PAGE_SIZE = 60

/** The most the server sends in one page (ItemEndpoints' MaxPageSize). */
private const val MAX_PAGE_SIZE = 500

/** Room on the right for the A–Z rail; the six posters close up a little to make it. */
private val RAIL_GUTTER = 85.dp
