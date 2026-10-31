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
import androidx.compose.material3.rememberModalBottomSheetState
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
import androidx.compose.ui.input.pointer.PointerEventPass
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.Dp
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.interaction.collectIsDraggedAsState
import androidx.compose.foundation.layout.heightIn
import androidx.compose.material3.minimumInteractiveComponentSize
import android.content.Context
import android.provider.Settings
import android.view.accessibility.AccessibilityManager
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.github.flexarr.AppModel
import io.github.flexarr.appModel
import io.github.flexarr.core.EmptyHomeMessage
import io.github.flexarr.core.Home
import io.github.flexarr.core.HomeRow
import io.github.flexarr.core.HomeRowStyle
import io.github.flexarr.ui.livetv.HomeChannelCard
import io.github.flexarr.core.ItemSummary
import io.github.flexarr.core.facts
import io.github.flexarr.core.isResumable
import io.github.flexarr.ui.components.isWide
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
import io.github.flexarr.core.PodcastEpisode
import io.github.flexarr.core.newPodcastEpisodes
import io.github.flexarr.ui.components.ProgressBar
import io.github.flexarr.ui.podcasts.EpisodeSheet
import io.github.flexarr.ui.podcasts.PodcastArtwork
import io.github.flexarr.ui.podcasts.setEpisodePlayed
import androidx.compose.foundation.layout.width
import kotlinx.coroutines.async
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.delay

private class HomeContent(val home: Home, val newEpisodes: List<PodcastEpisode>, val isEmpty: Boolean, val emptyMessage: EmptyHomeMessage?)

/**
 * Featured titles to swipe through, then the server's rows: Continue Watching, the newest episodes of the podcasts
 * followed, recently added and the rest.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun HomeScreen(model: AppModel) {
    val client = model.client ?: return
    // The watchlist too, when a change could alter Home's row of what on it is ready.
    val loader = rememberLoader(model.credentials, model.watchStateVersion, model.homeWatchlistVersion) {
        coroutineScope {
            // Empty without podcasts (none followed, no library, a server from before them: an error, also nothing).
            val episodes = async {
                if (model.isKids) emptyList() else runCatching { client.newPodcastEpisodes(unplayed = true, take = 20) }.getOrDefault(emptyList())
            }
            val home = client.home()
            val newEpisodes = episodes.await()
            // Nothing at all yet: the libraries say whether a scan is under way or their folders hold nothing. Not to a
            // profile with an age limit, for whom it's only that nothing's within it.
            val isEmpty = home.featured.isEmpty() && newEpisodes.isEmpty() && home.rows.none { it.hasContent }
            val empty = if (isEmpty && model.currentProfile?.hasAgeLimit != true) {
                runCatching { EmptyHomeMessage.of(client.libraries()) }.getOrNull()
            } else {
                null
            }
            HomeContent(home, newEpisodes, isEmpty, empty)
        }
    }
    var featuredIndex by remember { mutableIntStateOf(0) }
    var choosingProfile by remember { mutableStateOf(false) }

    val featured = (loader.state as? Load.Loaded)?.value?.home?.featured
    // Read only when the room is drawn (RoomBackground), so the 800 ms ease doesn't recompose Home.
    val ambient = animateColorAsState(
        featured?.getOrNull(featuredIndex % featured.size.coerceAtLeast(1))?.let { glowColor(it.accentColor) } ?: Flexarr.room,
        tween(800),
        label = "ambient",
    )

    RoomBackground(ambient = { ambient.value }, intensity = 0.28f) {
        Loaded(loader) { content ->
            val home = content.home
            PullToRefreshBox(isRefreshing = loader.refreshing, onRefresh = loader.retry, modifier = Modifier.fillMaxSize()) {
                LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 32.dp), verticalArrangement = Arrangement.spacedBy(28.dp)) {
                    // Only when Home has nothing else at all; a profile with an age limit gets one line, not the setup talk.
                    // One or the other: a profile just switched to can briefly have both.
                    val limited = model.currentProfile?.takeIf { it.hasAgeLimit }
                    if (content.isEmpty && limited != null) {
                        item(key = "empty") { NothingYet(limited.name) }
                    } else {
                        content.emptyMessage?.let { message -> item(key = "empty") { EmptyHome(message) } }
                    }
                    if (home.featured.isNotEmpty()) {
                        item(key = "hero") { HomeHero(home, onIndex = { featuredIndex = it }) }
                    } else {
                        item(key = "top") { Box(Modifier.statusBarsPadding()) }
                    }
                    // A row with nothing in it (a kind this app doesn't show yet, say) would be a bare heading.
                    val rows = home.rows.filter { it.hasContent }
                    val continuing = if (rows.firstOrNull()?.id == "continue") 1 else 0
                    val row: @Composable (HomeRow) -> Unit = { if (it.style == HomeRowStyle.Live) LiveRowView(it) else HomeRowView(model, it) }
                    items(rows.take(continuing), key = { it.id }) { row(it) }
                    if (content.newEpisodes.isNotEmpty()) {
                        item(key = "new-podcasts") { NewEpisodesRow(model, content.newEpisodes) }
                    }
                    items(rows.drop(continuing), key = { it.id }) { row(it) }
                }
            }
        }
        // Locked to one kids profile, the picker is still the way to unlock.
        if (model.profiles.size > 1 || model.kidsLock) {
            model.currentProfile?.let { current ->
                // Big enough to find at a glance, and to touch.
                Box(
                    Modifier.align(Alignment.TopEnd).statusBarsPadding().padding(4.dp).minimumInteractiveComponentSize().clip(CircleShape)
                        .pressable({ choosingProfile = true }, onClickLabel = "Switch profile")
                        .semantics { contentDescription = "Profile: ${current.name}" },
                    contentAlignment = Alignment.Center,
                ) {
                    ProfileAvatar(model, current, size = 56)
                }
            }
        }
    }

    if (choosingProfile) {
        ModalBottomSheet(onDismissRequest = { choosingProfile = false }, sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true), containerColor = Flexarr.room) {
            ProfilePicker(model, marksCurrent = true, onChosen = { choosingProfile = false })
        }
    }
}

@Composable
private fun HomeHero(home: Home, onIndex: (Int) -> Unit) {
    val items = home.featured
    val pager = rememberPagerState { items.size }
    val context = LocalContext.current
    // A finger on it holds it where it is.
    var touching by remember { mutableStateOf(false) }
    val dragging by pager.interactionSource.collectIsDraggedAsState()
    LaunchedEffect(pager.currentPage) { onIndex(pager.currentPage) }
    // Keyed on the settled page, not the current one: that changes halfway through a move, which would cancel the
    // move and leave the carousel stuck between two slides.
    LaunchedEffect(pager.settledPage, touching || dragging) {
        if (touching || dragging) return@LaunchedEffect
        // A move cut short (a touch while it slid) is finished rather than left half-way.
        if (!pager.isScrollInProgress && pager.currentPageOffsetFraction != 0f) pager.animateScrollToPage(pager.currentPage)
        // Moves on by itself after a while; a swipe restarts the clock. Not while it's held, nor for someone using
        // TalkBack (it would move out from under them) or who has turned animations off.
        if (items.size < 2 || !movesOnItsOwn(context)) return@LaunchedEffect
        delay(10_000)
        if (movesOnItsOwn(context)) pager.animateScrollToPage((pager.currentPage + 1) % items.size)
    }
    val height = if (isWide) 620.dp else 560.dp
    Box(
        Modifier.fillMaxWidth().heightIn(min = height).pointerInput(Unit) {
            awaitEachGesture {
                awaitFirstDown(requireUnconsumed = false, pass = PointerEventPass.Initial)
                touching = true
                do {
                    val event = awaitPointerEvent(PointerEventPass.Initial)
                } while (event.changes.any { it.pressed })
                touching = false
            }
        },
    ) {
        // Taller than usual only when large text needs it.
        HorizontalPager(pager, Modifier.fillMaxWidth(), verticalAlignment = Alignment.Top) { page -> HeroSlide(home, items[page], height) }
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
private fun HeroSlide(home: Home, item: ItemSummary, height: Dp) {
    val navigator = LocalNavigator.current
    val glow = glowColor(item.accentColor)
    Box(Modifier.fillMaxWidth().heightIn(min = height).pressable({ navigator.openItem(item.id) }, onClickLabel = "Open")) {
        // Fades out rather than painting the room colour, so there's no seam with the rows below.
        RemoteImage(
            item.artwork.backdrop,
            1000,
            Modifier.matchParentSize().fadeOut(),
        ) {
            Box(Modifier.fillMaxSize().background(Brush.verticalGradient(listOf(glow.copy(alpha = 0.4f), Flexarr.room))))
        }
        Box(Modifier.matchParentSize().background(Brush.verticalGradient(0.25f to Color.Transparent, 1f to Flexarr.room.copy(alpha = 0.7f))))
        // Room above for the backdrop and the status bar when large text makes the slide grow.
        Column(Modifier.align(Alignment.BottomStart).padding(start = Edge, end = Edge, bottom = 44.dp, top = 160.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            TitleArt(item, maxWidth = 280.dp, maxHeight = 110.dp, fontSize = 52)
            // No scores for a child.
            FactsLine(item.facts(scores = !LocalContext.current.appModel.isKids))
            home.overviews[item.id]?.let {
                Text(it, fontSize = 15.sp, color = Flexarr.screen.copy(alpha = 0.85f), maxLines = 2, overflow = TextOverflow.Ellipsis, modifier = Modifier.widthIn(max = 520.dp))
            }
            Row(horizontalArrangement = Arrangement.spacedBy(10.dp), modifier = Modifier.padding(top = 4.dp)) {
                val resume = home.resuming(item)
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

/** Whether the featured titles may move on by themselves: not under TalkBack, nor with animations turned off. */
private fun movesOnItsOwn(context: Context): Boolean {
    val touchExploring = context.getSystemService(AccessibilityManager::class.java)?.isTouchExplorationEnabled == true
    val animations = Settings.Global.getFloat(context.contentResolver, Settings.Global.ANIMATOR_DURATION_SCALE, 1f)
    return !touchExploring && animations != 0f
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

/**
 * The newest unplayed episodes of the podcasts followed: square artwork, the episode and its show. Tapping opens the
 * episode (its notes, Play, its download), as everywhere else episodes are listed.
 */
@Composable
private fun NewEpisodesRow(model: AppModel, episodes: List<PodcastEpisode>) {
    var opened by remember { mutableStateOf<PodcastEpisode?>(null) }
    val width = PosterWidth * 1.15f
    Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
        SectionHeading("New podcasts")
        LazyRow(contentPadding = PaddingValues(horizontal = Edge), horizontalArrangement = Arrangement.spacedBy(RowSpacing), verticalAlignment = Alignment.Top) {
            items(episodes, key = { it.id }) { episode ->
                Column(
                    Modifier.width(width).pressable({ opened = episode }, onClickLabel = "Show the episode")
                        .semantics(mergeDescendants = true) { contentDescription = "${episode.title}, ${episode.podcastTitle}" },
                    verticalArrangement = Arrangement.spacedBy(4.dp),
                ) {
                    Box {
                        PodcastArtwork(episode.artwork.thumb ?: episode.artwork.poster, episode.podcastTitle, episode.accentColor, imageWidth = 342)
                        episode.progress?.let { ProgressBar(it, Modifier.align(Alignment.BottomCenter).padding(6.dp)) }
                    }
                    Text(episode.title, fontSize = 15.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen, maxLines = 2, overflow = TextOverflow.Ellipsis)
                    Text(episode.podcastTitle, fontSize = 12.sp, color = Flexarr.usher, maxLines = 1, overflow = TextOverflow.Ellipsis)
                }
            }
        }
    }
    opened?.let { episode ->
        EpisodeSheet(model, episode, setPlayed = { setEpisodePlayed(model, episode, it) }, onDismiss = { opened = null })
    }
}

/** Favourite channels: each one's logo with how far through what's on, and what's on beneath; the heading opens Live TV. */
@Composable
private fun LiveRowView(row: HomeRow) {
    val navigator = LocalNavigator.current
    val channels = row.channels.orEmpty()
    Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
        SectionHeading(row.title, onSeeAll = navigator::openLiveTv)
        LazyRow(contentPadding = PaddingValues(horizontal = Edge), horizontalArrangement = Arrangement.spacedBy(RowSpacing), verticalAlignment = Alignment.Top) {
            items(channels, key = { it.id }) { HomeChannelCard(it, channels) }
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

/** Home with nothing within a profile's age limit: one calm line, and nothing to set up. */
@Composable
private fun NothingYet(name: String) {
    // As the empty Home looks, as the iPhone's does.
    Column(Modifier.fillMaxWidth().statusBarsPadding().padding(horizontal = 24.dp).padding(top = 72.dp), verticalArrangement = Arrangement.spacedBy(14.dp)) {
        Icon(Icons.Outlined.VideoLibrary, contentDescription = null, tint = Flexarr.silver, modifier = Modifier.size(44.dp))
        Text("Nothing here for $name yet", style = marquee(34))
    }
}

/** The row has something to show: a row with nothing in it (a kind this app doesn't show yet, say) is left out. */
private val HomeRow.hasContent: Boolean
    get() = items.isNotEmpty() || !playlists.isNullOrEmpty() || !channels.isNullOrEmpty()
