package io.github.flexarr.player

import android.content.Context
import android.content.Intent
import android.content.pm.ActivityInfo
import android.content.pm.PackageManager
import android.content.res.Configuration
import android.graphics.Color as AndroidColor
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.SystemBarStyle
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.WindowInsetsSides
import androidx.compose.foundation.layout.only
import androidx.compose.foundation.layout.safeDrawing
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.List
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.KeyboardArrowDown
import androidx.compose.material.icons.filled.KeyboardArrowUp
import androidx.compose.material.icons.filled.Pause
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.PictureInPictureAlt
import androidx.compose.material.icons.filled.Star
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Slider
import androidx.compose.material3.SliderDefaults
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.Icon
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import androidx.media3.session.MediaSession
import androidx.media3.ui.AspectRatioFrameLayout
import androidx.media3.ui.PlayerView
import coil3.compose.AsyncImage
import io.github.flexarr.ActivePlayback
import io.github.flexarr.appModel
import io.github.flexarr.usesTvInterface
import io.github.flexarr.core.Format
import io.github.flexarr.core.LiveChannel
import io.github.flexarr.core.liveChannels
import io.github.flexarr.ui.components.ActionButton
import io.github.flexarr.ui.components.pressable
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.FlexarrTheme
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.onClick
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.stateDescription
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

/**
 * A live channel, full screen and sideways, with the rewind window as its timeline. Closing it lets the channel go.
 * Leaving the app while it plays carries on in picture in picture, as a film does.
 */
class LivePlayerActivity : ComponentActivity() {
    private var controller: LiveTvController? = null
    private var session: MediaSession? = null
    private var pictureInPicture: PictureInPicture? = null
    /** Fetching the channels before the first one starts: closing meanwhile lets it go. */
    private var loading: Job? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        enableEdgeToEdge(SystemBarStyle.dark(AndroidColor.TRANSPARENT), SystemBarStyle.dark(AndroidColor.TRANSPARENT))
        super.onCreate(savedInstanceState)
        requestedOrientation = ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE
        WindowInsetsControllerCompat(window, window.decorView).apply {
            hide(WindowInsetsCompat.Type.systemBars())
            systemBarsBehavior = WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
        }
        WindowCompat.setDecorFitsSystemWindows(window, false)

        val model = appModel
        val client = model.client ?: return finish()
        val controller = LiveTvController(this, model, client, onClose = ::finish).also { controller = it }
        session = MediaSession.Builder(this, controller.player).setId("live").build()
        // Another picture still up (a film in the corner, say) gives way: one at a time, as on iPhone.
        model.commands.active?.stop()
        model.commands.active = playback
        // A TV is the big screen already: no corner window there, and controls for the remote.
        if (usesTvInterface) {
            watchFrom(intent)
            setContent { FlexarrTheme { TvLivePlayerScreen(controller, onClose = ::finish) } }
            return
        }
        val pictureInPicture = PictureInPicture(this, object : PictureInPicture.Source {
            override val videoSize get() = controller.videoSize
            override val isPlaying get() = controller.isPlaying
            override val skipSeconds get() = controller.skipSeconds
            override val canSkipForward get() = controller.isBehind
            override fun skip(seconds: Int) = controller.skip(seconds)
            override fun togglePlayPause() = controller.togglePlayPause()
        }).also { pictureInPicture = it }
        watchFrom(intent)
        setContent {
            FlexarrTheme {
                // The corner window keeps the picture's shape, and goes there by itself only while playing.
                LaunchedEffect(controller.videoSize, controller.isPlaying, controller.isBehind) { pictureInPicture.update() }
                LivePlayerScreen(controller, onClose = ::finish, inPictureInPicture = pictureInPicture.isActive, onPictureInPicture = pictureInPicture::enter)
            }
        }
    }

    /** The intent's channel, among the channels as the screen listed them (fetched now, so what's on is current). */
    private fun watchFrom(intent: Intent) {
        val controller = controller ?: return
        val client = controller.client
        val channelId = intent.getStringExtra(EXTRA_CHANNEL) ?: return
        val order = intent.getStringArrayExtra(EXTRA_ORDER).orEmpty().toList()
        loading?.cancel()
        loading = appModel.scope.launch {
            val all = runCatching { client.liveChannels() }.getOrDefault(emptyList()).associateBy { it.id }
            val listed = order.mapNotNull(all::get).ifEmpty { all.values.toList() }
            val channel = all[channelId] ?: return@launch finish()
            controller.watch(channel, listed)
        }
    }

    /** Another channel chosen while this one is open (in the corner, say): it changes to that. */
    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        watchFrom(intent)
    }

    override fun onUserLeaveHint() {
        super.onUserLeaveHint()
        pictureInPicture?.userLeaving()
    }

    override fun onPictureInPictureModeChanged(isInPictureInPictureMode: Boolean, newConfig: Configuration) {
        super.onPictureInPictureModeChanged(isInPictureInPictureMode, newConfig)
        pictureInPicture?.modeChanged(isInPictureInPictureMode)
    }

    override fun onStop() {
        super.onStop()
        // In the background (or the screen off) it pauses, the channel still held (the heartbeat says so). In the
        // corner it plays on.
        if (!isInPictureInPictureMode) controller?.player?.pause()
    }

    /** For another device's pause, resume and stop. */
    private val playback = object : ActivePlayback {
        override fun pause() { controller?.player?.pause() }
        override fun resume() { controller?.player?.play() }
        override fun stop() = finish()
    }

    override fun onDestroy() {
        super.onDestroy()
        if (appModel.commands.active === playback) appModel.commands.active = null
        session?.release()
        session = null
        loading?.cancel()
        pictureInPicture?.release()
        pictureInPicture = null
        controller?.release()
        controller = null
    }

    companion object {
        private const val EXTRA_CHANNEL = "channelId"
        private const val EXTRA_ORDER = "order"

        /** Watches [channel]; Previous and Next go through [order], as the screen listed them. */
        fun intent(context: Context, channel: LiveChannel, order: List<LiveChannel>): Intent =
            Intent(context, LivePlayerActivity::class.java)
                .putExtra(EXTRA_CHANNEL, channel.id)
                .putExtra(EXTRA_ORDER, order.map { it.id }.toTypedArray())
    }
}

@Composable
private fun LivePlayerScreen(controller: LiveTvController, onClose: () -> Unit, inPictureInPicture: Boolean, onPictureInPicture: () -> Unit) {
    var controlsVisible by remember { mutableStateOf(true) }
    var interaction by remember { mutableIntStateOf(0) }
    var choosingChannel by remember { mutableStateOf(false) }

    // Hidden after a while of playing untouched (never while TalkBack explores the screen by touch).
    val hideAfter = controlsTimeout()
    LaunchedEffect(controlsVisible, interaction, controller.isPlaying, choosingChannel, hideAfter) {
        if (controlsVisible && controller.isPlaying && !choosingChannel && hideAfter != null) {
            delay(hideAfter)
            controlsVisible = false
        }
    }

    Box(Modifier.fillMaxSize().background(Color.Black)) {
        AndroidView(
            factory = { context ->
                PlayerView(context).apply {
                    useController = false
                    resizeMode = AspectRatioFrameLayout.RESIZE_MODE_FIT
                    keepScreenOn = true
                    setShutterBackgroundColor(AndroidColor.BLACK)
                    player = controller.player
                }
            },
            modifier = Modifier.fillMaxSize(),
        )
        // In the corner window, the picture is all there is.
        if (inPictureInPicture) return@Box
        val toggleControls = {
            controlsVisible = !controlsVisible
            interaction++
        }
        Box(
            Modifier.fillMaxSize().semantics { onClick(label = if (controlsVisible) "Hide controls" else "Show controls") { toggleControls(); true } }.pointerInput(Unit) {
                detectTapGestures(
                    onTap = { toggleControls() },
                    onDoubleTap = { offset ->
                        val x = offset.x / size.width
                        when {
                            x < 0.35f -> controller.skip(-controller.skipSeconds)
                            x > 0.65f -> controller.skip(controller.skipSeconds)
                            else -> controller.togglePlayPause()
                        }
                    },
                )
            },
        )
        if ((controller.isBuffering || controller.startingChannel != null) && controller.error == null) {
            CircularProgressIndicator(Modifier.align(Alignment.Center), color = Flexarr.screen)
        }
        controller.error?.let { message ->
            Column(Modifier.align(Alignment.Center).padding(32.dp), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(16.dp)) {
                Text(message, fontSize = 17.sp, fontWeight = FontWeight.Medium, color = Flexarr.screen)
                ActionButton("Close", onClick = onClose, prominent = true)
            }
        }
        val asleep = controller.sleep.phase != VideoSleep.Phase.Off
        AnimatedVisibility(controlsVisible && !asleep && controller.error == null, enter = fadeIn(), exit = fadeOut(), modifier = Modifier.fillMaxSize()) {
            LiveControls(controller, onClose, onPictureInPicture, onInteract = { interaction++ }, onChannels = { choosingChannel = true })
        }
        // Behind live: the way back, with or without the controls.
        if (controller.isBehind && controller.error == null && !asleep) {
            Box(Modifier.align(Alignment.BottomEnd).safeDrawingPadding().padding(end = 24.dp, bottom = if (controlsVisible) 132.dp else 32.dp)) {
                ActionButton("Back to live", onClick = controller::goLive, prominent = true)
            }
        }
        SleepPrompt(controller.sleep)
        ShakeForSleep(controller.sleep)
    }

    if (choosingChannel) {
        ChannelPicker(controller, onPick = { channel ->
            choosingChannel = false
            if (channel.id != controller.watching?.channel?.id) controller.start(channel)
        }, onClose = { choosingChannel = false })
    }
    controller.recordingInTheWay?.let { (recording, _) ->
        AlertDialog(
            onDismissRequest = controller::dismissRecordingChoice,
            title = { Text("Recording") },
            text = { Text(recording.message) },
            confirmButton = { TextButton(onClick = controller::watchRecorded) { Text("Watch ${recording.channelName}") } },
            dismissButton = {
                Row {
                    TextButton(onClick = controller::stopRecordingAndWatch) { Text("Stop the recording", color = Flexarr.danger) }
                    TextButton(onClick = controller::dismissRecordingChoice) { Text("Cancel") }
                }
            },
            containerColor = Flexarr.seat,
        )
    }
}

@Composable
private fun LiveControls(controller: LiveTvController, onClose: () -> Unit, onPictureInPicture: () -> Unit, onInteract: () -> Unit, onChannels: () -> Unit) {
    Box(Modifier.fillMaxSize()) {
        Box(Modifier.fillMaxWidth().height(140.dp).background(Brush.verticalGradient(listOf(Color.Black.copy(alpha = 0.6f), Color.Transparent))))
        Box(Modifier.fillMaxWidth().height(160.dp).align(Alignment.BottomCenter).background(Brush.verticalGradient(listOf(Color.Transparent, Color.Black.copy(alpha = 0.6f)))))

        Row(Modifier.fillMaxWidth().safeDrawingPadding().padding(horizontal = 20.dp, vertical = 12.dp), horizontalArrangement = Arrangement.spacedBy(14.dp), verticalAlignment = Alignment.Top) {
            GlassButton(Icons.Filled.Close, "Close", size = 44, onClick = onClose)
            Column(Modifier.weight(1f).padding(top = 2.dp)) {
                Text(controller.title, fontSize = 20.sp, fontWeight = FontWeight.Bold, color = Flexarr.screen, maxLines = 1, overflow = TextOverflow.Ellipsis)
                Text(controller.subtitle, fontSize = 15.sp, color = Flexarr.screen.copy(alpha = 0.8f), maxLines = 1, overflow = TextOverflow.Ellipsis)
                controller.detail?.let { Text(it.uppercase(), fontSize = 11.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.usher, maxLines = 1, modifier = Modifier.padding(top = 4.dp)) }
            }
            GlassButton(Icons.AutoMirrored.Filled.List, "Channels", size = 44) { onChannels(); onInteract() }
            if (LocalContext.current.packageManager.hasSystemFeature(PackageManager.FEATURE_PICTURE_IN_PICTURE)) {
                GlassButton(Icons.Filled.PictureInPictureAlt, "Picture in picture", size = 44, onClick = onPictureInPicture)
            }
        }

        Row(Modifier.align(Alignment.Center), horizontalArrangement = Arrangement.spacedBy(28.dp), verticalAlignment = Alignment.CenterVertically) {
            val many = controller.channels.size > 1
            GlassButton(Icons.Filled.KeyboardArrowUp, "Previous channel", enabled = many) { controller.step(-1); onInteract() }
            SkipButton(-controller.skipSeconds) { controller.skip(-controller.skipSeconds); onInteract() }
            GlassButton(if (controller.isPlaying) Icons.Filled.Pause else Icons.Filled.PlayArrow, if (controller.isPlaying) "Pause" else "Play", size = 76) {
                controller.togglePlayPause()
                onInteract()
            }
            SkipButton(controller.skipSeconds, enabled = controller.isBehind) { controller.skip(controller.skipSeconds); onInteract() }
            GlassButton(Icons.Filled.KeyboardArrowDown, "Next channel", enabled = many) { controller.step(1); onInteract() }
        }

        Column(Modifier.align(Alignment.BottomCenter), horizontalAlignment = Alignment.End) {
            SleepMenuBar(controller.sleep, onInteract, Modifier.windowInsetsPadding(WindowInsets.safeDrawing.only(WindowInsetsSides.Horizontal)).padding(end = 24.dp))
            LiveTimeline(controller, onInteract, Modifier)
        }
    }
}

/** The rewind window: how far back the picture is from live ("−4:12"), or LIVE at the edge. */
@Composable
private fun LiveTimeline(controller: LiveTvController, onInteract: () -> Unit, modifier: Modifier) {
    var scrubbing by remember { mutableStateOf(false) }
    var scrubTo by remember { mutableFloatStateOf(0f) }
    val duration = controller.duration
    if (duration <= 0) return
    val shown = if (scrubbing) scrubTo.toDouble() else controller.position
    Row(
        modifier.fillMaxWidth().safeDrawingPadding().padding(horizontal = 24.dp, vertical = 16.dp),
        horizontalArrangement = Arrangement.spacedBy(10.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Slider(
            value = shown.toFloat().coerceIn(0f, duration.toFloat()),
            onValueChange = {
                scrubbing = true
                scrubTo = it
                onInteract()
            },
            onValueChangeFinished = {
                controller.seekTo(scrubTo.toDouble())
                scrubbing = false
            },
            valueRange = 0f..duration.toFloat(),
            colors = SliderDefaults.colors(thumbColor = Flexarr.screen, activeTrackColor = Flexarr.screen, inactiveTrackColor = Flexarr.screen.copy(alpha = 0.3f)),
            modifier = Modifier.weight(1f).semantics {
                contentDescription = "Position"
                stateDescription = if (!controller.isBehind && !scrubbing) "Live" else "${Format.timestamp((duration - shown).coerceAtLeast(0.0))} behind live"
            },
        )
        val behind = duration - shown
        Text(
            if (!controller.isBehind && !scrubbing) "LIVE" else "−" + Format.timestamp(behind.coerceAtLeast(0.0)),
            fontSize = 13.sp,
            style = TimeStyle,
            maxLines = 1,
            softWrap = false,
            fontWeight = if (controller.isBehind || scrubbing) FontWeight.Normal else FontWeight.Bold,
            color = if (controller.isBehind || scrubbing) Flexarr.screen else Flexarr.danger,
            modifier = Modifier.widthIn(min = 72.dp),
        )
    }
}

/**
 * The channels, over the live picture: Last watched first (the channel before this one, to flip between two), then
 * every channel in number order, each with what's on and how far through it is. Choosing another changes to it.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun ChannelPicker(controller: LiveTvController, onPick: (LiveChannel) -> Unit, onClose: () -> Unit) {
    // Every channel and what's on now, fetched as the list opens (the player's copy may be an hour old).
    var all by remember { mutableStateOf<List<LiveChannel>?>(null) }
    LaunchedEffect(Unit) { all = runCatching { controller.client.liveChannels() }.getOrNull() }
    val listed = (all ?: controller.channels).sortedWith(compareBy({ it.number }, { it.name }))
    val current = controller.watching?.channel?.id ?: controller.startingChannel?.id
    val previous = controller.previousChannel?.takeIf { it.id != current }
    val list = rememberLazyListState()
    LaunchedEffect(listed.size) {
        if (previous == null) listed.indexOfFirst { it.id == current }.takeIf { it > 2 }?.let { list.scrollToItem(it - 2) }
    }
    ModalBottomSheet(onDismissRequest = onClose, sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true), containerColor = Flexarr.room) {
        Row(Modifier.fillMaxWidth().padding(start = 20.dp, end = 8.dp), verticalAlignment = Alignment.CenterVertically) {
            Text("Channels", fontSize = 17.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen, modifier = Modifier.weight(1f))
            TextButton(onClick = onClose) { Text("Done", color = Flexarr.screen, fontWeight = FontWeight.SemiBold) }
        }
        LazyColumn(state = list, contentPadding = PaddingValues(horizontal = 12.dp, vertical = 8.dp)) {
            if (previous != null) {
                item { PickerHeading("Last watched") }
                item(key = "last") { PickerRow(controller, listed.firstOrNull { it.id == previous.id } ?: previous, current = false, onPick) }
                item { PickerHeading("All channels") }
            }
            items(listed, key = { it.id }) { channel -> PickerRow(controller, channel, current = channel.id == current, onPick) }
        }
    }
}

@Composable
private fun PickerHeading(text: String) {
    Text(text, fontSize = 13.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.usher, modifier = Modifier.padding(horizontal = 14.dp, vertical = 8.dp))
}

@Composable
private fun PickerRow(controller: LiveTvController, channel: LiveChannel, current: Boolean, onPick: (LiveChannel) -> Unit) {
    Row(
        Modifier.fillMaxWidth().clip(RoundedCornerShape(12.dp)).background(if (current) Flexarr.screen.copy(alpha = 0.07f) else Color.Transparent)
            .pressable({ onPick(channel) }).padding(horizontal = 14.dp, vertical = 6.dp),
        horizontalArrangement = Arrangement.spacedBy(12.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(channel.numberText, fontSize = 12.sp, fontWeight = FontWeight.Bold, color = Flexarr.usher, modifier = Modifier.width(36.dp), textAlign = androidx.compose.ui.text.style.TextAlign.End)
        Box(Modifier.size(76.dp, 43.dp).clip(RoundedCornerShape(6.dp)).background(Flexarr.room.copy(alpha = 0.8f)), contentAlignment = Alignment.Center) {
            AsyncImage(controller.client.imageUrl(channel.logoUrl, 300), contentDescription = null, contentScale = ContentScale.Fit, modifier = Modifier.fillMaxSize().padding(6.dp))
        }
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(4.dp)) {
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalAlignment = Alignment.CenterVertically) {
                Text(channel.now?.displayTitle ?: "No listings", fontSize = 16.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f, fill = false))
                if (channel.isFavourite) Icon(Icons.Filled.Star, contentDescription = "Favourite", tint = LiveStar, modifier = Modifier.size(12.dp))
            }
            Row(horizontalArrangement = Arrangement.spacedBy(10.dp), verticalAlignment = Alignment.CenterVertically) {
                Text(channel.name, fontSize = 12.sp, color = Flexarr.usher, maxLines = 1)
                channel.now?.let { now ->
                    LinearProgressIndicator(progress = { now.progress() }, modifier = Modifier.width(80.dp), color = Flexarr.silver, trackColor = Flexarr.aisle)
                }
            }
        }
        if (current) Box(Modifier.size(4.dp, 24.dp).clip(RoundedCornerShape(2.dp)).background(Flexarr.silver))
    }
}

/** Favourites' star: projected white, never gold. */
internal val LiveStar = Flexarr.screen
