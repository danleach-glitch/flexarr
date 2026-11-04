package io.github.flexarr.ui.tv

import io.github.flexarr.core.i18n.t
import io.github.flexarr.core.i18n.Words

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.background
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsFocusedAsState
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Search
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.MutableState
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.getValue
import androidx.compose.runtime.setValue
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.listSaver
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.saveable.rememberSaveableStateHolder
import androidx.compose.runtime.snapshots.SnapshotStateList
import androidx.compose.runtime.toMutableStateList
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.draw.drawBehind
import androidx.compose.ui.focus.FocusDirection
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.focus.onFocusChanged
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalFocusManager
import androidx.compose.ui.platform.LocalSoftwareKeyboardController
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.tv.material3.ClickableSurfaceDefaults
import androidx.tv.material3.Icon
import androidx.tv.material3.Surface
import androidx.tv.material3.Text
import io.github.flexarr.AppModel
import io.github.flexarr.core.Availability
import io.github.flexarr.core.ExternalRow
import io.github.flexarr.core.ExternalTitle
import io.github.flexarr.core.FlexarrClient
import io.github.flexarr.core.Genre
import io.github.flexarr.core.MusicDiscover
import io.github.flexarr.core.MusicGenreDetail
import io.github.flexarr.core.MusicSearch
import io.github.flexarr.core.musicDiscover
import io.github.flexarr.core.musicGenre
import io.github.flexarr.ui.music.LidarrNotAnswering
import io.github.flexarr.ui.theme.FlexarrIcons
import io.github.flexarr.core.searchMusic
import io.github.flexarr.core.RequestStatus
import io.github.flexarr.core.discoverRows
import io.github.flexarr.core.genres
import io.github.flexarr.core.requestStatus
import io.github.flexarr.core.searchEverything
import io.github.flexarr.ui.components.LocalNavigator
import io.github.flexarr.ui.components.RemoteImage
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.RoomBackground
import io.github.flexarr.ui.theme.marquee
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.async
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.delay

/** A page Discover opens over itself: the search, a film or show from outside the library, or a genre. */
internal sealed interface DiscoverPage {
    /** Which page it is, for keeping its state while another is over it. */
    val key: String

    data object Search : DiscoverPage {
        override val key = "search"
    }

    data class Title(val title: ExternalTitle) : DiscoverPage {
        override val key get() = "title/${title.id}"
    }

    data class GenrePage(val name: String) : DiscoverPage {
        override val key get() = "genre/$name"
    }

    /** Music mode's search: any artist or album Deezer knows. */
    data object MusicSearchPage : DiscoverPage {
        override val key = "music-search"
    }

    /** A Deezer genre's artists, from Music mode's tiles. */
    data class MusicGenrePage(val id: Long, val name: String) : DiscoverPage {
        override val key get() = "music-genre/$id"
    }
}

/** Discover's pages as words, so they're kept while a title from the library is open over Discover. */
private val pagesSaver = listSaver<SnapshotStateList<DiscoverPage>, String>(
    save = { pages ->
        pages.map { page ->
            when (page) {
                DiscoverPage.Search -> "search"
                is DiscoverPage.Title -> "title:" + FlexarrClient.json.encodeToString(ExternalTitle.serializer(), page.title)
                is DiscoverPage.GenrePage -> "genre:" + page.name
                DiscoverPage.MusicSearchPage -> "music-search"
                is DiscoverPage.MusicGenrePage -> "music-genre:${page.id}:${page.name}"
            }
        }
    },
    restore = { saved ->
        saved.mapNotNull { text ->
            when {
                text == "search" -> DiscoverPage.Search
                text.startsWith("title:") -> runCatching { DiscoverPage.Title(FlexarrClient.json.decodeFromString(ExternalTitle.serializer(), text.removePrefix("title:"))) }.getOrNull()
                text.startsWith("genre:") -> DiscoverPage.GenrePage(text.removePrefix("genre:"))
                text == "music-search" -> DiscoverPage.MusicSearchPage
                text.startsWith("music-genre:") -> text.removePrefix("music-genre:").split(":", limit = 2).let { parts ->
                    parts.first().toLongOrNull()?.let { DiscoverPage.MusicGenrePage(it, parts.getOrElse(1) { "" }) }
                }
                else -> null
            }
        }.toMutableStateList()
    },
)

/** What Discover shows: whether Seerr is there, its rows, and the library's genres. */
private class DiscoverContent(val status: RequestStatus, val rows: List<ExternalRow>, val genres: List<Genre>)

/**
 * Discover, as the Apple TV's DiscoverView: what's trending and popular (from Seerr, when it's connected), searching for
 * any film or show to request it, and the library by genre. Its own pages (the search, a title from outside the
 * library, a genre) open over it, and Back closes them; a title the library has opens as itself.
 */
@Composable
fun TvDiscoverScreen(model: AppModel) {
    val client = model.client ?: return
    val navigator = LocalNavigator.current
    val pages = rememberSaveable(saver = pagesSaver) { mutableStateListOf() }
    val states = rememberSaveableStateHolder()
    var content by remember { mutableStateOf<DiscoverContent?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    var attempt by remember { mutableIntStateOf(0) }
    val browsing = pages.isEmpty()
    // Music mode, when this profile can request music (through Lidarr): Deezer's rows and genres, and music search.
    var musicOn by remember { mutableStateOf(false) }
    var musicMode by rememberSaveable { mutableStateOf(false) }
    var music by remember { mutableStateOf<MusicDiscover?>(null) }
    var musicError by remember { mutableStateOf<String?>(null) }
    var musicAttempt by remember { mutableIntStateOf(0) }
    // Switching modes replaces the screen under the switch: focus goes back to the word chosen.
    val switchFocus = remember { FocusRequester() }
    var focusSwitch by remember { mutableStateOf(false) }

    // The same title can be open twice (from "If you like this"), so its place in the stack is in the key too.
    fun pageKey(index: Int) = "$index/${pages[index].key}"

    fun pop() {
        states.removeState(pageKey(pages.lastIndex))
        pages.removeAt(pages.lastIndex)
    }

    val open: (ExternalTitle) -> Unit = { title ->
        val id = title.libraryItemId
        if (id != null) navigator.openItem(id) else pages.add(DiscoverPage.Title(title))
    }

    // Again on coming back, so what was just requested says so.
    LaunchedEffect(model.credentials, browsing, attempt) {
        if (!browsing) return@LaunchedEffect
        try {
            content = loadDiscover(client, content?.rows.orEmpty())
            error = null
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            error = e.message ?: t("Something went wrong.")
        }
    }
    LaunchedEffect(model.credentials) {
        musicOn = try {
            model.musicStatus().configured
        } catch (e: CancellationException) {
            throw e
        } catch (_: Exception) {
            false
        }
        if (!musicOn) musicMode = false
    }
    LaunchedEffect(model.credentials, browsing, musicMode, musicOn, musicAttempt) {
        if (!browsing || !musicMode || !musicOn) return@LaunchedEffect
        try {
            music = client.musicDiscover()
            musicError = null
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            if (music == null) musicError = e.message ?: t("Something went wrong.")
        }
    }
    BackHandler(enabled = !browsing, onBack = ::pop)

    val showsMusic = musicMode && musicOn
    // Browse, Search for films and shows (when Seerr's connected, or not yet known not to be), then Music when it's
    // on, as the Apple TV's: Music has its own search inside it.
    val seerr = content?.status?.configured != false
    val choices = buildList {
        add(Words.browse)
        if (seerr) add(Words.search)
        if (musicOn) add(Words.music)
    }
    val switch: (@Composable (Modifier) -> Unit)? = if (seerr || musicOn) {
        { modifier ->
            TvViewSwitch(
                choices.map { t(it) },
                selected = choices.indexOf(if (showsMusic) Words.music else Words.browse),
                onSelect = { index ->
                    when (choices[index]) {
                        Words.search -> pages.add(DiscoverPage.Search)
                        Words.music -> if (!showsMusic) { musicMode = true; focusSwitch = true }
                        Words.browse -> if (showsMusic) { musicMode = false; focusSwitch = true }
                    }
                },
                modifier = modifier,
                selectedFocus = switchFocus,
            )
        }
    } else {
        null
    }
    LaunchedEffect(focusSwitch, showsMusic) {
        if (!focusSwitch) return@LaunchedEffect
        for (attempt in 0 until 10) {
            if (runCatching { switchFocus.requestFocus() }.getOrDefault(false)) break
            delay(50)
        }
        focusSwitch = false
    }

    val page = pages.lastOrNull()
    states.SaveableStateProvider(if (page == null) "browse" else pageKey(pages.lastIndex)) {
        when (page) {
            null -> if (showsMusic) {
                MusicBrowse(
                    music, musicError, retry = { musicAttempt++ }, switch = switch, takesFocus = !focusSwitch,
                    paused = model.musicUnreachable,
                    onSearch = { pages.add(DiscoverPage.MusicSearchPage) },
                    onGenre = { id, name -> pages.add(DiscoverPage.MusicGenrePage(id, name)) },
                )
            } else {
                DiscoverBrowse(content, error, retry = { attempt++ }, switch = switch, takesFocus = !focusSwitch, onGenre = { pages.add(DiscoverPage.GenrePage(it)) }, open = open)
            }
            DiscoverPage.Search -> DiscoverSearch(client, open)
            DiscoverPage.MusicSearchPage -> MusicSearchScreen(client)
            is DiscoverPage.MusicGenrePage -> TvMusicGenreScreen(client, page.id, page.name)
            is DiscoverPage.Title -> TvExternalTitleScreen(model, page.title, open)
            is DiscoverPage.GenrePage -> TvGenreScreen(client, page.name)
        }
    }
}

private suspend fun loadDiscover(client: FlexarrClient, shownRows: List<ExternalRow>): DiscoverContent = coroutineScope {
    val status = async { client.requestStatus() }
    val genres = async { client.genres() }
    val requests = status.await()
    // The rows are extras on top of the genres: Seerr failing leaves them out rather than the whole screen.
    val rows = if (!requests.configured) {
        emptyList()
    } else {
        try {
            client.discoverRows()
        } catch (e: CancellationException) {
            throw e
        } catch (_: Exception) {
            shownRows
        }
    }
    DiscoverContent(requests, rows, genres.await())
}

/** Under the sidebar's place at the top-left: the Apple TV leaves room for its collapsed sidebar's label, which isn't here. */
private val SwitchTop = 30.dp

@Composable
private fun DiscoverBrowse(
    content: DiscoverContent?,
    error: String?,
    retry: () -> Unit,
    switch: (@Composable (Modifier) -> Unit)?,
    takesFocus: Boolean,
    onGenre: (String) -> Unit,
    open: (ExternalTitle) -> Unit,
) {
    val focus = rememberFocusMemory()
    val rows = content?.rows.orEmpty().filter { it.items.isNotEmpty() }
    val genres = content?.genres.orEmpty()
    // The first title takes focus as Discover appears (a genre, when there's nothing to request); coming back, the
    // card that had it.
    LaunchedEffect(content != null) {
        if (content == null || !takesFocus) return@LaunchedEffect
        focus.restore(rows.firstOrNull()?.let { "${it.id}/${it.items.first().id}" } ?: genres.firstOrNull()?.let { "genre/${it.name}" })
    }
    RoomBackground(ambient = null) {
        LazyColumn(
            Modifier.fillMaxSize(),
            contentPadding = PaddingValues(top = if (switch == null) SwitchTop else 0.dp, bottom = 40.dp),
            verticalArrangement = Arrangement.spacedBy(25.dp),
        ) {
            // Where a library's Browse and All are; gone once it's known neither Seerr nor Lidarr is connected.
            if (switch != null) {
                item(key = "switch") { switch(Modifier.padding(start = TvLayout.Edge, top = SwitchTop)) }
            }
            when {
                content == null -> item(key = "status") {
                    Box(Modifier.fillMaxWidth().height(300.dp)) { if (error != null) TvStatusView(error, retry = retry) else TvStatusView() }
                }
                else -> {
                    items(rows, key = { it.id }) { row ->
                        DiscoverRow(row.title, row.items, open, cardModifier = { Modifier.focusMemory(focus, "${row.id}/${it.id}") })
                    }
                    seerrNote(content, rows)?.let { note ->
                        item(key = "note") {
                            Text(note, fontSize = 13.5.sp, fontWeight = FontWeight.Medium, color = Flexarr.usher, modifier = Modifier.widthIn(max = 600.dp).padding(start = TvLayout.Edge, end = TvLayout.Edge, top = 10.dp))
                        }
                    }
                    if (genres.isNotEmpty()) {
                        item(key = "genres") { TvRowHeading(t("Your library by genre")) }
                        items(genres.chunked(GenreTileAcross), key = { "genres/" + it.first().name }) { line ->
                            Row(Modifier.padding(horizontal = TvLayout.Edge), horizontalArrangement = Arrangement.spacedBy(GenreTileSpacing)) {
                                line.forEach { genre ->
                                    TvGenreTile(genre.name, genreCounts(genre), genre.backdrop, onClick = { onGenre(genre.name) }, modifier = Modifier.focusMemory(focus, "genre/${genre.name}"))
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}

/** "12 films and 3 shows". */
private fun genreCounts(genre: Genre): String = when {
    genre.movies > 0 && genre.shows > 0 -> t(
        "{movies, plural, one {# movie} other {# movies}} and {shows, plural, one {# show} other {# shows}}",
        "movies" to genre.movies, "shows" to genre.shows,
    )
    genre.movies > 0 -> t("{count, plural, one {# movie} other {# movies}}", "count" to genre.movies)
    genre.shows > 0 -> t("{count, plural, one {# show} other {# shows}}", "count" to genre.shows)
    else -> ""
}

/** Why there are no rows to request from: Seerr isn't connected, or isn't answering. */
private fun seerrNote(content: DiscoverContent, rows: List<ExternalRow>): String? {
    if (rows.isNotEmpty()) return null
    if (!content.status.configured) return t("Connect Seerr under Settings › Connections on the web to find new movies and shows here and request them.")
    return if (content.status.reachable) null else t("Seerr isn’t answering, so there’s nothing new to request just now.")
}

// Search

/** Any film or show TMDB knows, to find something new and request it: a search field first, then shows and films. */
@Composable
private fun DiscoverSearch(client: FlexarrClient, open: (ExternalTitle) -> Unit) {
    var query by rememberSaveable { mutableStateOf("") }
    var results by remember { mutableStateOf<List<ExternalTitle>?>(null) }
    var searched by remember { mutableStateOf("") }
    var error by remember { mutableStateOf<String?>(null) }
    var attempt by remember { mutableIntStateOf(0) }
    val focus = rememberFocusMemory()

    LaunchedEffect(query, attempt) {
        // Once typing pauses, so each letter doesn't go to Seerr.
        delay(350)
        val text = query.trim()
        if (text.length < 2) {
            results = null
            return@LaunchedEffect
        }
        try {
            results = client.searchEverything(text)
            searched = text
            error = null
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            error = e.message ?: t("Something went wrong.")
        }
    }
    // Straight to the field (and the keyboard) as the search opens; coming back from a title, the card that had focus.
    LaunchedEffect(results != null) {
        if (results != null || focus.lastKey == null) focus.restore("field")
    }

    RoomBackground(ambient = null) {
        LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(top = SwitchTop, bottom = 40.dp), verticalArrangement = Arrangement.spacedBy(15.dp)) {
            item(key = "field") {
                SearchField(query, { query = it }, Modifier.padding(horizontal = TvLayout.Edge).focusMemory(focus, "field"))
            }
            val found = results
            when {
                found != null && found.isEmpty() -> item(key = "empty") {
                    Box(Modifier.fillMaxWidth().height(150.dp)) {
                        TvStatusView(t("Nothing found for “{query}”. Try a different spelling, or the original title.", "query" to searched), empty = true)
                    }
                }
                found != null -> {
                    val owned = found.count { it.libraryItemId != null }
                    item(key = "summary") {
                        Text(
                            if (owned > 0) t("{count} already in your library. Request anything else to add it.", "count" to owned) else t("Request anything to add it to your library."),
                            fontSize = 13.sp,
                            fontWeight = FontWeight.Medium,
                            color = Flexarr.usher,
                            modifier = Modifier.padding(horizontal = TvLayout.Edge),
                        )
                    }
                    val shows = found.filter { it.isShow }
                    val films = found.filter { !it.isShow }
                    if (shows.isNotEmpty()) item(key = "shows") { DiscoverRow(t(Words.shows), shows, open, cardModifier = { Modifier.focusMemory(focus, "shows/${it.id}") }) }
                    if (films.isNotEmpty()) item(key = "films") { DiscoverRow(t(Words.movies), films, open, cardModifier = { Modifier.focusMemory(focus, "films/${it.id}") }) }
                }
                error != null -> item(key = "error") {
                    Box(Modifier.fillMaxWidth().height(150.dp)) { TvStatusView(error, retry = { attempt++ }) }
                }
            }
        }
    }
}

/** The search field, opening the TV's keyboard; Search on the keyboard goes down to the results. */
@Composable
private fun SearchField(value: String, onValue: (String) -> Unit, modifier: Modifier, placeholder: String = t("Any movie or show")) {
    val keyboard = LocalSoftwareKeyboardController.current
    val focusManager = LocalFocusManager.current
    TvTextField(
        value, onValue, placeholder, modifier.widthIn(max = 520.dp).fillMaxWidth(), icon = Icons.Filled.Search,
        keyboardOptions = KeyboardOptions(autoCorrectEnabled = false, imeAction = ImeAction.Search),
        keyboardActions = KeyboardActions(onSearch = {
            keyboard?.hide()
            focusManager.moveFocus(FocusDirection.Down)
        }),
    )
}

// Music

/**
 * Music mode: Search (any artist or album), Deezer's rows (like artists played here, new from them, charts), then genre
 * tiles opening their artists. [paused]: Lidarr isn't answering, said in one quiet line at the top.
 */
@Composable
private fun MusicBrowse(
    music: MusicDiscover?,
    error: String?,
    retry: () -> Unit,
    switch: (@Composable (Modifier) -> Unit)?,
    takesFocus: Boolean,
    paused: Boolean,
    onSearch: () -> Unit,
    onGenre: (Long, String) -> Unit,
) {
    val focus = rememberFocusMemory()
    val rows = music?.rows.orEmpty().filter { it.items.isNotEmpty() }
    val genres = music?.genres.orEmpty()
    LaunchedEffect(music != null) {
        if (music == null || !takesFocus) return@LaunchedEffect
        focus.restore(rows.firstOrNull()?.let { "${it.id}/${it.items.first().id}" } ?: genres.firstOrNull()?.let { "genre/${it.id}" } ?: "search")
    }
    RoomBackground(ambient = null) {
        LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 40.dp), verticalArrangement = Arrangement.spacedBy(25.dp)) {
            if (switch != null) item(key = "switch") { switch(Modifier.padding(start = TvLayout.Edge, top = SwitchTop)) }
            if (paused) {
                item(key = "paused") {
                    Text(LidarrNotAnswering, fontSize = 13.5.sp, fontWeight = FontWeight.Medium, color = Flexarr.usher, modifier = Modifier.padding(horizontal = TvLayout.Edge))
                }
            }
            item(key = "search") {
                TvActionButton(t(Words.search), onClick = onSearch, icon = FlexarrIcons.search, modifier = Modifier.padding(start = TvLayout.Edge).focusMemory(focus, "search"))
            }
            when {
                music == null -> item(key = "status") {
                    Box(Modifier.fillMaxWidth().height(300.dp)) { if (error != null) TvStatusView(error, retry = retry) else TvStatusView() }
                }
                else -> {
                    items(rows, key = { it.id }) { row ->
                        TvMusicExternalRow(row.title, row.items, cardModifier = { Modifier.focusMemory(focus, "${row.id}/${it.id}") })
                    }
                    if (genres.isNotEmpty()) {
                        item(key = "genres") { TvRowHeading(t(Words.genres)) }
                        itemsIndexed(genres.chunked(GenreTileAcross), key = { index, _ -> "genres/$index" }) { _, line ->
                            Row(Modifier.padding(horizontal = TvLayout.Edge), horizontalArrangement = Arrangement.spacedBy(GenreTileSpacing)) {
                                line.forEach { genre ->
                                    TvGenreTile(genre.name, null, genre.image, onClick = { onGenre(genre.id, genre.name) }, modifier = Modifier.focusMemory(focus, "genre/${genre.id}"))
                                }
                            }
                        }
                    }
                    item(key = "note") {
                        Text(t("Data from Deezer"), fontSize = 12.sp, fontWeight = FontWeight.Medium, color = Flexarr.usher, modifier = Modifier.padding(start = TvLayout.Edge, top = 10.dp))
                    }
                }
            }
        }
    }
}

/** Any artist or album Deezer knows, to find something new and request it: a search field, then artists and albums. */
@Composable
private fun MusicSearchScreen(client: FlexarrClient) {
    var query by rememberSaveable { mutableStateOf("") }
    var results by remember { mutableStateOf<MusicSearch?>(null) }
    var searched by remember { mutableStateOf("") }
    var error by remember { mutableStateOf<String?>(null) }
    var attempt by remember { mutableIntStateOf(0) }
    val focus = rememberFocusMemory()

    LaunchedEffect(query, attempt) {
        delay(350)
        val text = query.trim()
        if (text.length < 2) {
            results = null
            return@LaunchedEffect
        }
        try {
            results = client.searchMusic(text)
            searched = text
            error = null
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            error = e.message ?: t("Something went wrong.")
        }
    }
    LaunchedEffect(results != null) {
        if (results != null || focus.lastKey == null) focus.restore("field")
    }

    RoomBackground(ambient = null) {
        LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(top = SwitchTop, bottom = 40.dp), verticalArrangement = Arrangement.spacedBy(15.dp)) {
            item(key = "field") {
                SearchField(query, { query = it }, Modifier.padding(horizontal = TvLayout.Edge).focusMemory(focus, "field"), placeholder = t("Any artist or album"))
            }
            val found = results
            when {
                found != null && found.isEmpty -> item(key = "empty") {
                    Box(Modifier.fillMaxWidth().height(150.dp)) { TvStatusView(t("Nothing found for “{query}”.", "query" to searched), empty = true) }
                }
                found != null -> {
                    if (found.artists.isNotEmpty()) item(key = "artists") { TvMusicExternalRow(t(Words.artists), found.artists, cardModifier = { Modifier.focusMemory(focus, "artists/${it.id}") }) }
                    if (found.albums.isNotEmpty()) item(key = "albums") { TvMusicExternalRow(t(Words.albums), found.albums, cardModifier = { Modifier.focusMemory(focus, "albums/${it.id}") }) }
                }
                error != null -> item(key = "error") {
                    Box(Modifier.fillMaxWidth().height(150.dp)) { TvStatusView(error, retry = { attempt++ }) }
                }
            }
        }
    }
}

/** A Deezer genre's artists, six across; each opens their page (as themselves when the household has them). */
@Composable
private fun TvMusicGenreScreen(client: FlexarrClient, id: Long, name: String) {
    val navigator = LocalNavigator.current
    var genre by remember { mutableStateOf<MusicGenreDetail?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    var attempt by remember { mutableIntStateOf(0) }
    val focus = rememberFocusMemory()
    LaunchedEffect(id, attempt) {
        try {
            genre = client.musicGenre(id)
            error = null
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            error = e.message ?: t("Something went wrong.")
        }
    }
    LaunchedEffect(genre != null) {
        genre?.let { focus.restore(it.artists.firstOrNull()?.id) }
    }
    RoomBackground(ambient = null) {
        LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(top = 30.dp, bottom = 40.dp), verticalArrangement = Arrangement.spacedBy(24.dp)) {
            item(key = "header") { Text(genre?.name ?: name, style = marquee(36), modifier = Modifier.padding(start = TvLayout.Edge)) }
            val shown = genre
            when {
                shown != null && shown.artists.isEmpty() -> item(key = "empty") { Box(Modifier.fillMaxWidth().height(200.dp)) { TvStatusView(t("No artists here yet."), empty = true) } }
                shown != null -> itemsIndexed(shown.artists.chunked(MusicGenreAcross), key = { index, _ -> "artists/$index" }) { _, line ->
                    Row(Modifier.padding(horizontal = TvLayout.Edge), horizontalArrangement = Arrangement.spacedBy(TvLayout.RowSpacing), verticalAlignment = Alignment.Top) {
                        line.forEach { artist -> TvMusicExternalCard(artist, onClick = { navigator.openMusic(artist) }, modifier = Modifier.focusMemory(focus, artist.id)) }
                    }
                }
                error != null -> item(key = "error") { Box(Modifier.fillMaxWidth().height(200.dp)) { TvStatusView(error, retry = { attempt++ }) } }
                else -> item(key = "loading") { Box(Modifier.fillMaxWidth().height(200.dp)) { TvStatusView() } }
            }
        }
    }
}

/** Round artist cards across the content width. */
private const val MusicGenreAcross = 6

// Rows and cards

/** A row of films and shows from outside the library. [cardModifier]: each card's, so the page can steer focus to it. */
@Composable
internal fun DiscoverRow(title: String, titles: List<ExternalTitle>, open: (ExternalTitle) -> Unit, cardModifier: (ExternalTitle) -> Modifier = { Modifier }) {
    Column {
        TvRowHeading(title)
        LazyRow(
            contentPadding = PaddingValues(start = TvLayout.Edge, end = TvLayout.Edge, top = 14.dp, bottom = 20.dp),
            horizontalArrangement = Arrangement.spacedBy(TvLayout.RowSpacing),
            verticalAlignment = Alignment.Top,
        ) {
            items(titles, key = { it.id }) { TvExternalCard(it, onClick = { open(it) }, modifier = cardModifier(it)) }
        }
    }
}

/**
 * A film or show from Discover: its poster, then the title and whether it's in the library or requested. One the
 * library has opens as itself.
 */
@Composable
internal fun TvExternalCard(title: ExternalTitle, onClick: () -> Unit, modifier: Modifier = Modifier, width: Dp = TvLayout.PosterWidth) {
    TvArtworkButton(
        ambient = Flexarr.silver,
        onClick = onClick,
        modifier = modifier.width(width),
        contentDescription = title.title,
        artwork = {
            Box(Modifier.size(width, width * 1.5f)) {
                RemoteImage(title.poster, 342, Modifier.fillMaxSize()) {
                    Box(Modifier.fillMaxSize().background(Brush.verticalGradient(listOf(Flexarr.silver.copy(alpha = 0.3f), Flexarr.seat))), contentAlignment = Alignment.Center) {
                        Text(title.title, style = marquee(15, Flexarr.screen.copy(alpha = 0.8f)).copy(fontWeight = FontWeight.Bold), textAlign = TextAlign.Center, modifier = Modifier.padding(8.dp))
                    }
                }
                if (title.isInLibrary) LibraryBadge(Modifier.align(Alignment.TopStart).padding(5.dp))
            }
        },
        caption = { focused ->
            Column(verticalArrangement = Arrangement.spacedBy(2.dp)) {
                Text(title.title, fontSize = 12.sp, fontWeight = FontWeight.SemiBold, color = if (focused) Flexarr.screen else Flexarr.screen.copy(alpha = 0.8f), maxLines = 1, overflow = TextOverflow.Ellipsis)
                Text(listOfNotNull(title.year?.toString(), title.state).joinToString("  "), fontSize = 11.5.sp, color = Flexarr.usher, maxLines = 1)
            }
        },
    )
}

/** Ready to watch: the poster says so with a badge (see [LibraryBadge]). */
internal val ExternalTitle.isInLibrary: Boolean get() = libraryItemId != null || availability == Availability.Available

/** "Requested" or "Partly in your library", under the title; null for something that's neither, or that's in. */
private val ExternalTitle.state: String? get() = if (isInLibrary) null else availability.label

internal val Availability.label: String?
    get() = when (this) {
        Availability.None -> null
        Availability.Requested -> t(Words.requested)
        Availability.PartlyAvailable -> t(Words.partlyInYourLibrary)
        Availability.Available -> t(Words.inYourLibrary)
        Availability.Unknown -> t("Not available")
    }

/**
 * On a poster from Discover: it's in the library, ready to play. Green, top-left, away from the watched tick's corner;
 * a play mark rather than a tick, which on a poster means watched.
 */
@Composable
private fun LibraryBadge(modifier: Modifier = Modifier) {
    Box(modifier.size(18.dp).background(Flexarr.go, CircleShape), contentAlignment = Alignment.Center) {
        Icon(FlexarrIcons.play, contentDescription = t(Words.inYourLibrary), tint = Flexarr.room, modifier = Modifier.size(13.dp).offset(x = 0.5.dp))
    }
}

/** Five across the content width: a genre is a way in, not something to look at, so smaller than a row's cards. */
private const val GenreTileAcross = 5
private val GenreTileSpacing = 20.dp
private val GenreTileWidth = 160.dp

/** A genre as a way in: a backdrop from one of its titles, its name and how many there are. */
@Composable
internal fun TvGenreTile(name: String, caption: String?, backdrop: String?, onClick: () -> Unit, modifier: Modifier = Modifier) {
    TvArtworkButton(
        ambient = Flexarr.silver,
        onClick = onClick,
        modifier = modifier.width(GenreTileWidth),
        contentDescription = listOfNotNull(name, caption).joinToString(", "),
        artwork = {
            RemoteImage(backdrop, 400, Modifier.size(GenreTileWidth, GenreTileWidth * 9f / 16f)) {
                Box(Modifier.fillMaxSize().background(Brush.linearGradient(listOf(Flexarr.silver.copy(alpha = 0.3f), Flexarr.seat))))
            }
        },
        caption = { focused ->
            Column(verticalArrangement = Arrangement.spacedBy(2.dp)) {
                Text(name, fontSize = 12.sp, fontWeight = FontWeight.SemiBold, color = if (focused) Flexarr.screen else Flexarr.screen.copy(alpha = 0.85f), maxLines = 1, overflow = TextOverflow.Ellipsis)
                caption?.let { Text(it, fontSize = 11.sp, color = Flexarr.usher, maxLines = 1) }
            }
        },
    )
}

/**
 * The words at the top-left that switch a screen's view (Discover's Browse and Search, a library's Browse, All and
 * Genres), as the Apple TV's ViewSwitch: bright and underlined for the view shown, dim otherwise; focus lifts a word
 * onto a capsule. [selectedFocus] reaches the word shown, to give it focus once a switch has replaced the screen.
 */
@Composable
internal fun TvViewSwitch(choices: List<String>, selected: Int, onSelect: (Int) -> Unit, modifier: Modifier = Modifier, selectedFocus: FocusRequester? = null) {
    // The words line up with the edge below them; the padding is for the capsule focus puts behind them.
    Row(modifier.offset(x = (-11).dp), horizontalArrangement = Arrangement.spacedBy(3.dp)) {
        choices.forEachIndexed { index, title ->
            val chosen = index == selected
            val source = remember { MutableInteractionSource() }
            val focused by source.collectIsFocusedAsState()
            Surface(
                onClick = { onSelect(index) },
                interactionSource = source,
                modifier = Modifier.height(28.dp).then(if (chosen && selectedFocus != null) Modifier.focusRequester(selectedFocus) else Modifier),
                shape = ClickableSurfaceDefaults.shape(CircleShape),
                colors = ClickableSurfaceDefaults.colors(
                    containerColor = Color.Transparent,
                    contentColor = if (chosen) Flexarr.screen else Flexarr.screen.copy(alpha = 0.55f),
                    focusedContainerColor = Flexarr.screen,
                    focusedContentColor = Flexarr.room,
                    pressedContainerColor = Flexarr.screen,
                    pressedContentColor = Flexarr.room,
                ),
                scale = ClickableSurfaceDefaults.scale(focusedScale = 1.06f),
            ) {
                Box(Modifier.fillMaxHeight().padding(horizontal = 11.dp), contentAlignment = Alignment.Center) {
                    // The underline is drawn under the word rather than laid out, so it doesn't widen the switch.
                    Text(
                        title,
                        fontSize = 14.sp,
                        fontWeight = if (chosen || focused) FontWeight.Bold else FontWeight.Medium,
                        maxLines = 1,
                        modifier = Modifier.drawBehind {
                            if (chosen && !focused) {
                                val thickness = 1.5.dp.toPx()
                                drawRoundRect(Flexarr.screen, topLeft = Offset(0f, size.height + 2.dp.toPx()), size = Size(size.width, thickness), cornerRadius = CornerRadius(thickness))
                            }
                        },
                    )
                }
            }
        }
    }
}

// Focus

/** Remembers which of a page's cards had focus, so coming back to the page can give it back. */
@Composable
internal fun rememberFocusMemory(): FocusMemory {
    val last = rememberSaveable { mutableStateOf<String?>(null) }
    return remember { FocusMemory(last) }
}

/** The card (or button) known as [key] on a page with [memory], which can give focus back to it. */
internal fun Modifier.focusMemory(memory: FocusMemory, key: String): Modifier =
    focusRequester(memory.requester(key)).onFocusChanged { memory.focusChanged(key, it.hasFocus) }

internal class FocusMemory(private val last: MutableState<String?>) {
    private val requesters = mutableMapOf<String, FocusRequester>()
    private var focused: String? = null

    /** The card (or button) that last had focus on this page. */
    val lastKey: String? get() = last.value

    internal fun requester(key: String): FocusRequester = requesters.getOrPut(key) { FocusRequester() }

    internal fun focusChanged(key: String, hasFocus: Boolean) {
        if (hasFocus) {
            focused = key
            last.value = key
        } else if (focused == key) {
            focused = null
        }
    }

    /**
     * Focus back to the card that last had it, else to [fallback]. Tries for a moment: a lazy row lays out its cards
     * after the page, and the one that had focus may take a frame or two to be there.
     */
    suspend fun restore(fallback: String?) {
        val keys = listOfNotNull(last.value, fallback).distinct()
        repeat(10) { attempt ->
            // The card that had focus first; after a few tries, the fallback.
            for (key in if (attempt < 4) keys.take(1) else keys) {
                val requester = requesters[key] ?: continue
                runCatching { requester.requestFocus() }
                if (focused == key) return
            }
            delay(100)
        }
    }
}
