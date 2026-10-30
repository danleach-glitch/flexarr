package io.github.flexarr.ui.watchlist

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Text
import androidx.compose.material3.pulltorefresh.PullToRefreshBox
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.github.flexarr.AppModel
import io.github.flexarr.core.WatchlistEntry
import io.github.flexarr.core.WatchlistFilter
import io.github.flexarr.core.WatchlistStatus
import io.github.flexarr.ui.components.Edge
import io.github.flexarr.ui.components.GridMinimum
import io.github.flexarr.ui.components.ItemMenu
import io.github.flexarr.ui.components.Loaded
import io.github.flexarr.ui.components.LocalNavigator
import io.github.flexarr.ui.components.MenuAction
import io.github.flexarr.ui.components.PageTitle
import io.github.flexarr.ui.components.PillButton
import io.github.flexarr.ui.components.PosterCard
import io.github.flexarr.ui.components.RemoteImage
import io.github.flexarr.ui.components.StatusView
import io.github.flexarr.ui.components.menuActions
import io.github.flexarr.ui.components.pressable
import io.github.flexarr.ui.components.rememberLoader
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.RoomBackground
import io.github.flexarr.ui.theme.marquee
import java.time.format.DateTimeFormatter
import java.time.format.FormatStyle

/**
 * The profile's watchlist: what's ready plays from the usual poster card, and the rest says where it's got to (on its
 * way, out on a date, or a Request button). Filters along the top; a long press takes a title off.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun WatchlistScreen(model: AppModel) {
    val navigator = LocalNavigator.current
    var filter by rememberSaveable { mutableStateOf(WatchlistFilter.Everything) }
    val loader = rememberLoader(model.credentials, model.watchlistVersion) { model.loadWatchlist().orEmpty() }
    RoomBackground(ambient = null) {
        Column {
            PageTitle("Watchlist", onBack = { navigator.back() })
            Loaded(loader) { entries ->
                PullToRefreshBox(isRefreshing = loader.refreshing, onRefresh = loader.retry) {
                    if (entries.isEmpty()) {
                        StatusView("Nothing here yet. Add films and shows from their pages, Discover or search.", empty = true)
                    } else Column {
                        LazyRow(
                            contentPadding = PaddingValues(horizontal = Edge),
                            horizontalArrangement = Arrangement.spacedBy(8.dp),
                            modifier = Modifier.padding(bottom = 12.dp),
                        ) {
                            items(WatchlistFilter.entries) { option -> PillButton(option.label, option == filter, { filter = option }) }
                        }
                        val shown = entries.filter(filter::matches)
                        if (shown.isEmpty()) {
                            StatusView("Nothing here.", empty = true)
                        } else {
                            LazyVerticalGrid(
                                columns = GridCells.Adaptive(GridMinimum),
                                contentPadding = PaddingValues(start = Edge, end = Edge, bottom = 32.dp),
                                horizontalArrangement = Arrangement.spacedBy(12.dp),
                                verticalArrangement = Arrangement.spacedBy(20.dp),
                                modifier = Modifier.fillMaxSize(),
                            ) {
                                items(shown, key = { it.id }) { entry ->
                                    val item = entry.item
                                    // Ready: the usual card, whose menu takes it off the list too.
                                    if (entry.status == WatchlistStatus.Ready && item != null) PosterCard(item, width = null) else EntryCard(model, entry)
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}

/** A title that isn't ready yet: its poster, name and where it's got to. */
@Composable
private fun EntryCard(model: AppModel, entry: WatchlistEntry) {
    val navigator = LocalNavigator.current
    var menu by remember { mutableStateOf(false) }
    var requesting by remember(entry.id, entry.status) { mutableStateOf(false) }
    val canRequest = entry.status == WatchlistStatus.NotAvailable && entry.tmdbId != null
    fun request() {
        requesting = true
        navigator.watchlistChange("Couldn’t request this") {
            try {
                model.request(entry)
            } finally {
                requesting = false
            }
        }
    }
    val actions = buildList {
        if (canRequest && !requesting) add(MenuAction("Request") { request() })
        add(MenuAction("Remove from watchlist") { navigator.watchlistChange("Couldn’t remove this from your watchlist") { model.removeFromWatchlist(entry) } })
    }
    // A show in the library with nothing to play yet still has its page.
    val libraryId = entry.libraryItemId
    Box {
        Column(
            Modifier.fillMaxWidth()
                .pressable(onClick = { if (libraryId != null) navigator.openItem(libraryId) else menu = true }, onLongClick = { menu = true })
                .menuActions(actions),
            verticalArrangement = Arrangement.spacedBy(6.dp),
        ) {
            Box(Modifier.fillMaxWidth().aspectRatio(2f / 3f).clip(RoundedCornerShape(6.dp)).background(Flexarr.seat)) {
                RemoteImage(entry.poster, 342, Modifier.fillMaxSize().alpha(0.75f)) {
                    Box(
                        Modifier.fillMaxSize().background(Brush.verticalGradient(listOf(Flexarr.silver.copy(alpha = 0.3f), Flexarr.seat))),
                        contentAlignment = Alignment.Center,
                    ) {
                        Text(entry.title, style = marquee(18, Flexarr.screen.copy(alpha = 0.8f)).copy(fontWeight = FontWeight.Bold), textAlign = TextAlign.Center, modifier = Modifier.padding(8.dp))
                    }
                }
            }
            Column(verticalArrangement = Arrangement.spacedBy(1.dp)) {
                Text(entry.title, fontSize = 13.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen.copy(alpha = 0.9f), maxLines = 1, overflow = TextOverflow.Ellipsis)
                if (canRequest) {
                    Text(
                        "Request",
                        fontSize = 12.sp,
                        fontWeight = FontWeight.SemiBold,
                        color = Flexarr.screen,
                        modifier = Modifier.padding(top = 4.dp).alpha(if (requesting) 0.5f else 1f).heightIn(min = 28.dp).clip(CircleShape)
                            .background(Flexarr.screen.copy(alpha = 0.14f))
                            .pressable({ request() }, enabled = !requesting).padding(horizontal = 12.dp, vertical = 6.dp),
                    )
                } else {
                    statusLine(entry)?.let { Text(it, fontSize = 12.sp, color = Flexarr.usher, maxLines = 1, overflow = TextOverflow.Ellipsis) }
                }
            }
        }
        ItemMenu(actions, menu, onDismiss = { menu = false })
    }
}

/** "On its way", "Out 5 Mar 2027" (in the phone's own date format), or the year. */
private fun statusLine(entry: WatchlistEntry): String? = when (entry.status) {
    WatchlistStatus.OnItsWay -> "On its way"
    WatchlistStatus.Coming -> entry.releaseDay?.let { "Out ${it.format(DateTimeFormatter.ofLocalizedDate(FormatStyle.MEDIUM))}" } ?: "Coming"
    else -> entry.year?.toString()
}
