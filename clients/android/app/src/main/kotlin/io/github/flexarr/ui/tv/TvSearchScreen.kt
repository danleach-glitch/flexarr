package io.github.flexarr.ui.tv

import android.app.Activity
import android.content.Intent
import android.speech.RecognizerIntent
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.GridItemSpan
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Mic
import androidx.compose.material.icons.filled.Search
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.getValue
import androidx.compose.runtime.setValue
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import kotlinx.coroutines.launch
import androidx.compose.material.icons.filled.History
import io.github.flexarr.ui.components.MenuAction
import io.github.flexarr.core.clearRecentSearches
import io.github.flexarr.core.forgetSearch
import io.github.flexarr.core.recordSearch
import io.github.flexarr.core.recentSearches
import io.github.flexarr.core.RecentSearches
import androidx.compose.ui.draw.clip
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.focus.FocusDirection
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalFocusManager
import androidx.compose.ui.platform.LocalSoftwareKeyboardController
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.foundation.gestures.animateScrollBy
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.key.Key
import androidx.compose.ui.input.key.KeyEventType
import androidx.compose.ui.input.key.key
import androidx.compose.ui.input.key.onPreviewKeyEvent
import androidx.compose.ui.input.key.type
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import androidx.tv.material3.ClickableSurfaceDefaults
import androidx.tv.material3.Surface
import androidx.tv.material3.Text
import io.github.flexarr.AppModel
import io.github.flexarr.core.CreditRole
import io.github.flexarr.core.Person
import io.github.flexarr.core.SearchResults
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

/**
 * Search, as the Apple TV's: the field at the top (OK opens the TV's keyboard; the microphone listens instead), then
 * rows of films and shows, episodes and people as the words come. [takeFocus]: arriving from the sidebar, the field.
 */
@Composable
fun TvSearchScreen(model: AppModel, takeFocus: Boolean, onFocusTaken: () -> Unit) {
    val client = model.client ?: return
    val navigator = LocalNavigator.current
    val context = LocalContext.current
    var query by rememberSaveable { mutableStateOf("") }
    var results by remember { mutableStateOf<SearchResults?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    var attempt by remember { mutableStateOf(0) }
    var recents by remember { mutableStateOf(RecentSearches()) }
    var forgetting by remember { mutableStateOf<String?>(null) }
    val field = remember { FocusRequester() }

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
    // A pick from the results is remembered (not what's typed on the way), with the words that found it; one from the
    // recents themselves is just the title, moved to the front. On the app's own scope, as opening it leaves Search.
    // Runs without history remember nothing.
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

    // "Search Flexarr for Fargo" said to the Assistant: the words go in the field and the search runs.
    LaunchedEffect(model.pendingSearch) {
        val words = model.pendingSearch ?: return@LaunchedEffect
        model.pendingSearch = null
        query = words
    }
    LaunchedEffect(takeFocus) {
        if (!takeFocus) return@LaunchedEffect
        delay(50)
        runCatching { field.requestFocus() }
        onFocusTaken()
    }
    LaunchedEffect(query, attempt) {
        val text = query.trim()
        if (text.isEmpty()) {
            results = null
            error = null
            return@LaunchedEffect
        }
        // So each letter typed doesn't ask the server.
        delay(300)
        try {
            results = client.search(text)
            error = null
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            error = e.message
        }
    }
    val listen = rememberLauncherForActivityResult(ActivityResultContracts.StartActivityForResult()) { result ->
        if (result.resultCode == Activity.RESULT_OK) {
            result.data?.getStringArrayListExtra(RecognizerIntent.EXTRA_RESULTS)?.firstOrNull()?.let { query = it }
        }
    }
    val speech = remember { Intent(RecognizerIntent.ACTION_RECOGNIZE_SPEECH).putExtra(RecognizerIntent.EXTRA_LANGUAGE_MODEL, RecognizerIntent.LANGUAGE_MODEL_FREE_FORM) }
    val canListen = remember { speech.resolveActivity(context.packageManager) != null }

    RoomBackground(ambient = null) {
        LazyColumn(contentPadding = PaddingValues(top = 28.dp, bottom = 40.dp), verticalArrangement = Arrangement.spacedBy(15.dp)) {
            item(key = "field") {
                Row(Modifier.padding(horizontal = TvLayout.Edge), horizontalArrangement = Arrangement.spacedBy(12.dp), verticalAlignment = Alignment.CenterVertically) {
                    SearchField(query, { query = it }, Modifier.weight(1f).focusRequester(field))
                    if (canListen) TvActionButton(null, onClick = { runCatching { listen.launch(speech) } }, icon = Icons.Filled.Mic, contentDescription = "Search by voice")
                }
            }
            val found = results
            val words = query.trim()
            when {
                // Nothing typed: the last few searches to run again, and the titles opened from search.
                words.isEmpty() && !recents.isEmpty -> {
                    if (recents.queries.isNotEmpty()) item(key = "recent-searches") {
                        Section("Recent searches") {
                            items(recents.queries, key = { it }) { text ->
                                TvActionButton(text, onClick = { query = text }, icon = Icons.Filled.History, onLongClick = { forgetting = text })
                            }
                            item(key = "clear") { TvActionButton("Clear", onClick = ::clear) }
                        }
                    }
                    if (recents.items.isNotEmpty()) item(key = "recently-opened") {
                        Section("Recently opened") {
                            items(recents.items.filter { it.showsOnTv }, key = { it.id }) { item ->
                                TvPosterCard(item, onClick = {
                                    record(null, item.id)
                                    navigator.open(item)
                                })
                            }
                        }
                    }
                }
                found != null && found.titles.isEmpty() && found.episodes.isEmpty() && found.people.isEmpty() -> item(key = "none") {
                    Box(Modifier.height(200.dp)) { TvStatusView("Nothing matches “${query.trim()}”.", empty = true) }
                }
                found != null -> {
                    val titles = found.titles.filter { it.showsOnTv }
                    if (titles.isNotEmpty()) item(key = "titles") {
                        Section("Movies and shows") { items(titles, key = { it.id }) { TvPosterCard(it, onClick = { record(words, it.id); navigator.open(it) }) } }
                    }
                    if (found.episodes.isNotEmpty()) item(key = "episodes") {
                        Section("Episodes") { items(found.episodes, key = { it.id }) { TvWideCard(it, onClick = { record(words, it.id); navigator.openItem(it.id) }, width = 220.dp) } }
                    }
                    if (found.people.isNotEmpty()) item(key = "people") {
                        Section("People") {
                            items(found.people, key = { it.id }) { person ->
                                TvPersonCard(person.name, if (person.credits == 1) "1 title" else "${person.credits} titles", person.image) {
                                    record(words, null)
                                    navigator.openPerson(person.id, person.name)
                                }
                            }
                        }
                    }
                }
                error != null -> item(key = "error") { Box(Modifier.height(200.dp)) { TvStatusView(error, retry = { attempt++ }) } }
            }
        }
    }
    forgetting?.let { text ->
        TvItemMenu(text, listOf(MenuAction("Remove") { forget(text) }), onDismiss = { forgetting = null })
    }
}

@Composable
private fun Section(title: String, content: androidx.compose.foundation.lazy.LazyListScope.() -> Unit) {
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

/** The search field: a magnifier and the words; Search on the TV's keyboard puts it away and goes down to the results. */
@Composable
private fun SearchField(value: String, onValue: (String) -> Unit, modifier: Modifier) {
    val keyboard = LocalSoftwareKeyboardController.current
    val focusManager = LocalFocusManager.current
    TvTextField(
        value, onValue, "Movies, shows, episodes and people", modifier, icon = Icons.Filled.Search,
        keyboardOptions = KeyboardOptions(imeAction = ImeAction.Search),
        keyboardActions = KeyboardActions(onSearch = {
            keyboard?.hide()
            focusManager.moveFocus(FocusDirection.Down)
        }),
    )
}

/** Everything in the library someone's in: their picture and name, then a poster grid of their titles. */
@Composable
fun TvPersonScreen(model: AppModel, id: String, name: String) {
    val client = model.client ?: return
    val navigator = LocalNavigator.current
    val loader = rememberLoader(id) { client.person(id) }
    val person = (loader.state as? Load.Loaded)?.value
    val first = remember { FocusRequester() }
    val biographyFocus = remember { FocusRequester() }
    val biography = person?.biography?.takeIf { it.isNotBlank() }
    LaunchedEffect(person) {
        if (person == null) return@LaunchedEffect
        delay(50)
        // An author's books aren't on the TV, so with no titles the biography takes focus.
        runCatching { if (titles(person).isNotEmpty()) first.requestFocus() else if (biography != null) biographyFocus.requestFocus() }
    }
    RoomBackground(ambient = person?.credits?.firstOrNull()?.item?.let { glowColor(it.accentColor) }, intensity = 0.22f) {
        LazyVerticalGrid(
            GridCells.Fixed(6),
            contentPadding = PaddingValues(start = TvLayout.Edge, end = TvLayout.Edge, top = 28.dp, bottom = 40.dp),
            horizontalArrangement = Arrangement.spacedBy((960.dp - TvLayout.Edge * 2 - TvLayout.PosterWidth * 6) / 5),
            verticalArrangement = Arrangement.spacedBy(28.dp),
        ) {
            item(span = { GridItemSpan(maxLineSpan) }) {
                Column(Modifier.padding(bottom = 6.dp), verticalArrangement = Arrangement.spacedBy(14.dp)) {
                    Row(horizontalArrangement = Arrangement.spacedBy(20.dp), verticalAlignment = Alignment.CenterVertically) {
                        RemoteImage(person?.image, 300, Modifier.size(85.dp).background(Flexarr.seat, CircleShape).clip(CircleShape)) {}
                        Column(verticalArrangement = Arrangement.spacedBy(3.dp)) {
                            Text(name, style = marquee(36).copy(fontWeight = FontWeight.ExtraBold))
                            person?.let { titles(it).size }?.takeIf { it > 0 }?.let { count ->
                                Text(if (count == 1) "1 title in your library" else "$count titles in your library", fontSize = 13.sp, color = Flexarr.usher)
                            }
                        }
                    }
                    biography?.let { TvBiography(name, it, Modifier.focusRequester(biographyFocus)) }
                    // An author or narrator with nothing to show here: say where their books are rather than leave the page bare.
                    if (person != null && biography == null && titles(person).isEmpty() && person.credits.any { it.role in BookRoles }) {
                        Text("Books are on your phone.", fontSize = 14.sp, color = Flexarr.usher)
                    }
                }
            }
            when (val state = loader.state) {
                is Load.Loaded -> {
                    val credits = titles(state.value)
                    items(credits.size) { index ->
                    val item = credits[index]
                    TvPosterCard(item, onClick = { navigator.open(item) }, modifier = if (index == 0) Modifier.focusRequester(first) else Modifier)
                    }
                }
                is Load.Failed -> item(span = { GridItemSpan(maxLineSpan) }) { Box(Modifier.height(300.dp)) { TvStatusView(state.message, retry = loader.retry) } }
                Load.Loading -> item(span = { GridItemSpan(maxLineSpan) }) { Box(Modifier.height(300.dp)) { TvStatusView() } }
            }
        }
    }
}

/**
 * An author's or narrator's biography, three lines of it under their name. It takes focus like a quiet button, and OK
 * opens all of it, Up and Down scrolling, Close or Back to return.
 */
@Composable
private fun TvBiography(name: String, text: String, modifier: Modifier = Modifier) {
    var open by remember { mutableStateOf(false) }
    Surface(
        onClick = { open = true },
        // The text lines up with the name; the padding is the focus highlight's.
        modifier = modifier.offset(x = (-12).dp).widthIn(max = 640.dp),
        shape = ClickableSurfaceDefaults.shape(RoundedCornerShape(10.dp)),
        colors = ClickableSurfaceDefaults.colors(
            containerColor = Color.Transparent,
            contentColor = Flexarr.screen.copy(alpha = 0.88f),
            focusedContainerColor = Flexarr.screen.copy(alpha = 0.12f),
            focusedContentColor = Flexarr.screen,
            pressedContainerColor = Flexarr.screen.copy(alpha = 0.18f),
            pressedContentColor = Flexarr.screen,
        ),
        scale = ClickableSurfaceDefaults.scale(focusedScale = 1.02f),
    ) {
        Text(text, fontSize = 14.sp, lineHeight = 20.sp, maxLines = 3, overflow = TextOverflow.Ellipsis, modifier = Modifier.padding(horizontal = 12.dp, vertical = 8.dp))
    }
    if (open) {
        val close = remember { FocusRequester() }
        val scroll = rememberScrollState()
        val scope = rememberCoroutineScope()
        Dialog(onDismissRequest = { open = false }, properties = DialogProperties(usePlatformDefaultWidth = false)) {
            Column(
                Modifier.width(580.dp).heightIn(max = 470.dp).background(Flexarr.seat, RoundedCornerShape(14.dp)).padding(24.dp),
                verticalArrangement = Arrangement.spacedBy(14.dp),
            ) {
                Text(name, style = marquee(26).copy(fontWeight = FontWeight.ExtraBold))
                Text(
                    text, fontSize = 14.5.sp, lineHeight = 21.sp, color = Flexarr.screen.copy(alpha = 0.9f),
                    modifier = Modifier.weight(1f, fill = false).verticalScroll(scroll),
                )
                TvActionButton(
                    "Close", onClick = { open = false },
                    modifier = Modifier.focusRequester(close).onPreviewKeyEvent { event ->
                        if (event.type != KeyEventType.KeyDown) return@onPreviewKeyEvent false
                        val step = when (event.key) {
                            Key.DirectionDown -> 160f
                            Key.DirectionUp -> -160f
                            else -> return@onPreviewKeyEvent false
                        }
                        scope.launch { scroll.animateScrollBy(step) }
                        true
                    },
                )
            }
        }
        LaunchedEffect(Unit) {
            delay(50)
            runCatching { close.requestFocus() }
        }
    }
}

/** Someone's titles, each once: a person who both wrote and starred in a show is credited on it twice. */
private val BookRoles = setOf(CreditRole.Author, CreditRole.Narrator, CreditRole.Translator)

private fun titles(person: Person) =person.credits.map { it.item }.distinctBy { it.id }.filter { it.showsOnTv }
