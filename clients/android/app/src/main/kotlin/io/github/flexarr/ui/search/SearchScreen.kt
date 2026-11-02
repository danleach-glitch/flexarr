package io.github.flexarr.ui.search

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyListScope
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.GridItemSpan
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Cancel
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.History
import androidx.compose.material.icons.filled.Search
import androidx.compose.material.icons.outlined.BookmarkRemove
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.input.nestedscroll.NestedScrollConnection
import androidx.compose.ui.input.nestedscroll.NestedScrollSource
import androidx.compose.ui.input.nestedscroll.nestedScroll
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalFocusManager
import androidx.compose.ui.platform.LocalSoftwareKeyboardController
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.github.flexarr.AppModel
import io.github.flexarr.core.ExternalTitle
import io.github.flexarr.core.ItemSummary
import io.github.flexarr.core.RecentSearches
import io.github.flexarr.core.SearchResults
import io.github.flexarr.core.clearRecentSearches
import io.github.flexarr.core.forgetSearch
import io.github.flexarr.core.recentSearches
import io.github.flexarr.core.recordSearch
import io.github.flexarr.core.requestsStatus
import io.github.flexarr.core.searchEverything
import io.github.flexarr.ui.components.Edge
import io.github.flexarr.ui.components.GridMinimum
import io.github.flexarr.ui.components.ItemMenu
import io.github.flexarr.ui.components.Load
import io.github.flexarr.ui.components.Loaded
import io.github.flexarr.ui.components.LocalNavigator
import io.github.flexarr.ui.components.MenuAction
import io.github.flexarr.ui.components.PageTitle
import io.github.flexarr.ui.components.PersonCard
import io.github.flexarr.ui.components.PillButton
import io.github.flexarr.ui.components.PosterCard
import io.github.flexarr.ui.components.PosterWidth
import io.github.flexarr.ui.components.RemoteImage
import io.github.flexarr.ui.components.RowSpacing
import io.github.flexarr.ui.components.SectionHeading
import io.github.flexarr.ui.components.StatusView
import io.github.flexarr.ui.components.WideCard
import io.github.flexarr.ui.components.menuActions
import io.github.flexarr.ui.components.pressable
import io.github.flexarr.ui.components.rememberLoader
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.FlexarrIcons
import io.github.flexarr.ui.theme.RoomBackground
import io.github.flexarr.ui.theme.Tokens
import io.github.flexarr.ui.theme.marquee
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

/** Films, shows, episodes and people in the libraries, as they're typed. The iPhone app's SearchView. */
@Composable
fun SearchScreen(model: AppModel) {
    val client = model.client ?: return
    var query by rememberSaveable { mutableStateOf("") }
    var results by remember { mutableStateOf<SearchResults?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    var attempt by remember { mutableIntStateOf(0) }
    var recents by remember { mutableStateOf(RecentSearches()) }
    // Films and shows beyond the library, when Seerr's set up: what was found, and the words that found it.
    var searchesEverything by remember { mutableStateOf(false) }
    var everything by remember { mutableStateOf<Pair<String, List<ExternalTitle>>?>(null) }
    val focus = remember { FocusRequester() }
    val keyboard = LocalSoftwareKeyboardController.current
    val focusManager = LocalFocusManager.current

    // Back with something typed clears it (as iPhone's Cancel), before it leaves Search.
    BackHandler(enabled = query.isNotEmpty()) { query = "" }
    // Opening Search is for typing: the field's ready with the keyboard up.
    LaunchedEffect(Unit) { if (query.isEmpty() && model.pendingSearch == null) focus.requestFocus() }
    // The Assistant's words ("search Flexarr for Fargo"), typed in for it.
    LaunchedEffect(model.pendingSearch) {
        model.pendingSearch?.let {
            query = it
            model.pendingSearch = null
        }
    }
    // What this profile searched for and opened lately, whenever the field's empty: on opening Search, and once it's
    // cleared. A server from before recent searches (or one that can't be reached) has none, and none show.
    LaunchedEffect(query.isEmpty()) {
        if (query.isNotEmpty()) return@LaunchedEffect
        recents = try {
            client.recentSearches()
        } catch (e: CancellationException) {
            throw e
        } catch (_: Exception) {
            RecentSearches()
        }
    }
    LaunchedEffect(model.credentials) {
        searchesEverything = try {
            client.requestsStatus()?.configured == true
        } catch (e: CancellationException) {
            throw e
        } catch (_: Exception) {
            false
        }
    }
    LaunchedEffect(query, attempt, searchesEverything) {
        val text = query.trim()
        if (text.isEmpty() || !searchesEverything) {
            everything = null
            return@LaunchedEffect
        }
        delay(300)
        // Seerr failing leaves the library's results as they are.
        everything = text to try {
            client.searchEverything(text)
        } catch (e: CancellationException) {
            throw e
        } catch (_: Exception) {
            emptyList()
        }
    }
    LaunchedEffect(query, attempt) {
        val text = query.trim()
        if (text.isEmpty()) {
            results = null
            error = null
            return@LaunchedEffect
        }
        // Debounce typing so each keystroke doesn't hit the server.
        delay(300)
        try {
            results = client.search(text)
            error = null
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            error = e.message ?: "Something went wrong."
        }
    }

    // A pick from the results is remembered (not what's typed on the way, but pressing Search too), with the words that found it; one from the
    // recents themselves is just the title, moved to the front. On the app's own scope, as opening it leaves Search.
    // Debug runs without history remember nothing.
    fun record(text: String?, itemId: String?) {
        if (!model.recordsHistory) return
        model.scope.launch { runCatching { client.recordSearch(text, itemId) } }
    }
    fun forget(text: String) {
        recents = recents.copy(queries = recents.queries - text)
        model.scope.launch { runCatching { client.forgetSearch(text) } }
    }
    fun clear() {
        recents = RecentSearches()
        model.scope.launch { runCatching { client.clearRecentSearches() } }
    }

    // Scrolling the results puts the keyboard away.
    val dismissOnScroll = remember {
        object : NestedScrollConnection {
            override fun onPreScroll(available: Offset, source: NestedScrollSource): Offset {
                if (source == NestedScrollSource.UserInput && available.y != 0f) {
                    keyboard?.hide()
                    focusManager.clearFocus()
                }
                return Offset.Zero
            }
        }
    }

    RoomBackground(ambient = null) {
        Column(Modifier.fillMaxSize()) {
            PageTitle("Search")
            SearchField(query, onChange = { query = it }, modifier = Modifier.padding(horizontal = Edge).padding(bottom = 8.dp).focusRequester(focus)) {
                keyboard?.hide()
                // Pressing Search keeps the words as a recent search, as picking a result does, shown at once.
                val words = query.trim()
                if (words.isNotEmpty() && model.recordsHistory) {
                    record(words, null)
                    recents = recents.copy(queries = (listOf(words) + recents.queries.filterNot { it.equals(words, ignoreCase = true) }).take(5))
                }
            }
            val shown = results
            LazyColumn(
                Modifier.fillMaxSize().nestedScroll(dismissOnScroll),
                contentPadding = PaddingValues(vertical = 12.dp),
                verticalArrangement = Arrangement.spacedBy(24.dp),
            ) {
                when {
                    shown != null -> {
                        val text = query.trim()
                        // Beyond the library: what the library's own results haven't already shown.
                        val found = everything?.takeIf { it.first == text }?.second
                        val inLibrary = shown.titles.mapTo(HashSet()) { it.id }
                        val beyond = found?.filter { it.libraryItemId == null || it.libraryItemId !in inLibrary }
                        results(shown, text, beyond, waitingForMore = searchesEverything && found == null, model = model, onPick = { record(text, it?.id) })
                    }
                    error != null -> item { StatusView(error, retry = { attempt++ }, modifier = Modifier.height(240.dp)) }
                    query.isBlank() && !recents.isEmpty -> recentRows(
                        recents,
                        onSearch = {
                            query = it
                            keyboard?.hide()
                            focusManager.clearFocus()
                        },
                        onForget = ::forget,
                        onClear = ::clear,
                        onReopen = { record(null, it.id) },
                    )
                    else -> item { StatusView("Find movies, shows, episodes and people in your libraries.", empty = true, modifier = Modifier.height(240.dp)) }
                }
            }
        }
    }
}

/** The results; [onPick] hears what was opened from them (null for a person). */
private fun LazyListScope.results(
    results: SearchResults,
    query: String,
    beyond: List<ExternalTitle>?,
    waitingForMore: Boolean,
    model: AppModel,
    onPick: (ItemSummary?) -> Unit,
) {
    if (results.isEmpty && beyond.isNullOrEmpty() && !waitingForMore) {
        item { StatusView("Nothing matches “$query”.", empty = true, modifier = Modifier.height(240.dp)) }
    }
    if (results.titles.isNotEmpty()) {
        item(key = "titles") {
            Section("Movies and shows") {
                LazyRow(contentPadding = PaddingValues(horizontal = Edge), horizontalArrangement = Arrangement.spacedBy(RowSpacing)) {
                    items(results.titles, key = { it.id }) { PosterCard(it, onOpen = { onPick(it) }) }
                }
            }
        }
    }
    if (results.books.isNotEmpty() || results.bookSeries.isNotEmpty()) {
        item(key = "books") {
            Section("Audiobooks") {
                val navigator = LocalNavigator.current
                Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
                    if (results.books.isNotEmpty()) {
                        LazyRow(contentPadding = PaddingValues(horizontal = Edge), horizontalArrangement = Arrangement.spacedBy(RowSpacing)) {
                            items(results.books, key = { it.id }) { PosterCard(it, onOpen = { onPick(it) }) }
                        }
                    }
                    // Series by name, each opening its books in order.
                    LazyRow(contentPadding = PaddingValues(horizontal = Edge), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        items(results.bookSeries, key = { it.id }) { series ->
                            PillButton(series.name, false, { onPick(null); navigator.openBookSeries(series.id) })
                        }
                    }
                }
            }
        }
    }
    if (results.episodes.isNotEmpty()) {
        item(key = "episodes") {
            Section("Episodes") {
                val navigator = LocalNavigator.current
                LazyRow(contentPadding = PaddingValues(horizontal = Edge), horizontalArrangement = Arrangement.spacedBy(RowSpacing)) {
                    items(results.episodes, key = { it.id }) { episode ->
                        WideCard(episode, onClick = { onPick(episode); navigator.play(episode) })
                    }
                }
            }
        }
    }
    if (!beyond.isNullOrEmpty()) {
        item(key = "beyond") {
            Section("Not in your library") {
                LazyRow(contentPadding = PaddingValues(horizontal = Edge), horizontalArrangement = Arrangement.spacedBy(RowSpacing)) {
                    items(beyond, key = { it.key }) { ExternalCard(model, it) }
                }
            }
        }
    }
    if (results.people.isNotEmpty()) {
        item(key = "people") {
            Section("People") {
                val navigator = LocalNavigator.current
                LazyRow(contentPadding = PaddingValues(horizontal = Edge), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    items(results.people, key = { it.id }) { person ->
                        PersonCard(person.name, if (person.credits == 1) "1 title" else "${person.credits} titles", person.image) {
                            onPick(null)
                            navigator.openPerson(person.id, person.name)
                        }
                    }
                }
            }
        }
    }
}

/**
 * A film or show from beyond the library: its poster, name and "Film, 2014". Tapping (or a long press) offers the
 * watchlist and, when nobody's asked for it, Request; one the library has after all opens as itself.
 */
@Composable
private fun ExternalCard(model: AppModel, title: ExternalTitle) {
    val navigator = LocalNavigator.current
    var menu by remember { mutableStateOf(false) }
    // What's been done here since the search, ahead of the server's next answer.
    var requested by remember(title.key) { mutableStateOf(false) }
    var onWatchlist by remember(title.key, title.onWatchlist) { mutableStateOf<Boolean?>(null) }
    val on = onWatchlist ?: title.onWatchlist ?: (title.key in model.watchlistTitles)
    val actions = buildList {
        if (model.hasWatchlist) {
            add(MenuAction(if (on) "Remove from watchlist" else "Add to watchlist", if (on) Icons.Outlined.BookmarkRemove else FlexarrIcons.watchlist) {
                onWatchlist = !on
                navigator.watchlistChange(if (on) "Couldn’t remove this from your watchlist" else "Couldn’t add this to your watchlist") {
                    try {
                        val added = model.setTitleOnWatchlist(title.mediaType, title.tmdbId, !on)
                        if (added?.requested == true) requested = true
                    } catch (e: Exception) {
                        onWatchlist = on
                        throw e
                    }
                }
            })
        }
        if (title.canRequest && !requested) {
            add(MenuAction("Request") {
                requested = true
                navigator.watchlistChange("Couldn’t request this") {
                    try {
                        model.requestTitle(title.mediaType, title.tmdbId)
                    } catch (e: Exception) {
                        requested = false
                        throw e
                    }
                }
            })
        }
    }
    val libraryId = title.libraryItemId
    Box {
        Column(
            Modifier.width(PosterWidth)
                .pressable(
                    onClick = { if (libraryId != null) navigator.openItem(libraryId) else if (actions.isNotEmpty()) menu = true },
                    onLongClick = { if (actions.isNotEmpty()) menu = true },
                )
                .menuActions(actions),
            verticalArrangement = Arrangement.spacedBy(6.dp),
        ) {
            Box(Modifier.fillMaxWidth().aspectRatio(2f / 3f).clip(RoundedCornerShape(Tokens.radiusArt)).background(Flexarr.seat)) {
                RemoteImage(title.poster, 342, Modifier.fillMaxSize()) {
                    Box(
                        Modifier.fillMaxSize().background(Brush.verticalGradient(listOf(Flexarr.silver.copy(alpha = 0.3f), Flexarr.seat))),
                        contentAlignment = Alignment.Center,
                    ) {
                        Text(title.title, style = marquee(18, Flexarr.screen.copy(alpha = 0.8f)).copy(fontWeight = FontWeight.Bold), textAlign = TextAlign.Center, modifier = Modifier.padding(8.dp))
                    }
                }
            }
            Column(verticalArrangement = Arrangement.spacedBy(1.dp)) {
                Text(title.title, fontSize = 13.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen.copy(alpha = 0.9f), maxLines = 1, overflow = TextOverflow.Ellipsis)
                Text(title.kindLine, fontSize = 12.sp, color = Flexarr.usher, maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
        }
        ItemMenu(actions, menu, onDismiss = { menu = false })
    }
}

/**
 * With nothing typed: the searches made lately (tap one to search again, × to forget it) and the titles opened from
 * them. One Clear, on the first heading, forgets both.
 */
private fun LazyListScope.recentRows(
    recents: RecentSearches,
    onSearch: (String) -> Unit,
    onForget: (String) -> Unit,
    onClear: () -> Unit,
    onReopen: (ItemSummary) -> Unit,
) {
    if (recents.queries.isNotEmpty()) {
        item(key = "recent-searches") {
            Column {
                RecentHeading("Recent searches", onClear)
                Column(Modifier.padding(top = 4.dp)) {
                    recents.queries.forEach { text ->
                        Row(
                            Modifier.fillMaxWidth().heightIn(min = 48.dp).pressable({ onSearch(text) }, onClickLabel = "Search").padding(start = Edge, end = Edge - 12.dp),
                            horizontalArrangement = Arrangement.spacedBy(12.dp),
                            verticalAlignment = Alignment.CenterVertically,
                        ) {
                            Icon(Icons.Filled.History, contentDescription = null, tint = Flexarr.usher, modifier = Modifier.size(20.dp))
                            Text(text, fontSize = 17.sp, color = Flexarr.screen, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f))
                            // The usual 48 to touch, around a small ×.
                            Box(
                                Modifier.size(48.dp).clickable(role = Role.Button, onClickLabel = "Remove") { onForget(text) },
                                contentAlignment = Alignment.Center,
                            ) {
                                Icon(Icons.Filled.Close, contentDescription = "Remove $text", tint = Flexarr.usher, modifier = Modifier.size(18.dp))
                            }
                        }
                    }
                }
            }
        }
    }
    if (recents.items.isNotEmpty()) {
        item(key = "recently-opened") {
            Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                RecentHeading("Recently opened", onClear.takeIf { recents.queries.isEmpty() })
                LazyRow(contentPadding = PaddingValues(horizontal = Edge), horizontalArrangement = Arrangement.spacedBy(RowSpacing)) {
                    items(recents.items, key = { it.id }) { PosterCard(it, onOpen = { onReopen(it) }) }
                }
            }
        }
    }
}

/** A section heading with Clear beside it, as See all sits on other rows. */
@Composable
private fun RecentHeading(title: String, onClear: (() -> Unit)?) {
    Row(verticalAlignment = Alignment.Bottom) {
        SectionHeading(title, Modifier.weight(1f))
        if (onClear != null) {
            Text(
                "Clear",
                fontSize = 15.sp,
                fontWeight = FontWeight.SemiBold,
                color = Flexarr.usher,
                modifier = Modifier.padding(end = Edge - 4.dp).pressable(onClear, onClickLabel = "Clear recent searches").padding(start = 12.dp, top = 20.dp).padding(4.dp),
            )
        }
    }
}

@Composable
private fun Section(title: String, content: @Composable () -> Unit) {
    Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
        SectionHeading(title)
        content()
    }
}

/** A rounded field on the seat colour with a magnifying glass, and a clear button once something's typed. */
@Composable
fun SearchField(
    query: String,
    onChange: (String) -> Unit,
    modifier: Modifier = Modifier,
    placeholder: String = "Movies, shows, episodes and people",
    onSearch: () -> Unit,
) {
    Row(
        // At least the usual height, taller when the text is set larger.
        modifier.fillMaxWidth().heightIn(min = 40.dp).clip(RoundedCornerShape(Tokens.radiusControl)).background(Flexarr.well)
            .border(1.dp, Flexarr.aisle, RoundedCornerShape(Tokens.radiusControl)).padding(horizontal = 10.dp, vertical = 6.dp),
        horizontalArrangement = Arrangement.spacedBy(8.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Icon(Icons.Filled.Search, contentDescription = null, tint = Flexarr.usher, modifier = Modifier.size(20.dp))
        Box(Modifier.weight(1f), contentAlignment = Alignment.CenterStart) {
            if (query.isEmpty()) Text(placeholder, fontSize = 17.sp, color = Flexarr.usher, maxLines = 1, overflow = TextOverflow.Ellipsis)
            BasicTextField(
                query,
                onValueChange = onChange,
                singleLine = true,
                textStyle = TextStyle(fontSize = 17.sp, color = Flexarr.screen),
                cursorBrush = SolidColor(Flexarr.screen),
                keyboardOptions = KeyboardOptions(imeAction = ImeAction.Search),
                keyboardActions = KeyboardActions(onSearch = { onSearch() }),
                // TalkBack reads the placeholder as the field's name.
                modifier = Modifier.fillMaxWidth().semantics { contentDescription = placeholder },
            )
        }
        if (query.isNotEmpty()) {
            Icon(
                Icons.Filled.Cancel,
                contentDescription = "Clear",
                tint = Flexarr.usher,
                modifier = Modifier.size(20.dp).clickable(role = Role.Button, onClickLabel = "Clear") { onChange("") },
            )
        }
    }
}

/** Someone in the libraries: their picture and name, and every title they're in. The iPhone app's PersonView. */
@Composable
fun PersonScreen(model: AppModel, id: String, name: String) {
    val client = model.client ?: return
    val navigator = LocalNavigator.current
    val loader = rememberLoader(id, model.watchStateVersion) { client.person(id) }
    val person = (loader.state as? Load.Loaded)?.value
    RoomBackground(ambient = null) {
        Column(Modifier.fillMaxSize()) {
            PageTitle("", onBack = navigator::back)
            LazyVerticalGrid(
                GridCells.Adaptive(GridMinimum),
                contentPadding = PaddingValues(start = Edge, end = Edge, bottom = 24.dp),
                horizontalArrangement = Arrangement.spacedBy(12.dp),
                verticalArrangement = Arrangement.spacedBy(20.dp),
                modifier = Modifier.fillMaxSize(),
            ) {
                item(span = { GridItemSpan(maxLineSpan) }) {
                    Row(Modifier.padding(bottom = 4.dp), horizontalArrangement = Arrangement.spacedBy(16.dp), verticalAlignment = Alignment.CenterVertically) {
                        RemoteImage(person?.image, 240, Modifier.size(88.dp).clip(CircleShape).background(Flexarr.seat)) {}
                        Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
                            Text(name, style = marquee(34).copy(fontWeight = FontWeight.ExtraBold), maxLines = 2)
                            person?.let {
                                val count = it.credits.distinctBy { credit -> credit.item.id }.size
                                val books = it.credits.all { credit -> credit.item.kind == io.github.flexarr.core.MediaKind.Audiobook }
                                val noun = if (books) "book" else "title"
                                Text(if (count == 1) "1 $noun in your library" else "$count ${noun}s in your library", fontSize = 15.sp, color = Flexarr.usher)
                            }
                        }
                    }
                }
                // An author's or narrator's life, cut short until it's tapped.
                person?.biography?.takeIf { it.isNotBlank() }?.let { biography ->
                    item(span = { GridItemSpan(maxLineSpan) }) {
                        var open by remember { mutableStateOf(false) }
                        Text(
                            biography, fontSize = 15.sp, color = Flexarr.screen.copy(alpha = 0.85f),
                            maxLines = if (open) Int.MAX_VALUE else 5, overflow = TextOverflow.Ellipsis,
                            modifier = Modifier.pressable({ open = !open }, role = null),
                        )
                    }
                }
                if (person == null) {
                    item(span = { GridItemSpan(maxLineSpan) }) { Loaded(loader, Modifier.height(300.dp)) {} }
                } else {
                    // Someone who both acted and directed is in a title twice; it shows once.
                    items(person.credits.distinctBy { it.item.id }, key = { it.item.id }) { credit -> PosterCard(credit.item, width = null) }
                }
            }
        }
    }
}
