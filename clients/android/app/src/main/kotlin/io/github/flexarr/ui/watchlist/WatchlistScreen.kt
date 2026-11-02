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
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.FilterList
import androidx.compose.material.icons.outlined.BookmarkRemove
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.Text
import androidx.compose.material3.minimumInteractiveComponentSize
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
import androidx.compose.ui.semantics.heading
import androidx.compose.ui.semantics.selected
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.github.flexarr.AppModel
import io.github.flexarr.core.WatchlistEntry
import io.github.flexarr.core.WatchlistFilter
import io.github.flexarr.core.WatchlistKind
import io.github.flexarr.core.WatchlistSort
import io.github.flexarr.core.WatchlistStatus
import io.github.flexarr.ui.components.Edge
import io.github.flexarr.ui.components.GridMinimum
import io.github.flexarr.ui.components.ItemMenu
import io.github.flexarr.ui.components.Load
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
import io.github.flexarr.ui.theme.Tokens
import io.github.flexarr.ui.theme.marquee
import java.time.format.DateTimeFormatter
import java.time.format.FormatStyle

/**
 * The profile's watchlist: what's ready plays from the usual poster card, and the rest says where it's got to (on its
 * way, out on a date, where it streams, or a Request button). Status chips along the top, sort and films or shows
 * behind the button beside the title; a long press takes a title off.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun WatchlistScreen(model: AppModel) {
    val navigator = LocalNavigator.current
    var filter by rememberSaveable { mutableStateOf(WatchlistFilter.Everything) }
    var kind by rememberSaveable { mutableStateOf(WatchlistKind.All) }
    var sort by rememberSaveable { mutableStateOf(WatchlistSort.ReadyFirst) }
    // A refresh that fails leaves the list showing, so say so.
    val loader = rememberLoader(model.credentials, model.watchlistVersion, onRefreshFailed = { navigator.tellFailure("Couldn’t refresh your watchlist", it) }) {
        model.loadWatchlist().orEmpty()
    }
    val hasEntries = (loader.state as? Load.Loaded)?.value?.isNotEmpty() == true
    RoomBackground(ambient = null) {
        Column {
            PageTitle("Watchlist", onBack = { navigator.back() }) {
                if (hasEntries) FilterMenu(filter, { filter = it }, kind, { kind = it }, sort, { sort = it })
            }
            Loaded(loader) { entries ->
                PullToRefreshBox(isRefreshing = loader.refreshing, onRefresh = loader.retry) {
                    if (entries.isEmpty()) {
                        StatusView("Nothing here yet.", empty = true)
                    } else Column {
                        LazyRow(
                            contentPadding = PaddingValues(horizontal = Edge),
                            horizontalArrangement = Arrangement.spacedBy(8.dp),
                            modifier = Modifier.padding(bottom = 12.dp),
                        ) {
                            items(WatchlistFilter.entries) { option -> PillButton(option.label, option == filter, { filter = option }) }
                        }
                        val shown = remember(entries, filter, kind, sort) { sort.sorted(entries.filter { filter.matches(it) && kind.matches(it) }) }
                        if (shown.isEmpty()) {
                            StatusView("Nothing on your watchlist matches.", empty = true)
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
        add(MenuAction("Remove from watchlist", Icons.Outlined.BookmarkRemove) { navigator.watchlistChange("Couldn’t remove this from your watchlist") { model.removeFromWatchlist(entry) } })
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
            Box(Modifier.fillMaxWidth().aspectRatio(2f / 3f).clip(RoundedCornerShape(Tokens.radiusArt)).background(Flexarr.seat)) {
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
                // "Film, 2014": a title the library hasn't got has no other way of saying what it is.
                if (libraryId == null) Text(entry.kindLine, fontSize = 12.sp, color = Flexarr.usher, maxLines = 1, overflow = TextOverflow.Ellipsis)
                if (!canRequest) statusLine(entry)?.let { Text(it, fontSize = 12.sp, color = Flexarr.usher, maxLines = 1, overflow = TextOverflow.Ellipsis) }
                // Where it streams, for a title the library hasn't got.
                entry.streamingLine?.let { Text(it, fontSize = 12.sp, color = Flexarr.usher, maxLines = 1, overflow = TextOverflow.Ellipsis) }
                if (canRequest) {
                    // 28 to see, 48 to touch: the reserved space around it takes the taps too.
                    Text(
                        "Request",
                        fontSize = 12.sp,
                        fontWeight = FontWeight.SemiBold,
                        color = Flexarr.screen,
                        modifier = Modifier.minimumInteractiveComponentSize().alpha(if (requesting) 0.5f else 1f).heightIn(min = 28.dp).clip(CircleShape)
                            .background(Flexarr.screen.copy(alpha = 0.14f))
                            .pressable({ request() }, enabled = !requesting).padding(horizontal = 12.dp, vertical = 6.dp),
                    )
                }
            }
        }
        ItemMenu(actions, menu, onDismiss = { menu = false })
    }
}

/** Which titles (as the chips), films, shows or both, and the order: one button beside the title, as a library's. */
@Composable
private fun FilterMenu(
    filter: WatchlistFilter,
    onFilter: (WatchlistFilter) -> Unit,
    kind: WatchlistKind,
    onKind: (WatchlistKind) -> Unit,
    sort: WatchlistSort,
    onSort: (WatchlistSort) -> Unit,
) {
    var open by remember { mutableStateOf(false) }
    val filtered = filter != WatchlistFilter.Everything || kind != WatchlistKind.All
    Box {
        IconButton(onClick = { open = true }) {
            Icon(Icons.Filled.FilterList, contentDescription = "Filter", tint = if (filtered) Flexarr.silver else Flexarr.screen)
        }
        DropdownMenu(expanded = open, onDismissRequest = { open = false }) {
            MenuHeading("Show")
            WatchlistFilter.entries.forEach { option -> Choice(option.label, option == filter) { open = false; onFilter(option) } }
            HorizontalDivider(color = Flexarr.aisle)
            MenuHeading("Kind")
            WatchlistKind.entries.forEach { option -> Choice(option.label, option == kind) { open = false; onKind(option) } }
            HorizontalDivider(color = Flexarr.aisle)
            MenuHeading("Sort")
            WatchlistSort.entries.forEach { option -> Choice(option.label, option == sort) { open = false; onSort(option) } }
        }
    }
}

@Composable
private fun MenuHeading(text: String) {
    Text(text, fontSize = 13.sp, color = Flexarr.usher, modifier = Modifier.padding(horizontal = 16.dp, vertical = 6.dp).semantics { heading() })
}

@Composable
private fun Choice(label: String, selected: Boolean, onClick: () -> Unit) {
    DropdownMenuItem(
        text = { Text(label, fontWeight = if (selected) FontWeight.Bold else FontWeight.Normal) },
        trailingIcon = { if (selected) Icon(Icons.Filled.Check, contentDescription = null, modifier = Modifier.size(18.dp)) },
        onClick = onClick,
        modifier = Modifier.semantics { this.selected = selected },
    )
}

/** "On its way", "Out 5 Mar 2027" (in the phone's own date format), or the year. */
private fun statusLine(entry: WatchlistEntry): String? = when (entry.status) {
    WatchlistStatus.OnItsWay -> "On its way"
    WatchlistStatus.Coming -> entry.releaseDay?.let { "Out ${it.format(DateTimeFormatter.ofLocalizedDate(FormatStyle.MEDIUM))}" } ?: "Coming"
    // The year's in the kind line, for a title the library hasn't got.
    else -> if (entry.libraryItemId == null) null else entry.year?.toString()
}
