package io.github.flexarr.ui.home

import androidx.compose.animation.animateColorAsState
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
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.pager.HorizontalPager
import androidx.compose.foundation.pager.rememberPagerState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Info
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.outlined.VideoLibrary
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Text
import androidx.compose.material3.pulltorefresh.PullToRefreshBox
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.drawWithContent
import androidx.compose.ui.graphics.BlendMode
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.CompositingStrategy
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.github.flexarr.AppModel
import io.github.flexarr.core.EmptyHomeMessage
import io.github.flexarr.core.Home
import io.github.flexarr.core.HomeRow
import io.github.flexarr.core.HomeRowStyle
import io.github.flexarr.core.ItemSummary
import io.github.flexarr.core.facts
import io.github.flexarr.core.isResumable
import io.github.flexarr.ui.components.ActionButton
import io.github.flexarr.ui.components.Edge
import io.github.flexarr.ui.components.Load
import io.github.flexarr.ui.components.LocalInContinueWatching
import io.github.flexarr.ui.components.LocalNavigator
import io.github.flexarr.ui.components.Loaded
import io.github.flexarr.ui.components.PosterCard
import io.github.flexarr.ui.components.PosterWidth
import io.github.flexarr.ui.components.RemoteImage
import io.github.flexarr.ui.music.PlaylistCard
import io.github.flexarr.ui.music.TrackCard
import io.github.flexarr.ui.components.RowSpacing
import io.github.flexarr.ui.components.SectionHeading
import io.github.flexarr.ui.components.TitleArt
import io.github.flexarr.ui.components.Top10Card
import io.github.flexarr.ui.components.WideCard
import io.github.flexarr.ui.components.FactsLine
import io.github.flexarr.ui.components.pressable
import io.github.flexarr.ui.components.rememberLoader
import io.github.flexarr.ui.profiles.ProfileAvatar
import io.github.flexarr.ui.profiles.ProfilePicker
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.RoomBackground
import io.github.flexarr.ui.theme.glowColor
import io.github.flexarr.ui.theme.marquee
import kotlinx.coroutines.delay

private class HomeContent(val home: Home, val emptyMessage: EmptyHomeMessage?)

/** Featured titles to swipe through, then the server's rows: Continue Watching, recently added and the rest. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun HomeScreen(model: AppModel) {
    val client = model.client ?: return
    val loader = rememberLoader(model.credentials, model.watchStateVersion) {
        val home = client.home()
        // Nothing yet: the libraries say whether a scan is under way or their folders hold nothing.
        val empty = if (home.featured.isEmpty()) runCatching { EmptyHomeMessage.of(client.libraries()) }.getOrNull() else null
        HomeContent(home, empty)
    }
    var featuredIndex by remember { mutableIntStateOf(0) }
    var choosingProfile by remember { mutableStateOf(false) }

    val featured = (loader.state as? Load.Loaded)?.value?.home?.featured
    val ambient by animateColorAsState(
        featured?.getOrNull(featuredIndex % featured.size.coerceAtLeast(1))?.let { glowColor(it.accentColor) } ?: Flexarr.room,
        tween(800),
        label = "ambient",
    )

    RoomBackground(ambient = ambient, intensity = 0.28f) {
        Loaded(loader) { content ->
            val home = content.home
            PullToRefreshBox(isRefreshing = loader.refreshing, onRefresh = loader.retry, modifier = Modifier.fillMaxSize()) {
                LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 32.dp), verticalArrangement = Arrangement.spacedBy(28.dp)) {
                    // Above whatever else Home has; the whole screen when there's nothing else.
                    content.emptyMessage?.let { message -> item(key = "empty") { EmptyHome(message) } }
                    if (home.featured.isNotEmpty()) {
                        item(key = "hero") { HomeHero(home, onIndex = { featuredIndex = it }) }
                    } else {
                        item(key = "top") { Box(Modifier.statusBarsPadding()) }
                    }
                    // A row with nothing in it (a kind this app doesn't show yet, say) would be a bare heading.
                    items(home.rows.filter { it.items.isNotEmpty() || !it.playlists.isNullOrEmpty() }, key = { it.id }) { row ->
                        HomeRowView(model, row)
                    }
                }
            }
        }
        if (model.profiles.size > 1) {
            model.currentProfile?.let { current ->
                Box(Modifier.align(Alignment.TopEnd).statusBarsPadding().padding(12.dp).clip(CircleShape).pressable({ choosingProfile = true })) {
                    ProfileAvatar(model, current, size = 32)
                }
            }
        }
    }

    if (choosingProfile) {
        ModalBottomSheet(onDismissRequest = { choosingProfile = false }, containerColor = Flexarr.room) {
            ProfilePicker(model, marksCurrent = true, onChosen = { choosingProfile = false })
        }
    }
}

@Composable
private fun HomeHero(home: Home, onIndex: (Int) -> Unit) {
    val items = home.featured
    val pager = rememberPagerState { items.size }
    LaunchedEffect(pager.currentPage) {
        onIndex(pager.currentPage)
        // Moves on by itself after a while; a swipe restarts the clock.
        if (items.size < 2) return@LaunchedEffect
        delay(10_000)
        pager.animateScrollToPage((pager.currentPage + 1) % items.size)
    }
    Box(Modifier.fillMaxWidth().height(560.dp)) {
        HorizontalPager(pager, Modifier.fillMaxSize()) { page -> HeroSlide(home, items[page]) }
        if (items.size > 1) {
            Row(Modifier.align(Alignment.BottomCenter).padding(bottom = 16.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                repeat(items.size) { index ->
                    Box(Modifier.size(7.dp).clip(CircleShape).background(Flexarr.screen.copy(alpha = if (index == pager.currentPage) 1f else 0.35f)))
                }
            }
        }
    }
}

/** A featured title: backdrop, clear logo, facts, a line of overview, Play and More info. */
@Composable
private fun HeroSlide(home: Home, item: ItemSummary) {
    val navigator = LocalNavigator.current
    val glow = glowColor(item.accentColor)
    Box(Modifier.fillMaxSize().pressable({ navigator.openItem(item.id) })) {
        // Fades out rather than painting the room colour, so there's no seam with the rows below.
        RemoteImage(
            item.artwork.backdrop,
            1000,
            Modifier.fillMaxSize().fadeOut(),
        ) {
            Box(Modifier.fillMaxSize().background(Brush.verticalGradient(listOf(glow.copy(alpha = 0.4f), Flexarr.room))))
        }
        Box(Modifier.fillMaxSize().background(Brush.verticalGradient(0.25f to Color.Transparent, 1f to Flexarr.room.copy(alpha = 0.7f))))
        Column(Modifier.align(Alignment.BottomStart).padding(start = Edge, end = Edge, bottom = 44.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            TitleArt(item, maxWidth = 280.dp, maxHeight = 110.dp, fontSize = 52)
            FactsLine(item.facts)
            home.overviews[item.id]?.let {
                Text(it, fontSize = 15.sp, color = Flexarr.screen.copy(alpha = 0.85f), maxLines = 2, overflow = TextOverflow.Ellipsis, modifier = Modifier.widthIn(max = 520.dp))
            }
            Row(horizontalArrangement = Arrangement.spacedBy(10.dp), modifier = Modifier.padding(top = 4.dp)) {
                val resume = home.resumes?.get(item.id)
                ActionButton(
                    if (resume != null || item.isResumable) "Resume" else "Play",
                    onClick = { if (resume != null) navigator.play(resume) else navigator.playBest(item) },
                    icon = Icons.Filled.PlayArrow,
                    prominent = true,
                    ambient = glow,
                )
                ActionButton("More info", onClick = { navigator.openItem(item.id) }, icon = Icons.Filled.Info, ambient = glow)
            }
        }
    }
}

/** Drawn into its own layer, then faded to nothing over the bottom half. */
private fun Modifier.fadeOut() = graphicsLayer(compositingStrategy = CompositingStrategy.Offscreen).drawWithContent {
    drawContent()
    drawRect(Brush.verticalGradient(0.5f to Color.Black, 1f to Color.Transparent), blendMode = BlendMode.DstIn)
}

@Composable
private fun HomeRowView(model: AppModel, row: HomeRow) {
    val navigator = LocalNavigator.current
    Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
        SectionHeading(row.title, onSeeAll = navigator.libraryLink(row.link))
        CompositionLocalProvider(LocalInContinueWatching provides (row.id == "continue")) {
            LazyRow(
                contentPadding = PaddingValues(horizontal = Edge),
                horizontalArrangement = Arrangement.spacedBy(if (row.style == HomeRowStyle.Top10) 6.dp else RowSpacing),
                verticalAlignment = Alignment.Top,
            ) {
                when (row.style) {
                    HomeRowStyle.Wide -> items(row.items, key = { it.id }) { WideCard(it) }
                    HomeRowStyle.Top10 -> itemsIndexed(row.items, key = { _, item -> item.id }) { index, item -> Top10Card(index + 1, item) }
                    // A row of tracks plays: the one tapped first, the rest of the row after it.
                    HomeRowStyle.Tracks -> itemsIndexed(row.items, key = { _, item -> item.id }) { index, item ->
                        TrackCard(model, item, PosterWidth) { model.music.play(row.items, start = index) }
                    }
                    HomeRowStyle.Playlists -> items(row.playlists.orEmpty(), key = { it.id }) { PlaylistCard(it, PosterWidth) }
                    else -> items(row.items, key = { it.id }) { PosterCard(it) }
                }
            }
        }
    }
}

/** Home with nothing to show: no library yet, a first scan under way, or libraries with nothing recognised in them. */
@Composable
private fun EmptyHome(message: EmptyHomeMessage) {
    Column(Modifier.fillMaxWidth().statusBarsPadding().padding(horizontal = 24.dp).padding(top = 72.dp), verticalArrangement = Arrangement.spacedBy(14.dp)) {
        Icon(Icons.Outlined.VideoLibrary, contentDescription = null, tint = Flexarr.silver, modifier = Modifier.size(44.dp))
        Text(message.title, style = marquee(34))
        Text(message.body, fontSize = 17.sp, color = Flexarr.usher)
        if (message.folders.isNotEmpty()) {
            Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
                message.folders.forEach { Text(it, fontSize = 13.sp, fontFamily = FontFamily.Monospace, color = Flexarr.screen) }
            }
        }
        Text("Pull down to refresh.", fontSize = 13.sp, color = Flexarr.usher, fontWeight = FontWeight.Normal)
    }
}
