package io.github.flexarr.ui.detail

import androidx.compose.animation.animateContentSize
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.Cast
import androidx.compose.material.icons.filled.ClosedCaption
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Replay
import androidx.compose.material.icons.filled.Shuffle
import androidx.compose.material.icons.filled.VideoLibrary
import androidx.compose.material.icons.filled.VisibilityOff
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.Text
import androidx.compose.material3.pulltorefresh.PullToRefreshBox
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
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
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.github.flexarr.AppModel
import io.github.flexarr.core.CreditRole
import io.github.flexarr.core.Format
import io.github.flexarr.core.ItemDetail
import io.github.flexarr.core.ItemSummary
import io.github.flexarr.core.MediaFile
import io.github.flexarr.core.MediaKind
import io.github.flexarr.core.effectiveDuration
import io.github.flexarr.core.episodeCode
import io.github.flexarr.core.episodeSubtitle
import io.github.flexarr.core.facts
import io.github.flexarr.core.isResumable
import io.github.flexarr.core.progress
import io.github.flexarr.ui.components.ActionButton
import io.github.flexarr.ui.components.Edge
import io.github.flexarr.ui.components.FactsLine
import io.github.flexarr.ui.components.ItemMenu
import io.github.flexarr.ui.components.LabeledAction
import io.github.flexarr.ui.components.LocalClient
import io.github.flexarr.ui.components.LocalNavigator
import io.github.flexarr.ui.subtitles.SubtitleSearchSheet
import io.github.flexarr.ui.playon.PlayOnSheet
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.TextButton
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import io.github.flexarr.ui.components.PersonCard
import io.github.flexarr.ui.components.PillButton
import io.github.flexarr.ui.components.PosterCard
import io.github.flexarr.ui.components.PosterWidth
import io.github.flexarr.ui.components.ProgressBar
import io.github.flexarr.ui.components.RefreshWhenStale
import io.github.flexarr.ui.components.RemoteImage
import io.github.flexarr.ui.components.RowSpacing
import io.github.flexarr.ui.components.SectionHeading
import io.github.flexarr.ui.components.StatusView
import io.github.flexarr.ui.components.TitleArt
import io.github.flexarr.ui.components.WatchedBadge
import io.github.flexarr.ui.components.pressable
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.RoomBackground
import io.github.flexarr.ui.theme.glowColor
import io.github.flexarr.ui.theme.marquee
import java.util.Locale

/**
 * Detail for a film, show or episode (a season opens its show with that season picked). The backdrop sits at the top,
 * washed in the title's ambient colour, with the actions and episodes beneath it.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun DetailScreen(model: AppModel, id: String) {
    val client = model.client ?: return
    val navigator = LocalNavigator.current
    var detail by remember(id) { mutableStateOf<ItemDetail?>(null) }
    var error by remember(id) { mutableStateOf<String?>(null) }
    var nextUp by remember(id) { mutableStateOf<ItemSummary?>(null) }
    var similar by remember(id) { mutableStateOf(emptyList<ItemSummary>()) }
    var selectedSeasonId by rememberSaveable(id) { mutableStateOf<String?>(null) }
    val seasons = remember(id) { mutableStateMapOf<String, ItemDetail>() }
    var attempt by remember { mutableStateOf(0) }
    var refreshing by remember { mutableStateOf(false) }
    var loadedAt by remember(id) { mutableStateOf<Long?>(null) }
    RefreshWhenStale(loadedAt) { attempt++ }

    LaunchedEffect(id, model.watchStateVersion, attempt) {
        try {
            var loaded = client.item(id)
            // A season opens as its show, with that season selected.
            val parent = loaded.item.parentId
            if (loaded.item.kind == MediaKind.Season && parent != null) {
                seasons[loaded.item.id] = loaded
                if (selectedSeasonId == null) selectedSeasonId = loaded.item.id
                loaded = client.item(parent)
            }
            detail = loaded
            error = null
            loadedAt = System.currentTimeMillis()
            val item = loaded.item
            when (item.kind) {
                MediaKind.Series -> {
                    nextUp = runCatching { client.nextUp(item.id) }.getOrNull()
                    if (selectedSeasonId == null) selectedSeasonId = nextUp?.parentId ?: loaded.children.firstOrNull()?.id
                }
                MediaKind.Episode -> item.parentId?.let { selectedSeasonId = it }
                else -> Unit
            }
            selectedSeasonId?.let { season -> runCatching { client.item(season) }.onSuccess { seasons[season] = it } }
            if (similar.isEmpty() && item.kind != MediaKind.Episode) similar = runCatching { client.similar(item.id) }.getOrDefault(emptyList())
        } catch (e: Exception) {
            if (detail == null) error = e.message
        } finally {
            refreshing = false
        }
    }
    LaunchedEffect(selectedSeasonId) {
        val season = selectedSeasonId ?: return@LaunchedEffect
        if (season !in seasons) runCatching { client.item(season) }.onSuccess { seasons[season] = it }
    }

    val loaded = detail
    val item = loaded?.item
    val series = item?.let { if (it.kind == MediaKind.Series) it else loaded.ancestors.firstOrNull { a -> a.kind == MediaKind.Series } }
    val ambient = item?.let { glowColor(it.accentColor) } ?: Flexarr.silver

    RoomBackground(ambient = ambient, intensity = 0.22f) {
        when {
            loaded != null && item != null -> PullToRefreshBox(
                isRefreshing = refreshing,
                onRefresh = {
                    refreshing = true
                    attempt++
                },
            ) {
                LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 40.dp), verticalArrangement = Arrangement.spacedBy(28.dp)) {
                    item(key = "header") { Header(item, series, loaded, ambient, nextUp, seasons[selectedSeasonId]) }
                    sections(item, loaded, seasons[selectedSeasonId], selectedSeasonId, { selectedSeasonId = it }, similar, ambient)
                }
            }
            error != null -> StatusView(error, retry = { attempt++ })
            else -> StatusView()
        }
        IconButton(
            onClick = { navigator.back() },
            modifier = Modifier.statusBarsPadding().padding(8.dp).clip(CircleShape).background(Flexarr.room.copy(alpha = 0.55f)),
        ) { Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = "Back", tint = Flexarr.screen) }
    }
}

@Composable
private fun Header(item: ItemSummary, series: ItemSummary?, detail: ItemDetail, ambient: Color, nextUp: ItemSummary?, season: ItemDetail?) {
    var overviewExpanded by remember { mutableStateOf(false) }
    Column(verticalArrangement = Arrangement.spacedBy(14.dp)) {
        Box(Modifier.fillMaxWidth().height(420.dp)) {
            RemoteImage(item.artwork.backdrop ?: series?.artwork?.backdrop, 1000, Modifier.fillMaxSize().fadeOut()) {
                Box(Modifier.fillMaxSize().background(Flexarr.room))
            }
            Box(
                Modifier.fillMaxSize().fadeOut()
                    .background(Brush.linearGradient(listOf(ambient.copy(alpha = 0.1f), ambient.copy(alpha = 0.35f))))
                    .background(Brush.verticalGradient(0.3f to Color.Transparent, 1f to Flexarr.room.copy(alpha = 0.6f))),
            )
            Column(Modifier.align(Alignment.BottomStart).padding(horizontal = Edge), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                if (item.kind == MediaKind.Episode && series != null) {
                    TitleArt(series, maxWidth = 240.dp, maxHeight = 80.dp, fontSize = 40)
                    Text(item.episodeSubtitle ?: item.title, style = marquee(28).copy(fontWeight = FontWeight.Bold))
                } else {
                    TitleArt(item, maxWidth = 300.dp, maxHeight = 120.dp, fontSize = 52)
                }
                FactsLine(item.facts)
                if (detail.genres.isNotEmpty()) {
                    Text(detail.genres.take(3).joinToString(", "), fontSize = 15.sp, fontWeight = FontWeight.Medium, color = Flexarr.usher)
                }
            }
        }
        Actions(item, series, nextUp, season, ambient, detail)
        detail.overview?.takeIf { it.isNotEmpty() }?.let { overview ->
            Text(
                overview,
                fontSize = 15.sp,
                lineHeight = 21.sp,
                color = Flexarr.screen.copy(alpha = 0.88f),
                maxLines = if (overviewExpanded) Int.MAX_VALUE else 4,
                overflow = TextOverflow.Ellipsis,
                modifier = Modifier.fillMaxWidth().pressable({ overviewExpanded = !overviewExpanded }).padding(horizontal = Edge).animateContentSize(),
            )
        }
    }
}

/** Play on a line of its own, so "Resume from 1:02:35" never wraps; the rest underneath, named. */
@Composable
private fun Actions(item: ItemSummary, series: ItemSummary?, nextUp: ItemSummary?, season: ItemDetail?, ambient: Color, detail: ItemDetail) {
    val navigator = LocalNavigator.current
    // Subtitles from the server's Bazarr, for the first file (the copy playback picks); saying so once they're saved.
    var searchingSubtitlesFor by remember(item.id) { mutableStateOf<String?>(null) }
    var savedSubtitles by remember { mutableStateOf<String?>(null) }
    var choosingScreen by remember(item.id) { mutableStateOf(false) }
    // What Play starts: the item itself, or for a show the next-up episode (falling back to the selected season's first).
    val playable = when (item.kind) {
        MediaKind.Movie, MediaKind.Episode -> item
        MediaKind.Series -> nextUp ?: season?.children?.firstOrNull()
        else -> null
    }
    val watched = item.userData.watched || item.userData.unwatchedCount == 0
    Column(Modifier.padding(horizontal = Edge).widthIn(max = 520.dp), verticalArrangement = Arrangement.spacedBy(18.dp)) {
        if (playable != null) {
            ActionButton(playLabel(playable, item), onClick = { navigator.play(playable) }, icon = Icons.Filled.PlayArrow, prominent = true, ambient = ambient, modifier = Modifier.fillMaxWidth())
        }
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            val slot = Modifier.weight(1f)
            if (playable != null && playable.isResumable) {
                LabeledAction("Start over", Icons.Filled.Replay, { navigator.play(playable, fromBeginning = true) }, slot)
            }
            if (item.kind == MediaKind.Series && item.childCount > 0) {
                LabeledAction("Shuffle", Icons.Filled.Shuffle, { navigator.shuffle(item) }, slot)
            }
            if (item.isResumable && !watched) {
                // Part way through, either way: unwatched forgets the place (it leaves Continue Watching).
                var menu by remember { mutableStateOf(false) }
                Box(slot) {
                    LabeledAction("Watched", Icons.Filled.Check, { menu = true })
                    DropdownMenu(menu, onDismissRequest = { menu = false }) {
                        DropdownMenuItem(text = { Text("Mark as watched") }, onClick = { menu = false; navigator.setWatched(item, true) })
                        DropdownMenuItem(text = { Text("Mark as unwatched") }, onClick = { menu = false; navigator.setWatched(item, false) })
                    }
                }
            } else {
                LabeledAction(if (watched) "Unwatched" else "Watched", if (watched) Icons.Filled.VisibilityOff else Icons.Filled.Check, { navigator.setWatched(item, !watched) }, slot)
            }
            if (item.kind == MediaKind.Episode && series != null) {
                LabeledAction("Show", Icons.Filled.VideoLibrary, { navigator.openItem(series.id) }, slot)
            }
            // Another screen: a film or episode as it is, a show the episode that screen's profile is up to.
            if (item.kind == MediaKind.Movie || item.kind == MediaKind.Episode || item.kind == MediaKind.Series) {
                LabeledAction("Play on", Icons.Filled.Cast, { choosingScreen = true }, slot)
            }
            val file = detail.files.firstOrNull()
            if (detail.canSearchSubtitles == true && file != null) {
                LabeledAction("Subtitles", Icons.Filled.ClosedCaption, { searchingSubtitlesFor = file.id }, slot.semantics { contentDescription = "Search for subtitles" })
            }
        }
    }
    if (choosingScreen) PlayOnSheet(LocalClient.current, item, onClose = { choosingScreen = false })
    searchingSubtitlesFor?.let { fileId ->
        SubtitleSearchSheet(
            LocalClient.current,
            item.id,
            fileId,
            onDownloaded = { saved ->
                searchingSubtitlesFor = null
                savedSubtitles = "${saved.label} subtitles saved"
            },
            onClose = { searchingSubtitlesFor = null },
        )
    }
    savedSubtitles?.let { message ->
        AlertDialog(
            onDismissRequest = { savedSubtitles = null },
            confirmButton = { TextButton(onClick = { savedSubtitles = null }) { Text("OK") } },
            text = { Text(message) },
            containerColor = Flexarr.seat,
        )
    }
}

private fun playLabel(target: ItemSummary, container: ItemSummary): String {
    val resume = target.isResumable
    val code = target.episodeCode
    if (container.kind == MediaKind.Series && code != null) return if (resume) "Resume $code" else "Play $code"
    return if (resume) "Resume from ${Format.timestamp(target.userData.positionSeconds)}" else "Play"
}

private fun androidx.compose.foundation.lazy.LazyListScope.sections(
    item: ItemSummary,
    detail: ItemDetail,
    season: ItemDetail?,
    selectedSeasonId: String?,
    onSeason: (String) -> Unit,
    similar: List<ItemSummary>,
    ambient: Color,
) {
    if (item.kind == MediaKind.Series && detail.children.isNotEmpty()) {
        item(key = "seasons") { SeasonPicker(detail.children, selectedSeasonId, onSeason, ambient) }
        if (season == null) {
            item(key = "season-loading") { Box(Modifier.fillMaxWidth().height(200.dp), contentAlignment = Alignment.Center) { CircularProgressIndicator(color = Flexarr.screen) } }
        } else {
            items(season.children, key = { "episode-${it.id}" }) { episode ->
                EpisodeRow(episode, season.childOverviews[episode.id], isCurrent = false, playsOnTap = true)
            }
        }
    } else if (item.kind == MediaKind.Episode && season != null && season.children.size > 1) {
        item(key = "season-heading") { SectionHeading(season.item.title) }
        items(season.children, key = { "episode-${it.id}" }) { episode ->
            EpisodeRow(episode, season.childOverviews[episode.id], isCurrent = episode.id == item.id, playsOnTap = episode.id == item.id)
        }
    } else if (item.kind != MediaKind.Movie && item.kind != MediaKind.Episode && detail.children.isNotEmpty()) {
        // Anything else that holds titles (an album, say, until music has its own screens) lists them.
        item(key = "children") {
            Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                SectionHeading("In this")
                LazyRow(contentPadding = PaddingValues(horizontal = Edge), horizontalArrangement = Arrangement.spacedBy(RowSpacing)) {
                    items(detail.children, key = { it.id }) { PosterCard(it) }
                }
            }
        }
    }

    val rank = mapOf(CreditRole.Actor to 0, CreditRole.Creator to 1, CreditRole.Director to 2, CreditRole.Writer to 3)
    val cast = detail.credits.sortedBy { rank[it.role] ?: 4 }
    if (cast.isNotEmpty()) {
        item(key = "cast") {
            Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                SectionHeading("Cast and crew")
                LazyRow(contentPadding = PaddingValues(horizontal = Edge), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    items(cast.take(30)) { credit ->
                        val navigator = LocalNavigator.current
                        PersonCard(credit.name, credit.character ?: credit.role.raw, credit.image) { navigator.openPerson(credit.personId, credit.name) }
                    }
                }
            }
        }
    }
    if (similar.isNotEmpty()) {
        item(key = "similar") {
            Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                SectionHeading("More like this")
                LazyRow(contentPadding = PaddingValues(horizontal = Edge), horizontalArrangement = Arrangement.spacedBy(RowSpacing)) {
                    items(similar, key = { it.id }) { PosterCard(it, width = PosterWidth) }
                }
            }
        }
    }
    detail.files.firstOrNull()?.let { file -> item(key = "file") { FileInfo(file, detail.studio) } }
}

@Composable
private fun SeasonPicker(seasons: List<ItemSummary>, selected: String?, onSeason: (String) -> Unit, ambient: Color) {
    if (seasons.size == 1) {
        SectionHeading(seasons.first().title)
        return
    }
    val state = rememberLazyListState()
    // The chosen season in view (once it's known: the show's next-up episode decides it).
    LaunchedEffect(selected) {
        val index = seasons.indexOfFirst { it.id == selected }
        if (index >= 0 && state.layoutInfo.visibleItemsInfo.none { it.index == index && it.offset >= 0 }) state.animateScrollToItem(index)
    }
    LazyRow(state = state, contentPadding = PaddingValues(horizontal = Edge), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        items(seasons, key = { it.id }) { season -> PillButton(season.title, season.id == selected, { onSeason(season.id) }, ambient) }
    }
}

/** An episode in a season list: still with progress beside the number, title and time, then the overview. */
@Composable
private fun EpisodeRow(episode: ItemSummary, overview: String?, isCurrent: Boolean, playsOnTap: Boolean) {
    val navigator = LocalNavigator.current
    var menu by remember { mutableStateOf(false) }
    Box(Modifier.padding(horizontal = Edge)) {
        Column(
            Modifier.fillMaxWidth().pressable(
                onClick = { if (playsOnTap) navigator.play(episode) else navigator.openItem(episode.id) },
                onLongClick = { menu = true },
            ),
            verticalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            Row(horizontalArrangement = Arrangement.spacedBy(12.dp), verticalAlignment = Alignment.CenterVertically) {
                Box(Modifier.width(140.dp).aspectRatio(16f / 9f).clip(RoundedCornerShape(6.dp)).background(Flexarr.seat)) {
                    RemoteImage(episode.artwork.thumb ?: episode.artwork.backdrop, 400, Modifier.fillMaxSize()) {
                        Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) { Icon(Icons.Filled.PlayArrow, contentDescription = null, tint = Flexarr.usher) }
                    }
                    episode.progress?.let { ProgressBar(it, Modifier.align(Alignment.BottomCenter).padding(6.dp)) }
                    if (episode.userData.watched) WatchedBadge(Modifier.align(Alignment.TopEnd).padding(5.dp))
                }
                Column(verticalArrangement = Arrangement.spacedBy(3.dp)) {
                    Text(
                        episode.episodeNumber?.let { "Episode $it" } ?: "Special",
                        fontSize = 12.sp,
                        fontWeight = FontWeight.SemiBold,
                        color = if (isCurrent) Flexarr.screen else Flexarr.usher,
                    )
                    Text(episode.title, fontSize = 15.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen, maxLines = 2, overflow = TextOverflow.Ellipsis)
                    episodeTime(episode)?.let { Text(it, fontSize = 12.sp, color = Flexarr.usher) }
                }
                Spacer(Modifier.weight(1f))
            }
            overview?.takeIf { it.isNotEmpty() }?.let { Text(it, fontSize = 13.sp, color = Flexarr.usher, maxLines = 3, overflow = TextOverflow.Ellipsis) }
        }
        ItemMenu(episode, menu, onDismiss = { menu = false }, showsDetails = true)
    }
}

private fun episodeTime(episode: ItemSummary): String? {
    val duration = episode.effectiveDuration
    if (episode.progress != null && duration != null) return Format.remaining(duration - episode.userData.positionSeconds)
    return episode.runtimeMinutes?.let(Format::runtime) ?: episode.durationSeconds?.let(Format::runtime)
}

@Composable
private fun FileInfo(file: MediaFile, studio: String?) {
    val video = listOfNotNull(file.height?.let { "${it}p" }, file.videoCodec?.uppercase(Locale.ROOT), file.pictureLabel).joinToString(" ")
    val audio = listOfNotNull(file.audioCodec?.uppercase(Locale.ROOT), file.audioChannels?.let(::channels)).joinToString(" ")
    val facts = listOfNotNull(
        video.takeIf { it.isNotEmpty() }?.let { "Video" to it },
        audio.takeIf { it.isNotEmpty() }?.let { "Audio" to it },
        studio?.let { "Studio" to it },
        "Size" to size(file.size),
    )
    Column(Modifier.padding(horizontal = Edge), verticalArrangement = Arrangement.spacedBy(14.dp)) {
        facts.chunked(2).forEach { pair ->
            Row(Modifier.fillMaxWidth()) {
                pair.forEach { (label, value) ->
                    Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                        Text(label, fontSize = 12.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.usher)
                        Text(value, fontSize = 15.sp, color = Flexarr.screen.copy(alpha = 0.85f))
                    }
                }
                if (pair.size == 1) Spacer(Modifier.weight(1f))
            }
        }
    }
}

/** "5.1", "Stereo", "Mono". */
private fun channels(count: Int) = when (count) {
    1 -> "Mono"
    2 -> "Stereo"
    6 -> "5.1"
    8 -> "7.1"
    else -> "$count ch"
}

private fun size(bytes: Long): String {
    val gb = bytes / 1_000_000_000.0
    return if (gb >= 1) String.format(Locale.getDefault(), "%.1f GB", gb) else String.format(Locale.getDefault(), "%.0f MB", bytes / 1_000_000.0)
}

/** Drawn into its own layer, then faded to nothing over the bottom of it, so there's no seam with the wash behind. */
private fun Modifier.fadeOut() = graphicsLayer(compositingStrategy = CompositingStrategy.Offscreen).drawWithContent {
    drawContent()
    drawRect(Brush.verticalGradient(0.55f to Color.Black, 1f to Color.Transparent), blendMode = BlendMode.DstIn)
}
