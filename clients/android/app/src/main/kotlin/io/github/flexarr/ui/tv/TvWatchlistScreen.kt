package io.github.flexarr.ui.tv

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.GridItemSpan
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.tv.material3.Text
import io.github.flexarr.AppModel
import io.github.flexarr.core.WatchlistEntry
import io.github.flexarr.core.WatchlistFilter
import io.github.flexarr.core.WatchlistKind
import io.github.flexarr.core.WatchlistSort
import io.github.flexarr.core.WatchlistStatus
import io.github.flexarr.ui.components.Load
import io.github.flexarr.ui.components.LocalNavigator
import io.github.flexarr.ui.components.MenuAction
import io.github.flexarr.ui.components.RemoteImage
import io.github.flexarr.ui.components.rememberLoader
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.RoomBackground
import io.github.flexarr.ui.theme.glowColor
import io.github.flexarr.ui.theme.marquee
import java.time.format.DateTimeFormatter
import java.time.format.FormatStyle

/**
 * The profile's watchlist, as the Apple TV's WatchlistView: what's ready plays from its usual card, everything else
 * shows where it's got to under the title. Status pills along the top, kind and order behind the last one; pressing
 * and holding a card takes it off.
 */
@Composable
fun TvWatchlistScreen(model: AppModel) {
    val navigator = LocalNavigator.current
    var filter by rememberSaveable { mutableStateOf(WatchlistFilter.Everything) }
    var kind by rememberSaveable { mutableStateOf(WatchlistKind.All) }
    var sort by rememberSaveable { mutableStateOf(WatchlistSort.ReadyFirst) }
    var choosing by remember { mutableStateOf(false) }
    val loader = rememberLoader(model.credentials, model.watchlistVersion) { model.loadWatchlist().orEmpty() }
    val entries = (loader.state as? Load.Loaded)?.value
    RoomBackground(ambient = entries?.firstOrNull()?.item?.let { glowColor(it.accentColor) }, intensity = 0.18f) {
        when (val state = loader.state) {
            Load.Loading -> TvStatusView()
            is Load.Failed -> TvStatusView(state.message, retry = loader.retry)
            is Load.Loaded -> {
                val shown = remember(state.value, filter, kind, sort) { sort.sorted(state.value.filter { filter.matches(it) && kind.matches(it) }) }
                LazyVerticalGrid(
                    columns = GridCells.Fixed(6),
                    contentPadding = PaddingValues(start = TvLayout.Edge, end = TvLayout.Edge, top = 28.dp, bottom = 45.dp),
                    horizontalArrangement = Arrangement.spacedBy(TvLayout.RowSpacing),
                    verticalArrangement = Arrangement.spacedBy(26.dp),
                    modifier = Modifier.fillMaxSize(),
                ) {
                    item(key = "title", span = { GridItemSpan(maxLineSpan) }) { Text("Watchlist", style = marquee(32)) }
                    if (state.value.isEmpty()) {
                        item(key = "empty", span = { GridItemSpan(maxLineSpan) }) { Box(Modifier.size(880.dp, 250.dp)) { TvStatusView("Nothing here yet.", empty = true) } }
                    } else {
                        item(key = "filters", span = { GridItemSpan(maxLineSpan) }) {
                            Row(horizontalArrangement = Arrangement.spacedBy(9.dp), modifier = Modifier.padding(vertical = 6.dp)) {
                                WatchlistFilter.entries.forEach { option -> TvPill(option.label, selected = option == filter, onClick = { filter = option }) }
                                // The kind shows here too, so a filtered list says why shows (or films) are missing.
                                val label = listOfNotNull(kind.takeIf { it != WatchlistKind.All }?.label, sort.label).joinToString(", ")
                                TvPill(label, selected = kind != WatchlistKind.All || sort != WatchlistSort.ReadyFirst, onClick = { choosing = true })
                            }
                        }
                        if (shown.isEmpty()) {
                            item(key = "none", span = { GridItemSpan(maxLineSpan) }) { Box(Modifier.size(880.dp, 200.dp)) { TvStatusView("Nothing on your watchlist matches.", empty = true) } }
                        }
                        items(shown, key = { it.id }) { entry ->
                            val item = entry.item
                            // Ready: the usual card, whose menu takes it off the list too.
                            if (entry.status == WatchlistStatus.Ready && item != null) TvPosterCard(item, onClick = { navigator.open(item) }) else EntryCard(model, entry)
                        }
                    }
                }
            }
        }
    }
    if (choosing) {
        TvItemMenu(
            "Show and sort",
            WatchlistKind.entries.map { option -> MenuAction(if (option == kind) "${option.label} (showing)" else option.label) { kind = option } } +
                WatchlistSort.entries.map { option -> MenuAction(if (option == sort) "${option.label} (sorted)" else option.label) { sort = option } },
            onDismiss = { choosing = false },
        )
    }
}

/** A title that isn't ready yet: its poster, name and where it's got to. Pressing it offers Request and Remove. */
@Composable
private fun EntryCard(model: AppModel, entry: WatchlistEntry) {
    val navigator = LocalNavigator.current
    var menu by remember { mutableStateOf(false) }
    val libraryId = entry.libraryItemId
    val canRequest = entry.status == WatchlistStatus.NotAvailable && entry.tmdbId != null
    val width = TvLayout.PosterWidth
    TvArtworkButton(
        ambient = Flexarr.silver,
        // A show in the library with nothing to play yet still has its page.
        onClick = { if (libraryId != null) navigator.openItem(libraryId) else menu = true },
        onLongClick = { menu = true },
        modifier = Modifier.width(width),
        contentDescription = listOfNotNull(entry.title, note(entry)).joinToString(", "),
        artwork = {
            Box(Modifier.size(width, width * 1.5f).background(Flexarr.seat)) {
                RemoteImage(entry.poster, 342, Modifier.fillMaxSize().alpha(0.75f)) {
                    Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                        Text(entry.title, style = marquee(16, Flexarr.screen.copy(alpha = 0.8f)), textAlign = TextAlign.Center, modifier = Modifier.padding(8.dp))
                    }
                }
            }
        },
        caption = { focused ->
            Column(Modifier.width(width), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                Text(entry.title, fontSize = 12.sp, fontWeight = FontWeight.SemiBold, color = if (focused) Flexarr.screen else Flexarr.screen.copy(alpha = 0.8f), maxLines = 1, overflow = TextOverflow.Ellipsis)
                note(entry)?.let { Text(it, fontSize = 11.5.sp, color = Flexarr.usher, maxLines = 1, overflow = TextOverflow.Ellipsis) }
            }
        },
    )
    if (menu) {
        TvItemMenu(
            entry.title,
            listOfNotNull(
                MenuAction("Request") { navigator.watchlistChange("Couldn’t request this") { model.request(entry) } }.takeIf { canRequest },
                MenuAction("Remove from watchlist") { navigator.watchlistChange("Couldn’t remove this from your watchlist") { model.removeFromWatchlist(entry) } },
            ),
            onDismiss = { menu = false },
        )
    }
}

/** Where it's got to, then where it streams: "On its way   On Netflix", "Request   On Prime Video". */
private fun note(entry: WatchlistEntry): String? {
    val status = when (entry.status) {
        WatchlistStatus.OnItsWay -> "On its way"
        WatchlistStatus.Coming -> entry.releaseDay?.let { "Out ${it.format(DateTimeFormatter.ofLocalizedDate(FormatStyle.MEDIUM))}" } ?: "Coming"
        WatchlistStatus.NotAvailable -> if (entry.tmdbId != null) "Request" else null
        else -> null
    }
    return listOfNotNull(status, entry.streamingLine).joinToString("   ").takeIf { it.isNotEmpty() }
}
