package io.github.flexarr.ui.tv

import androidx.compose.animation.Crossfade
import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.core.animateDpAsState
import androidx.compose.animation.core.tween
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
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.Info
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.outlined.GridView
import androidx.compose.material.icons.outlined.VideoLibrary
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.drawWithContent
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.focus.onFocusChanged
import androidx.compose.ui.graphics.BlendMode
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.CompositingStrategy
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.input.key.Key
import androidx.compose.ui.input.key.KeyEventType
import androidx.compose.ui.input.key.key
import androidx.compose.ui.input.key.onPreviewKeyEvent
import androidx.compose.ui.input.key.type
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.tv.material3.Icon
import androidx.tv.material3.Text
import io.github.flexarr.AppModel
import io.github.flexarr.appModel
import androidx.compose.ui.platform.LocalContext
import io.github.flexarr.core.EmptyHomeMessage
import io.github.flexarr.core.Home
import io.github.flexarr.core.HomeRow
import io.github.flexarr.core.HomeRowStyle
import io.github.flexarr.core.facts
import io.github.flexarr.core.isResumable
import io.github.flexarr.ui.components.Load
import io.github.flexarr.ui.components.LocalInContinueWatching
import io.github.flexarr.ui.components.LocalNavigator
import io.github.flexarr.ui.components.RemoteImage
import io.github.flexarr.ui.components.TitleArt
import io.github.flexarr.ui.components.rememberLoader
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.RoomBackground
import io.github.flexarr.ui.theme.glowColor
import io.github.flexarr.ui.theme.marquee
import io.github.flexarr.core.UpcomingItem
import io.github.flexarr.core.cardDetail
import io.github.flexarr.core.cardTitle
import io.github.flexarr.core.dayLabel
import io.github.flexarr.core.MediaKind
import androidx.compose.foundation.layout.width
import androidx.compose.ui.draw.clip
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import io.github.flexarr.ui.theme.FlexarrIcons

private class TvHomeContent(val home: Home, val emptyMessage: EmptyHomeMessage?)

/**
 * The featured titles in a full-bleed hero, then the server's rows: Continue Watching, recently added and the rest. As
 * the Apple TV's HomeView. [takeFocus]: Play takes focus as Home first shows (the app opening), then [onFocusTaken].
 */
@Composable
fun TvHomeScreen(model: AppModel, takeFocus: Boolean, onFocusTaken: () -> Unit) {
    val client = model.client ?: return
    val loader = rememberLoader(model.credentials, model.watchStateVersion) {
        // Books (and podcasts) aren't featured on the TV, and its rows of them go (see [shows]).
        // Asks for no books or podcasts in Continue watching: the TV app plays neither from there.
        // Coming soon leaves out books too: dev servers before 0.58 sent them.
        val home = client.home().let { home ->
            home.copy(featured = home.featured.filter { it.showsOnTv }, rows = home.rows.filterNot { it.id.startsWith("books-") }, upcoming = home.upcoming.filter { it.kind != MediaKind.Audiobook })
        }
        // Nothing yet: the libraries say whether a scan is under way or their folders hold nothing.
        val empty = if (home.featured.isEmpty() && home.rows.none { it.shows } && home.upcoming.isEmpty()) runCatching { EmptyHomeMessage.of(client.libraries()) }.getOrNull() else null
        TvHomeContent(home, empty)
    }
    when (val state = loader.state) {
        is Load.Loaded -> TvHomeRows(state.value.home, state.value.emptyMessage, takeFocus, onFocusTaken)
        is Load.Failed -> RoomBackground(ambient = null) { TvStatusView(state.message, retry = loader.retry) }
        Load.Loading -> RoomBackground(ambient = null) { TvStatusView() }
    }
}

/**
 * Home's layout, which a library's Browse shares: the hero (with [top] over its top-left corner: a library's view
 * switch), then the rows, on a room washed in the featured title's colour.
 */
@Composable
internal fun TvHomeRows(home: Home, emptyMessage: EmptyHomeMessage?, takeFocus: Boolean, onFocusTaken: () -> Unit, top: (@Composable () -> Unit)? = null) {
    var featuredIndex by remember { mutableIntStateOf(0) }
    val featured = home.featured
    val ambient = animateColorAsState(
        featured.getOrNull(featuredIndex % featured.size.coerceAtLeast(1))?.let { glowColor(it.accentColor) } ?: Flexarr.room,
        tween(800),
        label = "ambient",
    )
    val list = rememberLazyListState()
    val scope = rememberCoroutineScope()
    val rows = remember { FocusRequester() }
    // No hero to take focus: the first row's first card does (the request finds the first focusable thing in the list).
    LaunchedEffect(takeFocus, featured.isEmpty()) {
        if (!takeFocus || featured.isNotEmpty()) return@LaunchedEffect
        for (attempt in 0 until 10) {
            if (runCatching { rows.requestFocus() }.getOrDefault(false)) break
            delay(50)
        }
        onFocusTaken()
    }
    RoomBackground(ambient = { ambient.value }, intensity = 0.28f, reach = 900.dp) {
        LazyColumn(Modifier.fillMaxSize().focusRequester(rows), state = list, contentPadding = PaddingValues(bottom = 40.dp), verticalArrangement = Arrangement.spacedBy(15.dp)) {
            if (featured.isNotEmpty()) {
                item(key = "hero") {
                    // With focus in the hero (back up from the rows, or arriving), the page sits at the top, all of it in view.
                    Box(Modifier.onFocusChanged { if (it.hasFocus) scope.launch { list.animateScrollToItem(0) } }) {
                        HomeHero(home, featuredIndex, onIndex = { featuredIndex = it }, takeFocus = takeFocus, onFocusTaken = onFocusTaken)
                        top?.let { Box(Modifier.padding(start = TvLayout.Edge, top = 28.dp)) { it() } }
                    }
                }
            } else {
                top?.let { item(key = "top-controls") { Box(Modifier.padding(start = TvLayout.Edge, top = 28.dp)) { it() } } }
                emptyMessage?.let { message -> item(key = "empty") { EmptyHome(message) } }
                item(key = "top") { Box(Modifier.height(30.dp)) }
            }
            // Coming soon's titles come separately, in `upcoming`.
            items(home.rows.filter { it.shows || (it.style == HomeRowStyle.Upcoming && home.upcoming.isNotEmpty()) }, key = { it.id }) { row ->
                if (row.style == HomeRowStyle.Upcoming) ComingSoonRow(home.upcoming, home.downloading) else HomeRowView(row)
            }
        }
    }
}

/**
 * What the TV shows of a row: everything but podcasts (nor the empty row placing New podcasts), which stay on the phone
 * and the web; a row with nothing in it would be a bare heading.
 */
private val HomeRow.shows: Boolean
    get() = when (style) {
        HomeRowStyle.Live -> !channels.isNullOrEmpty()
        HomeRowStyle.Playlists -> !playlists.isNullOrEmpty()
        HomeRowStyle.Mixes -> !mixes.isNullOrEmpty()
        // Where New podcasts goes, an empty row; the TV has no podcasts.
        HomeRowStyle.Podcasts -> false
        else -> items.any { it.showsOnTv }
    }

/**
 * Full-bleed featured hero: backdrop, clear logo, facts, two lines of overview, Play and More info. Right on More info
 * moves on to the next featured title, and while it has focus it also moves on by itself; left on Play goes to the
 * sidebar, as left from the screen's left edge does everywhere else.
 */
@Composable
private fun HomeHero(home: Home, index: Int, onIndex: (Int) -> Unit, takeFocus: Boolean, onFocusTaken: () -> Unit) {
    val navigator = LocalNavigator.current
    val items = home.featured
    val item = items[index % items.size]
    val glow = glowColor(item.accentColor)
    val play = remember { FocusRequester() }
    var focused by remember { mutableStateOf(false) }
    fun step(delta: Int) = onIndex((index + delta + items.size) % items.size)

    LaunchedEffect(takeFocus) {
        if (!takeFocus) return@LaunchedEffect
        // As the app opens, keep asking for a moment: the first request can come before Play is placed.
        for (attempt in 0 until 8) {
            runCatching { play.requestFocus() }
            delay(150)
            if (focused) break
        }
        onFocusTaken()
    }
    // While the hero has focus, on to the next title after a while; a step by hand starts the clock again.
    LaunchedEffect(index, focused) {
        if (!focused || items.size < 2) return@LaunchedEffect
        delay(12_000)
        step(1)
    }

    Box(Modifier.fillMaxWidth().height(450.dp)) {
        Crossfade(item, animationSpec = tween(600), label = "backdrop") { shown ->
            Box(Modifier.fillMaxSize().fadeOut(0.55f, 0.95f)) {
                RemoteImage(shown.artwork.backdrop, tvBackdropWidth(), Modifier.fillMaxWidth().height(540.dp)) {}
                // A scrim so text stays legible, then the ambient colour bleeding in from the corner.
                Box(Modifier.matchParentSize().background(Brush.horizontalGradient(0f to Flexarr.room.copy(alpha = 0.92f), 0.4f to Flexarr.room.copy(alpha = 0.55f), 0.75f to Color.Transparent)))
                Box(Modifier.matchParentSize().background(Brush.linearGradient(listOf(glowColor(shown.accentColor).copy(alpha = 0.18f), Color.Transparent), start = androidx.compose.ui.geometry.Offset(0f, Float.POSITIVE_INFINITY), end = androidx.compose.ui.geometry.Offset(Float.POSITIVE_INFINITY, 0f))))
            }
        }
        Column(Modifier.align(Alignment.BottomStart).padding(start = TvLayout.Edge, bottom = 20.dp), verticalArrangement = Arrangement.spacedBy(13.dp)) {
            Crossfade(item, animationSpec = tween(600), label = "title") { shown ->
                Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
                    TitleArt(shown, maxWidth = 380.dp, maxHeight = 115.dp, fontSize = 55)
                    TvFactsLine(shown.facts())
                    home.overviews[shown.id]?.let {
                        Text(it, fontSize = 13.5.sp, lineHeight = 18.sp, color = Flexarr.screen.copy(alpha = 0.85f), maxLines = 2, overflow = TextOverflow.Ellipsis, modifier = Modifier.widthIn(max = 450.dp))
                    }
                }
            }
            Row(
                Modifier.padding(top = 5.dp).onFocusChanged { focused = it.hasFocus },
                horizontalArrangement = Arrangement.spacedBy(14.dp),
            ) {
                val resume = home.resuming(item)
                TvActionButton(
                    if (resume != null || item.isResumable) "Resume" else "Play",
                    onClick = { if (resume != null) navigator.play(resume) else navigator.playBest(item) },
                    icon = FlexarrIcons.play,
                    prominent = true,
                    ambient = glow,
                    modifier = Modifier.focusRequester(play),
                )
                TvActionButton(
                    "More info",
                    onClick = { navigator.openItem(item.id) },
                    icon = Icons.Outlined.Info,
                    ambient = glow,
                    modifier = Modifier.onPreviewKeyEvent { event ->
                        (event.type == KeyEventType.KeyDown && event.key == Key.DirectionRight && items.size > 1).also { if (it) step(1) }
                    },
                )
            }
            if (items.size > 1) {
                Row(Modifier.padding(top = 3.dp), horizontalArrangement = Arrangement.spacedBy(6.dp), verticalAlignment = Alignment.CenterVertically) {
                    items.indices.forEach { i ->
                        val current = i == index % items.size
                        val width by animateDpAsState(if (current) 17.dp else 6.dp, label = "dot")
                        Box(Modifier.size(width, 6.dp).background(Flexarr.screen.copy(alpha = if (current) 1f else 0.3f), CircleShape))
                    }
                }
            }
        }
    }
}

@Composable
private fun ComingSoonRow(items: List<UpcomingItem>, downloading: Int) {
    val navigator = LocalNavigator.current
    Column {
        TvRowHeading(if (downloading > 0) "Coming soon ($downloading downloading)" else "Coming soon")
        LazyRow(
            contentPadding = PaddingValues(start = TvLayout.Edge, end = TvLayout.Edge, top = 14.dp, bottom = 20.dp),
            horizontalArrangement = Arrangement.spacedBy(TvLayout.RowSpacing),
            verticalAlignment = Alignment.Top,
        ) {
            items(items, key = { it.id }) { item -> TvComingSoonCard(item) }
            // The rest, a day at a time, as on the Apple TV.
            item(key = "see-all") { TvSeeAllCard(onClick = navigator::openComingSoon) }
        }
    }
}

@Composable
private fun HomeRowView(row: HomeRow) {
    val navigator = LocalNavigator.current
    val items = row.items.filter { it.showsOnTv }
    Column {
        TvRowHeading(row.title)
        CompositionLocalProvider(LocalInContinueWatching provides (row.id == "continue")) {
            LazyRow(
                contentPadding = PaddingValues(start = TvLayout.Edge, end = TvLayout.Edge, top = 14.dp, bottom = 20.dp),
                horizontalArrangement = Arrangement.spacedBy(if (row.style == HomeRowStyle.Top10) 15.dp else TvLayout.RowSpacing),
                verticalAlignment = Alignment.Top,
            ) {
                when (row.style) {
                    // The profile's favourite channels; channel up and down go through them.
                    HomeRowStyle.Live -> items(row.channels.orEmpty(), key = { it.id }) { TvHomeChannelCard(LocalContext.current.appModel, it, row.channels.orEmpty()) }
                    // A row of tracks plays: the one pressed first, the rest of the row after it.
                    HomeRowStyle.Tracks -> itemsIndexed(items, key = { _, item -> item.id }) { index, item ->
                        val model = LocalContext.current.appModel
                        TvTrackCard(model, item, onClick = { model.music.play(items, start = index) })
                    }
                    HomeRowStyle.Playlists -> items(row.playlists.orEmpty(), key = { it.id }) { TvPlaylistCard(it, onClick = { navigator.openPlaylist(it.id) }) }
                    HomeRowStyle.Mixes -> items(row.mixes.orEmpty(), key = { it.id }) { TvMixCard(it, onClick = { navigator.playMix(it.id) }) }
                    HomeRowStyle.Wide -> items(items, key = { it.id }) { TvWideCard(it, onClick = { navigator.play(it) }) }
                    HomeRowStyle.Top10 -> itemsIndexed(items, key = { _, item -> item.id }) { index, item -> TvTop10Card(index + 1, item, onClick = { navigator.open(item) }) }
                    else -> items(items, key = { it.id }) { TvPosterCard(it, onClick = { navigator.open(it) }) }
                }
                navigator.libraryLink(row.link)?.let { seeAll -> item(key = "see-all") { TvSeeAllCard(wide = row.style == HomeRowStyle.Wide, onClick = seeAll) } }
            }
        }
    }
}

/** The end of a row that links to its library: all of it, in a grid. */
@Composable
private fun TvSeeAllCard(wide: Boolean, onClick: () -> Unit) {
    val width = if (wide) TvLayout.WideWidth else TvLayout.PosterWidth
    TvArtworkButton(ambient = Flexarr.silver, onClick = onClick, contentDescription = "See all", artwork = {
        Column(
            Modifier.size(width, if (wide) width * 9f / 16f else width * 1.5f),
            verticalArrangement = Arrangement.spacedBy(8.dp, Alignment.CenterVertically),
            horizontalAlignment = Alignment.CenterHorizontally,
        ) {
            Icon(Icons.Outlined.GridView, contentDescription = null, tint = Flexarr.screen.copy(alpha = 0.85f), modifier = Modifier.size(24.dp))
            Text("See all", fontSize = 14.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen.copy(alpha = 0.85f))
        }
    })
}

/** Home with nothing to show: no library yet, a first scan under way, or libraries with nothing recognised in them. */
@Composable
private fun EmptyHome(message: EmptyHomeMessage) {
    Column(Modifier.widthIn(max = 550.dp).padding(start = TvLayout.Edge, top = 80.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        Icon(Icons.Outlined.VideoLibrary, contentDescription = null, tint = Flexarr.silver, modifier = Modifier.size(38.dp))
        Text(message.title, style = marquee(32))
        Text(message.body, fontSize = 16.sp, color = Flexarr.usher)
        message.folders.forEach { Text(it, fontSize = 13.sp, fontFamily = FontFamily.Monospace) }
    }
}

/** Drawn into its own layer, then faded to nothing from [from] down to [to], so there's no seam with the room below. */
internal fun Modifier.fadeOut(from: Float, to: Float) = graphicsLayer(compositingStrategy = CompositingStrategy.Offscreen).drawWithContent {
    drawContent()
    drawRect(Brush.verticalGradient(from to Color.Black, to to Color.Transparent), blendMode = BlendMode.DstIn)
}
