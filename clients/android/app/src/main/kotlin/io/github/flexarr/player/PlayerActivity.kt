package io.github.flexarr.player

import android.content.Context
import android.content.Intent
import android.content.pm.ActivityInfo
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
import androidx.compose.foundation.border
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.WindowInsetsSides
import androidx.compose.foundation.layout.only
import androidx.compose.foundation.layout.safeDrawing
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Forward10
import androidx.compose.material.icons.filled.Pause
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Replay10
import androidx.compose.material.icons.filled.SkipNext
import androidx.compose.material.icons.filled.SkipPrevious
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.Slider
import androidx.compose.material3.SliderDefaults
import androidx.compose.material3.Text
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
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import androidx.media3.ui.AspectRatioFrameLayout
import androidx.media3.ui.PlayerView
import io.github.flexarr.appModel
import io.github.flexarr.core.Format
import io.github.flexarr.core.MediaKind
import io.github.flexarr.core.displayTitle
import io.github.flexarr.core.episodeSubtitle
import io.github.flexarr.ui.components.ActionButton
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.FlexarrTheme
import kotlinx.coroutines.delay
import java.util.Locale

/** Full screen, sideways, the picture edge to edge. Closing it ends the session and tells the server where it got to. */
class PlayerActivity : ComponentActivity() {
    private var controller: PlaybackController? = null

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
        val controller = PlaybackController(this, model, client, onClose = ::finish).also { controller = it }
        controller.start(
            itemId = intent.getStringExtra(EXTRA_ITEM)!!,
            fromBeginning = intent.getBooleanExtra(EXTRA_FROM_BEGINNING, false),
            shuffle = intent.getBooleanExtra(EXTRA_SHUFFLE, false),
        )
        setContent { FlexarrTheme { PlayerScreen(controller, onClose = ::finish) } }
    }

    override fun onStop() {
        super.onStop()
        // In the background (or the screen off): stop, rather than play to nobody.
        controller?.player?.pause()
    }

    override fun onDestroy() {
        super.onDestroy()
        controller?.close()
        controller = null
    }

    companion object {
        private const val EXTRA_ITEM = "itemId"
        private const val EXTRA_FROM_BEGINNING = "fromBeginning"
        private const val EXTRA_SHUFFLE = "shuffle"

        fun intent(context: Context, itemId: String, fromBeginning: Boolean, shuffle: Boolean = false): Intent =
            Intent(context, PlayerActivity::class.java)
                .putExtra(EXTRA_ITEM, itemId)
                .putExtra(EXTRA_FROM_BEGINNING, fromBeginning)
                .putExtra(EXTRA_SHUFFLE, shuffle)
    }
}

/**
 * Our controls over the video, as on iPhone: the title, previous and next episode, skip back and forward, play/pause,
 * the timeline, and the Skip Intro / Next Episode pill above it. A tap shows or hides them; a double tap on either
 * side skips ten seconds.
 */
@Composable
private fun PlayerScreen(controller: PlaybackController, onClose: () -> Unit) {
    var controlsVisible by remember { mutableStateOf(true) }
    var interaction by remember { mutableIntStateOf(0) }
    var flash by remember { mutableStateOf<String?>(null) }
    var flashCount by remember { mutableIntStateOf(0) }
    var audioSyncOpen by remember { mutableStateOf(false) }

    // Hidden after a while of playing untouched.
    LaunchedEffect(controlsVisible, interaction, controller.isPlaying, audioSyncOpen) {
        if (controlsVisible && controller.isPlaying && !audioSyncOpen) {
            delay(4_000)
            controlsVisible = false
        }
    }
    LaunchedEffect(flashCount) {
        if (flash == null) return@LaunchedEffect
        delay(700)
        flash = null
    }

    Box(Modifier.fillMaxSize().background(Color.Black)) {
        AndroidView(
            factory = { context ->
                PlayerView(context).apply {
                    useController = false
                    resizeMode = AspectRatioFrameLayout.RESIZE_MODE_FIT
                    keepScreenOn = true
                    setShutterBackgroundColor(AndroidColor.BLACK)
                    // Subtitles are drawn by SubtitleLayer, at the profile's size and height.
                    subtitleView?.visibility = android.view.View.GONE
                    player = controller.player
                }
            },
            modifier = Modifier.fillMaxSize(),
        )
        SubtitleLayer(controller)
        Box(
            Modifier.fillMaxSize().pointerInput(Unit) {
                detectTapGestures(
                    onTap = {
                        // With Audio sync open, a tap puts it away and brings the controls back.
                        controlsVisible = audioSyncOpen || !controlsVisible
                        audioSyncOpen = false
                        interaction++
                    },
                    onDoubleTap = { offset ->
                        val x = offset.x / size.width
                        when {
                            x < 0.35f -> { controller.skip(-10); flash = "−10 s"; flashCount++ }
                            x > 0.65f -> { controller.skip(10); flash = "+10 s"; flashCount++ }
                            else -> controller.togglePlayPause()
                        }
                    },
                )
            },
        )
        if (controller.isBuffering && controller.error == null) {
            CircularProgressIndicator(Modifier.align(Alignment.Center), color = Flexarr.screen)
        }
        flash?.let {
            Text(it, fontSize = 22.sp, fontWeight = FontWeight.Bold, color = Flexarr.screen, modifier = Modifier.align(Alignment.Center).glass(CircleShape).padding(horizontal = 18.dp, vertical = 10.dp))
        }
        controller.error?.let { message ->
            Column(Modifier.align(Alignment.Center).padding(32.dp), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(16.dp)) {
                Text(message, fontSize = 17.sp, fontWeight = FontWeight.Medium, color = Flexarr.screen)
                ActionButton("Close", onClick = onClose, prominent = true)
            }
        }

        val asleep = controller.sleepPhase != PlaybackController.SleepPhase.Off
        AnimatedVisibility(controlsVisible && !asleep && controller.error == null, enter = fadeIn(), exit = fadeOut(), modifier = Modifier.fillMaxSize()) {
            Controls(controller, onClose, onInteract = { interaction++ }, onAudioSync = {
                // The panel takes the controls' place until Done.
                audioSyncOpen = true
                controlsVisible = false
            })
        }
        if (audioSyncOpen && !asleep) {
            AudioSyncPanel(
                controller,
                onDone = {
                    audioSyncOpen = false
                    controlsVisible = true
                    interaction++
                },
                modifier = Modifier.align(Alignment.BottomCenter).safeDrawingPadding().padding(bottom = 32.dp),
            )
        }
        // The pill stays up with or without the controls: it's how you skip.
        controller.promptTitle?.takeIf { !asleep }?.let { title ->
            Row(
                Modifier.align(Alignment.BottomEnd).safeDrawingPadding().padding(end = 24.dp, bottom = if (controlsVisible) 132.dp else 32.dp),
                horizontalArrangement = Arrangement.spacedBy(10.dp),
            ) {
                if (controller.countingDown) {
                    Text("Cancel", fontSize = 15.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen, modifier = Modifier.glass(RoundedCornerShape(12.dp)).clickableNoRipple(controller::cancelNext).padding(horizontal = 18.dp, vertical = 12.dp))
                }
                ActionButton(title, onClick = controller::promptTapped, icon = if (title.startsWith("Skip")) Icons.Filled.SkipNext else Icons.Filled.PlayArrow, prominent = true)
            }
        }
        SleepPrompt(controller)
    }
}

@Composable
private fun Controls(controller: PlaybackController, onClose: () -> Unit, onInteract: () -> Unit, onAudioSync: () -> Unit) {
    val info = controller.info
    Box(Modifier.fillMaxSize()) {
        // Keep the title and times readable over bright pictures.
        Box(Modifier.fillMaxWidth().height(140.dp).background(Brush.verticalGradient(listOf(Color.Black.copy(alpha = 0.6f), Color.Transparent))))
        Box(Modifier.fillMaxWidth().height(160.dp).align(Alignment.BottomCenter).background(Brush.verticalGradient(listOf(Color.Transparent, Color.Black.copy(alpha = 0.6f)))))

        Row(Modifier.fillMaxWidth().safeDrawingPadding().padding(horizontal = 20.dp, vertical = 12.dp), horizontalArrangement = Arrangement.spacedBy(14.dp), verticalAlignment = Alignment.Top) {
            GlassButton(Icons.Filled.Close, "Close", size = 44, onClick = onClose)
            if (info != null) {
                Column(Modifier.weight(1f).padding(top = 2.dp)) {
                    Text(info.item.displayTitle, fontSize = 20.sp, fontWeight = FontWeight.Bold, color = Flexarr.screen, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    info.item.episodeSubtitle?.let { Text(it, fontSize = 15.sp, color = Flexarr.screen.copy(alpha = 0.8f), maxLines = 1, overflow = TextOverflow.Ellipsis) }
                    info.transcodeSummary?.let { Text(it.uppercase(Locale.getDefault()), fontSize = 11.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.usher, maxLines = 1, modifier = Modifier.padding(top = 4.dp)) }
                }
            }
        }

        Row(Modifier.align(Alignment.Center), horizontalArrangement = Arrangement.spacedBy(28.dp), verticalAlignment = Alignment.CenterVertically) {
            // A film has neither episode button; an episode has both, with the one that goes nowhere dimmed.
            val episode = info?.item?.kind == MediaKind.Episode
            if (episode) GlassButton(Icons.Filled.SkipPrevious, "Previous episode", enabled = info.previous != null) { controller.playPrevious(); onInteract() }
            GlassButton(Icons.Filled.Replay10, "Back 10 seconds") { controller.skip(-10); onInteract() }
            GlassButton(if (controller.isPlaying) Icons.Filled.Pause else Icons.Filled.PlayArrow, if (controller.isPlaying) "Pause" else "Play", size = 76) {
                controller.togglePlayPause()
                onInteract()
            }
            GlassButton(Icons.Filled.Forward10, "Forward 10 seconds") { controller.skip(10); onInteract() }
            if (episode) GlassButton(Icons.Filled.SkipNext, "Next episode", enabled = info.next != null) { controller.playNext(); onInteract() }
        }

        Column(Modifier.align(Alignment.BottomCenter), horizontalAlignment = Alignment.End) {
            MenuBar(controller, onInteract, onAudioSync, Modifier.windowInsetsPadding(WindowInsets.safeDrawing.only(WindowInsetsSides.Horizontal)).padding(end = 24.dp))
            Timeline(controller, onInteract, Modifier)
        }
    }
}

@Composable
private fun Timeline(controller: PlaybackController, onInteract: () -> Unit, modifier: Modifier) {
    var scrubbing by remember { mutableStateOf(false) }
    var scrubTo by remember { mutableFloatStateOf(0f) }
    val duration = controller.duration.coerceAtLeast(1.0)
    val shown = if (scrubbing) scrubTo.toDouble() else controller.position
    Row(
        modifier.fillMaxWidth().safeDrawingPadding().padding(horizontal = 24.dp, vertical = 16.dp),
        horizontalArrangement = Arrangement.spacedBy(10.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(Format.timestamp(shown), fontSize = 13.sp, fontFamily = FontFamily.Monospace, color = Flexarr.screen, modifier = Modifier.width(64.dp))
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
            modifier = Modifier.weight(1f),
        )
        Text("−" + Format.timestamp((controller.duration - shown).coerceAtLeast(0.0)), fontSize = 13.sp, fontFamily = FontFamily.Monospace, color = Flexarr.screen, modifier = Modifier.width(72.dp))
    }
}

/** A round button on translucent dark glass with a hairline edge, so it reads over any picture. */
@Composable
private fun GlassButton(icon: ImageVector, label: String, size: Int = 56, enabled: Boolean = true, onClick: () -> Unit) {
    Box(
        Modifier.size(size.dp).glass(CircleShape).alpha(if (enabled) 1f else 0.35f).clickableNoRipple(onClick, enabled),
        contentAlignment = Alignment.Center,
    ) {
        Icon(icon, contentDescription = label, tint = Flexarr.screen, modifier = Modifier.size((size * 0.5).dp))
    }
}

internal fun Modifier.glass(shape: androidx.compose.ui.graphics.Shape) =
    clip(shape).background(Color.Black.copy(alpha = 0.45f)).border(0.5.dp, Color.White.copy(alpha = 0.18f), shape)

@Composable
internal fun Modifier.clickableNoRipple(onClick: () -> Unit, enabled: Boolean = true): Modifier =
    this.then(Modifier.pointerInput(enabled) { if (enabled) detectTapGestures { onClick() } })
