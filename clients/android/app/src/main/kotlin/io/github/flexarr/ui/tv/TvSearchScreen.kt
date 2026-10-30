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
import androidx.tv.material3.Text
import io.github.flexarr.AppModel
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
    LaunchedEffect(person?.credits?.firstOrNull()?.item?.id) {
        if (person == null || titles(person).isEmpty()) return@LaunchedEffect
        delay(50)
        runCatching { first.requestFocus() }
    }
    RoomBackground(ambient = person?.credits?.firstOrNull()?.item?.let { glowColor(it.accentColor) }, intensity = 0.22f) {
        LazyVerticalGrid(
            GridCells.Fixed(6),
            contentPadding = PaddingValues(start = TvLayout.Edge, end = TvLayout.Edge, top = 28.dp, bottom = 40.dp),
            horizontalArrangement = Arrangement.spacedBy((960.dp - TvLayout.Edge * 2 - TvLayout.PosterWidth * 6) / 5),
            verticalArrangement = Arrangement.spacedBy(28.dp),
        ) {
            item(span = { GridItemSpan(maxLineSpan) }) {
                Row(Modifier.padding(bottom = 6.dp), horizontalArrangement = Arrangement.spacedBy(20.dp), verticalAlignment = Alignment.CenterVertically) {
                    RemoteImage(person?.image, 300, Modifier.size(85.dp).background(Flexarr.seat, CircleShape).clip(CircleShape)) {}
                    Column(verticalArrangement = Arrangement.spacedBy(3.dp)) {
                        Text(name, style = marquee(36).copy(fontWeight = FontWeight.ExtraBold))
                        person?.let {
                            val count = titles(it).size
                            Text(if (count == 1) "1 title in your library" else "$count titles in your library", fontSize = 13.sp, color = Flexarr.usher)
                        }
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

/** Someone's titles, each once: a person who both wrote and starred in a show is credited on it twice. */
private fun titles(person: Person) = person.credits.map { it.item }.distinctBy { it.id }.filter { it.showsOnTv }
