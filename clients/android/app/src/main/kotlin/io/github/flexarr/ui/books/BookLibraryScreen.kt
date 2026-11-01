package io.github.flexarr.ui.books

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyListScope
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.GridItemSpan
import androidx.compose.foundation.lazy.grid.LazyGridScope
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.itemsIndexed
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.FilterList
import androidx.compose.material.icons.automirrored.filled.KeyboardArrowRight
import androidx.compose.material.icons.automirrored.filled.Sort
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
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.github.flexarr.AppModel
import io.github.flexarr.core.Artwork
import io.github.flexarr.core.BookPerson
import io.github.flexarr.core.BookPersonRole
import io.github.flexarr.core.BookProgressFilter
import io.github.flexarr.core.BookQuery
import io.github.flexarr.core.BookSeriesSummary
import io.github.flexarr.core.BookShelves
import io.github.flexarr.core.BookSort
import io.github.flexarr.core.Format
import io.github.flexarr.core.GenreCount
import io.github.flexarr.core.ItemSummary
import io.github.flexarr.core.Paged
import io.github.flexarr.core.bookPeople
import io.github.flexarr.core.bookSeriesList
import io.github.flexarr.core.bookShelves
import io.github.flexarr.core.books
import io.github.flexarr.ui.components.Edge
import io.github.flexarr.ui.components.GridMinimum
import io.github.flexarr.ui.components.Load
import io.github.flexarr.ui.components.LocalNavigator
import io.github.flexarr.ui.components.Loaded
import io.github.flexarr.ui.components.PageTitle
import io.github.flexarr.ui.components.PersonCard
import io.github.flexarr.ui.components.PillButton
import io.github.flexarr.ui.components.PosterCard
import io.github.flexarr.ui.components.PosterWidth
import io.github.flexarr.ui.components.RemoteImage
import io.github.flexarr.ui.components.RowSpacing
import io.github.flexarr.ui.components.SectionHeading
import io.github.flexarr.ui.components.StatusView
import io.github.flexarr.ui.components.pressable
import io.github.flexarr.ui.components.rememberLoader
import io.github.flexarr.ui.podcasts.Tabular
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.RoomBackground
import io.github.flexarr.ui.theme.glowColor
import io.github.flexarr.ui.theme.marquee
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.launch

// An audiobooks library, as the iPhone app's music library is laid out: shelves to browse, then every book (with filters
// and orders), the series, the authors and the genres.

private enum class BooksView(val label: String) { Shelves("Browse"), All("All books"), Series("Series"), Authors("Authors"), Genres("Genres") }

/** An audiobooks library; [view] "" opens its shelves, or a [BookSort]'s raw value every book in that order. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun BookLibraryScreen(model: AppModel, libraryId: String, view: String) {
    val client = model.client ?: return
    val navigator = LocalNavigator.current
    val library = rememberLoader(libraryId) { client.libraries().firstOrNull { it.id == libraryId } }
    val initialSort = BookSort.entries.firstOrNull { it.raw == view }
    var shown by rememberSaveable { mutableStateOf(if (initialSort != null) BooksView.All else BooksView.Shelves) }
    var query by remember { mutableStateOf(BookQuery(sort = initialSort ?: BookSort.Title, descending = initialSort == BookSort.Added)) }
    val name = (library.state as? Load.Loaded)?.value?.name.orEmpty()
    RoomBackground(ambient = null) {
        Column {
            PageTitle(name, onBack = { navigator.back() }) {
                if (shown == BooksView.All) {
                    FilterMenu(query) { query = it }
                    SortMenu(query) { query = it }
                }
            }
            Row(
                Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()).padding(horizontal = Edge).padding(bottom = 8.dp),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                BooksView.entries.forEach { view -> PillButton(view.label, view == shown, { shown = view }) }
            }
            when (shown) {
                BooksView.Shelves -> Shelves(model, libraryId)
                BooksView.All -> AllBooks(model, libraryId, query) { query = BookQuery(sort = query.sort, descending = query.descending) }
                BooksView.Series -> SeriesGrid(model, libraryId)
                BooksView.Authors -> AuthorsGrid(model, libraryId)
                BooksView.Genres -> GenreList(model, libraryId) { genre ->
                    query = BookQuery(genre = genre)
                    shown = BooksView.All
                }
            }
        }
    }
}

/** The library's front page: what's on the go, what's next, what's new, its series and authors, and what's done. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun Shelves(model: AppModel, libraryId: String) {
    val client = model.client ?: return
    val navigator = LocalNavigator.current
    val loader = rememberLoader(libraryId, model.watchStateVersion) { client.bookShelves(libraryId) }
    Loaded(loader) { shelves: BookShelves ->
        val empty = shelves.recentlyAdded.isEmpty() && shelves.continueListening.isEmpty() && shelves.series.isEmpty()
        if (empty) return@Loaded StatusView("No books yet.", empty = true)
        PullToRefreshBox(isRefreshing = loader.refreshing, onRefresh = loader.retry) {
            LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 32.dp), verticalArrangement = Arrangement.spacedBy(28.dp)) {
                bookShelf("Continue listening", shelves.continueListening, plays = { navigator.playBook(it) })
                bookShelf("Next in series", shelves.nextInSeries)
                bookShelf("Recently added", shelves.recentlyAdded)
                if (shelves.series.isNotEmpty()) item("series") {
                    Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                        SectionHeading("Series")
                        LazyRow(contentPadding = PaddingValues(horizontal = Edge), horizontalArrangement = Arrangement.spacedBy(RowSpacing)) {
                            items(shelves.series, key = { it.id }) { SeriesCard(it, PosterWidth * 1.15f) { navigator.openBookSeries(it.id) } }
                        }
                    }
                }
                peopleShelf("Authors", shelves.authors)
                bookShelf("Finished", shelves.finished)
            }
        }
    }
}

private fun LazyListScope.bookShelf(title: String, books: List<ItemSummary>, plays: ((ItemSummary) -> Unit)? = null) {
    if (books.isEmpty()) return
    item(title) {
        Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
            SectionHeading(title)
            LazyRow(contentPadding = PaddingValues(horizontal = Edge), horizontalArrangement = Arrangement.spacedBy(RowSpacing)) {
                items(books, key = { it.id }) { book ->
                    PosterCard(book, width = PosterWidth * 1.15f, onClick = plays?.let { play -> { play(book) } })
                }
            }
        }
    }
}

private fun LazyListScope.peopleShelf(title: String, people: List<BookPerson>) {
    if (people.isEmpty()) return
    item(title) {
        val navigator = LocalNavigator.current
        Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
            SectionHeading(title)
            LazyRow(contentPadding = PaddingValues(horizontal = Edge), horizontalArrangement = Arrangement.spacedBy(RowSpacing)) {
                items(people, key = { it.id }) { person ->
                    PersonCard(person.name, bookCount(person.bookCount), person.image) { navigator.openPerson(person.id, person.name) }
                }
            }
        }
    }
}

private fun bookCount(count: Int) = if (count == 1) "1 book" else "${Format.count(count)} books"

/** A series: the covers of its first books fanned behind one another, its name, and how far through it this profile is. */
@Composable
internal fun SeriesCard(series: BookSeriesSummary, width: Dp?, onClick: () -> Unit) {
    Column(
        (if (width != null) Modifier.width(width) else Modifier.fillMaxWidth()).pressable(onClick),
        verticalArrangement = Arrangement.spacedBy(6.dp),
    ) {
        StackedCovers(series.covers, series.name, series.next?.accentColor)
        Column(verticalArrangement = Arrangement.spacedBy(1.dp)) {
            Text(series.name, fontSize = 13.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen.copy(alpha = 0.9f), maxLines = 1, overflow = TextOverflow.Ellipsis)
            val done = series.finishedCount
            Text(
                if (done > 0) "$done of ${series.bookCount} finished" else bookCount(series.bookCount),
                fontSize = 12.sp, color = Flexarr.usher, maxLines = 1, style = Tabular,
            )
        }
    }
}

/** Up to three covers, the first in front, the rest peeking out behind it. */
@Composable
private fun StackedCovers(covers: List<Artwork>, name: String, accent: String?) {
    Box(Modifier.fillMaxWidth().aspectRatio(1f)) {
        val shown = covers.take(3).ifEmpty { listOf(Artwork()) }
        shown.indices.reversed().forEach { index ->
            val step = (index * 7).dp
            Box(
                Modifier.fillMaxSize(0.86f).align(Alignment.BottomStart).offset(x = step, y = -step)
                    .shadow(if (index == 0) 6.dp else 2.dp, RoundedCornerShape(6.dp))
                    .clip(RoundedCornerShape(6.dp))
                    .background(Flexarr.seat),
            ) {
                RemoteImage(shown[index].poster, 342, Modifier.fillMaxSize()) {
                    Box(Modifier.fillMaxSize().background(Brush.verticalGradient(listOf(glowColor(accent).copy(alpha = 0.35f), Flexarr.seat))), contentAlignment = Alignment.Center) {
                        if (index == 0) Text(name, style = marquee(16, Flexarr.screen.copy(alpha = 0.8f)).copy(fontWeight = FontWeight.Bold), modifier = Modifier.padding(8.dp))
                    }
                }
            }
        }
    }
}

/** Pages of something a library lists, loaded as the grid nears its end. */
private class Pager<T>(private val key: (T) -> String, private val page: suspend (skip: Int, take: Int) -> Paged<T>) {
    val items = mutableStateListOf<T>()
    var total by mutableIntStateOf(0)
    var loading by mutableStateOf(false)
    var error by mutableStateOf<String?>(null)

    suspend fun reload() {
        loading = true
        try {
            val first = page(0, PAGE)
            items.clear()
            items.addAll(first.items)
            total = first.total
            error = null
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
            val next = page(skip, PAGE)
            if (skip == items.size) {
                val known = items.mapTo(HashSet(), key)
                items.addAll(next.items.filter { key(it) !in known })
                total = next.total
            }
        } catch (e: CancellationException) {
            throw e
        } catch (_: Exception) {
        } finally {
            loading = false
        }
    }

    companion object {
        const val PAGE = 60
    }
}

@Composable
private fun <T> PagedGrid(
    pager: Pager<T>,
    emptyMessage: String,
    retry: () -> Unit,
    minimum: Dp = GridMinimum,
    /** Offered beneath [emptyMessage]: a way out of filters that match nothing. */
    emptyAction: Pair<String, () -> Unit>? = null,
    header: (LazyGridScope.() -> Unit)? = null,
    cell: @Composable (T) -> Unit,
) {
    val scope = rememberCoroutineScope()
    val failed = pager.error
    when {
        failed != null -> StatusView(failed, retry)
        pager.items.isEmpty() && pager.loading -> StatusView()
        pager.items.isEmpty() && emptyAction != null -> Column(
            Modifier.fillMaxSize().padding(32.dp),
            verticalArrangement = Arrangement.spacedBy(16.dp, Alignment.CenterVertically),
            horizontalAlignment = Alignment.CenterHorizontally,
        ) {
            Text(emptyMessage, fontSize = 17.sp, fontWeight = FontWeight.Medium, color = Flexarr.usher)
            PillButton(emptyAction.first, false, emptyAction.second)
        }
        pager.items.isEmpty() -> StatusView(emptyMessage, empty = true)
        else -> LazyVerticalGrid(
            columns = GridCells.Adaptive(minimum),
            contentPadding = PaddingValues(start = Edge, end = Edge, bottom = 24.dp),
            horizontalArrangement = Arrangement.spacedBy(12.dp),
            verticalArrangement = Arrangement.spacedBy(20.dp),
            modifier = Modifier.fillMaxSize(),
        ) {
            header?.invoke(this)
            itemsIndexed(pager.items) { index, entry ->
                LaunchedEffect(index) { scope.launch { pager.more(index) } }
                cell(entry)
            }
        }
    }
}

/** Every book, in the order and with the filters chosen. */
@Composable
private fun AllBooks(model: AppModel, libraryId: String, query: BookQuery, clearFilters: () -> Unit) {
    val client = model.client ?: return
    val pager = remember(libraryId, query) { Pager<ItemSummary>({ it.id }) { skip, take -> client.books(libraryId, query, skip, take) } }
    var attempt by remember { mutableIntStateOf(0) }
    LaunchedEffect(pager, attempt, model.watchStateVersion) { pager.reload() }
    PagedGrid(
        pager,
        if (query.isFiltered) "No books match." else "No books yet.",
        retry = { attempt++ },
        emptyAction = if (query.isFiltered) "Clear filters" to clearFilters else null,
        header = {
            item(span = { GridItemSpan(maxLineSpan) }) {
                val words = listOfNotNull(query.genre, query.progress?.label, lengthLabel(query)).joinToString("   ")
                Text(
                    listOfNotNull(bookCount(pager.total), words.ifEmpty { null }).joinToString("   "),
                    fontSize = 15.sp, fontWeight = FontWeight.Medium, color = Flexarr.usher, style = Tabular,
                )
            }
        },
    ) { book -> PosterCard(book, width = null) }
}

/** The lengths a list can be narrowed to, in hours. */
private val lengths = listOf(
    "Under 5h" to (null to 5.0),
    "5–10h" to (5.0 to 10.0),
    "10–20h" to (10.0 to 20.0),
    "Over 20h" to (20.0 to null),
)

private fun lengthLabel(query: BookQuery): String? = lengths.firstOrNull { it.second == (query.minHours to query.maxHours) }?.first

@Composable
private fun FilterMenu(query: BookQuery, onChange: (BookQuery) -> Unit) {
    var open by remember { mutableStateOf(false) }
    Box {
        IconButton(onClick = { open = true }) {
            Icon(Icons.Filled.FilterList, contentDescription = "Filter", tint = if (query.isFiltered) Flexarr.silver else Flexarr.screen)
        }
        DropdownMenu(open, onDismissRequest = { open = false }, containerColor = Flexarr.seat) {
            Choice("All books", !query.isFiltered) { open = false; onChange(BookQuery(sort = query.sort, descending = query.descending)) }
            HorizontalDivider(color = Flexarr.aisle)
            BookProgressFilter.entries.forEach { filter ->
                Choice(filter.label, query.progress == filter) { open = false; onChange(query.copy(progress = if (query.progress == filter) null else filter)) }
            }
            HorizontalDivider(color = Flexarr.aisle)
            lengths.forEach { (label, range) ->
                val chosen = (query.minHours to query.maxHours) == range
                Choice(label, chosen) {
                    open = false
                    onChange(if (chosen) query.copy(minHours = null, maxHours = null) else query.copy(minHours = range.first, maxHours = range.second))
                }
            }
            HorizontalDivider(color = Flexarr.aisle)
            Choice("Unabridged only", query.abridged == false) { open = false; onChange(query.copy(abridged = if (query.abridged == false) null else false)) }
        }
    }
}

@Composable
private fun SortMenu(query: BookQuery, onChange: (BookQuery) -> Unit) {
    var open by remember { mutableStateOf(false) }
    Box {
        IconButton(onClick = { open = true }) { Icon(Icons.AutoMirrored.Filled.Sort, contentDescription = "Sort", tint = Flexarr.screen) }
        DropdownMenu(open, onDismissRequest = { open = false }, containerColor = Flexarr.seat) {
            BookSort.entries.forEach { sort ->
                Choice(sort.label, query.sort == sort) {
                    open = false
                    // The newest, longest and latest come first; names from A.
                    val descending = sort == BookSort.Added || sort == BookSort.Released || sort == BookSort.Listened || sort == BookSort.Length
                    onChange(if (query.sort == sort) query.copy(descending = !query.descending) else query.copy(sort = sort, descending = descending))
                }
            }
        }
    }
}

@Composable
internal fun Choice(label: String, checked: Boolean, onClick: () -> Unit) {
    DropdownMenuItem(
        text = { Text(label, color = Flexarr.screen) },
        onClick = onClick,
        leadingIcon = { Box(Modifier.size(20.dp)) { if (checked) Icon(Icons.Filled.Check, contentDescription = "Chosen", tint = Flexarr.screen) } },
    )
}

@Composable
private fun SeriesGrid(model: AppModel, libraryId: String) {
    val client = model.client ?: return
    val navigator = LocalNavigator.current
    val pager = remember(libraryId) { Pager<BookSeriesSummary>({ it.id }) { skip, take -> client.bookSeriesList(libraryId, skip = skip, take = take) } }
    var attempt by remember { mutableIntStateOf(0) }
    LaunchedEffect(pager, attempt, model.watchStateVersion) { pager.reload() }
    PagedGrid(pager, "No series here yet.", retry = { attempt++ }, minimum = GridMinimum * 1.3f) { series ->
        SeriesCard(series, width = null) { navigator.openBookSeries(series.id) }
    }
}

@Composable
private fun AuthorsGrid(model: AppModel, libraryId: String) {
    val client = model.client ?: return
    val navigator = LocalNavigator.current
    val pager = remember(libraryId) { Pager<BookPerson>({ it.id }) { skip, take -> client.bookPeople(libraryId, BookPersonRole.Author, skip = skip, take = take) } }
    var attempt by remember { mutableIntStateOf(0) }
    LaunchedEffect(pager, attempt) { pager.reload() }
    PagedGrid(pager, "No authors here yet.", retry = { attempt++ }, minimum = 96.dp) { person ->
        Box(Modifier.fillMaxWidth(), contentAlignment = Alignment.TopCenter) {
            PersonCard(person.name, bookCount(person.bookCount), person.image) { navigator.openPerson(person.id, person.name) }
        }
    }
}

/** The library's genres, A to Z, each opening every book in it. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun GenreList(model: AppModel, libraryId: String, openGenre: (String) -> Unit) {
    val client = model.client ?: return
    val loader = rememberLoader(libraryId, model.watchStateVersion) { client.bookShelves(libraryId) }
    Loaded(loader) { shelves: BookShelves ->
        val genres = shelves.genres.sortedWith(compareBy(String.CASE_INSENSITIVE_ORDER) { it.name })
        if (genres.isEmpty()) return@Loaded StatusView("No genres yet.", empty = true)
        PullToRefreshBox(isRefreshing = loader.refreshing, onRefresh = loader.retry) {
            LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(start = Edge, end = Edge, bottom = 24.dp)) {
                item {
                    Column(Modifier.clip(RoundedCornerShape(12.dp)).background(Flexarr.seat)) {
                        genres.forEachIndexed { index, genre ->
                            if (index > 0) HorizontalDivider(Modifier.padding(start = 16.dp), color = Flexarr.aisle.copy(alpha = 0.6f))
                            GenreRow(genre) { openGenre(genre.name) }
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun GenreRow(genre: GenreCount, onClick: () -> Unit) {
    val count = bookCount(genre.count)
    Row(
        // pressable merges the row for TalkBack, which reads it as "Fantasy, 12 books, button".
        Modifier.fillMaxWidth().pressable(onClick).padding(horizontal = 16.dp, vertical = 14.dp),
        horizontalArrangement = Arrangement.spacedBy(12.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(genre.name, fontSize = 17.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f))
        Text(count, fontSize = 15.sp, color = Flexarr.usher, style = Tabular)
        Icon(Icons.AutoMirrored.Filled.KeyboardArrowRight, contentDescription = null, tint = Flexarr.usher)
    }
}
