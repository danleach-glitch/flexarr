package io.github.flexarr.player

import android.view.KeyEvent
import androidx.activity.compose.BackHandler
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.focusable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsFocusedAsState
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.requiredSize
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Pause
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.SkipNext
import androidx.compose.material.icons.filled.SkipPrevious
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
import androidx.tv.material3.Icon
import androidx.tv.material3.Text
import io.github.flexarr.core.Format
import io.github.flexarr.core.MediaKind
import io.github.flexarr.core.displayTitle
import io.github.flexarr.core.episodeSubtitle
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.tv.TvActionButton
import io.github.flexarr.ui.tv.TvTheme
import kotlinx.coroutines.delay
import java.util.Locale

/**
 * The player on a TV, worked with the remote as on the Apple TV. With the controls away: OK or play/pause pauses and
 * plays, left and right skip back and forward (Settings › Playback), and up or down brings the controls. On the
 * timeline, left and right move through the film with its picture above (OK goes there, Back doesn't); below it are
 * the previous and next episode and the menus. Back puts the controls away, then closes the player.
 */
@Composable
internal fun TvPlayerScreen(controller: PlaybackController, onClose: () -> Unit) = TvTheme {
    var controlsVisible by remember { mutableStateOf(true) }
    var interaction by remember { mutableIntStateOf(0) }
    var scrubTo by remember { mutableStateOf<Double?>(null) }
    var flash by remember { mutableStateOf<String?>(null) }
    var flashCount by remember { mutableIntStateOf(0) }
    val root = remember { FocusRequester() }
    var controlsFocused by remember { mutableStateOf(false) }
    var rootFocused by remember { mutableStateOf(false) }
    val timeline = remember { FocusRequester() }
    val prompt = remember { FocusRequester() }
    var promptFocused by remember { mutableStateOf(false) }
    var audioSyncOpen by remember { mutableStateOf(false) }
    val asleep = controller.sleep.phase != VideoSleep.Phase.Off
    val searchingSubtitles = controller.subtitleSearchFile != null
    // What's on screen to work with: the controls, or nothing (the remote's keys go to the screen itself).
    val showing = controlsVisible && !asleep && !audioSyncOpen && !searchingSubtitles && controller.error == null
    // Skip Intro or Next Episode, which stays up with or without the controls: it's how you skip.
    val promptUp = controller.promptTitle != null && !asleep && !audioSyncOpen && !searchingSubtitles

    fun show() {
        controlsVisible = true
        interaction++
    }

    fun skip(seconds: Int) {
        controller.skip(seconds)
        flash = if (seconds < 0) "−${-seconds} s" else "+$seconds s"
        flashCount++
    }

    // Put away after a while of playing untouched, unless something's being done with them.
    LaunchedEffect(showing, interaction, controller.isPlaying, scrubTo) {
        if (showing && controller.isPlaying && scrubTo == null) {
            delay(5_000)
            controlsVisible = false
        }
    }
    // Focus follows: the timeline with the controls up; without them, Skip Intro or Next Episode while it's up (so OK
    // presses it), else the screen itself (for the remote's keys). One place decides, so the controls going away can't
    // take focus back from the pill.
    LaunchedEffect(showing, promptUp, audioSyncOpen, searchingSubtitles, controller.error) {
        if (controller.error != null || audioSyncOpen || searchingSubtitles) return@LaunchedEffect
        // The controls are placed a frame or two after they're asked for: keep asking for a moment.
        repeat(6) {
            delay(50)
            // Until they have it: once they do, focus is the viewer's to move.
            if (showing && controlsFocused) return@LaunchedEffect
            if (!showing && promptUp && promptFocused) return@LaunchedEffect
            runCatching {
                when {
                    showing -> timeline.requestFocus()
                    promptUp -> prompt.requestFocus()
                    else -> root.requestFocus()
                }
            }
        }
    }
    LaunchedEffect(flashCount) {
        if (flash == null) return@LaunchedEffect
        delay(if ((flash?.length ?: 0) <= 6) 700 else 2_500)
        flash = null
    }
    // The controller's own messages ("Re-syncing subtitles…"), with the controls up.
    LaunchedEffect(controller.flash) {
        val (text, _) = controller.flash ?: return@LaunchedEffect
        flash = text
        flashCount++
        show()
    }

    BackHandler(enabled = scrubTo != null || showing || audioSyncOpen) {
        when {
            audioSyncOpen -> {
                audioSyncOpen = false
                show()
            }
            scrubTo != null -> scrubTo = null
            else -> controlsVisible = false
        }
    }

    Box(
        Modifier.fillMaxSize().background(Color.Black)
            // The remote's play/pause, fast-forward and rewind work wherever focus is.
            .onPreviewKeyEvent { event ->
                if (event.type != KeyEventType.KeyDown) return@onPreviewKeyEvent false
                when (event.nativeKeyEvent.keyCode) {
                    KeyEvent.KEYCODE_MEDIA_PLAY_PAUSE -> controller.togglePlayPause().let { show(); true }
                    KeyEvent.KEYCODE_MEDIA_PLAY -> controller.player.play().let { show(); true }
                    KeyEvent.KEYCODE_MEDIA_PAUSE -> controller.player.pause().let { show(); true }
                    KeyEvent.KEYCODE_MEDIA_FAST_FORWARD -> skip(controller.skipSeconds).let { true }
                    KeyEvent.KEYCODE_MEDIA_REWIND -> skip(-controller.skipSeconds).let { true }
                    KeyEvent.KEYCODE_MEDIA_NEXT -> (controller.info?.next != null).also { if (it) controller.playNext() }
                    else -> false
                }
            }
            .focusRequester(root)
            .onFocusChanged { rootFocused = it.isFocused }
            .focusable()
            .onKeyEvent { event ->
                // Only while the screen itself has focus: keys the controls don't use pass by here on their way up,
                // and must go on to move focus among them.
                if (event.type != KeyEventType.KeyDown || !rootFocused) return@onKeyEvent false
                // The controls are up but focus is still on the picture (they came back with a channel change, say):
                // the key goes to them rather than acting on the picture.
                if (showing) {
                    runCatching { timeline.requestFocus() }
                    return@onKeyEvent true
                }
                if (asleep) {
                    // Any press is 15 minutes more while it asks, and wakes the screen once it's run out.
                    if (controller.sleep.phase == VideoSleep.Phase.Asking) controller.sleep.extend() else controller.sleep.wake()
                    return@onKeyEvent true
                }
                when (event.nativeKeyEvent.keyCode) {
                    KeyEvent.KEYCODE_DPAD_CENTER, KeyEvent.KEYCODE_ENTER, KeyEvent.KEYCODE_NUMPAD_ENTER -> controller.togglePlayPause().let { show(); true }
                    KeyEvent.KEYCODE_DPAD_LEFT -> skip(-controller.skipSeconds).let { true }
                    KeyEvent.KEYCODE_DPAD_RIGHT -> skip(controller.skipSeconds).let { true }
                    KeyEvent.KEYCODE_DPAD_UP, KeyEvent.KEYCODE_DPAD_DOWN -> show().let { true }
                    else -> false
                }
            },
    ) {
        VideoSurface(controller.player)
        SubtitleLayer(controller)
        if ((controller.isBuffering || controller.isOpening) && controller.error == null) {
            CircularProgressIndicator(Modifier.align(Alignment.Center), color = Flexarr.screen)
        }
        flash?.let {
            Text(it, fontSize = 20.sp, fontWeight = FontWeight.Bold, modifier = Modifier.align(Alignment.Center).glass(CircleShape).padding(horizontal = 18.dp, vertical = 10.dp))
        }
        controller.error?.let { message -> PlayerError(controller, message, onClose) }

        AnimatedVisibility(showing, enter = fadeIn(), exit = fadeOut(), modifier = Modifier.fillMaxSize().onFocusChanged { controlsFocused = it.hasFocus }) {
            Controls(controller, scrubTo, onScrub = { scrubTo = it; interaction++ }, onInteract = { interaction++ }, onAudioSync = {
                // The panel takes the controls' place until Done.
                audioSyncOpen = true
                controlsVisible = false
            }, timeline = timeline)
        }
        if (audioSyncOpen && !asleep) {
            TvAudioSync(controller, onDone = {
                audioSyncOpen = false
                show()
            }, modifier = Modifier.align(Alignment.BottomCenter).padding(bottom = 40.dp))
        }
        // The pill stays up with or without the controls: it's how you skip.
        controller.promptTitle?.takeIf { promptUp }?.let { title ->
            Row(
                Modifier.align(Alignment.BottomEnd).padding(end = 40.dp, bottom = if (showing) 120.dp else 40.dp)
                    .onFocusChanged { promptFocused = it.hasFocus }
                    .onKeyEvent { event ->
                        // Up and down bring the controls, as they do from the picture.
                        if (event.type != KeyEventType.KeyDown || showing) return@onKeyEvent false
                        when (event.nativeKeyEvent.keyCode) {
                            KeyEvent.KEYCODE_DPAD_UP, KeyEvent.KEYCODE_DPAD_DOWN -> show().let { true }
                            else -> false
                        }
                    },
                horizontalArrangement = Arrangement.spacedBy(12.dp),
            ) {
                if (controller.countingDown) TvActionButton("Cancel", onClick = controller::cancelNext)
                TvActionButton(
                    title,
                    onClick = controller::promptTapped,
                    icon = if (title.startsWith("Skip")) Icons.Filled.SkipNext else Icons.Filled.PlayArrow,
                    prominent = true,
                    ambient = Flexarr.screen,
                    modifier = Modifier.focusRequester(prompt),
                )
            }
        }
        controller.subtitleSearchFile?.let { fileId ->
            controller.info?.let { info ->
                TvSubtitleSearch(controller.client, info.item.id, fileId, onDownloaded = controller::subtitlesDownloaded, onClose = {
                    controller.closeSubtitleSearch()
                    show()
                }, modifier = Modifier.align(Alignment.CenterEnd))
            }
        }
        SleepPrompt(controller.sleep)
    }
}

/** The title at the top; the timeline at the foot, with the episode buttons and the menus beneath it. */
@Composable
private fun Controls(controller: PlaybackController, scrubTo: Double?, onScrub: (Double?) -> Unit, onInteract: () -> Unit, onAudioSync: () -> Unit, timeline: FocusRequester) {
    val info = controller.info
    Box(Modifier.fillMaxSize()) {
        // Keep the title and times readable over bright pictures.
        Box(Modifier.fillMaxWidth().height(130.dp).background(Brush.verticalGradient(listOf(Color.Black.copy(alpha = 0.6f), Color.Transparent))))
        Box(Modifier.fillMaxWidth().height(190.dp).align(Alignment.BottomCenter).background(Brush.verticalGradient(listOf(Color.Transparent, Color.Black.copy(alpha = 0.7f)))))
        if (info != null) {
            Column(Modifier.padding(horizontal = 40.dp, vertical = 28.dp)) {
                Text(info.item.displayTitle, fontSize = 22.sp, fontWeight = FontWeight.Bold, maxLines = 1, overflow = TextOverflow.Ellipsis)
                info.item.episodeSubtitle?.let { Text(it, fontSize = 15.sp, color = Flexarr.screen.copy(alpha = 0.8f), maxLines = 1, overflow = TextOverflow.Ellipsis) }
                info.transcodeSummary?.let { Text(it.uppercase(Locale.ROOT), fontSize = 10.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.usher, maxLines = 1, modifier = Modifier.padding(top = 4.dp)) }
            }
        }
        Column(Modifier.align(Alignment.BottomCenter).padding(horizontal = 40.dp, vertical = 26.dp), verticalArrangement = Arrangement.spacedBy(14.dp)) {
            Timeline(controller, scrubTo, onScrub, timeline)
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                // A film has neither episode button; an episode has both. Previous always works (with nothing before,
                // it starts this one again); Next is dimmed at the last.
                if (info?.item?.kind == MediaKind.Episode) {
                    TvActionButton("Previous episode", onClick = { controller.playPrevious(); onInteract() }, icon = Icons.Filled.SkipPrevious)
                    TvActionButton("Next episode", onClick = { controller.playNext(); onInteract() }, icon = Icons.Filled.SkipNext, enabled = info.next != null)
                }
                Spacer(Modifier.weight(1f))
                MenuBar(controller, onInteract, onAudioSync)
            }
        }
    }
}

/**
 * Where it's up to, and how long is left. Focused, left and right move a playhead through the film with the picture
 * there above it, faster the longer they're held; OK goes there. Without a move, OK pauses and plays.
 */
@Composable
private fun Timeline(controller: PlaybackController, scrubTo: Double?, onScrub: (Double?) -> Unit, focus: FocusRequester) {
    val source = remember { MutableInteractionSource() }
    val focused by source.collectIsFocusedAsState()
    val duration = controller.duration.coerceAtLeast(1.0)
    val shown = scrubTo ?: controller.position
    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        BoxWithConstraints(
            Modifier.fillMaxWidth().height(24.dp).focusRequester(focus).focusable(interactionSource = source).onKeyEvent { event ->
                if (event.type != KeyEventType.KeyDown) return@onKeyEvent false
                val held = event.nativeKeyEvent.repeatCount
                val step = when {
                    held > 30 -> 60
                    held > 10 -> 30
                    else -> 10
                }
                when (event.nativeKeyEvent.keyCode) {
                    KeyEvent.KEYCODE_DPAD_LEFT -> onScrub((shown - step).coerceAtLeast(0.0)).let { true }
                    KeyEvent.KEYCODE_DPAD_RIGHT -> onScrub((shown + step).coerceAtMost(duration)).let { true }
                    KeyEvent.KEYCODE_DPAD_CENTER, KeyEvent.KEYCODE_ENTER, KeyEvent.KEYCODE_NUMPAD_ENTER -> {
                        if (scrubTo != null) {
                            controller.seekTo(scrubTo)
                            onScrub(null)
                        } else {
                            controller.togglePlayPause()
                            onScrub(null)
                        }
                        true
                    }
                    else -> false
                }
            },
            contentAlignment = Alignment.CenterStart,
        ) {
            val fraction = (shown / duration).toFloat().coerceIn(0f, 1f)
            val track = if (focused) 8.dp else 5.dp
            Box(Modifier.fillMaxWidth().height(track).background(Flexarr.screen.copy(alpha = 0.3f), CircleShape))
            Box(Modifier.fillMaxWidth(fraction).height(track).background(Flexarr.screen, CircleShape))
            if (focused) Box(Modifier.offset(x = maxWidth * fraction - 8.dp).size(16.dp).background(Flexarr.screen, CircleShape))
            // While moving through it, the picture at the playhead's place, above it.
            val previews = controller.previews
            if (scrubTo != null && previews != null && previews.isAvailable) {
                previews.version // Read so a sheet arriving draws the picture.
                val image = previews.image(scrubTo)
                val width = 200.dp
                val height = width / previews.aspectRatio
                val x = (maxWidth * fraction - width / 2).coerceIn(0.dp, (maxWidth - width).coerceAtLeast(0.dp))
                Box(
                    // Taller than the timeline's row, so sized regardless of it, and lifted clear of the track.
                    Modifier.offset(x = x, y = -height / 2 - 16.dp).requiredSize(width, height).background(Color.Black, RoundedCornerShape(8.dp))
                        .border(1.dp, Flexarr.screen.copy(alpha = 0.6f), RoundedCornerShape(8.dp)),
                ) {
                    image?.let { Image(it, contentDescription = null, contentScale = ContentScale.Crop, modifier = Modifier.fillMaxSize()) }
                }
            }
        }
        Row(verticalAlignment = Alignment.CenterVertically) {
            Icon(if (controller.isPlaying) Icons.Filled.Pause else Icons.Filled.PlayArrow, contentDescription = if (controller.isPlaying) "Playing" else "Paused", modifier = Modifier.size(16.dp))
            Text(Format.timestamp(shown), fontSize = 13.sp, style = TimeStyle, modifier = Modifier.padding(start = 6.dp).widthIn(min = 64.dp))
            Spacer(Modifier.weight(1f))
            Text("−" + Format.timestamp((controller.duration - shown).coerceAtLeast(0.0)), fontSize = 13.sp, style = TimeStyle, textAlign = TextAlign.End)
        }
    }
}

/**
 * Moving the sound against the picture, as the phone's Audio sync panel: left and right a step at a time (bigger
 * steps while held). The value is the household's, kept by the server.
 */
@Composable
private fun TvAudioSync(controller: PlaybackController, onDone: () -> Unit, modifier: Modifier = Modifier) {
    val seconds = controller.audioSync ?: return
    val done = remember { FocusRequester() }
    LaunchedEffect(Unit) { runCatching { done.requestFocus() } }
    fun step(by: Double) = controller.audioSync?.let { controller.setAudioDelay(it + by) }
    Column(
        modifier.glass(RoundedCornerShape(22.dp)).padding(horizontal = 24.dp, vertical = 18.dp).onPreviewKeyEvent { event ->
            if (event.type != KeyEventType.KeyDown) return@onPreviewKeyEvent false
            // A press moves it a little; held, it moves in bigger steps.
            val by = if (event.nativeKeyEvent.repeatCount > 0) PlaybackController.AUDIO_SYNC_JUMP else PlaybackController.AUDIO_SYNC_STEP
            when (event.nativeKeyEvent.keyCode) {
                KeyEvent.KEYCODE_DPAD_LEFT -> step(-by).let { true }
                KeyEvent.KEYCODE_DPAD_RIGHT -> step(by).let { true }
                else -> false
            }
        },
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Text("Audio sync", fontSize = 13.sp, fontWeight = FontWeight.SemiBold, color = Color.White.copy(alpha = 0.7f))
        Text(PlaybackController.syncLabel(seconds), fontSize = 26.sp, fontWeight = FontWeight.Bold, style = TimeStyle)
        Text(
            if (seconds > 0.0005) "Sound later" else if (seconds < -0.0005) "Sound earlier" else "As the file is",
            fontSize = 12.sp, fontWeight = FontWeight.Medium, color = Color.White.copy(alpha = 0.6f),
        )
        Text("Left and right move the sound", fontSize = 11.sp, color = Flexarr.usher)
        Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
            TvActionButton("Reset", onClick = { controller.setAudioDelay(0.0) })
            TvActionButton("Done", onClick = onDone, prominent = true, ambient = Flexarr.screen, modifier = Modifier.focusRequester(done))
        }
    }
}

@Composable
private fun PlayerError(controller: PlaybackController, message: String, onClose: () -> Unit) {
    val first = remember { FocusRequester() }
    LaunchedEffect(message) { runCatching { first.requestFocus() } }
    Column(Modifier.fillMaxSize().padding(60.dp), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(16.dp, Alignment.CenterVertically)) {
        Text(message, fontSize = 17.sp, fontWeight = FontWeight.Medium, textAlign = TextAlign.Center, modifier = Modifier.widthIn(max = 600.dp))
        Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            val retry = controller.retryLabel
            if (retry != null) TvActionButton(retry, onClick = controller::retry, prominent = true, modifier = Modifier.focusRequester(first))
            TvActionButton("Close", onClick = onClose, prominent = retry == null, modifier = if (retry == null) Modifier.focusRequester(first) else Modifier)
        }
    }
}
