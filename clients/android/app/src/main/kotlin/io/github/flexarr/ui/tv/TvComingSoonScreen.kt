package io.github.flexarr.ui.tv

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.CalendarMonth
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.window.Dialog
import androidx.tv.material3.Icon
import androidx.tv.material3.Text
import io.github.flexarr.AppModel
import io.github.flexarr.core.MediaKind
import io.github.flexarr.core.UpcomingDays
import io.github.flexarr.core.UpcomingItem
import io.github.flexarr.core.cardDetail
import io.github.flexarr.core.cardTitle
import io.github.flexarr.core.dayLabel
import io.github.flexarr.core.status
import io.github.flexarr.ui.components.Load
import io.github.flexarr.ui.components.LocalNavigator
import io.github.flexarr.ui.components.RemoteImage
import io.github.flexarr.ui.components.rememberLoader
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.RoomBackground
import io.github.flexarr.ui.theme.Tokens
import io.github.flexarr.ui.theme.marquee
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.time.format.FormatStyle
import io.github.flexarr.ui.theme.FlexarrIcons

/** Coming soon: what Sonarr and Radarr expect over the next few weeks, a row a day, as the Apple TV's. */
@Composable
fun TvComingSoonScreen(model: AppModel) {
    val loader = rememberLoader(model.credentials) { UpcomingDays.days(model.client?.upcoming(30).orEmpty()) }
    RoomBackground(ambient = null) {
        when (val state = loader.state) {
            Load.Loading -> TvStatusView()
            is Load.Failed -> TvStatusView(state.message, retry = loader.retry)
            is Load.Loaded -> {
                val days = state.value
                val first = remember { FocusRequester() }
                LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(top = 28.dp, bottom = 45.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
                    item(key = "title") { Text("Coming soon", style = marquee(32), modifier = Modifier.padding(start = TvLayout.Edge, bottom = 8.dp)) }
                    if (days.isEmpty()) {
                        item(key = "empty") {
                            Box(Modifier.size(960.dp, 250.dp)) { TvStatusView("Nothing is scheduled. Monitored shows and movies appear here as their dates get close.", empty = true) }
                        }
                    }
                    days.forEachIndexed { dayIndex, day ->
                        item(key = "day-${day.date}") {
                            Column {
                                TvRowHeading(UpcomingDays.heading(day.date))
                                LazyRow(
                                    contentPadding = PaddingValues(start = TvLayout.Edge, end = TvLayout.Edge, top = 14.dp, bottom = 20.dp),
                                    horizontalArrangement = Arrangement.spacedBy(TvLayout.RowSpacing),
                                    verticalAlignment = Alignment.Top,
                                ) {
                                    items(day.entries, key = { it.first.id }) { entry ->
                                        val takesFocus = dayIndex == 0 && entry === day.entries.first()
                                        TvComingSoonCard(entry.first, entry.detail, entry.status, modifier = if (takesFocus) Modifier.focusRequester(first) else Modifier)
                                    }
                                }
                            }
                        }
                    }
                }
                LaunchedEffect(days.isNotEmpty()) { if (days.isNotEmpty()) runCatching { first.requestFocus() } }
            }
        }
    }
}

/**
 * Something on its way, with the day it comes and any download's progress. Every card does something: one already in
 * the library (a show's next episode) opens, anything else shows what's known about it.
 */
@Composable
internal fun TvComingSoonCard(item: UpcomingItem, detail: String = item.cardDetail, status: String? = item.status, modifier: Modifier = Modifier) {
    val navigator = LocalNavigator.current
    var about by remember { mutableStateOf(false) }
    val width = TvLayout.WideWidth
    TvArtworkButton(
        ambient = Flexarr.silver,
        onClick = { item.libraryItemId?.let(navigator::openItem) ?: run { about = true } },
        modifier = modifier.width(width),
        contentDescription = "${item.cardTitle}, $detail, ${item.dayLabel()}",
        artwork = {
            Box(Modifier.size(width, width * 9f / 16f).background(Flexarr.seat)) {
                RemoteImage(item.still ?: item.poster, 560, Modifier.fillMaxSize()) {
                    Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                        Text(item.cardTitle, fontSize = 12.5.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.usher, modifier = Modifier.padding(10.dp))
                    }
                }
                Text(
                    item.dayLabel(), fontSize = 11.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen,
                    modifier = Modifier.align(Alignment.TopStart).padding(7.dp).clip(CircleShape).background(Color.Black.copy(alpha = 0.6f)).padding(horizontal = 7.dp, vertical = 3.dp),
                )
                item.download?.let { TvProgressBar(it.progress, Modifier.align(Alignment.BottomCenter).padding(8.dp)) }
            }
        },
        caption = { focused ->
            Column(Modifier.width(width), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                Text(item.cardTitle, fontSize = 12.5.sp, fontWeight = FontWeight.SemiBold, color = if (focused) Flexarr.screen else Flexarr.screen.copy(alpha = 0.85f), maxLines = 1, overflow = TextOverflow.Ellipsis)
                Text(detail, fontSize = 11.5.sp, color = Flexarr.usher, maxLines = 1, overflow = TextOverflow.Ellipsis)
                status?.let { Text(it, fontSize = 11.5.sp, fontWeight = FontWeight.Medium, color = if (item.available == true) Flexarr.go else Flexarr.usher, maxLines = 1) }
            }
        },
    )
    if (about) TvUpcomingAbout(item, detail, onClose = { about = false })
}

/** The end of Home's Coming soon row: the whole page. */
@Composable
internal fun TvSeeAllCard(onClick: () -> Unit) {
    val width = TvLayout.WideWidth * 0.6f
    TvArtworkButton(
        ambient = Flexarr.silver,
        onClick = onClick,
        contentDescription = "See all coming soon",
        artwork = {
            Column(
                Modifier.size(width, TvLayout.WideWidth * 9f / 16f).background(Flexarr.seat),
                verticalArrangement = Arrangement.spacedBy(7.dp, Alignment.CenterVertically),
                horizontalAlignment = Alignment.CenterHorizontally,
            ) {
                Icon(FlexarrIcons.comingSoon, contentDescription = null, tint = Flexarr.screen, modifier = Modifier.size(27.dp))
                Text("See all", fontSize = 14.5.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen)
            }
        },
    )
}

/** What's known about something coming: when, and its overview. */
@Composable
private fun TvUpcomingAbout(item: UpcomingItem, detail: String, onClose: () -> Unit) {
    val close = remember { FocusRequester() }
    Dialog(onDismissRequest = onClose) {
        Column(
            Modifier.widthIn(max = 550.dp).background(Flexarr.seat, RoundedCornerShape(Tokens.radiusPanel)).padding(32.dp),
            verticalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            Text(item.cardTitle, style = marquee(32))
            Text(detail, fontSize = 15.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen.copy(alpha = 0.85f))
            val at = item.date.atZone(ZoneId.systemDefault())
            val day = at.format(DateTimeFormatter.ofPattern("EEEE d MMMM"))
            val time = if (item.kind == MediaKind.Episode) " " + at.format(DateTimeFormatter.ofLocalizedTime(FormatStyle.SHORT)) else ""
            Text("${item.`when`} $day$time".trim(), fontSize = 13.sp, color = Flexarr.usher)
            item.overview?.takeIf { it.isNotBlank() }?.let { Text(it, fontSize = 14.5.sp, lineHeight = 21.sp, color = Flexarr.screen.copy(alpha = 0.88f)) }
            TvActionButton("Close", onClick = onClose, modifier = Modifier.padding(top = 10.dp).focusRequester(close))
        }
    }
    LaunchedEffect(Unit) { runCatching { close.requestFocus() } }
}
