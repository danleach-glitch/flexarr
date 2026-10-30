package io.github.flexarr.ui.tv

import android.text.format.DateFormat
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsFocusedAsState
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
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
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.GridView
import androidx.compose.material.icons.filled.RadioButtonChecked
import androidx.compose.material.icons.filled.Star
import androidx.compose.material.icons.outlined.StarOutline
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.focus.onFocusChanged
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.key.Key
import androidx.compose.ui.input.key.KeyEventType
import androidx.compose.ui.input.key.key
import androidx.compose.ui.input.key.onPreviewKeyEvent
import androidx.compose.ui.input.key.type
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.window.Dialog
import androidx.tv.material3.ClickableSurfaceDefaults
import androidx.tv.material3.Icon
import androidx.tv.material3.Surface
import androidx.tv.material3.Text
import coil3.compose.AsyncImage
import io.github.flexarr.AppModel
import io.github.flexarr.core.FlexarrClient
import io.github.flexarr.core.LiveChannel
import io.github.flexarr.core.LiveGuide
import io.github.flexarr.core.LiveProgramme
import io.github.flexarr.core.Recording
import io.github.flexarr.core.RecordingSeries
import io.github.flexarr.core.liveChannels
import io.github.flexarr.core.liveGuide
import io.github.flexarr.core.recordings
import io.github.flexarr.core.removeRecording
import io.github.flexarr.core.removeSeries
import io.github.flexarr.core.setFavouriteChannel
import io.github.flexarr.player.LiveTvController
import io.github.flexarr.ui.components.LocalClient
import io.github.flexarr.ui.components.LocalNavigator
import io.github.flexarr.ui.components.MenuAction
import io.github.flexarr.ui.components.RepeatWhileShowing
import io.github.flexarr.ui.livetv.recordLabel
import io.github.flexarr.ui.livetv.seriesLabel
import io.github.flexarr.ui.livetv.toggleRecording
import io.github.flexarr.ui.livetv.toggleSeries
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.RoomBackground
import io.github.flexarr.ui.theme.marquee
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import java.time.Duration
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.time.format.TextStyle
import java.util.Locale

/** A list down Live TV's left: Favourites, Recordings, every channel, or one of the source's groups. */
private sealed interface Category {
    data object Favourites : Category
    data object Recordings : Category
    data object All : Category
    data class Group(val name: String) : Category
}

/** Favourites' star and the row's gold. */
private val Gold = Color(0xFFF2C473)

/** A programme set to record. */
private val RecordRed = Color(0xFFE6474D)

/**
 * Live TV, as the Apple TV's: categories down the left (Favourites, Recordings, All, the source's groups) and the guide
 * on the right, three hours across for the channels in the category. Moving onto a category shows it; choosing what's on
 * now plays it; holding a programme adds its channel to Favourites or takes it off, or records it. Favourites is where
 * it opens when there are any. [takeFocus]: arriving from the sidebar, the first category.
 */
@Composable
fun TvLiveTvScreen(model: AppModel, takeFocus: Boolean, onFocusTaken: () -> Unit) {
    val client = model.client ?: return
    val navigator = LocalNavigator.current
    val scope = rememberCoroutineScope()
    var channels by remember { mutableStateOf<List<LiveChannel>?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    var guide by remember { mutableStateOf<LiveGuide?>(null) }
    var upcoming by remember { mutableStateOf<Int?>(null) }
    var chosen by remember { mutableStateOf<Category?>(null) }
    val firstCategory = remember { FocusRequester() }

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
    suspend fun loadGuide() {
        upcoming = runCatching { client.recordings().upcoming.size }.getOrNull() ?: upcoming
        runCatching { client.liveGuide(Instant.now().minusSeconds(3600), 24) }.onSuccess { guide = it }
    }
    // What's on moves on: fetched again each minute while the screen is up; the guide (a day's worth) every five.
    RepeatWhileShowing(60_000, model.credentials) { load() }
    RepeatWhileShowing(300_000, model.credentials) { loadGuide() }

    val all = channels.orEmpty()
    val favourites = all.filter { it.isFavourite }
    // Where the screen opens: Favourites when there are any, otherwise All channels.
    val category = chosen ?: if (favourites.isEmpty()) Category.All else Category.Favourites
    val groups = all.mapNotNull { it.groupName }.distinct()
    fun channelsIn(category: Category) = when (category) {
        Category.Favourites -> favourites
        Category.All -> all
        is Category.Group -> all.filter { it.groupName == category.name }
        Category.Recordings -> emptyList()
    }

    fun setFavourite(channel: LiveChannel, favourite: Boolean) {
        channels = channels?.map { if (it.id == channel.id) it.copy(isFavourite = favourite) else it }
        scope.launch {
            runCatching { client.setFavouriteChannel(channel.id, favourite) }.onFailure {
                channels = channels?.map { if (it.id == channel.id) it.copy(isFavourite = !favourite) else it }
            }
        }
    }

    LaunchedEffect(takeFocus, channels != null) {
        if (!takeFocus || channels == null) return@LaunchedEffect
        delay(50)
        runCatching { firstCategory.requestFocus() }
        onFocusTaken()
    }

    RoomBackground(ambient = null) {
        val loaded = channels
        when {
            loaded == null && error != null -> TvStatusView(error, retry = { scope.launch { load() } })
            loaded == null -> TvStatusView()
            loaded.isEmpty() -> TvStatusView("No channels yet. Dispatcharr gives Flexarr the channels it has for Flexarr’s user.", empty = true)
            else -> Row(Modifier.fillMaxSize().padding(start = TvLayout.Edge, top = 15.dp), horizontalArrangement = Arrangement.spacedBy(25.dp)) {
                LazyColumn(Modifier.width(200.dp).fillMaxHeight(), verticalArrangement = Arrangement.spacedBy(5.dp), contentPadding = PaddingValues(bottom = 30.dp)) {
                    val entries = listOf<Pair<Category, String>>(Category.Favourites to "Favourites", Category.Recordings to "Recordings", Category.All to "All channels") +
                        (if (groups.size > 1) groups.map { Category.Group(it) to it } else emptyList())
                    items(entries, key = { it.second + it.first::class.simpleName }) { (value, title) ->
                        CategoryButton(
                            title,
                            selected = category == value,
                            icon = when (value) {
                                Category.Favourites -> Icons.Filled.Star
                                Category.Recordings -> Icons.Filled.RadioButtonChecked
                                Category.All -> Icons.Filled.GridView
                                else -> null
                            },
                            count = if (value == Category.Recordings) upcoming else channelsIn(value).size,
                            onFocus = { chosen = value },
                            modifier = if (value == Category.Favourites) Modifier.focusRequester(firstCategory) else Modifier,
                        )
                    }
                }
                Box(Modifier.weight(1f)) {
                    when {
                        category == Category.Recordings -> TvLiveRecordings(model, onChanged = { scope.launch { loadGuide() } }, onUpcoming = { upcoming = it })
                        category == Category.Favourites && favourites.isEmpty() -> NoFavourites()
                        else -> TvLiveGuideGrid(
                            client,
                            channelsIn(category),
                            guide,
                            onWatch = { navigator.watch(it, channelsIn(category)) },
                            onFavourite = { setFavourite(it, !it.isFavourite) },
                            onRecordingsChanged = { scope.launch { loadGuide() } },
                        )
                    }
                }
            }
        }
    }
}

/** A category: filled when focused, marked when it's the one shown. Moving onto one shows it. */
@Composable
private fun CategoryButton(title: String, selected: Boolean, icon: androidx.compose.ui.graphics.vector.ImageVector?, count: Int?, onFocus: () -> Unit, modifier: Modifier) {
    val source = remember { MutableInteractionSource() }
    val focused by source.collectIsFocusedAsState()
    Surface(
        onClick = onFocus,
        interactionSource = source,
        modifier = modifier.fillMaxWidth().height(35.dp).onFocusChanged { if (it.isFocused) onFocus() },
        shape = ClickableSurfaceDefaults.shape(RoundedCornerShape(7.dp)),
        colors = ClickableSurfaceDefaults.colors(
            containerColor = if (selected) Flexarr.screen.copy(alpha = 0.12f) else Color.Transparent,
            contentColor = if (selected) Flexarr.screen else Flexarr.usher,
            focusedContainerColor = Flexarr.screen,
            focusedContentColor = Flexarr.room,
        ),
        scale = ClickableSurfaceDefaults.scale(focusedScale = 1.04f),
    ) {
        Row(Modifier.fillMaxSize().padding(horizontal = 11.dp), horizontalArrangement = Arrangement.spacedBy(7.dp), verticalAlignment = Alignment.CenterVertically) {
            Box(Modifier.width(16.dp)) {
                if (icon != null) Icon(icon, contentDescription = null, tint = if (icon == Icons.Filled.Star && !focused) Gold else androidx.tv.material3.LocalContentColor.current, modifier = Modifier.size(15.dp))
            }
            Text(title, fontSize = 14.5.sp, fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f))
            count?.let { Text("%,d".format(it), fontSize = 11.sp, fontWeight = FontWeight.SemiBold, modifier = Modifier.alpha(0.6f)) }
        }
    }
}

@Composable
private fun NoFavourites() {
    Column(Modifier.widthIn(max = 450.dp).padding(top = 30.dp), verticalArrangement = Arrangement.spacedBy(7.dp)) {
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalAlignment = Alignment.CenterVertically) {
            Icon(Icons.Outlined.StarOutline, contentDescription = null, modifier = Modifier.size(17.dp))
            Text("No favourites yet", fontSize = 17.sp, fontWeight = FontWeight.SemiBold)
        }
        Text("In All channels, press and hold a programme and choose Add to Favourites. Favourites come first here, on Home, and when changing channel.", fontSize = 13.sp, color = Flexarr.usher)
    }
}

/** Three hours across; past either edge the window moves an hour and a half. */
private val WINDOW: Duration = Duration.ofHours(3)
private val STEP: Duration = Duration.ofMinutes(90)
private val CHANNEL_WIDTH = 115.dp
private val ROW_HEIGHT = 48.dp

/**
 * The guide: channels down the left, three hours across. Up and down go between channels, left and right between
 * programmes; pressing past either edge moves the window an hour and a half. What's on now plays when chosen; anything
 * later says what it is. Holding a programme records it or its series, or (with [onFavourite]) adds its channel to
 * Favourites. [focusChannel]: where focus starts (over the player, the channel being watched).
 */
@Composable
fun TvLiveGuideGrid(
    client: FlexarrClient,
    channels: List<LiveChannel>,
    guide: LiveGuide?,
    onWatch: (LiveChannel) -> Unit,
    onFavourite: ((LiveChannel) -> Unit)? = null,
    onRecordingsChanged: () -> Unit = {},
    focusChannel: String? = null,
) {
    val scope = rememberCoroutineScope()
    var windowStart by remember { mutableStateOf(halfHourBefore(Instant.now())) }
    var now by remember { mutableStateOf(Instant.now()) }
    var details by remember { mutableStateOf<Pair<LiveProgramme, LiveChannel>?>(null) }
    var menu by remember { mutableStateOf<Pair<LiveProgramme, LiveChannel>?>(null) }
    var notice by remember { mutableStateOf<String?>(null) }
    // The programme that should keep focus as the window moves under it.
    var refocus by remember { mutableStateOf<Long?>(null) }
    val requesters = remember { mutableStateMapOf<Long, FocusRequester>() }
    val list = rememberLazyListState()
    val context = LocalContext.current
    val clock = remember { DateTimeFormatter.ofPattern(if (DateFormat.is24HourFormat(context)) "HH:mm" else "h:mm a") }
    fun time(instant: Instant) = clock.format(instant.atZone(ZoneId.systemDefault()))

    LaunchedEffect(Unit) {
        while (true) {
            delay(30_000)
            now = Instant.now()
        }
    }
    LaunchedEffect(refocus) {
        val id = refocus ?: return@LaunchedEffect
        delay(60)
        runCatching { requesters[id]?.requestFocus() }
        refocus = null
    }
    // Opens on the channel being watched, at what's on now.
    LaunchedEffect(focusChannel, guide != null) {
        if (focusChannel == null || guide == null) return@LaunchedEffect
        val index = channels.indexOfFirst { it.id == focusChannel }
        if (index >= 0) list.scrollToItem((index - 3).coerceAtLeast(0))
        val on = guide.channels.firstOrNull { it.id == focusChannel }?.programmes?.firstOrNull { it.start <= Instant.now() && it.end > Instant.now() }
        delay(300)
        on?.let { runCatching { requesters[it.id]?.requestFocus() } }
    }

    val earliest = guide?.let { halfHourBefore(maxOf(it.from, Instant.now().minus(Duration.ofHours(3)))) } ?: windowStart
    fun move(by: Duration) {
        val loaded = guide ?: return
        val latest = maxOf(earliest, loaded.until.minus(WINDOW))
        val next = windowStart.plus(by)
        windowStart = minOf(maxOf(next, earliest), latest)
    }

    /** Does a recording action, then has the guide fetched again; says why if it couldn't be done. */
    fun act(action: suspend () -> String?) {
        scope.launch {
            try {
                action()?.let { notice = it }
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                notice = e.message
            }
            onRecordingsChanged()
        }
    }

    BoxWithConstraints(Modifier.fillMaxSize()) {
        val width = (maxWidth - CHANNEL_WIDTH - TvLayout.Edge).coerceAtLeast(300.dp)
        val perSecond = width / WINDOW.seconds.toFloat()
        val windowEnd = windowStart.plus(WINDOW)
        Column {
            // The day, then the half hours across.
            Row(Modifier.height(22.dp), verticalAlignment = Alignment.CenterVertically) {
                Text(day(windowStart), fontSize = 11.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.usher, modifier = Modifier.width(CHANNEL_WIDTH))
                Box(Modifier.width(width)) {
                    for (slot in 0 until 6) {
                        Text(time(windowStart.plus(Duration.ofMinutes(30L * slot))), fontSize = 11.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.usher, modifier = Modifier.offset(x = width / 6 * slot))
                    }
                }
            }
            if (guide == null) {
                TvStatusView()
            } else {
                Box {
                    LazyColumn(state = list, verticalArrangement = Arrangement.spacedBy(4.dp), contentPadding = PaddingValues(top = 6.dp, bottom = 30.dp, end = TvLayout.Edge)) {
                        items(channels, key = { it.id }) { channel ->
                            val programmes = guide.channels.firstOrNull { it.id == channel.id }?.programmes.orEmpty().filter {
                                it.end > windowStart && it.start < windowEnd && Duration.between(maxOf(it.start, windowStart), minOf(it.end, windowEnd)) >= Duration.ofMinutes(5)
                            }
                            Row(verticalAlignment = Alignment.CenterVertically) {
                                ChannelLabel(channel, client)
                                Box(Modifier.width(width).height(ROW_HEIGHT)) {
                                    if (programmes.isEmpty()) {
                                        Text("No listings", fontSize = 11.sp, color = Flexarr.usher, modifier = Modifier.align(Alignment.CenterStart).padding(start = 10.dp))
                                    }
                                    programmes.forEachIndexed { index, programme ->
                                        val start = maxOf(programme.start, windowStart)
                                        val end = minOf(programme.end, windowEnd)
                                        val cellWidth = (perSecond * Duration.between(start, end).seconds.toFloat() - 3.dp).coerceAtLeast(20.dp)
                                        val requester = remember(programme.id) { requesters.getOrPut(programme.id) { FocusRequester() } }
                                        GuideCell(
                                            programme,
                                            time = "${time(programme.start)} – ${time(programme.end)}",
                                            onNow = programme.start <= now && programme.end > now,
                                            past = programme.end <= now,
                                            showsText = cellWidth >= 45.dp,
                                            onClick = {
                                                if (programme.start <= now && programme.end > now) onWatch(channel) else details = programme to channel
                                            },
                                            onLongClick = { menu = programme to channel },
                                            modifier = Modifier.offset(x = perSecond * Duration.between(windowStart, start).seconds.toFloat()).size(cellWidth, ROW_HEIGHT)
                                                .focusRequester(requester)
                                                .onPreviewKeyEvent { event ->
                                                    if (event.type != KeyEventType.KeyDown) return@onPreviewKeyEvent false
                                                    // Past either edge of the window, it moves, and focus stays on this programme (still in view).
                                                    // Left from the first programme at the guide's start goes on to the categories.
                                                    when {
                                                        event.key == Key.DirectionRight && index == programmes.size - 1 && windowEnd < guide.until -> {
                                                            move(STEP)
                                                            refocus = programme.id
                                                            true
                                                        }
                                                        event.key == Key.DirectionLeft && index == 0 && windowStart > earliest -> {
                                                            move(STEP.negated())
                                                            refocus = programme.id
                                                            true
                                                        }
                                                        else -> false
                                                    }
                                                },
                                        )
                                    }
                                }
                            }
                        }
                    }
                    // Now, as a line down the guide.
                    val offset = perSecond * Duration.between(windowStart, now).seconds.toFloat()
                    if (now >= windowStart && now < windowEnd) {
                        Box(Modifier.offset(x = CHANNEL_WIDTH + offset).width(1.5.dp).fillMaxHeight().background(Flexarr.danger))
                    }
                }
            }
        }
    }

    details?.let { (programme, channel) ->
        GuideDialog(programme.displayTitle, describe(programme, ::time), onDismiss = { details = null }, actions = buildList {
            add(MenuAction("Watch ${channel.name} now") { onWatch(channel) })
            if (programme.end > now) {
                add(MenuAction(recordLabel(programme)) { act { toggleRecording(client, programme, channel.id) } })
                add(MenuAction(seriesLabel(programme)) { act { toggleSeries(client, programme, channel.id); null } })
            }
        })
    }
    menu?.let { (programme, channel) ->
        TvItemMenu(programme.displayTitle, buildList {
            if (programme.end > now) {
                add(MenuAction(recordLabel(programme)) { act { toggleRecording(client, programme, channel.id) } })
                add(MenuAction(seriesLabel(programme)) { act { toggleSeries(client, programme, channel.id); null } })
            }
            if (onFavourite != null) add(MenuAction(if (channel.isFavourite) "Remove from Favourites" else "Add to Favourites") { onFavourite(channel) })
        }, onDismiss = { menu = null })
    }
    notice?.let { message ->
        GuideDialog("Recording", message, onDismiss = { notice = null }, actions = emptyList())
    }
}

/** A channel down the guide's left: its number and logo (or name), starred when it's a favourite. */
@Composable
private fun ChannelLabel(channel: LiveChannel, client: FlexarrClient) {
    Row(Modifier.width(CHANNEL_WIDTH), horizontalArrangement = Arrangement.spacedBy(7.dp), verticalAlignment = Alignment.CenterVertically) {
        Text(channel.numberText, fontSize = 10.sp, fontWeight = FontWeight.Bold, color = Flexarr.usher, modifier = Modifier.width(27.dp), textAlign = androidx.compose.ui.text.style.TextAlign.End)
        Box(Modifier.size(65.dp, 30.dp), contentAlignment = Alignment.CenterStart) {
            Text(channel.name, fontSize = 9.sp, fontWeight = FontWeight.SemiBold, maxLines = 2, overflow = TextOverflow.Ellipsis)
            client.imageUrl(channel.logoUrl, 300)?.let { url ->
                AsyncImage(url, contentDescription = null, contentScale = ContentScale.Fit, modifier = Modifier.fillMaxSize().background(Flexarr.room))
            }
            if (channel.isFavourite) Icon(Icons.Filled.Star, contentDescription = "Favourite", tint = Gold, modifier = Modifier.size(8.dp).align(Alignment.TopEnd).offset(x = 5.dp, y = (-3).dp))
        }
    }
}

/** A programme: quiet until focused (white, dark text); what's on now carries a bar, what's past is dim, a recording is outlined in red. */
@Composable
private fun GuideCell(
    programme: LiveProgramme,
    time: String,
    onNow: Boolean,
    past: Boolean,
    showsText: Boolean,
    onClick: () -> Unit,
    onLongClick: () -> Unit,
    modifier: Modifier,
) {
    val source = remember { MutableInteractionSource() }
    val focused by source.collectIsFocusedAsState()
    Surface(
        onClick = onClick,
        onLongClick = onLongClick,
        interactionSource = source,
        modifier = modifier.alpha(if (past && !focused) 0.55f else 1f)
            .then(if (programme.isSetToRecord) Modifier.border(1.5.dp, RecordRed, RoundedCornerShape(5.dp)) else Modifier),
        shape = ClickableSurfaceDefaults.shape(RoundedCornerShape(5.dp)),
        colors = ClickableSurfaceDefaults.colors(
            containerColor = Flexarr.seat.copy(alpha = if (onNow) 0.95f else 0.6f),
            contentColor = Flexarr.screen,
            focusedContainerColor = Flexarr.screen,
            focusedContentColor = Flexarr.room,
        ),
        scale = ClickableSurfaceDefaults.scale(focusedScale = 1.04f),
    ) {
        if (onNow && !focused) Box(Modifier.padding(vertical = 6.dp).width(2.dp).fillMaxHeight().background(Flexarr.silver, RoundedCornerShape(1.dp)))
        // Too narrow for its title (a five-minute filler): a plain block, named when focused.
        if (showsText || focused) {
            Column(Modifier.fillMaxSize().padding(horizontal = 7.dp), verticalArrangement = Arrangement.spacedBy(2.dp, Alignment.CenterVertically)) {
                Row(horizontalArrangement = Arrangement.spacedBy(4.dp), verticalAlignment = Alignment.CenterVertically) {
                    if (programme.isSetToRecord) Box(Modifier.size(7.dp).background(RecordRed, CircleShape))
                    Text(programme.displayTitle, fontSize = 11.5.sp, fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis)
                }
                Text(time, fontSize = 9.sp, maxLines = 1, modifier = Modifier.alpha(0.7f))
            }
        }
    }
}

/** What a programme is, or a recording's news, with what can be done about it. */
@Composable
private fun GuideDialog(title: String, message: String, onDismiss: () -> Unit, actions: List<MenuAction>) {
    val first = remember { FocusRequester() }
    Dialog(onDismissRequest = onDismiss) {
        Column(Modifier.width(380.dp).background(Flexarr.seat, RoundedCornerShape(14.dp)).padding(22.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            Text(title, fontSize = 16.sp, fontWeight = FontWeight.Bold)
            Text(message, fontSize = 13.sp, color = Flexarr.usher)
            Spacer(Modifier.height(4.dp))
            actions.forEachIndexed { index, action ->
                TvActionButton(action.label, onClick = {
                    onDismiss()
                    action.run()
                }, prominent = index == 0, modifier = Modifier.fillMaxWidth().then(if (index == 0) Modifier.focusRequester(first) else Modifier))
            }
            TvActionButton(if (actions.isEmpty()) "OK" else "Close", onClick = onDismiss, modifier = Modifier.fillMaxWidth().then(if (actions.isEmpty()) Modifier.focusRequester(first) else Modifier))
        }
    }
    LaunchedEffect(Unit) {
        delay(50)
        runCatching { first.requestFocus() }
    }
}

private fun describe(programme: LiveProgramme, time: (Instant) -> String): String = buildList {
    add("${day(programme.start)} ${time(programme.start)} – ${time(programme.end)}")
    programme.season?.let { season -> add(programme.episode?.let { "Series $season, episode $it" } ?: "Series $season") }
    if (programme.isNew) add("New")
    programme.description?.let(::add)
}.joinToString("\n")

private fun halfHourBefore(time: Instant): Instant = Instant.ofEpochSecond(time.epochSecond / 1800 * 1800)

/** "Today", "Tomorrow" or the day's name. */
private fun day(instant: Instant): String {
    val date = instant.atZone(ZoneId.systemDefault()).toLocalDate()
    val today = LocalDate.now()
    return when (date) {
        today -> "Today"
        today.plusDays(1) -> "Tomorrow"
        else -> date.dayOfWeek.getDisplayName(TextStyle.FULL, Locale.getDefault())
    }
}

/**
 * The DVR: what's coming up (choose one to take it off, or stop it), the series being recorded, and what's been
 * recorded (choose one to play it). Programmes are set to record from the guide.
 */
@Composable
private fun TvLiveRecordings(model: AppModel, onChanged: () -> Unit, onUpcoming: (Int) -> Unit) {
    val client = model.client ?: return
    val navigator = LocalNavigator.current
    val scope = rememberCoroutineScope()
    val context = LocalContext.current
    var recordings by remember { mutableStateOf<io.github.flexarr.core.Recordings?>(null) }
    var failed by remember { mutableStateOf<String?>(null) }
    var confirmingRecording by remember { mutableStateOf<Recording?>(null) }
    var confirmingSeries by remember { mutableStateOf<RecordingSeries?>(null) }
    val dayFormat = remember { DateTimeFormatter.ofPattern(android.text.format.DateFormat.getBestDateTimePattern(Locale.getDefault(), if (DateFormat.is24HourFormat(context)) "EEEdMMMHHmm" else "EEEdMMMhmma")) }
    fun day(instant: Instant) = dayFormat.format(instant.atZone(ZoneId.systemDefault()))

    suspend fun load() {
        try {
            val loaded = client.recordings()
            recordings = loaded
            onUpcoming(loaded.upcoming.size)
            failed = null
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            if (recordings == null) failed = e.message
        }
    }
    RepeatWhileShowing(30_000) { load() }

    fun act(action: suspend () -> Unit) {
        scope.launch {
            runCatching { action() }
            load()
            onChanged()
        }
    }

    fun detail(recording: Recording) = listOfNotNull(
        if (recording.season != null && recording.episode != null) "S${recording.season} E${recording.episode}" else null,
        recording.subtitle,
        recording.channelName,
        day(recording.start),
    ).joinToString(" · ")

    val loaded = recordings
    when {
        loaded != null -> LazyColumn(verticalArrangement = Arrangement.spacedBy(5.dp), contentPadding = PaddingValues(top = 6.dp, bottom = 30.dp, end = TvLayout.Edge)) {
            item { Heading("Coming up") }
            if (loaded.upcoming.isEmpty()) {
                item { Text("Nothing set to record. In the guide, press and hold a programme and choose Record, or Record series.", fontSize = 13.sp, color = Flexarr.usher) }
            }
            items(loaded.upcoming, key = { "u" + it.id }) { recording ->
                RecordingRow(recording.title, detail(recording), recordingNow = recording.isRecordingNow, warning = when {
                    recording.clashes -> "Won’t record: every stream is wanted by recordings scheduled before it."
                    recording.state != "scheduled" -> recording.reason
                    else -> null
                }) { confirmingRecording = recording }
            }
            if (loaded.series.isNotEmpty()) {
                item { Heading("Series", Modifier.padding(top = 12.dp)) }
                items(loaded.series, key = { "s" + it.id }) { series ->
                    val line = "${series.channelName?.let { "On $it" } ?: "On any channel"} · ${series.upcoming} coming up · ${series.recorded} recorded" +
                        if (series.newOnly) " · New episodes only" else ""
                    RecordingRow(series.title, line, recordingNow = false, warning = null) { confirmingSeries = series }
                }
            }
            if (loaded.recent.isNotEmpty()) {
                item { Heading("Recorded", Modifier.padding(top = 12.dp)) }
                items(loaded.recent, key = { "r" + it.id }) { recording ->
                    RecordingRow(recording.title, detail(recording), recordingNow = false, warning = null, enabled = recording.itemId != null) {
                        recording.itemId?.let { navigator.play(it) }
                    }
                }
            }
        }
        failed != null -> TvStatusView(failed, retry = { scope.launch { load() } })
        else -> TvStatusView()
    }

    confirmingRecording?.let { recording ->
        GuideDialog(
            recording.title,
            if (recording.isRecordingNow) "What’s recorded so far is kept." else "${recording.channelName}, ${day(recording.start)}",
            onDismiss = { confirmingRecording = null },
            actions = listOf(MenuAction(if (recording.isRecordingNow) "Stop recording" else "Don’t record") { act { client.removeRecording(recording.id) } }),
        )
    }
    confirmingSeries?.let { series ->
        GuideDialog(
            series.title,
            "Its scheduled showings come off the list. What it’s recorded stays.",
            onDismiss = { confirmingSeries = null },
            actions = listOf(MenuAction("Stop recording the series") { act { client.removeSeries(series.id) } }),
        )
    }
}

@Composable
private fun Heading(text: String, modifier: Modifier = Modifier) {
    Text(text, fontSize = 15.sp, fontWeight = FontWeight.Bold, modifier = modifier.padding(bottom = 2.dp))
}

/** A recording's row: a quiet panel, white with dark text when focused. */
@Composable
private fun RecordingRow(title: String, detail: String, recordingNow: Boolean, warning: String?, enabled: Boolean = true, onClick: () -> Unit) {
    Surface(
        onClick = onClick,
        enabled = enabled,
        modifier = Modifier.fillMaxWidth(),
        shape = ClickableSurfaceDefaults.shape(RoundedCornerShape(7.dp)),
        colors = ClickableSurfaceDefaults.colors(
            containerColor = Flexarr.seat.copy(alpha = 0.7f),
            contentColor = Flexarr.screen,
            focusedContainerColor = Flexarr.screen,
            focusedContentColor = Flexarr.room,
            disabledContainerColor = Flexarr.seat.copy(alpha = 0.7f),
            disabledContentColor = Flexarr.screen,
        ),
        scale = ClickableSurfaceDefaults.scale(focusedScale = 1.02f),
    ) {
        Row(Modifier.padding(horizontal = 11.dp, vertical = 8.dp), horizontalArrangement = Arrangement.spacedBy(9.dp), verticalAlignment = Alignment.CenterVertically) {
            Box(Modifier.size(8.dp).background(if (recordingNow) RecordRed else Color.Transparent, CircleShape))
            Column(verticalArrangement = Arrangement.spacedBy(2.dp)) {
                Text(title, fontSize = 13.5.sp, fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis)
                Text(detail, fontSize = 10.5.sp, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.alpha(0.7f))
                warning?.let { Text(it, fontSize = 10.5.sp, color = Flexarr.danger, maxLines = 2) }
            }
        }
    }
}

/** A favourite channel on Home: its logo on a dark plate with how far through what's on, and what's on beneath. */
@Composable
fun TvHomeChannelCard(model: AppModel, channel: LiveChannel, order: List<LiveChannel>, width: Dp = TvLayout.WideWidth * 0.8f) {
    val navigator = LocalNavigator.current
    val client = LocalClient.current
    TvArtworkButton(
        ambient = Flexarr.silver,
        onClick = { navigator.watch(channel, order) },
        modifier = Modifier.width(width),
        artwork = {
            Box(Modifier.size(width, width * 9f / 16f).background(Flexarr.seat)) {
                Text(channel.name, style = marquee(18, Flexarr.screen.copy(alpha = 0.8f)), modifier = Modifier.align(Alignment.Center).padding(10.dp))
                client.imageUrl(channel.logoUrl, 500)?.let { url ->
                    AsyncImage(url, contentDescription = null, contentScale = ContentScale.Fit, modifier = Modifier.fillMaxSize().background(Flexarr.seat).padding(horizontal = 30.dp, vertical = 18.dp))
                }
                Text(channel.numberText, fontSize = 11.sp, fontWeight = FontWeight.Bold, color = Flexarr.usher, modifier = Modifier.padding(7.dp))
                channel.now?.let { TvProgressBar(it.progress().toDouble(), Modifier.align(Alignment.BottomCenter).padding(8.dp), tint = Flexarr.silver) }
                if (model.liveStartingChannel == channel.id) {
                    Box(Modifier.fillMaxSize().background(Color.Black.copy(alpha = 0.45f)))
                    CircularProgressIndicator(Modifier.align(Alignment.Center).size(22.dp), strokeWidth = 2.dp, color = Flexarr.screen)
                }
            }
        },
        caption = { focused ->
            Column(verticalArrangement = Arrangement.spacedBy(2.dp)) {
                Text(channel.now?.displayTitle ?: channel.name, fontSize = 12.5.sp, fontWeight = FontWeight.SemiBold, color = if (focused) Flexarr.screen else Flexarr.screen.copy(alpha = 0.85f), maxLines = 1, overflow = TextOverflow.Ellipsis)
                Text(channel.now?.let { "${channel.name}   ${LiveTvController.timeRange(it)}" } ?: channel.name, fontSize = 10.5.sp, color = Flexarr.usher, maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
        },
    )
}
