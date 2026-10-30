package io.github.flexarr.ui.tv

import android.app.Activity
import android.content.Intent
import android.speech.RecognizerIntent
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsFocusedAsState
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
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
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Mic
import androidx.compose.material.icons.filled.Search
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.focus.FocusDirection
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalFocusManager
import androidx.compose.ui.platform.LocalSoftwareKeyboardController
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.tv.material3.Icon
import androidx.tv.material3.Text
import io.github.flexarr.AppModel
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
    val field = remember { FocusRequester() }

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
            when {
                found != null && found.titles.isEmpty() && found.episodes.isEmpty() && found.people.isEmpty() -> item(key = "none") {
                    Box(Modifier.height(200.dp)) { TvStatusView("Nothing matches “${query.trim()}”.", empty = true) }
                }
                found != null -> {
                    val titles = found.titles.filter { it.showsOnTv }
                    if (titles.isNotEmpty()) item(key = "titles") {
                        Section("Movies and shows") { items(titles, key = { it.id }) { TvPosterCard(it, onClick = { navigator.open(it) }) } }
                    }
                    if (found.episodes.isNotEmpty()) item(key = "episodes") {
                        Section("Episodes") { items(found.episodes, key = { it.id }) { TvWideCard(it, onClick = { navigator.openItem(it.id) }, width = 220.dp) } }
                    }
                    if (found.people.isNotEmpty()) item(key = "people") {
                        Section("People") {
                            items(found.people, key = { it.id }) { person ->
                                TvPersonCard(person.name, if (person.credits == 1) "1 title" else "${person.credits} titles", person.image) { navigator.openPerson(person.id, person.name) }
                            }
                        }
                    }
                }
                error != null -> item(key = "error") { Box(Modifier.height(200.dp)) { TvStatusView(error, retry = { attempt++ }) } }
            }
        }
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

/** The search field: a magnifier and the words, edged in white while it has focus; OK opens the TV's keyboard. */
@Composable
private fun SearchField(value: String, onValue: (String) -> Unit, modifier: Modifier) {
    val keyboard = LocalSoftwareKeyboardController.current
    val focusManager = LocalFocusManager.current
    val source = remember { MutableInteractionSource() }
    val focused by source.collectIsFocusedAsState()
    BasicTextField(
        value = value,
        onValueChange = onValue,
        singleLine = true,
        textStyle = TextStyle(fontSize = 17.sp, color = Flexarr.screen),
        cursorBrush = SolidColor(Flexarr.screen),
        keyboardOptions = KeyboardOptions(imeAction = ImeAction.Search),
        // The keyboard's Search: it goes, and focus moves down to what was found.
        keyboardActions = KeyboardActions(onSearch = {
            keyboard?.hide()
            focusManager.moveFocus(FocusDirection.Down)
        }),
        interactionSource = source,
        modifier = modifier,
        decorationBox = { text ->
            Row(
                Modifier.fillMaxWidth().height(42.dp).background(Flexarr.screen.copy(alpha = if (focused) 0.16f else 0.1f), RoundedCornerShape(10.dp))
                    .border(2.dp, if (focused) Flexarr.screen else Color.Transparent, RoundedCornerShape(10.dp)).padding(horizontal = 14.dp),
                horizontalArrangement = Arrangement.spacedBy(10.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Icon(Icons.Filled.Search, contentDescription = null, tint = Flexarr.usher, modifier = Modifier.size(18.dp))
                Box {
                    if (value.isEmpty()) Text("Movies, shows, episodes and people", fontSize = 17.sp, color = Flexarr.usher)
                    text()
                }
            }
        },
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
        if (person?.credits.isNullOrEmpty()) return@LaunchedEffect
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
                        person?.let { Text(if (it.credits.size == 1) "1 title in your library" else "${it.credits.size} titles in your library", fontSize = 13.sp, color = Flexarr.usher) }
                    }
                }
            }
            when (val state = loader.state) {
                is Load.Loaded -> {
                    val credits = state.value.credits.map { it.item }.filter { it.showsOnTv }
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
