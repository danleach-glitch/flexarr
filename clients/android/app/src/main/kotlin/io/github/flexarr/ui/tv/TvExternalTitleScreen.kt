package io.github.flexarr.ui.tv

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.GridItemSpan
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.RemoveCircleOutline
import androidx.compose.material.icons.filled.Schedule
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.focus.onFocusChanged
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.tv.material3.Text
import io.github.flexarr.AppModel
import io.github.flexarr.core.Availability
import io.github.flexarr.core.ExternalDetail
import io.github.flexarr.core.ExternalPerson
import io.github.flexarr.core.ExternalTitle
import io.github.flexarr.core.FlexarrClient
import io.github.flexarr.core.FlexarrException
import io.github.flexarr.core.Format
import io.github.flexarr.core.ItemSummary
import io.github.flexarr.core.MediaKind
import io.github.flexarr.core.Paged
import io.github.flexarr.core.externalTitle
import io.github.flexarr.core.genreItems
import io.github.flexarr.core.request
import io.github.flexarr.ui.components.LocalNavigator
import io.github.flexarr.ui.components.RemoteImage
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.RoomBackground
import io.github.flexarr.ui.theme.marquee
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.launch
import java.text.NumberFormat
import java.util.Locale

/**
 * A film or show from Discover that isn't in the library, as the Apple TV's ExternalTitleView: what it is, how it's
 * rated, who's in it, and a request. One that's come into the library since opens as itself.
 */
@Composable
internal fun TvExternalTitleScreen(model: AppModel, preview: ExternalTitle, open: (ExternalTitle) -> Unit) {
    val client = model.client ?: return
    var detail by remember { mutableStateOf<ExternalDetail?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    var attempt by remember { mutableIntStateOf(0) }
    LaunchedEffect(attempt) {
        try {
            detail = client.externalTitle(preview)
            error = null
        } catch (e: CancellationException) {
            throw e
        } catch (e: FlexarrException.Http) {
            error = if (e.status == 404) "Flexarr can’t find that title." else e.message
        } catch (e: Exception) {
            error = e.message ?: "Something went wrong."
        }
    }

    // It's come in since: it opens as itself.
    detail?.libraryItemId?.let { id ->
        TvDetailScreen(model, id)
        return
    }

    val loaded = detail
    val title = loaded?.summary ?: preview
    val focus = rememberFocusMemory()
    // Request takes focus as the page opens; coming back from a title in "If you like this", that card.
    LaunchedEffect(loaded != null, error != null) {
        if (loaded != null || error != null || focus.lastKey == null) focus.restore("request")
    }
    val scroll = rememberScrollState()
    val scope = rememberCoroutineScope()
    Box(Modifier.fillMaxSize().background(Flexarr.room)) {
        ExternalBackdrop(title.backdrop, dim = { (scroll.value / 350f).coerceIn(0f, 0.85f) })
        Column(Modifier.fillMaxSize().verticalScroll(scroll).padding(bottom = 45.dp), verticalArrangement = Arrangement.spacedBy(28.dp)) {
            Box(Modifier.fillMaxWidth().heightIn(min = 430.dp), contentAlignment = Alignment.BottomStart) {
                Header(title, preview, loaded) {
                    // With focus on Request, the page sits at the top, the whole header in view.
                    RequestButton(
                        client,
                        title,
                        focus,
                        modifier = Modifier.padding(top = 7.dp).onFocusChanged { if (it.hasFocus) scope.launch { scroll.animateScrollTo(0) } },
                    )
                }
            }
            when {
                loaded != null -> Sections(loaded, open, focus)
                error != null -> Box(Modifier.fillMaxWidth().height(150.dp)) { TvStatusView(error, retry = { attempt++ }) }
            }
        }
    }
}

/** The backdrop, dimmed further as the page scrolls so the rows stay legible over the artwork. */
@Composable
private fun ExternalBackdrop(path: String?, dim: () -> Float) {
    Box(Modifier.fillMaxSize()) {
        RemoteImage(path, tvBackdropWidth(), Modifier.fillMaxSize()) {}
        Box(Modifier.fillMaxSize().background(Brush.horizontalGradient(0f to Flexarr.room.copy(alpha = 0.95f), 0.45f to Flexarr.room.copy(alpha = 0.6f), 0.8f to Color.Transparent)))
        Box(Modifier.fillMaxSize().background(Brush.verticalGradient(0.35f to Color.Transparent, 0.9f to Flexarr.room.copy(alpha = 0.9f), 1f to Flexarr.room)))
        Box(Modifier.fillMaxSize().graphicsLayer { alpha = dim() }.background(Flexarr.room))
    }
}

@Composable
private fun Header(title: ExternalTitle, preview: ExternalTitle, detail: ExternalDetail?, request: @Composable () -> Unit) {
    Column(Modifier.padding(start = TvLayout.Edge, end = 150.dp, bottom = 5.dp), verticalArrangement = Arrangement.spacedBy(11.dp)) {
        Text(
            title.title,
            style = marquee(if (title.title.length > 24) 42 else 55),
            maxLines = 2,
            overflow = TextOverflow.Ellipsis,
            modifier = Modifier.widthIn(max = 550.dp),
        )
        detail?.tagline?.let {
            Text(it, fontSize = 14.sp, fontWeight = FontWeight.Medium, fontStyle = FontStyle.Italic, color = Flexarr.screen.copy(alpha = 0.85f))
        }
        Row(horizontalArrangement = Arrangement.spacedBy(11.dp), verticalAlignment = Alignment.CenterVertically) {
            TvFactsLine(facts(title, detail))
            val genres = detail?.genres.orEmpty()
            if (genres.isNotEmpty()) Text(genres.take(3).joinToString(", "), fontSize = 13.sp, fontWeight = FontWeight.Medium, color = Flexarr.usher, maxLines = 1)
        }
        val scores = detail?.let(::ratings).orEmpty()
        if (scores.isNotEmpty()) {
            Row(horizontalArrangement = Arrangement.spacedBy(22.dp)) {
                scores.forEach { (label, value) ->
                    Column(verticalArrangement = Arrangement.spacedBy(1.dp)) {
                        Text(value, style = TextStyle(fontSize = 17.sp, fontWeight = FontWeight.Bold, fontFeatureSettings = "tnum"))
                        Text(label, fontSize = 11.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.usher)
                    }
                }
            }
        }
        (detail?.overview ?: preview.overview)?.takeIf { it.isNotEmpty() }?.let {
            Text(it, fontSize = 13.5.sp, lineHeight = 19.sp, color = Flexarr.screen.copy(alpha = 0.88f), maxLines = 4, overflow = TextOverflow.Ellipsis, modifier = Modifier.widthIn(max = 520.dp))
        }
        request()
    }
}

/** "R   2026   1h 59m   Film". */
private fun facts(title: ExternalTitle, detail: ExternalDetail?): List<String> {
    if (detail == null) return listOfNotNull(title.year?.toString(), if (title.isShow) "Series" else "Film")
    val length = if (detail.isShow) {
        detail.seasons?.let { if (it == 1) "1 season" else "$it seasons" }
    } else {
        detail.runtimeMinutes?.let { Format.runtime(minutes = it) }
    }
    val status = detail.status?.takeIf { it != "Released" && it != "Ended" }
    return listOfNotNull(detail.certification, detail.year?.toString(), length, if (detail.isShow) "Series" else "Film", status)
}

/** The scores people check before watching: TMDB, IMDb, Rotten Tomatoes critics and audience. */
private fun ratings(detail: ExternalDetail): List<Pair<String, String>> = listOfNotNull(
    detail.tmdbRating?.let { "TMDB" to String.format(Locale.ROOT, "%.1f", it) },
    detail.imdbRating?.let { "IMDb" to String.format(Locale.ROOT, "%.1f", it) },
    detail.criticScore?.let { "Critics" to "$it%" },
    detail.audienceScore?.let { "Audience" to "$it%" },
)

@Composable
private fun Sections(detail: ExternalDetail, open: (ExternalTitle) -> Unit, focus: FocusMemory) {
    if (detail.cast.isNotEmpty()) {
        Column {
            TvRowHeading("Cast")
            // Not buttons: these people aren't in the library, so there's nowhere for them to go.
            LazyRow(contentPadding = PaddingValues(horizontal = TvLayout.Edge, vertical = 15.dp), horizontalArrangement = Arrangement.spacedBy(18.dp)) {
                items(detail.cast) { CastFace(it) }
            }
        }
    }
    if (detail.madeBy.isNotEmpty() || detail.studio != null) {
        Row(Modifier.padding(horizontal = TvLayout.Edge), horizontalArrangement = Arrangement.spacedBy(40.dp)) {
            if (detail.madeBy.isNotEmpty()) Labeled(if (detail.isShow) "Created by" else "Directed by", detail.madeBy.joinToString(", "))
            detail.studio?.let { Labeled(if (detail.isShow) "Network" else "Studio", it) }
        }
    }
    if (detail.recommendations.isNotEmpty()) {
        DiscoverRow("If you like this", detail.recommendations, open, cardModifier = { Modifier.focusMemory(focus, "like/${it.id}") })
    }
}

@Composable
private fun Labeled(label: String, value: String) {
    Column(verticalArrangement = Arrangement.spacedBy(3.dp)) {
        Text(label, fontSize = 11.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.usher)
        Text(value, fontSize = 13.sp, fontWeight = FontWeight.Medium)
    }
}

/** Someone in the cast: their face (or initials), name and part. */
@Composable
private fun CastFace(person: ExternalPerson) {
    Column(Modifier.width(105.dp), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(8.dp)) {
        RemoteImage(person.image, 300, Modifier.size(90.dp).clip(CircleShape)) {
            Box(Modifier.fillMaxSize().background(Flexarr.seat), contentAlignment = Alignment.Center) {
                val initials = person.name.split(" ").take(2).mapNotNull { it.firstOrNull()?.toString() }.joinToString("")
                Text(initials, style = marquee(28, Flexarr.usher).copy(fontWeight = FontWeight.Bold))
            }
        }
        Column(horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(1.dp)) {
            Text(person.name, fontSize = 11.5.sp, fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis)
            person.character?.let { Text(it, fontSize = 11.sp, color = Flexarr.usher, maxLines = 1, overflow = TextOverflow.Ellipsis) }
        }
    }
}

private sealed interface Sending {
    data object Idle : Sending
    data object InFlight : Sending
    data object Sent : Sending
    data class Failed(val message: String) : Sending
}

/** Request the title through Seerr, or say where it stands if it's already asked for or in. */
@Composable
private fun RequestButton(client: FlexarrClient, title: ExternalTitle, focus: FocusMemory, modifier: Modifier = Modifier) {
    var sending by remember(title.id) { mutableStateOf<Sending>(Sending.Idle) }
    val scope = rememberCoroutineScope()
    // Not asked for yet, here or anywhere else.
    val canRequest = sending != Sending.Sent && title.availability == Availability.None
    val failure = (sending as? Sending.Failed)?.message
    val label = when {
        sending == Sending.Sent -> "Requested"
        title.availability.label != null -> title.availability.label!!
        sending == Sending.InFlight -> "Requesting…"
        failure == null -> "Request"
        else -> "Try again"
    }
    val icon = if (sending == Sending.Sent) {
        Icons.Filled.Schedule
    } else {
        when (title.availability) {
            Availability.Available -> Icons.Filled.Check
            Availability.Requested, Availability.PartlyAvailable -> Icons.Filled.Schedule
            Availability.None -> Icons.Filled.Add
            Availability.Unknown -> Icons.Filled.RemoveCircleOutline
        }
    }
    Row(modifier, horizontalArrangement = Arrangement.spacedBy(15.dp), verticalAlignment = Alignment.CenterVertically) {
        // One button throughout, whatever it says: one that's taken away or disabled while it has focus leaves nothing
        // on the page focused.
        TvActionButton(
            label,
            onClick = {
                if (canRequest && sending != Sending.InFlight) {
                    sending = Sending.InFlight
                    scope.launch {
                        sending = try {
                            client.request(title)
                            Sending.Sent
                        } catch (e: CancellationException) {
                            throw e
                        } catch (e: Exception) {
                            Sending.Failed(e.message ?: "Something went wrong.")
                        }
                    }
                }
            },
            icon = icon,
            prominent = canRequest,
            modifier = Modifier.focusMemory(focus, "request"),
        )
        failure?.let {
            Text(it, fontSize = 12.sp, fontWeight = FontWeight.Medium, color = Flexarr.screen.copy(alpha = 0.85f), maxLines = 2, overflow = TextOverflow.Ellipsis, modifier = Modifier.widthIn(max = 350.dp))
        }
    }
}

// A genre

/** Six posters across the content width, as the Apple TV's grids. */
private val GridSpacing = 26.dp

/** Everything the library has in a genre, best rated first, narrowed to films or shows if wanted (GenreView). */
@Composable
internal fun TvGenreScreen(client: FlexarrClient, name: String) {
    val navigator = LocalNavigator.current
    var kind by rememberSaveable { mutableStateOf<MediaKind?>(null) }
    var page by remember { mutableStateOf<Paged<ItemSummary>?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    var attempt by remember { mutableIntStateOf(0) }
    val focus = rememberFocusMemory()
    LaunchedEffect(kind, attempt) {
        try {
            page = client.genreItems(name, kind)
            error = null
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            error = e.message ?: "Something went wrong."
        }
    }
    // The first title takes focus as the page appears (not when the choice of films or shows changes); coming back,
    // the one that had it.
    LaunchedEffect(page != null) {
        page?.let { focus.restore(it.items.firstOrNull()?.id) }
    }

    RoomBackground(ambient = null) {
        LazyVerticalGrid(
            GridCells.Fixed(6),
            Modifier.fillMaxSize(),
            contentPadding = PaddingValues(start = TvLayout.Edge, end = TvLayout.Edge, top = 30.dp, bottom = 40.dp),
            horizontalArrangement = Arrangement.spacedBy(GridSpacing),
            verticalArrangement = Arrangement.spacedBy(32.dp),
        ) {
            item(key = "header", span = { GridItemSpan(maxLineSpan) }) {
                Row(Modifier.padding(bottom = 4.dp), verticalAlignment = Alignment.Bottom) {
                    Column(verticalArrangement = Arrangement.spacedBy(3.dp)) {
                        Text(name, style = marquee(36))
                        val total = page?.total
                        Text(
                            if (total == null) " " else "${NumberFormat.getIntegerInstance().format(total)} ${if (total == 1) "title" else "titles"}, best rated first",
                            fontSize = 13.sp,
                            color = Flexarr.usher,
                        )
                    }
                    Spacer(Modifier.weight(1f))
                    Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        listOf(null to "Everything", MediaKind.Movie to "Films", MediaKind.Series to "Shows").forEach { (choice, label) ->
                            TvPill(label, selected = kind == choice, onClick = { kind = choice })
                        }
                    }
                }
            }
            val shown = page
            when {
                shown != null -> items(shown.items, key = { it.id }) { item ->
                    TvPosterCard(item, onClick = { navigator.open(item) }, modifier = Modifier.focusMemory(focus, item.id))
                }
                error != null -> item(key = "error", span = { GridItemSpan(maxLineSpan) }) {
                    Box(Modifier.fillMaxWidth().height(200.dp)) { TvStatusView(error, retry = { attempt++ }) }
                }
                else -> item(key = "loading", span = { GridItemSpan(maxLineSpan) }) {
                    Box(Modifier.fillMaxWidth().height(200.dp)) { TvStatusView() }
                }
            }
        }
    }
}
