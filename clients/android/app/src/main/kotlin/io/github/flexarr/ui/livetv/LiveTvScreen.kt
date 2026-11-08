package io.github.flexarr.ui.livetv

import io.github.flexarr.core.i18n.Words
import io.github.flexarr.core.i18n.t
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
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
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
import io.github.flexarr.core.GuideFilter
import io.github.flexarr.core.liveGuide
import io.github.flexarr.core.liveProgramme
import io.github.flexarr.core.record
import io.github.flexarr.core.recordSeries
import io.github.flexarr.core.recordings
import io.github.flexarr.core.removeRecording
import io.github.flexarr.core.removeSeries
import io.github.flexarr.core.setFavouriteChannel
import io.github.flexarr.player.LiveStar
import io.github.flexarr.player.LiveTvController
import io.github.flexarr.ui.components.Edge
import io.github.flexarr.ui.components.LocalClient
import io.github.flexarr.ui.components.LocalNavigator
import io.github.flexarr.ui.components.PageTitle
import io.github.flexarr.ui.components.StatusView
import io.github.flexarr.ui.components.pressable
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.FlexarrIcons
import io.github.flexarr.ui.theme.RoomBackground
import io.github.flexarr.ui.components.RepeatWhileShowing
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import java.time.Instant
import java.time.temporal.ChronoUnit
import io.github.flexarr.ui.theme.Tokens

/** The red of a programme set to record. */
internal val RecordRed = Flexarr.record

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
    // Counts the refreshes finished, so the guide shown is fetched again.
    var refreshed by remember { mutableIntStateOf(0) }
    // Recordings have no age to check, so a profile with an age limit has none.
    val records = model.currentProfile?.hasAgeLimit != true

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
    // What's on moves on: fetched again each minute while the list is up. Under the guide, which has every programme
    // itself, the list is only the channels and their stars: fetched every five minutes, and at once on going back to
    // the list.
    RepeatWhileShowing(if (showsGuide) GUIDE_REFRESH_MS else 60_000, model.credentials, showsGuide) {
        load()
        model.loadLiveTvStatus()
        // No channels yet (the server couldn't be reached): tried again each minute, as it was, not left five.
        while (showsGuide && channels == null) {
            delay(60_000)
            load()
        }
    }

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
                    t("{title} clashes with recordings already on the list: every stream is wanted then, so it won’t record unless one of them comes off.", "title" to programme.title)
                } else {
                    t("{title} will be recorded. Recordings, at the top, lists what’s set.", "title" to programme.title)
                }
            } catch (e: Exception) {
                e.message
            }
        }
    }

    RoomBackground(ambient = null) {
        Column {
            PageTitle(t(Words.liveTv), onBack = { navigator.back() }) {
                if (all.isNotEmpty()) {
                    if (model.isMainProfile) {
                        IconButton(
                            enabled = !model.isRefreshingLiveTv,
                            onClick = {
                                scope.launch {
                                    notice = model.refreshLiveTv()
                                    load()
                                    refreshed++
                                }
                            },
                        ) {
                            if (model.isRefreshingLiveTv) {
                                CircularProgressIndicator(Modifier.size(20.dp), color = Flexarr.screen, strokeWidth = 2.dp)
                            } else {
                                Icon(FlexarrIcons.refresh, contentDescription = t("Refresh guide"), tint = Flexarr.screen)
                            }
                        }
                    }
                    if (records) IconButton(onClick = navigator::openRecordings) { Icon(Icons.Filled.RadioButtonChecked, contentDescription = t("Recordings"), tint = Flexarr.screen) }
                    IconButton(onClick = {
                        showsGuide = !showsGuide
                        context.settings.edit().putBoolean(GUIDE_KEY, showsGuide).apply()
                    }) {
                        Icon(if (showsGuide) Icons.AutoMirrored.Filled.List else Icons.Filled.CalendarMonth, contentDescription = if (showsGuide) t("List") else t("Guide"), tint = Flexarr.screen)
                    }
                }
            }
            val loaded = channels
            when {
                loaded == null && error != null -> StatusView(error, retry = { scope.launch { load() } })
                loaded == null -> StatusView()
                loaded.isEmpty() -> StatusView(t("No channels yet. Dispatcharr gives Flexarr the channels it has for Flexarr’s user."), empty = true)
                else -> {
                    Chips(filter, groups, onChoose = { chosen = it })
                    val missing = model.liveTvStatus?.channelsWithoutListings ?: 0
                    if (model.isMainProfile && missing > 0) {
                        Text(
                            t("{count, plural, one {# channel has no listings} other {# channels have no listings}}", "count" to missing),
                            fontSize = 13.sp,
                            color = Flexarr.usher,
                            modifier = Modifier.padding(horizontal = Edge, vertical = 4.dp),
                        )
                    }
                    if (showsGuide) {
                        LiveGuideView(client, shown, order, filter == Filter.Favourites, model, refreshed, onFavourite = { setFavourite(it, !it.isFavourite) }, onNotice = { notice = it })
                    } else {
                        PullToRefreshBox(isRefreshing = refreshing, onRefresh = { refreshing = true; scope.launch { load(); refreshing = false } }) {
                            LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 24.dp)) {
                                items(shown, key = { it.id }) { channel ->
                                    ChannelRow(
                                        channel,
                                        starting = model.liveStartingChannel == channel.id,
                                        onWatch = { navigator.watch(channel, order) },
                                        onRecord = if (records) ::record else null,
                                        onFavourite = { setFavourite(channel, !channel.isFavourite) },
                                    )
                                }
                                if (favourites.isEmpty()) {
                                    item {
                                        Row(Modifier.padding(horizontal = Edge, vertical = 16.dp), horizontalArrangement = Arrangement.spacedBy(8.dp), verticalAlignment = Alignment.CenterVertically) {
                                            Icon(Icons.Outlined.StarOutline, contentDescription = null, tint = Flexarr.usher, modifier = Modifier.size(16.dp))
                                            Text(t("Hold the channels you watch most to keep them first."), fontSize = 13.sp, color = Flexarr.usher)
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
            title = { Text(t(Words.liveTv)) },
            text = { Text(message) },
            confirmButton = { TextButton(onClick = { notice = null }) { Text(t("OK")) } },
            containerColor = Flexarr.seat,
        )
    }
}

private const val GUIDE_KEY = "liveTvGuide"

private val Context.settings get() = getSharedPreferences("settings", Context.MODE_PRIVATE)

@Composable
private fun Chips(filter: Filter, groups: List<String>, onChoose: (Filter) -> Unit) {
    LazyRow(contentPadding = PaddingValues(horizontal = Edge, vertical = 6.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        item { Chip(t("Favourites"), filter == Filter.Favourites, star = true) { onChoose(Filter.Favourites) } }
        item { Chip(t("All"), filter == Filter.All) { onChoose(Filter.All) } }
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
        if (star) Icon(Icons.Filled.Star, contentDescription = null, tint = LiveStar, modifier = Modifier.size(16.dp))
        Text(title, fontSize = 14.sp, fontWeight = FontWeight.SemiBold, color = if (selected) Flexarr.screen else Flexarr.usher)
    }
}

/** A channel's logo on a dark plate, its initials while there's none. */
@Composable
internal fun ChannelLogo(channel: LiveChannel, width: Dp, starting: Boolean, modifier: Modifier = Modifier, textSize: Int = 18) {
    val client = LocalClient.current
    Box(modifier.size(width, width * 9 / 16).clip(RoundedCornerShape(Tokens.radiusArt)).background(Flexarr.seat), contentAlignment = Alignment.Center) {
        Text(channel.name.take(4), fontFamily = Flexarr.marquee, fontSize = textSize.sp, color = Flexarr.usher)
        AsyncImage(client.imageUrl(channel.logoUrl, 300), contentDescription = null, contentScale = ContentScale.Fit, modifier = Modifier.fillMaxSize().background(Flexarr.seat).padding(8.dp))
        if (starting) {
            Box(Modifier.fillMaxSize().background(Color.Black.copy(alpha = 0.45f)))
            CircularProgressIndicator(Modifier.size(22.dp), strokeWidth = 2.dp, color = Flexarr.screen)
        }
    }
}

@Composable
private fun ChannelRow(channel: LiveChannel, starting: Boolean, onWatch: () -> Unit, onRecord: ((LiveProgramme) -> Unit)?, onFavourite: () -> Unit) {
    var menu by remember { mutableStateOf(false) }
    val favouriteLabel = if (channel.isFavourite) t("Remove from favourites") else t("Add to favourites")
    Box {
        Row(
            Modifier.fillMaxWidth().pressable(onWatch, onLongClick = { menu = true }).padding(horizontal = Edge, vertical = 6.dp)
                .semantics(mergeDescendants = true) { customActions = listOf(CustomAccessibilityAction(favouriteLabel) { onFavourite(); true }) },
            horizontalArrangement = Arrangement.spacedBy(14.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Box {
                ChannelLogo(channel, 104.dp, starting)
                if (channel.isFavourite) Icon(Icons.Filled.Star, contentDescription = t("Favourite"), tint = LiveStar, modifier = Modifier.align(Alignment.TopEnd).padding(5.dp).size(10.dp))
            }
            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                    Text(channel.numberText, fontSize = 12.sp, fontWeight = FontWeight.SemiBold, style = Tabular, color = Flexarr.usher)
                    Text(channel.name, fontSize = 12.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.usher, maxLines = 1, overflow = TextOverflow.Ellipsis)
                }
                Row(horizontalArrangement = Arrangement.spacedBy(6.dp), verticalAlignment = Alignment.CenterVertically) {
                    channel.now?.takeIf { it.isSetToRecord }?.let { RecordMark(it.isRecordingNow) }
                    Text(channel.now?.displayTitle ?: t("No listings"), fontSize = 16.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen, maxLines = 1, overflow = TextOverflow.Ellipsis)
                }
                channel.now?.let { now ->
                    LinearProgressIndicator(progress = { now.progress() }, modifier = Modifier.width(220.dp), color = Flexarr.silver, trackColor = Flexarr.aisle)
                }
                channel.next?.let { next ->
                    Row(horizontalArrangement = Arrangement.spacedBy(5.dp), verticalAlignment = Alignment.CenterVertically) {
                        Text(t("Next: {time} {title}", "time" to LiveTvController.startTime(next), "title" to next.title), fontSize = 12.sp, color = Flexarr.usher, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f, fill = false))
                        if (next.isSetToRecord) RecordMark(false)
                    }
                }
            }
        }
        DropdownMenu(menu, onDismissRequest = { menu = false }, containerColor = Flexarr.seat) {
            DropdownMenuItem(text = { Text(t("Watch")) }, onClick = { menu = false; onWatch() })
            if (onRecord != null) listOfNotNull(channel.now, channel.next).forEach { programme ->
                DropdownMenuItem(text = { Text(t("Record {title}", "title" to programme.title)) }, onClick = { menu = false; onRecord(programme) })
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
            t("REC"),
            fontSize = 10.sp,
            fontWeight = FontWeight.Black,
            color = Color.White,
            modifier = Modifier.clip(CircleShape).background(RecordRed).padding(horizontal = 5.dp, vertical = 1.dp).semantics { contentDescription = t("Recording now") },
        )
    } else {
        Box(Modifier.size(9.dp).clip(CircleShape).background(RecordRed).semantics { contentDescription = t("Set to record") })
    }
}

// The guide

private const val GUIDE_HOURS = 6

/**
 * How often the guide is fetched again while it's up: every five minutes, as on the TV. It's the programmes of the
 * channels shown for the hours on screen, without descriptions (a programme's comes when it's opened), and changes only
 * when the server reads its source again or a recording is set elsewhere; one set here fetches it at once. Asked again
 * with its ETag, an unchanged guide isn't sent again.
 */
private const val GUIDE_REFRESH_MS = 300_000L

/** Until the guide has come once, it's asked for again this often. */
private const val GUIDE_RETRY_MS = 120_000L

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
    favouritesOnly: Boolean,
    model: AppModel,
    refreshed: Int,
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
    // Recordings have no age to check, so a profile with an age limit has none.
    val records = model.currentProfile?.hasAgeLimit != true

    // Only the channels shown ([order] is every channel): a big line-up isn't sent whole for a few favourites.
    val ask = remember(channels, order, favouritesOnly) { GuideFilter.forShown(channels, order, favouritesOnly) }
    // What the guide shown was asked for, and the ETag it came with.
    var guideAsk by remember { mutableStateOf<GuideFilter?>(null) }
    var etag by remember { mutableStateOf<String?>(null) }

    suspend fun load() {
        val asking = ask
        runCatching {
            client.liveGuide(windowStart, GUIDE_HOURS, asking, descriptions = false, etag = etag.takeIf { guide != null && guideAsk == asking })
        }.onSuccess { fetched ->
            // Null when it hadn't changed: the guide shown stands.
            fetched.guide?.let { guide = it }
            etag = fetched.etag
            guideAsk = asking
        }
    }
    RepeatWhileShowing(GUIDE_REFRESH_MS, ask, refreshed) {
        load()
        // No guide yet for what's shown (the server couldn't be reached, or it took too long to come): tried again
        // every two minutes, as it was, rather than left loading for five.
        while (guide == null || guideAsk != ask) {
            delay(GUIDE_RETRY_MS)
            load()
        }
    }
    // The line for now, and what's on, move on each minute without fetching anything.
    RepeatWhileShowing(60_000) { now = Instant.now() }
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
    // Another view chosen, its channels not in the guide yet: loading, rather than "No listings" for each.
    if (guideAsk != ask && channels.any { it.id !in byChannel }) return StatusView()
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
                    if (channel.isFavourite) Icon(Icons.Filled.Star, contentDescription = null, tint = LiveStar, modifier = Modifier.align(Alignment.TopEnd).padding(4.dp).size(9.dp))
                }
                Box(Modifier.weight(1f).horizontalScroll(across)) {
                    val programmes = byChannel[channel.id]?.programmes.orEmpty().filter { it.end > windowStart && it.start < windowEnd }
                    Box(Modifier.height(rowHeight).width(width + Edge)) {
                        Box(Modifier.height(rowHeight).width(width)) {
                            if (programmes.isEmpty()) Text(t("No listings"), fontSize = 13.sp, color = Flexarr.usher, modifier = Modifier.align(Alignment.CenterStart).padding(start = 12.dp))
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
                                            if (onNow) DropdownMenuItem(text = { Text(t("Watch")) }, onClick = { close(); navigator.watch(channel, order) })
                                            if (records && programme.end > now) {
                                                DropdownMenuItem(text = { Text(recordLabel(programme)) }, onClick = { close(); act { toggleRecording(client, programme, channel.id) } })
                                                DropdownMenuItem(text = { Text(seriesLabel(programme)) }, onClick = { close(); act { toggleSeries(client, programme, channel.id); null } })
                                            }
                                            DropdownMenuItem(
                                                text = { Text(if (channel.isFavourite) t("Remove from favourites") else t("Add to favourites")) },
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
        val description = rememberDescription(client, programme)
        AlertDialog(
            onDismissRequest = { details = null },
            title = { Text(programme.displayTitle) },
            text = { Text(describe(programme.copy(description = description))) },
            confirmButton = {
                Column(horizontalAlignment = Alignment.End) {
                    TextButton(onClick = { details = null; navigator.watch(channel, order) }) { Text(t("Watch {channel} now", "channel" to channel.name)) }
                    if (records && programme.end > now) {
                        TextButton(onClick = { details = null; act { toggleRecording(client, programme, channel.id) } }) { Text(recordLabel(programme)) }
                        TextButton(onClick = { details = null; act { toggleSeries(client, programme, channel.id); null } }) { Text(seriesLabel(programme)) }
                    }
                    TextButton(onClick = { details = null }) { Text(t("Close")) }
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
    val shape = RoundedCornerShape(Tokens.radiusControl)
    Box(
        modifier.alpha(if (past) 0.55f else 1f).clip(shape).background(Flexarr.seat.copy(alpha = if (onNow) 1f else 0.55f))
            .then(if (programme.isSetToRecord) Modifier.border(2.dp, RecordRed, shape) else Modifier)
            .pressable(onTap, onLongClick = { open = true })
            .semantics(mergeDescendants = true) {
                contentDescription = if (programme.isSetToRecord) t("{channel}, {programme}, {time}, set to record", "channel" to channel.name, "programme" to programme.displayTitle, "time" to LiveTvController.startTime(programme))
                    else t("{channel}, {programme}, {time}", "channel" to channel.name, "programme" to programme.displayTitle, "time" to LiveTvController.startTime(programme))
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

/**
 * [programme]'s description: as the guide gave it, or (from a guide fetched without them) asked for once it's shown.
 * Null until it comes, and when there's none.
 */
@Composable
internal fun rememberDescription(client: FlexarrClient, programme: LiveProgramme): String? {
    var fetched by remember(programme.id) { mutableStateOf<String?>(null) }
    LaunchedEffect(programme.id) {
        if (programme.description == null) fetched = runCatching { client.liveProgramme(programme.id)?.description }.getOrNull()
    }
    return programme.description ?: fetched
}

private fun halfHourBefore(time: Instant): Instant = Instant.ofEpochSecond(time.epochSecond / 1800 * 1800)

private fun describe(programme: LiveProgramme): String = buildList {
    add(LiveTvController.timeRange(programme))
    programme.season?.let { season -> add(programme.episode?.let { t("Series {season}, episode {episode}", "season" to season, "episode" to it) } ?: t("Series {season}", "season" to season)) }
    if (programme.isNew) add(t("New"))
    programme.description?.let(::add)
}.joinToString("\n")

// What the guide and the channel list can ask the DVR, as the iPhone's DvrActions.

internal fun recordLabel(programme: LiveProgramme) =
    if (programme.isRecordingNow) t("Stop recording") else if (programme.isSetToRecord) t("Don’t record") else t("Record")

internal fun seriesLabel(programme: LiveProgramme) = if (programme.series == true) t("Stop recording the series") else t("Record series")

/** Sets the showing to record, or takes it off the list (stopping it, if it's recording now). Says so if it clashes. */
internal suspend fun toggleRecording(client: FlexarrClient, programme: LiveProgramme, channelId: String): String? {
    if (programme.isSetToRecord) {
        client.recordings().recording(programme, channelId)?.let { client.removeRecording(it.id) }
        return null
    }
    val recording = client.record(programme.id)
    return if (recording.clashes) {
        t("{title} clashes with recordings already on the list: every stream is wanted then, so it won’t record unless one of them comes off.", "title" to programme.title)
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
