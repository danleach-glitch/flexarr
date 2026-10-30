package io.github.flexarr.player

import android.view.KeyEvent
import androidx.activity.compose.BackHandler
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.foundation.background
import androidx.compose.foundation.focusable
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
import androidx.compose.material.icons.automirrored.filled.List
import androidx.compose.material.icons.filled.CalendarMonth
import androidx.compose.material.icons.filled.FastForward
import androidx.compose.material.icons.filled.Pause
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Star
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.focus.onFocusChanged
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.key.KeyEventType
import androidx.compose.ui.input.key.onKeyEvent
import androidx.compose.ui.input.key.onPreviewKeyEvent
import androidx.compose.ui.input.key.type
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.window.Dialog
import androidx.tv.material3.ClickableSurfaceDefaults
import androidx.tv.material3.Icon
import androidx.tv.material3.Surface
import androidx.tv.material3.Text
import coil3.compose.AsyncImage
import io.github.flexarr.core.Format
import io.github.flexarr.core.LiveChannel
import io.github.flexarr.core.LiveGuide
import io.github.flexarr.core.liveChannels
import io.github.flexarr.core.liveGuide
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.marquee
import io.github.flexarr.ui.tv.TvActionButton
import io.github.flexarr.ui.tv.TvLayout
import io.github.flexarr.ui.tv.TvLiveGuideGrid
import io.github.flexarr.ui.tv.TvProgressBar
import io.github.flexarr.ui.tv.TvTheme
import kotlinx.coroutines.delay
import java.time.Instant

/**
 * A live channel on a TV, worked with the remote as on the Apple TV. With the controls away: OK pauses and plays, left
 * and right go back and forward through the rewind window, up and down (and the remote's channel buttons) change
 * channel. With them up: the rewind timeline, Channels (a list down the right), Guide (over the picture, which carries
 * on behind it), Back to live once the picture is behind, and the sleep timer. Back puts away what's open, then closes.
 */
@Composable
internal fun TvLivePlayerScreen(controller: LiveTvController, onClose: () -> Unit) = TvTheme {
    var controlsVisible by remember { mutableStateOf(true) }
    var interaction by remember { mutableIntStateOf(0) }
    var choosingChannel by remember { mutableStateOf(false) }
    var showingGuide by remember { mutableStateOf(false) }
    val root = remember { FocusRequester() }
    var controlsFocused by remember { mutableStateOf(false) }
    val timeline = remember { FocusRequester() }
    val asleep = controller.sleep.phase != VideoSleep.Phase.Off
    val overlay = choosingChannel || showingGuide
    val showing = controlsVisible && !asleep && !overlay && controller.error == null

    fun show() {
        controlsVisible = true
        interaction++
    }

    LaunchedEffect(showing, interaction, controller.isPlaying) {
        if (showing && controller.isPlaying) {
            delay(5_000)
            controlsVisible = false
        }
    }
    LaunchedEffect(showing, overlay, controller.error) {
        if (overlay || controller.error != null) return@LaunchedEffect
        // The controls are placed a frame or two after they're asked for: keep asking for a moment.
        repeat(6) {
            delay(50)
            // Until the controls have it: once they do, focus is the viewer's to move.
            if (showing && controlsFocused) return@LaunchedEffect
            runCatching { if (showing) timeline.requestFocus() else root.requestFocus() }
        }
    }

    BackHandler(enabled = overlay || showing) {
        when {
            showingGuide -> showingGuide = false
            choosingChannel -> choosingChannel = false
            else -> controlsVisible = false
        }
    }

    fun change(by: Int) {
        if (controller.channels.size < 2) return
        controller.step(by)
        // The new channel's name, with the controls, for a moment.
        show()
    }

    Box(
        Modifier.fillMaxSize().background(Color.Black)
            .onPreviewKeyEvent { event ->
                if (event.type != KeyEventType.KeyDown || overlay) return@onPreviewKeyEvent false
                when (event.nativeKeyEvent.keyCode) {
                    KeyEvent.KEYCODE_MEDIA_PLAY_PAUSE -> controller.togglePlayPause().let { show(); true }
                    KeyEvent.KEYCODE_MEDIA_PLAY -> controller.player.play().let { show(); true }
                    KeyEvent.KEYCODE_MEDIA_PAUSE -> controller.player.pause().let { show(); true }
                    KeyEvent.KEYCODE_CHANNEL_UP -> change(1).let { true }
                    KeyEvent.KEYCODE_CHANNEL_DOWN -> change(-1).let { true }
                    KeyEvent.KEYCODE_GUIDE -> { showingGuide = true; true }
                    else -> false
                }
            }
            .focusRequester(root)
            .focusable()
            .onKeyEvent { event ->
                // Only while the screen itself has focus: the controls are away.
                if (event.type != KeyEventType.KeyDown) return@onKeyEvent false
                // The controls are up but focus is still on the picture (they came back with a channel change, say):
                // the key goes to them rather than acting on the picture.
                if (showing) {
                    runCatching { timeline.requestFocus() }
                    return@onKeyEvent true
                }
                if (asleep) {
                    if (controller.sleep.phase == VideoSleep.Phase.Asking) controller.sleep.extend() else controller.sleep.wake()
                    return@onKeyEvent true
                }
                when (event.nativeKeyEvent.keyCode) {
                    KeyEvent.KEYCODE_DPAD_CENTER, KeyEvent.KEYCODE_ENTER, KeyEvent.KEYCODE_NUMPAD_ENTER -> controller.togglePlayPause().let { show(); true }
                    KeyEvent.KEYCODE_DPAD_LEFT -> controller.skip(-controller.skipSeconds).let { true }
                    KeyEvent.KEYCODE_DPAD_RIGHT -> (if (controller.isBehind) controller.skip(controller.skipSeconds) else Unit).let { true }
                    // Up and down change channel, as on the Apple TV: up to the one listed before, down to the next.
                    KeyEvent.KEYCODE_DPAD_UP -> change(-1).let { true }
                    KeyEvent.KEYCODE_DPAD_DOWN -> change(1).let { true }
                    else -> false
                }
            },
    ) {
        VideoSurface(controller.player)
        if ((controller.isBuffering || controller.startingChannel != null) && controller.error == null) {
            CircularProgressIndicator(Modifier.align(Alignment.Center), color = Flexarr.screen)
        }
        controller.error?.let { message ->
            val close = remember { FocusRequester() }
            LaunchedEffect(message) { runCatching { close.requestFocus() } }
            Column(Modifier.align(Alignment.Center).padding(60.dp), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(16.dp)) {
                Text(message, fontSize = 17.sp, fontWeight = FontWeight.Medium, textAlign = TextAlign.Center, modifier = Modifier.widthIn(max = 600.dp))
                TvActionButton("Close", onClick = onClose, prominent = true, ambient = Flexarr.screen, modifier = Modifier.focusRequester(close))
            }
        }
        AnimatedVisibility(showing, enter = fadeIn(), exit = fadeOut(), modifier = Modifier.fillMaxSize().onFocusChanged { controlsFocused = it.hasFocus }) {
            LiveControls(controller, timeline, onInteract = { interaction++ }, onChannels = { choosingChannel = true }, onGuide = { showingGuide = true })
        }
        SleepPrompt(controller.sleep)
        if (choosingChannel) {
            ChannelPanel(controller, onPick = { channel ->
                choosingChannel = false
                if (channel.id != controller.watching?.channel?.id) controller.start(channel)
                show()
            })
        }
        if (showingGuide) {
            GuideOverlay(controller, onWatch = { channel ->
                showingGuide = false
                if (channel.id != controller.watching?.channel?.id) controller.start(channel)
                show()
            })
        }
    }

    controller.recordingInTheWay?.let { (recording, _) ->
        val watch = remember { FocusRequester() }
        Dialog(onDismissRequest = controller::dismissRecordingChoice) {
            TvTheme {
                Column(Modifier.width(400.dp).background(Flexarr.seat, RoundedCornerShape(14.dp)).padding(22.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                    Text("Recording", fontSize = 16.sp, fontWeight = FontWeight.Bold)
                    Text(recording.message, fontSize = 13.sp, color = Flexarr.usher)
                    TvActionButton("Watch ${recording.channelName}", onClick = controller::watchRecorded, prominent = true, modifier = Modifier.fillMaxWidth().focusRequester(watch))
                    TvActionButton("Stop the recording", onClick = controller::stopRecordingAndWatch, modifier = Modifier.fillMaxWidth())
                    TvActionButton("Cancel", onClick = controller::dismissRecordingChoice, modifier = Modifier.fillMaxWidth())
                }
            }
        }
        LaunchedEffect(recording) {
            delay(50)
            runCatching { watch.requestFocus() }
        }
    }
}

/** What's on and the channel at the top; the rewind timeline at the foot, with Channels, Guide, Back to live and the sleep timer. */
@Composable
private fun LiveControls(controller: LiveTvController, timeline: FocusRequester, onInteract: () -> Unit, onChannels: () -> Unit, onGuide: () -> Unit) {
    Box(Modifier.fillMaxSize()) {
        Box(Modifier.fillMaxWidth().height(130.dp).background(Brush.verticalGradient(listOf(Color.Black.copy(alpha = 0.6f), Color.Transparent))))
        Box(Modifier.fillMaxWidth().height(190.dp).align(Alignment.BottomCenter).background(Brush.verticalGradient(listOf(Color.Transparent, Color.Black.copy(alpha = 0.7f)))))
        Column(Modifier.padding(horizontal = 40.dp, vertical = 28.dp)) {
            Text(controller.title, fontSize = 22.sp, fontWeight = FontWeight.Bold, maxLines = 1, overflow = TextOverflow.Ellipsis)
            Text(controller.subtitle, fontSize = 15.sp, color = Flexarr.screen.copy(alpha = 0.8f), maxLines = 1, overflow = TextOverflow.Ellipsis)
            controller.detail?.let { Text(it.uppercase(java.util.Locale.ROOT), fontSize = 10.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.usher, maxLines = 1, modifier = Modifier.padding(top = 4.dp)) }
        }
        Column(Modifier.align(Alignment.BottomCenter).padding(horizontal = 40.dp, vertical = 26.dp), verticalArrangement = Arrangement.spacedBy(14.dp)) {
            LiveTimeline(controller, timeline, onInteract)
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                TvActionButton("Channels", onClick = { onChannels(); onInteract() }, icon = Icons.AutoMirrored.Filled.List)
                TvActionButton("Guide", onClick = { onGuide(); onInteract() }, icon = Icons.Filled.CalendarMonth)
                if (controller.isBehind) TvActionButton("Back to live", onClick = { controller.goLive(); onInteract() }, icon = Icons.Filled.FastForward, prominent = true, ambient = Flexarr.screen)
                Spacer(Modifier.weight(1f))
                SleepMenuBar(controller.sleep, onInteract)
            }
        }
    }
}

/**
 * The rewind window: how far back the picture is from live ("−4:12"), or LIVE at the edge. Focused, left and right go
 * back and forward a step; OK pauses and plays.
 */
@Composable
private fun LiveTimeline(controller: LiveTvController, focus: FocusRequester, onInteract: () -> Unit) {
    val source = remember { MutableInteractionSource() }
    val focused by source.collectIsFocusedAsState()
    val duration = controller.duration
    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        BoxWithConstraints(
            Modifier.fillMaxWidth().height(24.dp).focusRequester(focus).focusable(interactionSource = source).onKeyEvent { event ->
                if (event.type != KeyEventType.KeyDown) return@onKeyEvent false
                when (event.nativeKeyEvent.keyCode) {
                    KeyEvent.KEYCODE_DPAD_LEFT -> controller.skip(-controller.skipSeconds).let { onInteract(); true }
                    KeyEvent.KEYCODE_DPAD_RIGHT -> (if (controller.isBehind) controller.skip(controller.skipSeconds) else Unit).let { onInteract(); true }
                    KeyEvent.KEYCODE_DPAD_CENTER, KeyEvent.KEYCODE_ENTER, KeyEvent.KEYCODE_NUMPAD_ENTER -> controller.togglePlayPause().let { onInteract(); true }
                    else -> false
                }
            },
            contentAlignment = Alignment.CenterStart,
        ) {
            val fraction = if (duration > 0) (controller.position / duration).toFloat().coerceIn(0f, 1f) else 1f
            val track = if (focused) 8.dp else 5.dp
            Box(Modifier.fillMaxWidth().height(track).background(Flexarr.screen.copy(alpha = 0.3f), CircleShape))
            Box(Modifier.fillMaxWidth(fraction).height(track).background(Flexarr.screen, CircleShape))
            if (focused) Box(Modifier.offset(x = maxWidth * fraction - 8.dp).size(16.dp).background(Flexarr.screen, CircleShape))
        }
        Row(verticalAlignment = Alignment.CenterVertically) {
            Icon(if (controller.isPlaying) Icons.Filled.Pause else Icons.Filled.PlayArrow, contentDescription = if (controller.isPlaying) "Playing" else "Paused", modifier = Modifier.size(16.dp))
            Spacer(Modifier.weight(1f))
            val behind = (duration - controller.position).coerceAtLeast(0.0)
            Text(
                if (!controller.isBehind) "LIVE" else "−" + Format.timestamp(behind),
                fontSize = 13.sp,
                style = TimeStyle,
                fontWeight = if (controller.isBehind) FontWeight.Normal else FontWeight.Bold,
                color = if (controller.isBehind) Flexarr.screen else Flexarr.danger,
            )
        }
    }
}

/**
 * The channels, down the right over the picture: every channel in number order with what's on and how far through it is,
 * opening on the one being watched. Choosing another changes to it; Back puts the list away.
 */
@Composable
private fun ChannelPanel(controller: LiveTvController, onPick: (LiveChannel) -> Unit) {
    // Every channel and what's on now, fetched as the list opens (the player's copy may be an hour old).
    var all by remember { mutableStateOf<List<LiveChannel>?>(null) }
    LaunchedEffect(Unit) { all = runCatching { controller.client.liveChannels() }.getOrNull() }
    val listed = (all ?: controller.channels).sortedWith(compareBy({ it.number }, { it.name }))
    val current = controller.watching?.channel?.id ?: controller.startingChannel?.id
    val list = rememberLazyListState()
    val currentFocus = remember { FocusRequester() }
    LaunchedEffect(listed.size) {
        val index = listed.indexOfFirst { it.id == current }
        if (index >= 0) list.scrollToItem((index - 3).coerceAtLeast(0))
        delay(100)
        runCatching { currentFocus.requestFocus() }
    }
    Box(Modifier.fillMaxSize().background(Brush.horizontalGradient(0.4f to Color.Transparent, 1f to Color.Black.copy(alpha = 0.75f)))) {
        Column(Modifier.align(Alignment.CenterEnd).width(380.dp).fillMaxHeight().background(Flexarr.room.copy(alpha = 0.92f)).padding(start = 18.dp, end = 18.dp, top = 28.dp)) {
            Text("Channels", fontSize = 20.sp, fontWeight = FontWeight.Bold, modifier = Modifier.padding(bottom = 12.dp))
            LazyColumn(state = list, verticalArrangement = Arrangement.spacedBy(4.dp), contentPadding = PaddingValues(bottom = 24.dp)) {
                items(listed, key = { it.id }) { channel ->
                    val isCurrent = channel.id == current
                    Surface(
                        onClick = { onPick(channel) },
                        modifier = Modifier.fillMaxWidth().then(if (isCurrent) Modifier.focusRequester(currentFocus) else Modifier),
                        shape = ClickableSurfaceDefaults.shape(RoundedCornerShape(8.dp)),
                        colors = ClickableSurfaceDefaults.colors(
                            containerColor = if (isCurrent) Flexarr.screen.copy(alpha = 0.1f) else Color.Transparent,
                            contentColor = Flexarr.screen,
                            focusedContainerColor = Flexarr.screen,
                            focusedContentColor = Flexarr.room,
                        ),
                        scale = ClickableSurfaceDefaults.scale(focusedScale = 1.02f),
                    ) {
                        Row(Modifier.padding(horizontal = 9.dp, vertical = 5.dp), horizontalArrangement = Arrangement.spacedBy(9.dp), verticalAlignment = Alignment.CenterVertically) {
                            Text(channel.numberText, fontSize = 11.sp, fontWeight = FontWeight.Bold, textAlign = TextAlign.End, modifier = Modifier.width(26.dp))
                            Box(Modifier.size(56.dp, 32.dp).background(Flexarr.seat, RoundedCornerShape(5.dp)), contentAlignment = Alignment.Center) {
                                Text(channel.name.take(4), style = marquee(12, Flexarr.usher))
                                controller.client.imageUrl(channel.logoUrl, 300)?.let { url ->
                                    AsyncImage(url, contentDescription = null, contentScale = ContentScale.Fit, modifier = Modifier.fillMaxSize().background(Flexarr.seat, RoundedCornerShape(5.dp)).padding(4.dp))
                                }
                            }
                            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(3.dp)) {
                                Row(horizontalArrangement = Arrangement.spacedBy(5.dp), verticalAlignment = Alignment.CenterVertically) {
                                    Text(channel.now?.displayTitle ?: "No listings", fontSize = 12.5.sp, fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f, fill = false))
                                    if (channel.isFavourite) Icon(Icons.Filled.Star, contentDescription = "Favourite", tint = LiveGold, modifier = Modifier.size(9.dp))
                                }
                                Text(channel.name, fontSize = 10.sp, maxLines = 1)
                                channel.now?.let { TvProgressBar(it.progress().toDouble(), Modifier.width(70.dp), tint = Flexarr.silver) }
                            }
                        }
                    }
                }
            }
        }
    }
}

/** The guide over the picture, which carries on behind it: the channels in their order, opening on the one being watched. */
@Composable
private fun GuideOverlay(controller: LiveTvController, onWatch: (LiveChannel) -> Unit) {
    var guide by remember { mutableStateOf<LiveGuide?>(null) }
    var version by remember { mutableIntStateOf(0) }
    LaunchedEffect(version) { runCatching { controller.client.liveGuide(Instant.now().minusSeconds(3600), 24) }.onSuccess { guide = it } }
    Box(Modifier.fillMaxSize().background(Color.Black.copy(alpha = 0.8f))) {
        Column(Modifier.padding(start = TvLayout.Edge, top = 25.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            Text("Guide", style = marquee(28))
            TvLiveGuideGrid(
                controller.client,
                controller.channels,
                guide,
                onWatch = onWatch,
                onRecordingsChanged = { version++ },
                focusChannel = controller.watching?.channel?.id ?: controller.startingChannel?.id,
            )
        }
    }
}
