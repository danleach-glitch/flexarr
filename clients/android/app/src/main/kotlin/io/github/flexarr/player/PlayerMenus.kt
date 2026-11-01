package io.github.flexarr.player

import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.tween
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.KeyboardArrowLeft
import androidx.compose.material.icons.automirrored.filled.KeyboardArrowRight
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Bedtime
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.ClosedCaption
import androidx.compose.material.icons.filled.GraphicEq
import androidx.compose.material.icons.filled.Remove
import androidx.compose.material.icons.filled.Search
import androidx.compose.material.icons.filled.Speed
import androidx.compose.material.icons.outlined.Bedtime
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.onClick
import androidx.compose.ui.semantics.role
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.media3.common.text.Cue
import androidx.media3.ui.CaptionStyleCompat
import androidx.media3.ui.SubtitleView
import io.github.flexarr.core.SubtitlePosition
import io.github.flexarr.core.SubtitleSize
import io.github.flexarr.core.Teletext
import io.github.flexarr.data.SoundSettings
import io.github.flexarr.player.subtitles.StyledSubtitles
import io.github.flexarr.ui.theme.Flexarr
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import io.github.flexarr.ui.theme.Tokens

// The player's menus for audio, subtitles, speed and the sleep timer, and what they open: the iPhone player's
// UIMenus, Audio sync panel and "Still watching?" question (PlayerViewController and PlayerPanels.swift).

/** The four menus side by side on one glass pill, above the timeline at the right. A menu with nothing in it isn't there; Audio always has the sound settings. */
@Composable
internal fun MenuBar(controller: PlaybackController, onInteract: () -> Unit, onAudioSync: () -> Unit, modifier: Modifier = Modifier) {
    val info = controller.info ?: return
    val hasSubtitles = info.subtitles.isNotEmpty() || controller.canSearchSubtitles
    Row(modifier.height(44.dp).glass(RoundedCornerShape(22.dp)).padding(horizontal = 2.dp), verticalAlignment = Alignment.CenterVertically) {
        MenuButton(Icons.Filled.GraphicEq, "Audio", onInteract) { close -> AudioMenu(controller, close, onAudioSync) }
        if (hasSubtitles) MenuButton(Icons.Filled.ClosedCaption, "Subtitles", onInteract) { close -> SubtitlesMenu(controller, close) }
        val badge = if (controller.rate == 1f) null else PlaybackController.rateLabel(controller.rate)
        MenuButton(Icons.Filled.Speed, "Speed", onInteract, badge = badge) { close -> SpeedMenu(controller, close) }
        SleepMenuButton(controller.sleep, controller.offersEndOfEpisode, onInteract)
    }
}

/** The sleep timer's menu alone on its glass pill, for a live channel (which has no other menus). */
@Composable
internal fun SleepMenuBar(sleep: VideoSleep, onInteract: () -> Unit, modifier: Modifier = Modifier) {
    Row(modifier.height(44.dp).glass(RoundedCornerShape(22.dp)).padding(horizontal = 2.dp), verticalAlignment = Alignment.CenterVertically) {
        SleepMenuButton(sleep, offersEndOfEpisode = false, onInteract)
    }
}

@Composable
private fun SleepMenuButton(sleep: VideoSleep, offersEndOfEpisode: Boolean, onInteract: () -> Unit) {
    val sleeping = sleep.setting != null
    MenuButton(if (sleeping) Icons.Filled.Bedtime else Icons.Outlined.Bedtime, "Sleep timer", onInteract) { close -> SleepMenu(sleep, offersEndOfEpisode, close) }
}

/** A menu's icon in its slot; tapping opens its menu (the controls stay up while it's open). */
@Composable
private fun MenuButton(
    icon: ImageVector,
    label: String,
    onInteract: () -> Unit,
    badge: String? = null,
    content: @Composable MenuScope.(close: () -> Unit) -> Unit,
) {
    var open by remember { mutableStateOf(false) }
    // A submenu (Timing, Size, Position) shows in place of the menu, with a way back.
    var page by remember { mutableStateOf<String?>(null) }
    Box {
        Row(
            Modifier.height(44.dp).widthIn(min = 44.dp).clickableNoRipple({ open = true; page = null; onInteract() }).padding(horizontal = 10.dp),
            horizontalArrangement = Arrangement.spacedBy(6.dp, Alignment.CenterHorizontally),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Icon(icon, contentDescription = label, tint = Flexarr.screen, modifier = Modifier.size(22.dp))
            badge?.let { Text(it, fontSize = 13.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen) }
        }
        DropdownMenu(open, onDismissRequest = { open = false }, containerColor = Flexarr.seat) {
            MenuScope(page, openPage = { page = it }).content { open = false }
        }
    }
    // The controls stay up while a menu is open.
    LaunchedEffect(open) {
        while (open) {
            onInteract()
            delay(1_000)
        }
    }
}

/** Which submenu is showing, so a menu can draw its main list or one of its pages. A new one for each page. */
internal data class MenuScope(val page: String?, val openPage: (String?) -> Unit)

@Composable
private fun MenuTitle(text: String) {
    Text(text, fontSize = 13.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.usher, modifier = Modifier.padding(horizontal = 16.dp, vertical = 8.dp))
}

/** A choice with a tick when it's the current one. */
@Composable
private fun Choice(label: String, checked: Boolean, enabled: Boolean = true, onClick: () -> Unit) {
    DropdownMenuItem(
        text = { Text(label, color = Flexarr.screen) },
        onClick = onClick,
        enabled = enabled,
        leadingIcon = { Box(Modifier.size(20.dp)) { if (checked) Icon(Icons.Filled.Check, contentDescription = "Chosen", tint = Flexarr.screen, modifier = Modifier.size(20.dp)) } },
    )
}

/** An entry that does something, with its icon. */
@Composable
private fun Action(label: String, icon: ImageVector, enabled: Boolean = true, onClick: () -> Unit) {
    DropdownMenuItem(
        text = { Text(label, color = Flexarr.screen) },
        onClick = onClick,
        enabled = enabled,
        leadingIcon = { Icon(icon, contentDescription = null, tint = Flexarr.screen, modifier = Modifier.size(20.dp)) },
    )
}

/** An entry that opens a submenu, saying what's chosen in it ("Timing  In time"). */
@Composable
private fun Submenu(label: String, value: String, onClick: () -> Unit) {
    DropdownMenuItem(
        text = {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(label, color = Flexarr.screen, modifier = Modifier.weight(1f, fill = false))
                Text("  $value", color = Flexarr.usher, fontSize = 14.sp)
            }
        },
        onClick = onClick,
        leadingIcon = { Box(Modifier.size(20.dp)) },
        trailingIcon = { Icon(Icons.AutoMirrored.Filled.KeyboardArrowRight, contentDescription = null, tint = Flexarr.usher) },
    )
}

@Composable
private fun Back(title: String, onClick: () -> Unit) {
    DropdownMenuItem(
        text = { Text(title, fontWeight = FontWeight.SemiBold, color = Flexarr.screen) },
        onClick = onClick,
        leadingIcon = { Icon(Icons.AutoMirrored.Filled.KeyboardArrowLeft, contentDescription = "Back", tint = Flexarr.screen) },
    )
    Divider()
}

@Composable
private fun Divider() = HorizontalDivider(color = Flexarr.aisle, modifier = Modifier.padding(vertical = 4.dp))

/** The file's audio tracks (when there's a choice), Audio sync, and the phone's dialogue boost, night mode and speakers. */
@Composable
private fun MenuScope.AudioMenu(controller: PlaybackController, close: () -> Unit, onAudioSync: () -> Unit) {
    val info = controller.info ?: return
    val sound = controller.sound
    when (page) {
        "dialogue" -> {
            Back("Dialogue boost") { openPage(null) }
            SoundSettings.DialogueBoost.entries.forEach { boost ->
                Choice(boost.name, boost == sound.dialogueBoost) { controller.changeSound { it.copy(dialogueBoost = boost) } }
            }
            return
        }
        "night" -> {
            Back("Night mode") { openPage(null) }
            SoundSettings.NightMode.entries.forEach { mode ->
                Choice(if (mode == SoundSettings.NightMode.Scheduled) sound.nightHours else mode.name, mode == sound.nightMode) {
                    controller.changeSound { it.copy(nightMode = mode) }
                }
            }
            return
        }
        "speakers" -> {
            Back("Speakers") { openPage(null) }
            SoundSettings.Speakers.entries.forEach { speakers ->
                Choice(speakers.label, speakers == sound.speakers) { controller.changeSound { it.copy(speakers = speakers) } }
            }
            return
        }
    }
    MenuTitle("Audio")
    if (info.audioTracks.size > 1) {
        info.audioTracks.forEach { track ->
            Choice(track.label, track.index == info.selectedAudioIndex, enabled = !controller.isReopening) {
                close()
                controller.selectAudio(track.index)
            }
        }
    }
    controller.audioSync?.let { sync ->
        if (info.audioTracks.size > 1) Divider()
        DropdownMenuItem(
            text = {
                Column {
                    Text("Audio sync", color = Flexarr.screen)
                    Text(if (sync == 0.0) "As the file is" else PlaybackController.syncLabel(sync), fontSize = 13.sp, color = Flexarr.usher)
                }
            },
            onClick = { close(); onAudioSync() },
            leadingIcon = { Icon(Icons.Filled.GraphicEq, contentDescription = null, tint = Flexarr.screen, modifier = Modifier.size(20.dp)) },
        )
    }
    if (info.audioTracks.size > 1 || controller.audioSync != null) Divider()
    Submenu("Dialogue boost", sound.dialogueBoost.name) { openPage("dialogue") }
    Submenu("Night mode", if (sound.nightMode == SoundSettings.NightMode.Scheduled) sound.nightHours else sound.nightMode.name) { openPage("night") }
    Submenu("Speakers", sound.speakers.label) { openPage("speakers") }
}

/** Off and each track; then Teletext page (for teletext), Timing, Size and Position, as the iPhone's subtitles menu. */
@Composable
private fun MenuScope.SubtitlesMenu(controller: PlaybackController, close: () -> Unit) {
    val info = controller.info ?: return
    when (page) {
        "timing" -> {
            val current = info.subtitleDelaySeconds ?: 0.0
            Back("Timing") { openPage(null) }
            (PlaybackController.delayChoices + current).distinct().sorted().forEach { seconds ->
                Choice(PlaybackController.delayLabel(seconds), seconds == current, enabled = !controller.isReopening) {
                    close()
                    controller.setSubtitleDelay(seconds)
                }
            }
        }
        "teletext" -> {
            Back("Teletext page") { openPage(null) }
            (listOf(null) + Teletext.pages).forEach { number ->
                Choice(number?.toString() ?: "Automatic", number == controller.teletextPage, enabled = !controller.isReopening) {
                    close()
                    controller.chooseTeletextPage(number)
                }
            }
        }
        "size" -> {
            Back("Size") { openPage(null) }
            SubtitleSize.choices.forEach { size ->
                Choice(size.label, size == controller.subtitleSize) { controller.chooseSubtitleSize(size) }
            }
        }
        "position" -> {
            Back("Position") { openPage(null) }
            SubtitlePosition.choices.forEach { position ->
                Choice(position.label, position == controller.subtitlePosition) {
                    controller.setSubtitlePlacement(position, controller.subtitlesInBlackBars)
                }
            }
            Divider()
            Choice("In the black bars", controller.subtitlesInBlackBars) {
                controller.setSubtitlePlacement(controller.subtitlePosition, !controller.subtitlesInBlackBars)
            }
        }
        else -> {
            MenuTitle("Subtitles")
            Choice("Off", controller.subtitleKey == null, enabled = !controller.isReopening) {
                close()
                controller.selectSubtitle(null)
            }
            info.subtitles.forEach { track ->
                Choice(track.label, track.key == controller.subtitleKey, enabled = !controller.isReopening) {
                    close()
                    controller.selectSubtitle(track)
                }
            }
            Divider()
            // Teletext subtitles are on a page of their own, which a recording doesn't always mark.
            if (controller.canChooseTeletextPage) {
                Submenu("Teletext page", controller.teletextPage?.toString() ?: "Automatic") { openPage("teletext") }
            }
            // Burned into the picture, they can't be moved: the server draws them where they fall.
            if (!info.subtitleBurnedIn) Submenu("Timing", PlaybackController.delayLabel(info.subtitleDelaySeconds ?: 0.0)) { openPage("timing") }
            Submenu("Size", controller.subtitleSize.label) { openPage("size") }
            Submenu("Position", controller.subtitlePosition.label) { openPage("position") }
            if (controller.canSearchSubtitles) {
                Divider()
                Action("Search for subtitles…", Icons.Filled.Search) {
                    close()
                    controller.searchSubtitles()
                }
                val resyncable = controller.resyncableSubtitle
                when {
                    controller.isResyncing -> Action("Re-syncing…", Icons.Filled.GraphicEq, enabled = false) {}
                    resyncable != null -> Action("Re-sync to audio", Icons.Filled.GraphicEq) {
                        close()
                        controller.resyncSubtitles(resyncable)
                    }
                }
            }
        }
    }
}

/** 0.5× to 2×, with Normal; a show's remembered speed is listed too if it's another. */
@Composable
private fun SpeedMenu(controller: PlaybackController, close: () -> Unit) {
    MenuTitle("Speed")
    (PlaybackController.rateChoices + controller.rate).distinct().sorted().filter { it <= PlaybackController.MAX_RATE }.forEach { rate ->
        Choice(PlaybackController.rateLabel(rate), rate == controller.rate) {
            close()
            controller.chooseRate(rate)
        }
    }
}

/** Off, 15 to 60 minutes, End of episode (episodes only); with the minutes left while one runs. */
@Composable
private fun SleepMenu(sleep: VideoSleep, offersEndOfEpisode: Boolean, close: () -> Unit) {
    val current = sleep.setting
    MenuTitle(sleep.timer.menuTitle)
    Choice("Off", current == null) {
        close()
        sleep.set(null)
    }
    Divider()
    val choices = SleepTimer.minuteChoices.map { SleepTimer.Setting.Minutes(it) } +
        if (offersEndOfEpisode) listOf(SleepTimer.Setting.EndOfEpisode) else emptyList()
    choices.forEach { setting ->
        Choice(setting.label, setting == current) {
            close()
            sleep.set(setting)
        }
    }
}

/**
 * Nudging a file's sound earlier or later when it's out of step with the picture: a glass panel over the bottom of
 * the picture, opened from the audio menu. Each press moves it 10 ms; holding a button keeps moving it in 50 ms jumps
 * until let go, up to two seconds either way. The value is the household's, kept by the server.
 */
@Composable
internal fun AudioSyncPanel(controller: PlaybackController, onDone: () -> Unit, modifier: Modifier = Modifier) {
    val seconds = controller.audioSync ?: return
    Column(
        modifier.glass(RoundedCornerShape(Tokens.radiusPanel)).padding(horizontal = 16.dp, vertical = 14.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(10.dp),
    ) {
        Text("Audio sync", fontSize = 13.sp, fontWeight = FontWeight.SemiBold, color = Color.White.copy(alpha = 0.7f))
        Row(horizontalArrangement = Arrangement.spacedBy(10.dp), verticalAlignment = Alignment.CenterVertically) {
            // The latest value, not this composition's: a hold jumps several times before it recomposes.
            HoldGlassIcon(Icons.Filled.Remove, "Sound earlier") { step -> controller.audioSync?.let { controller.setAudioDelay(it - step) } }
            Column(Modifier.width(110.dp), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(2.dp)) {
                Text(PlaybackController.syncLabel(seconds), fontSize = 24.sp, fontWeight = FontWeight.Bold, style = io.github.flexarr.ui.podcasts.Tabular, color = Color.White)
                Text(
                    if (seconds > 0.0005) "Sound later" else if (seconds < -0.0005) "Sound earlier" else "As the file is",
                    fontSize = 12.sp, fontWeight = FontWeight.Medium, color = Color.White.copy(alpha = 0.6f),
                )
            }
            HoldGlassIcon(Icons.Filled.Add, "Sound later") { step -> controller.audioSync?.let { controller.setAudioDelay(it + step) } }
        }
        Row(horizontalArrangement = Arrangement.spacedBy(5.dp)) {
            Pill("Reset", filled = false) { controller.setAudioDelay(0.0) }
            Pill("Done", filled = true, onClick = onDone)
        }
    }
}

/**
 * The sleep timer running out over a video: the picture dims and "Still watching?" offers 15 minutes more (or a tap
 * anywhere) or turning the timer off. Once it has run out the dim stays over the paused picture until someone touches
 * the screen.
 */
@Composable
internal fun SleepPrompt(sleep: VideoSleep) {
    val phase = sleep.phase
    val dim by animateFloatAsState(
        when (phase) {
            VideoSleep.Phase.Off -> 0f
            VideoSleep.Phase.Asking -> 0.55f
            VideoSleep.Phase.Asleep -> 0.7f
        },
        tween(if (phase == VideoSleep.Phase.Asking) 3_000 else 600),
        label = "sleep dim",
    )
    if (dim > 0f) Box(Modifier.fillMaxSize().background(Color.Black.copy(alpha = dim)))
    AnimatedVisibility(phase != VideoSleep.Phase.Off, enter = fadeIn(), exit = fadeOut(), modifier = Modifier.fillMaxSize()) {
        Box(
            Modifier.fillMaxSize().pointerInput(phase) {
                // A tap anywhere is 15 minutes more while it asks, and wakes the screen once it's run out.
                detectTapGestures {
                    if (sleep.phase == VideoSleep.Phase.Asking) sleep.extend() else sleep.wake()
                }
            },
            contentAlignment = Alignment.Center,
        ) {
            if (phase == VideoSleep.Phase.Asleep) {
                Text("Paused by the sleep timer", fontSize = 26.sp, fontWeight = FontWeight.Bold, color = Color.White, modifier = Modifier.alpha(0.6f))
            } else {
                Column(horizontalAlignment = Alignment.CenterHorizontally) {
                    Text("Still watching?", fontSize = 26.sp, fontWeight = FontWeight.Bold, color = Color.White)
                    Text(sleep.detail, fontSize = 15.sp, fontWeight = FontWeight.Medium, style = io.github.flexarr.ui.podcasts.Tabular, color = Color.White.copy(alpha = 0.75f), modifier = Modifier.padding(top = 8.dp))
                    Row(Modifier.padding(top = 22.dp), horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                        Pill("Cancel timer", filled = false) { sleep.set(null) }
                        Pill("+15 min", filled = true, icon = Icons.Filled.Bedtime, onClick = sleep::extend)
                    }
                }
            }
        }
    }
}

/**
 * The subtitles over the picture at the profile's size and height. With "In the black bars", a wide picture's bar below
 * takes them instead, when they fit there (as on iPhone); otherwise they stay on the picture.
 */
@Composable
internal fun SubtitleLayer(controller: PlaybackController) {
    val cues = StyledSubtitles.forScreen(controller.cues)
    if (cues.isEmpty()) return
    val size = controller.videoSize
    BoxWithConstraints(Modifier.fillMaxSize()) {
        val density = LocalDensity.current
        val screenWidth = constraints.maxWidth.toFloat()
        val screenHeight = constraints.maxHeight.toFloat()
        val ratio = if (size.width > 0 && size.height > 0) size.width * size.pixelWidthHeightRatio / size.height else 16f / 9f
        // Where the picture is (fitted, centred), in pixels.
        val pictureWidth = if (ratio > screenWidth / screenHeight) screenWidth else screenHeight * ratio
        val pictureHeight = pictureWidth / ratio
        val pictureTop = (screenHeight - pictureHeight) / 2
        val scale = controller.subtitleSize.percent / 100f
        // Text sized by the picture's height, wherever it's drawn.
        val textSize = SubtitleView.DEFAULT_TEXT_SIZE_FRACTION * scale * pictureHeight
        val lines = cues.sumOf { cue -> (cue.text?.count { it == '\n' } ?: 0) + 1 }
        val margin = with(density) { 6.dp.toPx() }
        val bar = screenHeight - pictureTop - pictureHeight
        val inBar = controller.subtitlesInBlackBars && controller.subtitlePosition != SubtitlePosition.Top &&
            bar >= lines * textSize * LINE_HEIGHT + with(density) { 12.dp.toPx() }

        val regionTop = if (inBar) pictureTop + pictureHeight else pictureTop
        val regionHeight = if (inBar) bar else pictureHeight
        val shown = if (inBar) {
            // Just below the picture, the lines in their own order.
            cues.map { it.buildUpon().setLine(margin / regionHeight, Cue.LINE_TYPE_FRACTION).setLineAnchor(Cue.ANCHOR_TYPE_START).build() }
        } else {
            placeCues(cues, controller.subtitlePosition, scale)
        }
        AndroidView(
            factory = { context ->
                SubtitleView(context).apply {
                    setStyle(CaptionStyleCompat(android.graphics.Color.WHITE, android.graphics.Color.TRANSPARENT, android.graphics.Color.TRANSPARENT, CaptionStyleCompat.EDGE_TYPE_DROP_SHADOW, android.graphics.Color.BLACK, null))
                }
            },
            update = { view ->
                view.setFixedTextSize(android.util.TypedValue.COMPLEX_UNIT_PX, textSize)
                // The cues carry their own place, measured from the region's edges.
                view.setBottomPaddingFraction(0f)
                view.setCues(shown)
            },
            modifier = with(density) {
                Modifier
                    .offset(x = ((screenWidth - pictureWidth) / 2).toDp(), y = regionTop.toDp())
                    .size(width = pictureWidth.toDp(), height = regionHeight.toDp())
            },
        )
    }
}

@Composable
private fun GlassIcon(icon: ImageVector, label: String, onClick: () -> Unit) {
    Box(Modifier.size(40.dp).glass(CircleShape).clickableNoRipple(onClick), contentAlignment = Alignment.Center) {
        Icon(icon, contentDescription = label, tint = Color.White, modifier = Modifier.size(20.dp))
    }
}

/** A [GlassIcon] that moves by [PlaybackController.AUDIO_SYNC_STEP] when tapped and by [PlaybackController.AUDIO_SYNC_JUMP] over and over while held. */
@Composable
private fun HoldGlassIcon(icon: ImageVector, label: String, onStep: (Double) -> Unit) {
    val step by rememberUpdatedState(onStep)
    Box(
        Modifier.size(40.dp).glass(CircleShape)
            .semantics { role = Role.Button; onClick(label) { step(PlaybackController.AUDIO_SYNC_STEP); true } }
            .pointerInput(Unit) {
                coroutineScope {
                    detectTapGestures(onPress = {
                        var jumped = false
                        val hold = launch {
                            delay(400)
                            while (true) {
                                jumped = true
                                step(PlaybackController.AUDIO_SYNC_JUMP)
                                delay(250)
                            }
                        }
                        val released = tryAwaitRelease()
                        hold.cancel()
                        if (released && !jumped) step(PlaybackController.AUDIO_SYNC_STEP)
                    })
                }
            },
        contentAlignment = Alignment.Center,
    ) {
        Icon(icon, contentDescription = null, tint = Color.White, modifier = Modifier.size(20.dp))
    }
}

/** The player's capsule buttons: filled white for the main one, glass for the other. */
@Composable
private fun Pill(label: String, filled: Boolean, icon: ImageVector? = null, onClick: () -> Unit) {
    Row(
        Modifier.height(40.dp)
            .then(if (filled) Modifier.background(Color.White, CircleShape) else Modifier.glass(CircleShape))
            .clickableNoRipple(onClick)
            .padding(horizontal = 18.dp),
        horizontalArrangement = Arrangement.spacedBy(6.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        val tint = if (filled) Color.Black else Color.White
        icon?.let { Icon(it, contentDescription = null, tint = tint, modifier = Modifier.size(18.dp)) }
        Text(label, fontSize = 15.sp, fontWeight = FontWeight.SemiBold, color = tint)
    }
}

/**
 * Puts the cues a file leaves to the player (WebVTT's "auto" lines, counted from the bottom) where the profile wants
 * them: the bottom of the last line [SubtitlePosition.lift] of the picture's height above its foot, as the web and the
 * Apple apps place them, or at the top. Cues the file places itself (a sign's translation, say) stay where they are.
 */
internal fun placeCues(cues: List<Cue>, position: SubtitlePosition, scale: Float): List<Cue> {
    // One line's share of the picture's height, for stacking cues that say "the line above the last".
    val lineStep = SubtitleView.DEFAULT_TEXT_SIZE_FRACTION * scale * LINE_HEIGHT
    val lift = position.lift
    return cues.map { cue ->
        val fromBottom = when {
            cue.line == Cue.DIMEN_UNSET -> 0
            cue.lineType == Cue.LINE_TYPE_NUMBER && cue.line < 0 -> (-cue.line).toInt() - 1
            else -> return@map cue
        }
        if (lift != null) {
            cue.buildUpon().setLine(1f - lift - fromBottom * lineStep, Cue.LINE_TYPE_FRACTION).setLineAnchor(Cue.ANCHOR_TYPE_END).build()
        } else {
            cue.buildUpon().setLine(TOP_INSET + fromBottom * lineStep, Cue.LINE_TYPE_FRACTION).setLineAnchor(Cue.ANCHOR_TYPE_START).build()
        }
    }
}

/** A line of subtitles, as a multiple of the text's size. */
private const val LINE_HEIGHT = 1.25f

/** How far below the picture's top edge subtitles at the top start. */
private const val TOP_INSET = 0.04f
