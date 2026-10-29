package io.github.flexarr.ui.livetv

import android.content.Context
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.List
import androidx.compose.material.icons.filled.CalendarMonth
import androidx.compose.material.icons.filled.RadioButtonChecked
import androidx.compose.material.icons.filled.Star
import androidx.compose.material.icons.outlined.StarOutline
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.pulltorefresh.PullToRefreshBox
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.semantics.CustomAccessibilityAction
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.customActions
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import coil3.compose.AsyncImage
import io.github.flexarr.AppModel
import io.github.flexarr.appModel
import io.github.flexarr.core.FlexarrClient
import io.github.flexarr.core.LiveChannel
import io.github.flexarr.core.LiveGuide
import io.github.flexarr.core.LiveProgramme
import io.github.flexarr.core.liveChannels
import io.github.flexarr.core.liveGuide
import io.github.flexarr.core.record
import io.github.flexarr.core.recordSeries
import io.github.flexarr.core.recordings
import io.github.flexarr.core.removeRecording
import io.github.flexarr.core.removeSeries
import io.github.flexarr.core.setFavouriteChannel
import io.github.flexarr.player.LiveGold
import io.github.flexarr.player.LiveTvController
import io.github.flexarr.ui.components.Edge
import io.github.flexarr.ui.components.LocalClient
import io.github.flexarr.ui.components.LocalNavigator
import io.github.flexarr.ui.components.PageTitle
import io.github.flexarr.ui.components.StatusView
import io.github.flexarr.ui.components.pressable
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.RoomBackground
import io.github.flexarr.ui.components.RepeatWhileShowing
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.launch
import java.time.Instant
import java.time.temporal.ChronoUnit

/** The red of a programme set to record. */
internal val RecordRed = Color(0xFFE6474D)

private sealed interface Filter {
    data object Favourites : Filter
    data object All : Filter
    data class Group(val name: String) : Filter
}

/**
 * Live TV on the phone: the channels with what's on now (and how far through) and what's next, or the guide (six
 * hours across). Favourites first, and shown on their own when there are any; tap to watch, hold for Watch, Record
 * and Favourites. As the iPhone's LiveTvView.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun LiveTvScreen(model: AppModel) {
    val client = model.client ?: return
    val navigator = LocalNavigator.current
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var channels by remember { mutableStateOf<List<LiveChannel>?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    var refreshing by remember { mutableStateOf(false) }
    // Unchosen, the view is Favourites when there are some, otherwise All.
    var chosen by remember { mutableStateOf<Filter?>(null) }
    // The list or the guide, remembered on this device.
    var showsGuide by remember { mutableStateOf(context.settings.getBoolean(GUIDE_KEY, false)) }
    var notice by remember { mutableStateOf<String?>(null) }

    suspend fun load() {
        try {
            channels = client.liveChannels()
            error = null
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            if (channels == null) error = e.message
        }
    }
    // What's on moves on: fetched again each minute while the screen is up.
    RepeatWhileShowing(60_000, model.credentials) { load() }

    val all = channels.orEmpty()
    val favourites = all.filter { it.isFavourite }
    val filter = chosen ?: if (favourites.isEmpty()) Filter.All else Filter.Favourites
    val groups = all.mapNotNull { it.groupName }.distinct()
    val shown = when (val f = filter) {
        Filter.Favourites -> favourites
        Filter.All -> all
        is Filter.Group -> all.filter { it.groupName == f.name }
    }
    // The order channel up and down go through: favourites first, then the rest by number.
    val order = favourites + all.filterNot { it.isFavourite }

    fun setFavourite(channel: LiveChannel, favourite: Boolean) {
        channels = channels?.map { if (it.id == channel.id) it.copy(isFavourite = favourite) else it }
        scope.launch {
            runCatching { client.setFavouriteChannel(channel.id, favourite) }.onFailure {
                channels = channels?.map { if (it.id == channel.id) it.copy(isFavourite = !favourite) else it }
            }
        }
    }

    fun record(programme: LiveProgramme) {
        scope.launch {
            notice = try {
                val recording = client.record(programme.id)
                if (recording.clashes) {
                    "${programme.title} clashes with recordings already on the list: every stream is wanted then, so it won’t record unless one of them comes off."
                } else {
                    "${programme.title} will be recorded. Recordings, at the top, lists what’s set."
                }
            } catch (e: Exception) {
                e.message
            }
        }
    }

    RoomBackground(ambient = null) {
        Column {
            PageTitle("Live TV", onBack = { navigator.back() }) {
                if (all.isNotEmpty()) {
                    IconButton(onClick = navigator::openRecordings) { Icon(Icons.Filled.RadioButtonChecked, contentDescription = "Recordings", tint = Flexarr.screen) }
                    IconButton(onClick = {
                        showsGuide = !showsGuide
                        context.settings.edit().putBoolean(GUIDE_KEY, showsGuide).apply()
                    }) {
                        Icon(if (showsGuide) Icons.AutoMirrored.Filled.List else Icons.Filled.CalendarMonth, contentDescription = if (showsGuide) "List" else "Guide", tint = Flexarr.screen)
                    }
                }
            }
            val loaded = channels
            when {
                loaded == null && error != null -> StatusView(error, retry = { scope.launch { load() } })
                loaded == null -> StatusView()
                loaded.isEmpty() -> StatusView("No channels yet. Dispatcharr gives Flexarr the channels it has for Flexarr’s user.", empty = true)
                else -> {
                    Chips(filter, groups, onChoose = { chosen = it })
                    if (showsGuide) {
                        LiveGuideView(client, shown, order, model, onFavourite = { setFavourite(it, !it.isFavourite) }, onNotice = { notice = it })
                    } else {
                        PullToRefreshBox(isRefreshing = refreshing, onRefresh = { refreshing = true; scope.launch { load(); refreshing = false } }) {
                            LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 24.dp)) {
                                items(shown, key = { it.id }) { channel ->
                                    ChannelRow(
                                        channel,
                                        starting = model.liveStartingChannel == channel.id,
                                        onWatch = { navigator.watch(channel, order) },
                                        onRecord = ::record,
                                        onFavourite = { setFavourite(channel, !channel.isFavourite) },
                                    )
                                }
                                if (favourites.isEmpty()) {
                                    item {
                                        Row(Modifier.padding(horizontal = Edge, vertical = 16.dp), horizontalArrangement = Arrangement.spacedBy(8.dp), verticalAlignment = Alignment.CenterVertically) {
                                            Icon(Icons.Outlined.StarOutline, contentDescription = null, tint = Flexarr.usher, modifier = Modifier.size(16.dp))
                                            Text("Hold the channels you watch most to keep them first.", fontSize = 13.sp, color = Flexarr.usher)
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
    }
    notice?.let { message ->
        AlertDialog(
            onDismissRequest = { notice = null },
            title = { Text("Recording") },
            text = { Text(message) },
            confirmButton = { TextButton(onClick = { notice = null }) { Text("OK") } },
            containerColor = Flexarr.seat,
        )
    }
}

private const val GUIDE_KEY = "liveTvGuide"

private val Context.settings get() = getSharedPreferences("settings", Context.MODE_PRIVATE)

@Composable
private fun Chips(filter: Filter, groups: List<String>, onChoose: (Filter) -> Unit) {
    LazyRow(contentPadding = PaddingValues(horizontal = Edge, vertical = 6.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        item { Chip("Favourites", filter == Filter.Favourites, star = true) { onChoose(Filter.Favourites) } }
        item { Chip("All", filter == Filter.All) { onChoose(Filter.All) } }
        if (groups.size > 1) items(groups) { name -> Chip(name, filter == Filter.Group(name)) { onChoose(Filter.Group(name)) } }
    }
}

@Composable
private fun Chip(title: String, selected: Boolean, star: Boolean = false, onClick: () -> Unit) {
    Row(
        Modifier.clip(CircleShape).border(1.dp, if (selected) Flexarr.screen else Flexarr.aisle, CircleShape).pressable(onClick)
            .padding(horizontal = 14.dp, vertical = 8.dp),
        horizontalArrangement = Arrangement.spacedBy(6.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        if (star) Icon(Icons.Filled.Star, contentDescription = null, tint = LiveGold, modifier = Modifier.size(16.dp))
        Text(title, fontSize = 14.sp, fontWeight = FontWeight.SemiBold, color = if (selected) Flexarr.screen else Flexarr.usher)
    }
}

/** A channel's logo on a dark plate, its initials while there's none. */
@Composable
internal fun ChannelLogo(channel: LiveChannel, width: Dp, starting: Boolean, modifier: Modifier = Modifier, textSize: Int = 18) {
    val client = LocalClient.current
    Box(modifier.size(width, width * 9 / 16).clip(RoundedCornerShape(8.dp)).background(Flexarr.seat), contentAlignment = Alignment.Center) {
        Text(channel.name.take(4), fontFamily = Flexarr.marquee, fontSize = textSize.sp, color = Flexarr.usher)
        AsyncImage(client.imageUrl(channel.logoUrl, 300), contentDescription = null, contentScale = ContentScale.Fit, modifier = Modifier.fillMaxSize().background(Flexarr.seat).padding(8.dp))
        if (starting) {
            Box(Modifier.fillMaxSize().background(Color.Black.copy(alpha = 0.45f)))
            CircularProgressIndicator(Modifier.size(22.dp), strokeWidth = 2.dp, color = Flexarr.screen)
        }
    }
}

@Composable
private fun ChannelRow(channel: LiveChannel, starting: Boolean, onWatch: () -> Unit, onRecord: (LiveProgramme) -> Unit, onFavourite: () -> Unit) {
    var menu by remember { mutableStateOf(false) }
    val favouriteLabel = if (channel.isFavourite) "Remove from Favourites" else "Add to Favourites"
    Box {
        Row(
            Modifier.fillMaxWidth().pressable(onWatch, onLongClick = { menu = true }).padding(horizontal = Edge, vertical = 6.dp)
                .semantics(mergeDescendants = true) { customActions = listOf(CustomAccessibilityAction(favouriteLabel) { onFavourite(); true }) },
            horizontalArrangement = Arrangement.spacedBy(14.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Box {
                ChannelLogo(channel, 104.dp, starting)
                if (channel.isFavourite) Icon(Icons.Filled.Star, contentDescription = "Favourite", tint = LiveGold, modifier = Modifier.align(Alignment.TopEnd).padding(5.dp).size(10.dp))
            }
            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                    Text(channel.numberText, fontSize = 12.sp, fontWeight = FontWeight.SemiBold, style = Tabular, color = Flexarr.usher)
                    Text(channel.name, fontSize = 12.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.usher, maxLines = 1, overflow = TextOverflow.Ellipsis)
                }
                Row(horizontalArrangement = Arrangement.spacedBy(6.dp), verticalAlignment = Alignment.CenterVertically) {
                    channel.now?.takeIf { it.isSetToRecord }?.let { RecordMark(it.isRecordingNow) }
                    Text(channel.now?.displayTitle ?: "No listings", fontSize = 16.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen, maxLines = 1, overflow = TextOverflow.Ellipsis)
                }
                channel.now?.let { now ->
                    LinearProgressIndicator(progress = { now.progress() }, modifier = Modifier.width(220.dp), color = Flexarr.silver, trackColor = Flexarr.aisle)
                }
                channel.next?.let { next ->
                    Row(horizontalArrangement = Arrangement.spacedBy(5.dp), verticalAlignment = Alignment.CenterVertically) {
                        Text("Next: ${LiveTvController.startTime(next)} ${next.title}", fontSize = 12.sp, color = Flexarr.usher, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f, fill = false))
                        if (next.isSetToRecord) RecordMark(false)
                    }
                }
            }
        }
        DropdownMenu(menu, onDismissRequest = { menu = false }, containerColor = Flexarr.seat) {
            DropdownMenuItem(text = { Text("Watch") }, onClick = { menu = false; onWatch() })
            listOfNotNull(channel.now, channel.next).forEach { programme ->
                DropdownMenuItem(text = { Text("Record ${programme.title}") }, onClick = { menu = false; onRecord(programme) })
            }
            DropdownMenuItem(text = { Text(favouriteLabel) }, onClick = { menu = false; onFavourite() })
        }
    }
}

/** On a programme set to record: a red dot; while it's recording, a red REC badge. */
@Composable
internal fun RecordMark(recordingNow: Boolean) {
    if (recordingNow) {
        Text(
            "REC",
            fontSize = 10.sp,
            fontWeight = FontWeight.Black,
            color = Color.White,
            modifier = Modifier.clip(CircleShape).background(RecordRed).padding(horizontal = 5.dp, vertical = 1.dp).semantics { contentDescription = "Recording now" },
        )
    } else {
        Box(Modifier.size(9.dp).clip(CircleShape).background(RecordRed).semantics { contentDescription = "Set to record" })
    }
}

// The guide

private const val GUIDE_HOURS = 6

/** Figures of one width, so times line up. */
private val Tabular = androidx.compose.ui.text.TextStyle(fontFeatureSettings = "tnum")

/**
 * The guide: the channels down the left, six hours across from the half hour before now, scrolled with a finger.
 * Tapping what's on now plays it; anything else shows what it is, with a way to watch that channel now and to record
 * it; holding a programme adds its channel to Favourites or takes it off. As the iPhone's LiveGuideView.
 */
@Composable
private fun LiveGuideView(
    client: FlexarrClient,
    channels: List<LiveChannel>,
    order: List<LiveChannel>,
    model: AppModel,
    onFavourite: (LiveChannel) -> Unit,
    onNotice: (String) -> Unit,
) {
    val navigator = LocalNavigator.current
    val scope = rememberCoroutineScope()
    var guide by remember { mutableStateOf<LiveGuide?>(null) }
    var now by remember { mutableStateOf(Instant.now()) }
    val windowStart = remember { halfHourBefore(Instant.now()) }
    val windowEnd = windowStart.plus(GUIDE_HOURS.toLong(), ChronoUnit.HOURS)
    var details by remember { mutableStateOf<Pair<LiveProgramme, LiveChannel>?>(null) }

    suspend fun load() {
        runCatching { client.liveGuide(Instant.now().minusSeconds(3600), GUIDE_HOURS + 2) }.onSuccess { guide = it }
    }
    RepeatWhileShowing(120_000) {
        load()
        now = Instant.now()
    }
    /** Does a recording action, then fetches the guide again for its marks; says why if it couldn't be done. */
    fun act(action: suspend () -> String?) {
        scope.launch {
            try {
                action()?.let(onNotice)
            } catch (e: Exception) {
                e.message?.let(onNotice)
            }
            load()
        }
    }

    val perMinute = 4.dp
    val channelWidth = 84.dp
    val rowHeight = 56.dp
    val width = perMinute * (GUIDE_HOURS * 60)
    val loaded = guide ?: return StatusView()
    val byChannel = loaded.channels.associateBy { it.id }
    // One sideways position for every row, so the times and all the channels scroll across together.
    val across = rememberScrollState()

    // Only the channels on screen are laid out: a long guide is hundreds of programmes, each with its menu.
    LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(vertical = 8.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
        item(key = "times") {
            // The channels stay put while the programmes scroll across.
            Row {
                Spacer(Modifier.width(channelWidth))
                Box(Modifier.weight(1f).horizontalScroll(across)) {
                    // The times along the top, every half hour.
                    Box(Modifier.height(30.dp).width(width + Edge)) {
                        repeat(GUIDE_HOURS * 2) { slot ->
                            Text(
                                LiveTvController.startTime(LiveProgramme(0, windowStart.plusSeconds(slot * 1800L), windowStart, "")),
                                fontSize = 12.sp,
                                fontWeight = FontWeight.SemiBold,
                                style = Tabular,
                                color = Flexarr.usher,
                                modifier = Modifier.offset(x = perMinute * (slot * 30) + 6.dp),
                            )
                        }
                    }
                }
            }
        }
        items(channels, key = { it.id }) { channel ->
            Row {
                Box(
                    Modifier.width(channelWidth).height(rowHeight).padding(horizontal = 6.dp).pressable({ navigator.watch(channel, order) })
                        .semantics(mergeDescendants = true) { contentDescription = "${channel.numberText} ${channel.name}" },
                    contentAlignment = Alignment.Center,
                ) {
                    ChannelLogo(channel, channelWidth - 12.dp, model.liveStartingChannel == channel.id, textSize = 14)
                    if (channel.isFavourite) Icon(Icons.Filled.Star, contentDescription = null, tint = LiveGold, modifier = Modifier.align(Alignment.TopEnd).padding(4.dp).size(9.dp))
                }
                Box(Modifier.weight(1f).horizontalScroll(across)) {
                    val programmes = byChannel[channel.id]?.programmes.orEmpty().filter { it.end > windowStart && it.start < windowEnd }
                    Box(Modifier.height(rowHeight).width(width + Edge)) {
                        Box(Modifier.height(rowHeight).width(width)) {
                            if (programmes.isEmpty()) Text("No listings", fontSize = 13.sp, color = Flexarr.usher, modifier = Modifier.align(Alignment.CenterStart).padding(start = 12.dp))
                            // Laid end to end, with a gap wherever the listings have one.
                            Row(Modifier.fillMaxHeight()) {
                                var at = windowStart
                                programmes.forEach { programme ->
                                    val start = maxOf(programme.start, windowStart, at)
                                    val end = minOf(programme.end, windowEnd)
                                    if (start > at) Spacer(Modifier.width(perMinute * ((start.epochSecond - at.epochSecond) / 60f)))
                                    val slot = perMinute * ((end.epochSecond - start.epochSecond).coerceAtLeast(0) / 60f)
                                    at = maxOf(end, start)
                                    val onNow = programme.start <= now && programme.end > now
                                    GuideCell(
                                        programme,
                                        onNow = onNow,
                                        past = programme.end <= now,
                                        narrow = slot < 59.dp,
                                        channel = channel,
                                        modifier = Modifier.width(slot).fillMaxHeight().padding(end = 3.dp),
                                        onTap = { if (onNow) navigator.watch(channel, order) else details = programme to channel },
                                        menu = { close ->
                                            if (onNow) DropdownMenuItem(text = { Text("Watch") }, onClick = { close(); navigator.watch(channel, order) })
                                            if (programme.end > now) {
                                                DropdownMenuItem(text = { Text(recordLabel(programme)) }, onClick = { close(); act { toggleRecording(client, programme, channel.id) } })
                                                DropdownMenuItem(text = { Text(seriesLabel(programme)) }, onClick = { close(); act { toggleSeries(client, programme, channel.id); null } })
                                            }
                                            DropdownMenuItem(
                                                text = { Text(if (channel.isFavourite) "Remove from Favourites" else "Add to Favourites") },
                                                onClick = { close(); onFavourite(channel) },
                                            )
                                        },
                                    )
                                }
                            }
                            // Now, as a line down the guide.
                            val offset = perMinute * ((now.epochSecond - windowStart.epochSecond) / 60f)
                            if (now >= windowStart && now < windowEnd) {
                                Box(Modifier.offset(x = offset).width(2.dp).fillMaxHeight().background(Flexarr.danger))
                            }
                        }
                    }
                }
            }
        }
    }

    details?.let { (programme, channel) ->
        AlertDialog(
            onDismissRequest = { details = null },
            title = { Text(programme.displayTitle) },
            text = { Text(describe(programme)) },
            confirmButton = {
                Column(horizontalAlignment = Alignment.End) {
                    TextButton(onClick = { details = null; navigator.watch(channel, order) }) { Text("Watch ${channel.name} now") }
                    if (programme.end > now) {
                        TextButton(onClick = { details = null; act { toggleRecording(client, programme, channel.id) } }) { Text(recordLabel(programme)) }
                        TextButton(onClick = { details = null; act { toggleSeries(client, programme, channel.id); null } }) { Text(seriesLabel(programme)) }
                    }
                    TextButton(onClick = { details = null }) { Text("Close") }
                }
            },
            containerColor = Flexarr.seat,
        )
    }
}

@Composable
private fun GuideCell(
    programme: LiveProgramme,
    onNow: Boolean,
    past: Boolean,
    narrow: Boolean,
    channel: LiveChannel,
    modifier: Modifier,
    onTap: () -> Unit,
    menu: @Composable (close: () -> Unit) -> Unit,
) {
    var open by remember { mutableStateOf(false) }
    val shape = RoundedCornerShape(8.dp)
    Box(
        modifier.alpha(if (past) 0.55f else 1f).clip(shape).background(Flexarr.seat.copy(alpha = if (onNow) 1f else 0.55f))
            .then(if (programme.isSetToRecord) Modifier.border(2.dp, RecordRed, shape) else Modifier)
            .pressable(onTap, onLongClick = { open = true })
            .semantics(mergeDescendants = true) {
                contentDescription = "${channel.name}, ${programme.displayTitle}, ${LiveTvController.startTime(programme)}" + if (programme.isSetToRecord) ", set to record" else ""
            },
    ) {
        if (onNow) Box(Modifier.padding(vertical = 10.dp).width(3.dp).fillMaxHeight().clip(RoundedCornerShape(2.dp)).background(Flexarr.silver))
        if (!narrow) {
            Column(Modifier.align(Alignment.CenterStart).padding(horizontal = 8.dp), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                Row(horizontalArrangement = Arrangement.spacedBy(4.dp), verticalAlignment = Alignment.CenterVertically) {
                    if (programme.isSetToRecord) RecordMark(programme.isRecordingNow)
                    Text(programme.displayTitle, fontSize = 13.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen, maxLines = 1, overflow = TextOverflow.Ellipsis)
                }
                Text(LiveTvController.timeRange(programme), fontSize = 11.sp, style = Tabular, color = Flexarr.usher, maxLines = 1)
            }
        }
        DropdownMenu(open, onDismissRequest = { open = false }, containerColor = Flexarr.seat) { menu { open = false } }
    }
}

private fun halfHourBefore(time: Instant): Instant = Instant.ofEpochSecond(time.epochSecond / 1800 * 1800)

private fun describe(programme: LiveProgramme): String = buildList {
    add(LiveTvController.timeRange(programme))
    programme.season?.let { season -> add(programme.episode?.let { "Series $season, episode $it" } ?: "Series $season") }
    if (programme.isNew) add("New")
    programme.description?.let(::add)
}.joinToString("\n")

// What the guide and the channel list can ask the DVR, as the iPhone's DvrActions.

internal fun recordLabel(programme: LiveProgramme) =
    if (programme.isRecordingNow) "Stop recording" else if (programme.isSetToRecord) "Don’t record" else "Record"

internal fun seriesLabel(programme: LiveProgramme) = if (programme.series == true) "Stop recording the series" else "Record series"

/** Sets the showing to record, or takes it off the list (stopping it, if it's recording now). Says so if it clashes. */
internal suspend fun toggleRecording(client: FlexarrClient, programme: LiveProgramme, channelId: String): String? {
    if (programme.isSetToRecord) {
        client.recordings().recording(programme, channelId)?.let { client.removeRecording(it.id) }
        return null
    }
    val recording = client.record(programme.id)
    return if (recording.clashes) {
        "${programme.title} clashes with recordings already on the list: every stream is wanted then, so it won’t record unless one of them comes off."
    } else {
        null
    }
}

/** Records every showing of the title on this channel, or stops the series covering it. */
internal suspend fun toggleSeries(client: FlexarrClient, programme: LiveProgramme, channelId: String) {
    if (programme.series == true) {
        client.recordings().series.firstOrNull { it.covers(programme, channelId) }?.let { client.removeSeries(it.id) }
        return
    }
    client.recordSeries(programme.id)
}

/** A favourite channel on Home: its logo on a dark plate with how far through what's on, and what's on beneath. */
@Composable
fun HomeChannelCard(channel: LiveChannel, order: List<LiveChannel>, width: Dp = 200.dp) {
    val navigator = LocalNavigator.current
    val model = LocalContext.current.appModel
    Column(Modifier.width(width).pressable({ navigator.watch(channel, order) }), verticalArrangement = Arrangement.spacedBy(6.dp)) {
        Box {
            ChannelLogo(channel, width, starting = model.liveStartingChannel == channel.id, textSize = 22)
            Text(channel.numberText, fontSize = 12.sp, fontWeight = FontWeight.Bold, style = Tabular, color = Flexarr.usher, modifier = Modifier.padding(8.dp))
            channel.now?.let { now ->
                LinearProgressIndicator(progress = { now.progress() }, modifier = Modifier.align(Alignment.BottomCenter).fillMaxWidth().padding(10.dp), color = Flexarr.silver, trackColor = Flexarr.aisle)
            }
        }
        Text(channel.now?.displayTitle ?: channel.name, fontSize = 14.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen, maxLines = 1, overflow = TextOverflow.Ellipsis)
        Text(channel.name, fontSize = 12.sp, color = Flexarr.usher, maxLines = 1, overflow = TextOverflow.Ellipsis)
    }
}
