package io.github.flexarr.ui.tv

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ViewList
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Replay
import androidx.compose.material.icons.filled.VisibilityOff
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.ExperimentalComposeUiApi
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import io.github.flexarr.appModel
import io.github.flexarr.core.displayTitle
import io.github.flexarr.ui.components.MenuAction
import io.github.flexarr.ui.components.LocalClient
import io.github.flexarr.player.TvSubtitleSearch
import androidx.compose.ui.window.DialogProperties
import androidx.compose.ui.window.Dialog
import androidx.compose.material.icons.filled.MoreHoriz
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.focus.focusRestorer
import androidx.compose.ui.focus.onFocusChanged
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.tv.material3.Text
import io.github.flexarr.AppModel
import io.github.flexarr.core.CreditRole
import io.github.flexarr.core.Format
import io.github.flexarr.core.ItemDetail
import io.github.flexarr.core.ItemSummary
import io.github.flexarr.core.MediaFile
import io.github.flexarr.core.MediaKind
import io.github.flexarr.core.episodeCode
import io.github.flexarr.core.episodeSubtitle
import io.github.flexarr.core.facts
import io.github.flexarr.core.isResumable
import io.github.flexarr.ui.components.LocalNavigator
import io.github.flexarr.ui.components.RefreshWhenStale
import io.github.flexarr.ui.components.RemoteImage
import io.github.flexarr.ui.components.TitleArt
import io.github.flexarr.ui.detail.channelsLabel
import io.github.flexarr.ui.detail.fileSizeLabel
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.glowColor
import io.github.flexarr.ui.theme.marquee
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.async
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.launch
import java.util.Locale

/**
 * A film, show or episode (a season opens its show with that season picked), as the Apple TV's DetailView: the backdrop
 * stays put, washed in the title's colour, while the page scrolls over it.
 */
@Composable
fun TvDetailScreen(model: AppModel, id: String) {
    val client = model.client ?: return
    var detail by remember(id) { mutableStateOf<ItemDetail?>(null) }
    var error by remember(id) { mutableStateOf<String?>(null) }
    var nextUp by remember(id) { mutableStateOf<ItemSummary?>(null) }
    var similar by remember(id) { mutableStateOf(emptyList<ItemSummary>()) }
    var selectedSeasonId by remember(id) { mutableStateOf<String?>(null) }
    val seasons = remember(id) { mutableStateMapOf<String, ItemDetail>() }
    var attempt by remember { mutableIntStateOf(0) }
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
        }
    }
    LaunchedEffect(selectedSeasonId) {
        val season = selectedSeasonId ?: return@LaunchedEffect
        if (season !in seasons) runCatching { client.item(season) }.onSuccess { seasons[season] = it }
    }

    val loaded = detail
    val item = loaded?.item
    Box(Modifier.fillMaxSize().background(Flexarr.room)) {
        when {
            loaded != null && item != null -> {
                val series = if (item.kind == MediaKind.Series) item else loaded.ancestors.firstOrNull { it.kind == MediaKind.Series }
                val ambient = glowColor(item.accentColor)
                val scroll = rememberScrollState()
                val scope = rememberCoroutineScope()
                Backdrop(item.artwork.backdrop ?: series?.artwork?.backdrop, ambient, dim = { (scroll.value / 350f).coerceIn(0f, 0.85f) })
                Column(Modifier.fillMaxSize().verticalScroll(scroll).padding(bottom = 45.dp), verticalArrangement = Arrangement.spacedBy(28.dp)) {
                    Box(Modifier.fillMaxWidth().heightIn(min = 430.dp), contentAlignment = Alignment.BottomStart) {
                        // With focus on the buttons, the page sits at the top, the whole header in view.
                        Header(item, series, loaded, ambient, nextUp, seasons[selectedSeasonId], onActionsFocused = { scope.launch { scroll.animateScrollTo(0) } })
                    }
                    Sections(item, loaded, nextUp, seasons, selectedSeasonId, onSeason = { selectedSeasonId = it }, similar = similar, ambient = ambient)
                }
            }
            error != null -> TvStatusView(error, retry = { attempt++ })
            else -> TvStatusView()
        }
    }
}

/** The backdrop, washed in the title's colour and dimmed further as the page scrolls so the rows stay legible. */
@Composable
private fun Backdrop(path: String?, ambient: Color, dim: () -> Float) {
    Box(Modifier.fillMaxSize()) {
        RemoteImage(path, tvBackdropWidth(), Modifier.fillMaxSize()) {}
        Box(Modifier.fillMaxSize().background(Brush.linearGradient(listOf(ambient.copy(alpha = 0.3f), ambient.copy(alpha = 0.05f)), start = Offset(0f, Float.POSITIVE_INFINITY), end = Offset(Float.POSITIVE_INFINITY, 0f))))
        Box(Modifier.fillMaxSize().background(Brush.horizontalGradient(0f to Flexarr.room.copy(alpha = 0.95f), 0.45f to Flexarr.room.copy(alpha = 0.6f), 0.8f to Color.Transparent)))
        Box(Modifier.fillMaxSize().background(Brush.verticalGradient(0.35f to Color.Transparent, 0.9f to Flexarr.room.copy(alpha = 0.9f), 1f to Flexarr.room)))
        Box(Modifier.fillMaxSize().background(Brush.radialGradient(listOf(ambient.copy(alpha = 0.35f), Color.Transparent), center = Offset(0f, Float.POSITIVE_INFINITY), radius = 1100f)))
        Box(Modifier.fillMaxSize().graphicsLayer { alpha = dim() }.background(Flexarr.room))
    }
}

@Composable
private fun Header(item: ItemSummary, series: ItemSummary?, detail: ItemDetail, ambient: Color, nextUp: ItemSummary?, season: ItemDetail?, onActionsFocused: () -> Unit) {
    Column(Modifier.padding(start = TvLayout.Edge, end = 150.dp, bottom = 5.dp), verticalArrangement = Arrangement.spacedBy(11.dp)) {
        if (item.kind == MediaKind.Episode && series != null) {
            TitleArt(series, maxWidth = 280.dp, maxHeight = 85.dp, fontSize = 40)
            Text(item.episodeSubtitle ?: item.title, style = marquee(27).copy(fontWeight = FontWeight.Bold))
        } else {
            TitleArt(item, maxWidth = 410.dp, maxHeight = 130.dp, fontSize = 60)
        }
        Row(horizontalArrangement = Arrangement.spacedBy(11.dp), verticalAlignment = Alignment.CenterVertically) {
            TvFactsLine(item.facts())
            if (detail.genres.isNotEmpty()) Text(detail.genres.take(3).joinToString(", "), fontSize = 13.sp, fontWeight = FontWeight.Medium, color = Flexarr.usher)
        }
        detail.overview?.takeIf { it.isNotEmpty() }?.let {
            Text(it, fontSize = 13.5.sp, lineHeight = 19.sp, color = Flexarr.screen.copy(alpha = 0.88f), maxLines = 4, overflow = TextOverflow.Ellipsis, modifier = Modifier.widthIn(max = 520.dp))
        }
        Actions(item, series, detail, nextUp, season, ambient, Modifier.padding(top = 7.dp).onFocusChanged { if (it.hasFocus) onActionsFocused() })
    }
}

@Composable
private fun Actions(item: ItemSummary, series: ItemSummary?, detail: ItemDetail, nextUp: ItemSummary?, season: ItemDetail?, ambient: Color, modifier: Modifier) {
    val navigator = LocalNavigator.current
    val client = LocalClient.current
    val scope = rememberCoroutineScope()
    var more by remember { mutableStateOf(false) }
    var searchingSubtitlesFor by remember { mutableStateOf<String?>(null) }
    // Bazarr can look for subtitles for the file (the first, as on the Apple TV), but not for a kids profile.
    val kids = LocalContext.current.appModel.isKids
    val subtitleFile = detail.files.firstOrNull()?.takeIf { detail.canSearchSubtitles == true && !kids }
    // What Play starts: the film or episode itself, or for a show the episode it's up to (else the season's first).
    val target = when (item.kind) {
        MediaKind.Movie, MediaKind.Episode -> item
        MediaKind.Series -> nextUp ?: season?.children?.firstOrNull()
        else -> null
    }
    val watched = item.userData.watched || item.userData.unwatchedCount == 0
    val play = remember { FocusRequester() }
    // Play takes focus as the page opens, so OK plays at once; with nothing to play, the first button does.
    LaunchedEffect(target != null) { runCatching { play.requestFocus() } }
    Row(modifier, horizontalArrangement = Arrangement.spacedBy(13.dp)) {
        if (target != null) {
            TvActionButton(playLabel(target, item), onClick = { navigator.play(target) }, icon = Icons.Filled.PlayArrow, prominent = true, ambient = ambient, modifier = Modifier.focusRequester(play))
            if (target.isResumable) TvActionButton("Start over", onClick = { navigator.play(target, fromBeginning = true) }, icon = Icons.Filled.Replay, ambient = ambient)
        }
        TvActionButton(
            if (watched) "Mark as unwatched" else "Mark as watched",
            onClick = { navigator.setWatched(item, !watched) },
            icon = if (watched) Icons.Filled.VisibilityOff else Icons.Filled.Check,
            ambient = ambient,
            modifier = if (target == null) Modifier.focusRequester(play) else Modifier,
        )
        // Part way through: forget the place without marking it watched (it leaves Continue Watching).
        if (item.isResumable && !watched) TvActionButton("Mark as unwatched", onClick = { navigator.setWatched(item, false) }, icon = Icons.Filled.VisibilityOff, ambient = ambient)
        if (item.kind == MediaKind.Episode && series != null) {
            TvActionButton("Go to show", onClick = { navigator.openItem(series.id) }, icon = Icons.AutoMirrored.Filled.ViewList, ambient = ambient)
        }
        // More, as on the Apple TV: what doesn't need a button of its own.
        if (subtitleFile != null) TvActionButton(null, onClick = { more = true }, icon = Icons.Filled.MoreHoriz, ambient = ambient, contentDescription = "More")
    }
    if (more && subtitleFile != null) {
        TvItemMenu(item.displayTitle, listOf(MenuAction("Search for subtitles") { searchingSubtitlesFor = subtitleFile.id }), onDismiss = { more = false })
    }
    searchingSubtitlesFor?.let { fileId ->
        // Over the page, as in the player: a panel down the right.
        Dialog(onDismissRequest = { searchingSubtitlesFor = null }, properties = DialogProperties(usePlatformDefaultWidth = false)) {
            Box(Modifier.fillMaxSize()) {
                TvSubtitleSearch(client, item.id, fileId, onDownloaded = { saved ->
                    searchingSubtitlesFor = null
                    scope.launch { navigator.notices.showSnackbar("${saved.label} subtitles saved") }
                }, onClose = { searchingSubtitlesFor = null }, modifier = Modifier.align(Alignment.CenterEnd))
            }
        }
    }
}

private fun playLabel(target: ItemSummary, container: ItemSummary): String {
    val resume = target.isResumable
    val code = target.episodeCode
    if (container.kind == MediaKind.Series && code != null) return if (resume) "Resume $code" else "Play $code"
    return if (resume) "Resume from ${Format.timestamp(target.userData.positionSeconds)}" else "Play"
}

@Composable
private fun Sections(
    item: ItemSummary,
    detail: ItemDetail,
    nextUp: ItemSummary?,
    seasons: Map<String, ItemDetail>,
    selectedSeasonId: String?,
    onSeason: (String) -> Unit,
    similar: List<ItemSummary>,
    ambient: Color,
) {
    val navigator = LocalNavigator.current
    val season = selectedSeasonId?.let { seasons[it] }
    if (item.kind == MediaKind.Series && detail.children.isNotEmpty()) {
        Column(verticalArrangement = Arrangement.spacedBy(5.dp)) {
            // Like the TV app: moving across the seasons switches the episode list.
            // Coming down from Play, focus lands on the season showing, not the nearest pill (which would switch to it).
            val selectedPill = remember { FocusRequester() }
            LazyRow(Modifier.focusRestorer(selectedPill), contentPadding = PaddingValues(horizontal = TvLayout.Edge, vertical = 10.dp), horizontalArrangement = Arrangement.spacedBy(9.dp)) {
                items(detail.children, key = { it.id }) { child ->
                    val chosen = child.id == selectedSeasonId
                    TvPill(child.title, selected = chosen, onClick = { onSeason(child.id) }, ambient = ambient, modifier = Modifier.onFocusChanged { if (it.isFocused) onSeason(child.id) }.then(if (chosen) Modifier.focusRequester(selectedPill) else Modifier))
                }
            }
            if (season != null) Episodes(item, season, nextUp) else Box(Modifier.heightIn(min = 210.dp))
        }
    } else if (item.kind == MediaKind.Episode && season != null && season.children.size > 1) {
        Column {
            TvRowHeading(season.item.title)
            Episodes(item, season, nextUp)
        }
    }

    val rank = mapOf(CreditRole.Actor to 0, CreditRole.Creator to 1, CreditRole.Director to 2, CreditRole.Writer to 3)
    val cast = detail.credits.sortedBy { rank[it.role] ?: 4 }
    if (cast.isNotEmpty()) {
        Column {
            TvRowHeading("Cast and crew")
            LazyRow(contentPadding = PaddingValues(horizontal = TvLayout.Edge, vertical = 15.dp), horizontalArrangement = Arrangement.spacedBy(18.dp)) {
                items(cast.take(30)) { credit -> TvPersonCard(credit.name, credit.character ?: credit.role.raw, credit.image, onClick = { navigator.openPerson(credit.personId, credit.name) }) }
            }
        }
    }
    if (similar.isNotEmpty()) {
        Column {
            TvRowHeading("More like this")
            LazyRow(contentPadding = PaddingValues(start = TvLayout.Edge, end = TvLayout.Edge, top = 14.dp, bottom = 20.dp), horizontalArrangement = Arrangement.spacedBy(TvLayout.RowSpacing)) {
                items(similar.filter { it.showsOnTv }, key = { it.id }) { other -> TvPosterCard(other, onClick = { navigator.open(other) }) }
            }
        }
    }
    detail.files.firstOrNull()?.let { FileInfo(it, detail.studio) }
}

/** A season's episodes in a row; coming into it lands on the one being viewed, else the one up next, else the first unwatched. */
@OptIn(ExperimentalComposeUiApi::class)
@Composable
private fun Episodes(item: ItemSummary, season: ItemDetail, nextUp: ItemSummary?) {
    val navigator = LocalNavigator.current
    val episodes = season.children
    val anchor = listOfNotNull(if (item.kind == MediaKind.Episode) item.id else null, nextUp?.id, episodes.firstOrNull { !it.userData.watched }?.id)
        .firstOrNull { id -> episodes.any { it.id == id } }
    val anchorIndex = episodes.indexOfFirst { it.id == anchor }.coerceAtLeast(0)
    val list = rememberLazyListState()
    val landing = remember { FocusRequester() }
    LaunchedEffect(season.item.id, anchorIndex) { list.scrollToItem(anchorIndex) }
    LazyRow(
        state = list,
        modifier = Modifier.focusRestorer(landing),
        contentPadding = PaddingValues(start = TvLayout.Edge, end = TvLayout.Edge, top = 12.dp, bottom = 20.dp),
        horizontalArrangement = Arrangement.spacedBy(20.dp),
    ) {
        items(episodes, key = { it.id }) { episode ->
            TvEpisodeCard(
                episode,
                overview = season.childOverviews[episode.id],
                isCurrent = episode.id == item.id,
                onClick = {
                    // On a show (or the episode's own page), the episode plays; another episode opens its page.
                    if (episode.id == item.id || item.kind == MediaKind.Series) navigator.play(episode) else navigator.openItem(episode.id)
                },
                modifier = if (episode.id == anchor) Modifier.focusRequester(landing) else Modifier,
            )
        }
    }
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
    Row(Modifier.padding(horizontal = TvLayout.Edge), horizontalArrangement = Arrangement.spacedBy(25.dp)) {
        facts.forEach { (label, value) ->
            Column(verticalArrangement = Arrangement.spacedBy(2.dp)) {
                Text(label, fontSize = 11.5.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.usher)
                Text(value, fontSize = 12.5.sp, color = Flexarr.screen.copy(alpha = 0.85f))
            }
        }
    }
}
