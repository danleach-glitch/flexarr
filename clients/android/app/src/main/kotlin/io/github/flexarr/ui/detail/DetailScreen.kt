package io.github.flexarr.ui.detail

import io.github.flexarr.ui.books.BookScreen

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
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.async
import kotlinx.coroutines.coroutineScope
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
import androidx.compose.material.icons.filled.Bookmark
import androidx.compose.material.icons.filled.BookmarkBorder
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.MoreHoriz
import androidx.compose.material.icons.filled.Theaters
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
import io.github.flexarr.appModel
import io.github.flexarr.core.CreditRole
import io.github.flexarr.core.DetailActions
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
import io.github.flexarr.ui.components.isWide
import io.github.flexarr.ui.components.ActionButton
import io.github.flexarr.ui.downloads.DownloadAction
import io.github.flexarr.ui.components.Edge
import io.github.flexarr.ui.components.FactsLine
import io.github.flexarr.ui.components.ItemMenu
import io.github.flexarr.ui.components.itemActions
import io.github.flexarr.ui.components.menuActions
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
import io.github.flexarr.ui.theme.Tokens
import io.github.flexarr.ui.theme.FlexarrIcons

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
            coroutineScope {
                // More like this comes alongside the episodes, rather than after them.
                val alike = if (similar.isEmpty() && item.kind != MediaKind.Episode) async { runCatching { client.similar(item.id) }.getOrNull() } else null
                when (item.kind) {
                    MediaKind.Series -> {
                        nextUp = runCatching { client.nextUp(item.id) }.getOrNull()
                        if (selectedSeasonId == null) selectedSeasonId = nextUp?.parentId ?: loaded.children.firstOrNull()?.id
                    }
                    MediaKind.Episode -> item.parentId?.let { selectedSeasonId = it }
                    else -> Unit
                }
                selectedSeasonId?.let { season -> runCatching { client.item(season) }.onSuccess { seasons[season] = it } }
                alike?.await()?.let { similar = it }
            }
        } catch (e: CancellationException) {
            throw e
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
    // A book reached by a link or an older route opens as itself, never as a film.
    if (item?.kind == MediaKind.Audiobook) return BookScreen(model, id)
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
                val episodeColumns = if (isWide) 2 else 1
                LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 40.dp), verticalArrangement = Arrangement.spacedBy(28.dp)) {
                    item(key = "header") { Header(item, series, loaded, ambient, nextUp, seasons[selectedSeasonId]) }
                    sections(item, loaded, seasons[selectedSeasonId], selectedSeasonId, { selectedSeasonId = it }, similar, ambient, columns = episodeColumns)
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
        // Taller than usual only when large text needs it.
        Box(Modifier.fillMaxWidth().heightIn(min = if (isWide) 520.dp else 420.dp)) {
            RemoteImage(item.artwork.backdrop ?: series?.artwork?.backdrop, 1000, Modifier.matchParentSize().fadeOut()) {
                Box(Modifier.fillMaxSize().background(Flexarr.room))
            }
            Box(
                Modifier.matchParentSize().fadeOut()
                    .background(Brush.linearGradient(listOf(ambient.copy(alpha = 0.1f), ambient.copy(alpha = 0.35f))))
                    .background(Brush.verticalGradient(0.3f to Color.Transparent, 1f to Flexarr.room.copy(alpha = 0.6f))),
            )
            // Clear of Back above it, however tall large text makes it.
            Column(Modifier.align(Alignment.BottomStart).statusBarsPadding().padding(top = 64.dp).padding(horizontal = Edge), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                if (item.kind == MediaKind.Episode && series != null) {
                    TitleArt(series, maxWidth = 240.dp, maxHeight = 80.dp, fontSize = 40)
                    Text(item.episodeSubtitle ?: item.title, style = marquee(28).copy(fontWeight = FontWeight.Bold))
                } else {
                    TitleArt(item, maxWidth = 300.dp, maxHeight = 120.dp, fontSize = 52)
                }
                // No scores for a child.
                FactsLine(item.facts(scores = !LocalContext.current.appModel.isKids))
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

/**
 * Play on a line of its own, so "Resume from 1:02:35" never wraps; the rest underneath, named, in the order every
 * client shares ([DetailActions]); ⋯ holds the rest.
 */
@Composable
private fun Actions(item: ItemSummary, series: ItemSummary?, nextUp: ItemSummary?, season: ItemDetail?, ambient: Color, detail: ItemDetail) {
    val navigator = LocalNavigator.current
    val model = androidx.compose.ui.platform.LocalContext.current.appModel
    // Subtitles from the server's Bazarr, for the first file (the copy playback picks); saying so once they're saved.
    var searchingSubtitlesFor by remember(item.id) { mutableStateOf<String?>(null) }
    var savedSubtitles by remember { mutableStateOf<String?>(null) }
    var choosingScreen by remember(item.id) { mutableStateOf(false) }
    var showingTrailer by remember(item.id) { mutableStateOf(false) }
    var more by remember(item.id) { mutableStateOf(false) }
    // What Play starts: the item itself, or for a show the next-up episode (falling back to the selected season's first).
    val playable = when (item.kind) {
        MediaKind.Movie, MediaKind.Episode -> item
        MediaKind.Series -> nextUp ?: season?.children?.firstOrNull()
        else -> null
    }
    val watched = item.userData.watched || item.userData.unwatchedCount == 0
    val kind = when (item.kind) {
        MediaKind.Series -> DetailActions.Kind.SHOW
        MediaKind.Episode -> DetailActions.Kind.EPISODE
        else -> DetailActions.Kind.MOVIE
    }
    val file = detail.files.firstOrNull()
    val trailer = detail.trailerId
    val actions = DetailActions.of(
        DetailActions.Input(
            kind = kind,
            // A show's Resume already starts its episode; Start over and Mark as unwatched are for the title itself.
            hasProgress = playable?.isResumable == true && item.kind != MediaKind.Series,
            isWatched = watched,
            hasTrailer = trailer != null,
            isKids = model.isKids,
            platform = DetailActions.Platform.PHONE,
            canDownload = item.kind == MediaKind.Movie || item.kind == MediaKind.Episode,
            canSearchSubtitles = detail.canSearchSubtitles == true && file != null,
        ),
    )
    // Another screen: a film or episode as it is, a show the episode that screen's profile is up to. The phone's own
    // addition to the menu.
    val playsOn = item.kind == MediaKind.Movie || item.kind == MediaKind.Episode || item.kind == MediaKind.Series
    val buttons = if (playsOn && DetailActions.Button.MORE !in actions.buttons) actions.buttons + DetailActions.Button.MORE else actions.buttons
    Column(Modifier.padding(horizontal = Edge).widthIn(max = 520.dp), verticalArrangement = Arrangement.spacedBy(18.dp)) {
        if (playable != null) {
            ActionButton(playLabel(playable, item), onClick = { navigator.play(playable) }, icon = FlexarrIcons.play, prominent = true, ambient = ambient, modifier = Modifier.fillMaxWidth())
        }
        // Sharing the width, until large text or many actions would shrink the names to fit: then each its own size,
        // scrolling sideways.
        val crowded = LocalDensity.current.fontScale > 1.3f || buttons.size > 6
        Row(if (crowded) Modifier.horizontalScroll(rememberScrollState()) else Modifier, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            val slot = if (crowded) Modifier.widthIn(min = 72.dp) else Modifier.weight(1f)
            buttons.forEach { button ->
                when (button) {
                    DetailActions.Button.PLAY -> Unit
                    DetailActions.Button.START_OVER -> playable?.let { target ->
                        LabeledAction("Start over", FlexarrIcons.startOver, { navigator.play(target, fromBeginning = true) }, slot)
                    }
                    DetailActions.Button.SHUFFLE -> if (item.childCount > 0) LabeledAction("Shuffle", FlexarrIcons.shuffle, { navigator.shuffle(item) }, slot)
                    DetailActions.Button.TRAILER -> LabeledAction("Trailer", FlexarrIcons.trailer, { showingTrailer = true }, slot)
                    // Pressed in once it's watched ("Watched", a filled tick); pressing it again unmarks it.
                    DetailActions.Button.WATCHED -> LabeledAction(
                        if (watched) "Watched" else "Mark as watched",
                        if (watched) FlexarrIcons.isWatched else FlexarrIcons.watched,
                        { navigator.setWatched(item, !watched) },
                        slot.semantics { contentDescription = if (watched) "Watched. Mark as unwatched" else "Mark as watched" },
                        maxLines = 2,
                    )
                    // Only what the server can put on a watchlist: films and shows, from servers that have one (0.56).
                    DetailActions.Button.WATCHLIST -> if (item.kind == MediaKind.Movie || item.kind == MediaKind.Series) {
                        detail.onWatchlist?.let { saved -> WatchlistAction(item, saved, detail, slot) }
                    }
                    DetailActions.Button.DOWNLOAD -> DownloadAction(model, item, slot)
                    DetailActions.Button.MORE -> Box(slot) {
                        LabeledAction("More", FlexarrIcons.more, { more = true }, Modifier.fillMaxWidth())
                        DropdownMenu(more, onDismissRequest = { more = false }) {
                            actions.menu.forEach { entry ->
                                when (entry) {
                                    // Part way through: forget the place without marking it watched (it leaves Continue watching).
                                    DetailActions.MenuItem.MARK_UNWATCHED ->
                                        DropdownMenuItem(text = { Text("Mark as unwatched") }, onClick = { more = false; navigator.setWatched(item, false) })
                                    DetailActions.MenuItem.SUBTITLES -> file?.let { found ->
                                        DropdownMenuItem(text = { Text("Search for subtitles") }, onClick = { more = false; searchingSubtitlesFor = found.id })
                                    }
                                    DetailActions.MenuItem.GO_TO_SHOW -> series?.let { show ->
                                        DropdownMenuItem(text = { Text("Go to show") }, onClick = { more = false; navigator.openItem(show.id) })
                                    }
                                }
                            }
                            if (playsOn) DropdownMenuItem(text = { Text("Play on another screen") }, onClick = { more = false; choosingScreen = true })
                        }
                    }
                }
            }
        }
    }
    if (showingTrailer && trailer != null) TrailerSheet(trailer, "${item.title} trailer", onClose = { showingTrailer = false })
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

/** "Watchlist", its bookmark filled once it's there; tapping puts it on or takes it off. */
@Composable
private fun WatchlistAction(item: ItemSummary, saved: Boolean, detail: ItemDetail, modifier: Modifier) {
    val navigator = LocalNavigator.current
    // What was tapped here, until the page loads again and the server says.
    var changed by remember(detail) { mutableStateOf<Boolean?>(null) }
    val on = changed ?: saved
    LabeledAction(
        "Watchlist",
        if (on) FlexarrIcons.onWatchlist else FlexarrIcons.watchlist,
        {
            changed = !on
            navigator.setOnWatchlist(item, !on, onFailed = { changed = on })
        },
        modifier.semantics { contentDescription = if (on) "On watchlist. Remove from watchlist" else "Add to watchlist" },
    )
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
    /** Episodes side by side on a tablet. */
    columns: Int,
) {
    if (item.kind == MediaKind.Series && detail.children.isNotEmpty()) {
        item(key = "seasons") { SeasonPicker(detail.children, selectedSeasonId, onSeason, ambient) }
        if (season == null) {
            item(key = "season-loading") { Box(Modifier.fillMaxWidth().height(200.dp), contentAlignment = Alignment.Center) { CircularProgressIndicator(color = Flexarr.screen) } }
        } else {
            items(season.children.chunked(columns), key = { "episode-${it.first().id}" }) { row ->
                EpisodeColumns(row, columns) { episode -> EpisodeRow(episode, season.childOverviews[episode.id], isCurrent = false, playsOnTap = true) }
            }
        }
    } else if (item.kind == MediaKind.Episode && season != null && season.children.size > 1) {
        item(key = "season-heading") { SectionHeading(season.item.title) }
        items(season.children.chunked(columns), key = { "episode-${it.first().id}" }) { row ->
            EpisodeColumns(row, columns) { episode ->
                EpisodeRow(episode, season.childOverviews[episode.id], isCurrent = episode.id == item.id, playsOnTap = episode.id == item.id)
            }
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
/** A row of [columns] episodes, sharing the width (one on a phone). */
@Composable
private fun EpisodeColumns(episodes: List<ItemSummary>, columns: Int, content: @Composable (ItemSummary) -> Unit) {
    if (columns == 1) return content(episodes.single())
    Row {
        episodes.forEach { Box(Modifier.weight(1f)) { content(it) } }
        repeat(columns - episodes.size) { Box(Modifier.weight(1f)) }
    }
}

@Composable
private fun EpisodeRow(episode: ItemSummary, overview: String?, isCurrent: Boolean, playsOnTap: Boolean) {
    val navigator = LocalNavigator.current
    var menu by remember { mutableStateOf(false) }
    val actions = itemActions(episode, showsDetails = true)
    Box(Modifier.padding(horizontal = Edge)) {
        Column(
            Modifier.fillMaxWidth().pressable(
                onClick = { if (playsOnTap) navigator.play(episode) else navigator.openItem(episode.id) },
                onLongClick = { menu = true },
            ).menuActions(actions),
            verticalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            Row(horizontalArrangement = Arrangement.spacedBy(12.dp), verticalAlignment = Alignment.CenterVertically) {
                Box(Modifier.width(140.dp).aspectRatio(16f / 9f).clip(RoundedCornerShape(Tokens.radiusArt)).background(Flexarr.seat)) {
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
        ItemMenu(actions, menu, onDismiss = { menu = false })
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
    val audio = listOfNotNull(file.audioCodec?.uppercase(Locale.ROOT), file.audioChannels?.let(::channelsLabel)).joinToString(" ")
    val facts = listOfNotNull(
        video.takeIf { it.isNotEmpty() }?.let { "Video" to it },
        audio.takeIf { it.isNotEmpty() }?.let { "Audio" to it },
        studio?.let { "Studio" to it },
        "Size" to fileSizeLabel(file.size),
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
internal fun channelsLabel(count: Int) = when (count) {
    1 -> "Mono"
    2 -> "Stereo"
    6 -> "5.1"
    8 -> "7.1"
    else -> "$count ch"
}

internal fun fileSizeLabel(bytes: Long): String {
    val gb = bytes / 1_000_000_000.0
    return if (gb >= 1) String.format(Locale.getDefault(), "%.1f GB", gb) else String.format(Locale.getDefault(), "%.0f MB", bytes / 1_000_000.0)
}

/** Drawn into its own layer, then faded to nothing over the bottom of it, so there's no seam with the wash behind. */
private fun Modifier.fadeOut() = graphicsLayer(compositingStrategy = CompositingStrategy.Offscreen).drawWithContent {
    drawContent()
    drawRect(Brush.verticalGradient(0.55f to Color.Black, 1f to Color.Transparent), blendMode = BlendMode.DstIn)
}
