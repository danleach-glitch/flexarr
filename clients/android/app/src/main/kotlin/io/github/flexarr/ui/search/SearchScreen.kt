package io.github.flexarr.ui.search

import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.detectTapGestures
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
import androidx.compose.material.icons.filled.Search
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
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.input.nestedscroll.NestedScrollConnection
import androidx.compose.ui.input.nestedscroll.NestedScrollSource
import androidx.compose.ui.input.nestedscroll.nestedScroll
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.platform.LocalFocusManager
import androidx.compose.ui.platform.LocalSoftwareKeyboardController
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.github.flexarr.AppModel
import io.github.flexarr.core.SearchResults
import io.github.flexarr.ui.components.GridMinimum
import io.github.flexarr.ui.components.Edge
import io.github.flexarr.ui.components.Load
import io.github.flexarr.ui.components.LocalNavigator
import io.github.flexarr.ui.components.Loaded
import io.github.flexarr.ui.components.PageTitle
import io.github.flexarr.ui.components.PersonCard
import io.github.flexarr.ui.components.PosterCard
import io.github.flexarr.ui.components.RemoteImage
import io.github.flexarr.ui.components.RowSpacing
import io.github.flexarr.ui.components.SectionHeading
import io.github.flexarr.ui.components.StatusView
import io.github.flexarr.ui.components.WideCard
import io.github.flexarr.ui.components.rememberLoader
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.RoomBackground
import io.github.flexarr.ui.theme.marquee
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.delay

/** Films, shows, episodes and people in the libraries, as they're typed. The iPhone app's SearchView. */
@Composable
fun SearchScreen(model: AppModel) {
    val client = model.client ?: return
    var query by rememberSaveable { mutableStateOf("") }
    var results by remember { mutableStateOf<SearchResults?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    var attempt by remember { mutableIntStateOf(0) }
    val focus = remember { FocusRequester() }
    val keyboard = LocalSoftwareKeyboardController.current
    val focusManager = LocalFocusManager.current

    // Opening Search is for typing: the field's ready with the keyboard up.
    LaunchedEffect(Unit) { if (query.isEmpty()) focus.requestFocus() }
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
            }
            val shown = results
            LazyColumn(
                Modifier.fillMaxSize().nestedScroll(dismissOnScroll),
                contentPadding = PaddingValues(vertical = 12.dp),
                verticalArrangement = Arrangement.spacedBy(24.dp),
            ) {
                when {
                    shown != null -> results(shown, query.trim())
                    error != null -> item { StatusView(error, retry = { attempt++ }, modifier = Modifier.height(240.dp)) }
                    else -> item { StatusView("Find films, shows, episodes and people in your libraries.", empty = true, modifier = Modifier.height(240.dp)) }
                }
            }
        }
    }
}

private fun LazyListScope.results(results: SearchResults, query: String) {
    if (results.isEmpty) {
        item { StatusView("Nothing matches “$query”.", empty = true, modifier = Modifier.height(240.dp)) }
    }
    if (results.titles.isNotEmpty()) {
        item(key = "titles") {
            Section("Movies and shows") {
                LazyRow(contentPadding = PaddingValues(horizontal = Edge), horizontalArrangement = Arrangement.spacedBy(RowSpacing)) {
                    items(results.titles, key = { it.id }) { PosterCard(it) }
                }
            }
        }
    }
    if (results.episodes.isNotEmpty()) {
        item(key = "episodes") {
            Section("Episodes") {
                LazyRow(contentPadding = PaddingValues(horizontal = Edge), horizontalArrangement = Arrangement.spacedBy(RowSpacing)) {
                    items(results.episodes, key = { it.id }) { WideCard(it) }
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
                            navigator.openPerson(person.id, person.name)
                        }
                    }
                }
            }
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
        modifier.fillMaxWidth().height(40.dp).clip(RoundedCornerShape(10.dp)).background(Flexarr.seat).padding(horizontal = 10.dp),
        horizontalArrangement = Arrangement.spacedBy(8.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Icon(Icons.Filled.Search, contentDescription = null, tint = Flexarr.usher, modifier = Modifier.size(20.dp))
        Box(Modifier.weight(1f), contentAlignment = Alignment.CenterStart) {
            if (query.isEmpty()) Text(placeholder, fontSize = 17.sp, color = Flexarr.usher, maxLines = 1)
            BasicTextField(
                query,
                onValueChange = onChange,
                singleLine = true,
                textStyle = TextStyle(fontSize = 17.sp, color = Flexarr.screen),
                cursorBrush = SolidColor(Flexarr.screen),
                keyboardOptions = KeyboardOptions(imeAction = ImeAction.Search),
                keyboardActions = KeyboardActions(onSearch = { onSearch() }),
                modifier = Modifier.fillMaxWidth(),
            )
        }
        if (query.isNotEmpty()) {
            Icon(
                Icons.Filled.Cancel,
                contentDescription = "Clear",
                tint = Flexarr.usher,
                modifier = Modifier.size(20.dp).pointerInput(Unit) { detectTapGestures { onChange("") } },
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
                                val count = it.credits.size
                                Text(if (count == 1) "1 title in your library" else "$count titles in your library", fontSize = 15.sp, color = Flexarr.usher)
                            }
                        }
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
