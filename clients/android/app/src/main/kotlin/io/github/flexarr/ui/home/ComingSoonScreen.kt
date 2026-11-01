package io.github.flexarr.ui.home

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Movie
import androidx.compose.material.icons.filled.Tv
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Text
import androidx.compose.material3.pulltorefresh.PullToRefreshBox
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.semantics.heading
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.github.flexarr.AppModel
import io.github.flexarr.core.MediaKind
import io.github.flexarr.core.UpcomingDays
import io.github.flexarr.core.UpcomingItem
import io.github.flexarr.core.cardTitle
import io.github.flexarr.core.status
import io.github.flexarr.ui.components.Edge
import io.github.flexarr.ui.components.Loaded
import io.github.flexarr.ui.components.LocalNavigator
import io.github.flexarr.ui.components.PageTitle
import io.github.flexarr.ui.components.RemoteImage
import io.github.flexarr.ui.components.StatusView
import io.github.flexarr.ui.components.pressable
import io.github.flexarr.ui.components.rememberLoader
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.RoomBackground
import io.github.flexarr.ui.theme.Tokens
import io.github.flexarr.ui.theme.marquee
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.time.format.FormatStyle

/**
 * Coming soon: what Sonarr and Radarr expect over the next few weeks, a day at a time, as on the web and the
 * iPhone. One in the library opens; anything else opens what's known about it.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ComingSoonScreen(model: AppModel) {
    val navigator = LocalNavigator.current
    val loader = rememberLoader(model.credentials) { UpcomingDays.days(model.client?.upcoming(30).orEmpty()) }
    RoomBackground(ambient = null) {
        Column {
            PageTitle("Coming soon", onBack = { navigator.back() })
            Loaded(loader) { days ->
                PullToRefreshBox(isRefreshing = loader.refreshing, onRefresh = loader.retry) {
                    if (days.isEmpty()) {
                        StatusView("Nothing is scheduled. Monitored shows and movies appear here as their dates get close.", empty = true)
                    } else {
                        LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 32.dp)) {
                            days.forEach { day ->
                                item(key = "day-${day.date}") {
                                    Text(
                                        UpcomingDays.heading(day.date),
                                        style = marquee(22),
                                        modifier = Modifier.padding(horizontal = Edge).padding(top = 18.dp, bottom = 6.dp).semantics { heading() },
                                    )
                                }
                                items(day.entries, key = { "entry-${day.date}-${it.first.id}" }) { entry -> EntryRow(entry) }
                            }
                        }
                    }
                }
            }
        }
    }
}

/** One title on a day: its poster, what's coming, and where it's got to. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun EntryRow(entry: UpcomingDays.Entry) {
    val navigator = LocalNavigator.current
    var about by remember { mutableStateOf(false) }
    val first = entry.first
    Row(
        Modifier.fillMaxWidth()
            .pressable({ first.libraryItemId?.let(navigator::openItem) ?: run { about = true } }, onClickLabel = if (first.libraryItemId != null) "Open" else "About")
            .semantics(mergeDescendants = true) {}
            .padding(horizontal = Edge, vertical = 8.dp),
        horizontalArrangement = Arrangement.spacedBy(12.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Poster(first, Modifier.size(46.dp, 69.dp))
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
            Text(entry.title, fontSize = 15.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen, maxLines = 2, overflow = TextOverflow.Ellipsis)
            Text(entry.detail, fontSize = 13.sp, color = Flexarr.screen.copy(alpha = 0.8f), maxLines = 2, overflow = TextOverflow.Ellipsis)
            val status = entry.status
            when {
                status != null -> Text(status, fontSize = 13.sp, fontWeight = FontWeight.Medium, color = if (first.available == true) Flexarr.go else Flexarr.usher)
                first.kind == MediaKind.Episode -> Text(time(first), fontSize = 13.sp, color = Flexarr.usher)
            }
        }
    }
    if (about) AboutSheet(first, entry.detail, onClose = { about = false })
}

@Composable
private fun Poster(item: UpcomingItem, modifier: Modifier) {
    Box(modifier.clip(RoundedCornerShape(Tokens.radiusArt)).background(Flexarr.seat), contentAlignment = Alignment.Center) {
        RemoteImage(item.poster ?: item.still, 200, Modifier.fillMaxSize()) {
            Icon(if (item.kind == MediaKind.Movie) Icons.Filled.Movie else Icons.Filled.Tv, contentDescription = null, tint = Flexarr.usher)
        }
    }
}

/** What's known about something coming that isn't in the library yet: when, and its overview. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun AboutSheet(item: UpcomingItem, detail: String, onClose: () -> Unit) {
    ModalBottomSheet(onDismissRequest = onClose, containerColor = Flexarr.seat) {
        Column(Modifier.verticalScroll(rememberScrollState()).navigationBarsPadding().padding(horizontal = Edge).padding(bottom = 24.dp), verticalArrangement = Arrangement.spacedBy(14.dp)) {
            Row(horizontalArrangement = Arrangement.spacedBy(16.dp)) {
                Poster(item, Modifier.size(92.dp, 138.dp))
                Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
                    Text(item.cardTitle, style = marquee(28))
                    Text(detail, fontSize = 15.sp, color = Flexarr.screen.copy(alpha = 0.85f))
                    Text("${item.`when`} ${date(item)}".trim(), fontSize = 13.sp, color = Flexarr.usher)
                    item.status?.let { Text(it, fontSize = 13.sp, fontWeight = FontWeight.Medium, color = Flexarr.go) }
                }
            }
            item.overview?.takeIf { it.isNotBlank() }?.let { Text(it, fontSize = 15.sp, lineHeight = 21.sp, color = Flexarr.screen.copy(alpha = 0.88f)) }
        }
    }
}

private fun time(item: UpcomingItem): String =
    item.date.atZone(ZoneId.systemDefault()).format(DateTimeFormatter.ofLocalizedTime(FormatStyle.SHORT))

/** "Friday 7 November", with the time for an episode. */
private fun date(item: UpcomingItem): String {
    val at = item.date.atZone(ZoneId.systemDefault())
    val day = at.format(DateTimeFormatter.ofPattern("EEEE d MMMM"))
    return if (item.kind == MediaKind.Episode) "$day ${time(item)}" else day
}
