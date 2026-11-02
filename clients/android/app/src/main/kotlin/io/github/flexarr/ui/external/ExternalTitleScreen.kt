package io.github.flexarr.ui.external

import androidx.compose.animation.animateContentSize
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
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
import androidx.compose.ui.text.font.FontStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.github.flexarr.AppModel
import io.github.flexarr.core.Availability
import io.github.flexarr.core.ExternalDetail
import io.github.flexarr.core.ExternalTitle
import io.github.flexarr.core.FlexarrException
import io.github.flexarr.core.Format
import io.github.flexarr.core.externalTitle
import io.github.flexarr.core.youTubeId
import io.github.flexarr.ui.components.ActionButton
import io.github.flexarr.ui.components.Edge
import io.github.flexarr.ui.components.FactsLine
import io.github.flexarr.ui.components.LabeledAction
import io.github.flexarr.ui.components.Load
import io.github.flexarr.ui.components.LocalNavigator
import io.github.flexarr.ui.components.PersonCard
import io.github.flexarr.ui.components.PosterWidth
import io.github.flexarr.ui.components.RemoteImage
import io.github.flexarr.ui.components.RowSpacing
import io.github.flexarr.ui.components.SectionHeading
import io.github.flexarr.ui.components.StatusView
import io.github.flexarr.ui.components.pressable
import io.github.flexarr.ui.components.rememberLoader
import io.github.flexarr.ui.detail.DetailScreen
import io.github.flexarr.ui.detail.TrailerSheet
import io.github.flexarr.ui.music.BackButton
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.FlexarrIcons
import io.github.flexarr.ui.theme.RoomBackground
import io.github.flexarr.ui.theme.Tokens
import io.github.flexarr.ui.theme.marquee
import java.util.Locale

/**
 * A film or show that isn't in the library (from search), as the TVs' and the web's Discover page: what it is, how it's
 * rated, who's in it, Request, the watchlist and the trailer. One that's come into the library since opens as itself.
 */
@Composable
fun ExternalTitleScreen(model: AppModel, mediaType: String, tmdbId: Int) {
    val client = model.client ?: return
    val loader = rememberLoader("$mediaType-$tmdbId") { client.externalTitle(mediaType, tmdbId) }
    when (val state = loader.state) {
        is Load.Loaded -> {
            val detail = state.value
            val inLibrary = detail.libraryItemId
            if (inLibrary != null) return DetailScreen(model, inLibrary)
            RoomBackground(ambient = Flexarr.silver, intensity = 0.2f) {
                Page(model, detail)
                BackButton()
            }
        }
        is Load.Failed -> RoomBackground(ambient = Flexarr.silver, intensity = 0.2f) {
            StatusView(state.message, loader.retry)
            BackButton()
        }
        Load.Loading -> RoomBackground(ambient = Flexarr.silver, intensity = 0.2f) {
            StatusView()
            BackButton()
        }
    }
}

@Composable
private fun Page(model: AppModel, detail: ExternalDetail) {
    val navigator = LocalNavigator.current
    var overviewExpanded by remember { mutableStateOf(false) }
    LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 40.dp), verticalArrangement = Arrangement.spacedBy(28.dp)) {
        item(key = "header") {
            Column(verticalArrangement = Arrangement.spacedBy(14.dp)) {
                Box(Modifier.fillMaxWidth().heightIn(min = 420.dp)) {
                    RemoteImage(detail.backdrop ?: detail.poster, 1000, Modifier.matchParentSize().fadeOut()) {
                        Box(Modifier.fillMaxSize().background(Flexarr.room))
                    }
                    Box(Modifier.matchParentSize().fadeOut().background(Brush.verticalGradient(0.3f to Color.Transparent, 1f to Flexarr.room.copy(alpha = 0.6f))))
                    Column(Modifier.align(Alignment.BottomStart).statusBarsPadding().padding(top = 64.dp).padding(horizontal = Edge), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                        Text(detail.title, style = marquee(if (detail.title.length > 24) 36 else 46).copy(fontWeight = FontWeight.Bold), maxLines = 3, overflow = TextOverflow.Ellipsis)
                        detail.tagline?.takeIf { it.isNotBlank() }?.let {
                            Text(it, fontSize = 14.sp, fontStyle = FontStyle.Italic, color = Flexarr.screen.copy(alpha = 0.85f))
                        }
                        FactsLine(facts(detail, scores = !model.isKids), scores = if (model.isKids) null else detail.scores)
                        if (detail.genres.isNotEmpty()) {
                            Text(detail.genres.take(3).joinToString(", "), fontSize = 15.sp, fontWeight = FontWeight.Medium, color = Flexarr.usher)
                        }
                    }
                }
                Actions(model, detail)
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
        if (detail.cast.isNotEmpty()) {
            item(key = "cast") {
                Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                    SectionHeading("Cast")
                    // Not buttons: these people aren't in the library, so there's nowhere for them to go.
                    LazyRow(contentPadding = PaddingValues(horizontal = Edge), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        items(detail.cast.take(20)) { PersonCard(it.name, it.character, it.image) }
                    }
                }
            }
        }
        if (detail.madeBy.isNotEmpty() || detail.studio != null) {
            item(key = "made-by") {
                Row(Modifier.padding(horizontal = Edge), horizontalArrangement = Arrangement.spacedBy(32.dp)) {
                    if (detail.madeBy.isNotEmpty()) Labeled(if (detail.isShow) "Created by" else "Directed by", detail.madeBy.joinToString(", "))
                    detail.studio?.let { Labeled(if (detail.isShow) "Network" else "Studio", it) }
                }
            }
        }
        if (detail.recommendations.isNotEmpty()) {
            item(key = "like") {
                Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                    SectionHeading("If you like this")
                    LazyRow(contentPadding = PaddingValues(horizontal = Edge), horizontalArrangement = Arrangement.spacedBy(RowSpacing), verticalAlignment = Alignment.Top) {
                        items(detail.recommendations, key = { it.key }) { title -> TitleCard(title) { navigator.openExternal(title) } }
                    }
                }
            }
        }
    }
}

/** Request (or where it stands), then the watchlist and the trailer. */
@Composable
private fun Actions(model: AppModel, detail: ExternalDetail) {
    val navigator = LocalNavigator.current
    var onWatchlist by remember(detail.tmdbId) { mutableStateOf<Boolean?>(null) }
    var showingTrailer by remember { mutableStateOf(false) }
    var confirming by remember { mutableStateOf(false) }
    val key = "${detail.mediaType}:${detail.tmdbId}"
    val saved = onWatchlist ?: (key in model.watchlistTitles)
    val trailer = youTubeId(detail.trailerYouTubeId)
    // Whether Seerr is answering, so Request and Requested can wait while it isn't.
    LaunchedEffect(Unit) { runCatching { model.titleRequestStatus() } }
    val requestKey = titleKey(detail.mediaType, detail.tmdbId)
    val state = model.titleState(requestKey, detail.availability)
    val canPress = state.pressable(model)
    Column(Modifier.padding(horizontal = Edge).widthIn(max = 520.dp), verticalArrangement = Arrangement.spacedBy(18.dp)) {
        Column(horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(8.dp)) {
            ActionButton(
                state.label(),
                onClick = {
                    when (state) {
                        TitleRequestState.CanRequest -> navigator.requestTitle(model, detail.mediaType, detail.tmdbId)
                        TitleRequestState.Requested -> confirming = true
                        else -> Unit
                    }
                },
                icon = state.icon(),
                prominent = state == TitleRequestState.CanRequest,
                enabled = canPress,
                modifier = Modifier.fillMaxWidth(),
            )
            TitleRequestNote(model, requestKey, state)
        }
        if (model.hasWatchlist || trailer != null) {
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                if (model.hasWatchlist) {
                    LabeledAction(
                        if (saved) "On watchlist" else "Watchlist",
                        if (saved) FlexarrIcons.onWatchlist else FlexarrIcons.watchlist,
                        {
                            onWatchlist = !saved
                            navigator.watchlistChange(if (saved) "Couldn’t remove this from your watchlist" else "Couldn’t add this to your watchlist") {
                                try {
                                    val added = model.setTitleOnWatchlist(detail.mediaType, detail.tmdbId, !saved)
                                    if (added?.requested == true) model.titleWasAsked(requestKey)
                                } catch (e: Exception) {
                                    onWatchlist = saved
                                    throw e
                                }
                            }
                        },
                        Modifier.weight(1f),
                    )
                }
                if (trailer != null) LabeledAction("Trailer", FlexarrIcons.trailer, { showingTrailer = true }, Modifier.weight(1f))
            }
        }
    }
    if (showingTrailer && trailer != null) TrailerSheet(trailer, "${detail.title} trailer", onClose = { showingTrailer = false })
    if (confirming) TitleUndoDialog(detail.title, onConfirm = { navigator.unrequestTitle(model, detail.mediaType, detail.tmdbId) }, onDismiss = { confirming = false })
}

/** "R   2026   1h 59m   Movie", as the TV's page; TMDB's score with [scores] (never for a kids profile). */
private fun facts(detail: ExternalDetail, scores: Boolean): List<String> {
    val length = if (detail.isShow) detail.seasons?.let { if (it == 1) "1 season" else "$it seasons" } else detail.runtimeMinutes?.let { Format.runtime(minutes = it) }
    val status = detail.status?.takeIf { it != "Released" && it != "Ended" }
    val tmdb = detail.tmdbRating?.takeIf { scores && detail.scores.imdb == null }?.let { String.format(Locale.ROOT, "%.1f/10", it) }
    return listOfNotNull(detail.certification, detail.year?.toString(), length, if (detail.isShow) "Show" else "Movie", status, tmdb)
}

@Composable
private fun Labeled(label: String, value: String) {
    Column(verticalArrangement = Arrangement.spacedBy(3.dp)) {
        Text(label, fontSize = 12.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.usher)
        Text(value, fontSize = 14.sp, fontWeight = FontWeight.Medium, color = Flexarr.screen)
    }
}

/** A film or show from Seerr: poster, name and "Movie, 2014". */
@Composable
internal fun TitleCard(title: ExternalTitle, onClick: () -> Unit) {
    Column(Modifier.width(PosterWidth).pressable(onClick), verticalArrangement = Arrangement.spacedBy(6.dp)) {
        Box(Modifier.fillMaxWidth().aspectRatio(2f / 3f).clip(RoundedCornerShape(Tokens.radiusArt)).background(Flexarr.seat)) {
            RemoteImage(title.poster, 342, Modifier.fillMaxSize()) {
                Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                    Text(title.title, fontSize = 14.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.usher, modifier = Modifier.padding(8.dp))
                }
            }
        }
        Column(verticalArrangement = Arrangement.spacedBy(1.dp)) {
            Text(title.title, fontSize = 13.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen.copy(alpha = 0.9f), maxLines = 1, overflow = TextOverflow.Ellipsis)
            Text(title.kindLine, fontSize = 12.sp, color = Flexarr.usher, maxLines = 1, overflow = TextOverflow.Ellipsis)
        }
    }
}

/** Fades the backdrop out at its foot, into the page. */
private fun Modifier.fadeOut() = graphicsLayer(compositingStrategy = CompositingStrategy.Offscreen).drawWithContent {
    drawContent()
    drawRect(Brush.verticalGradient(0.55f to Color.Black, 1f to Color.Transparent), blendMode = BlendMode.DstIn)
}
