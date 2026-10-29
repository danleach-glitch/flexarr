package io.github.flexarr.ui.podcasts

import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.spring
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.sizeIn
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Bedtime
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Forward30
import androidx.compose.material.icons.filled.Pause
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Replay
import androidx.compose.material.icons.outlined.Bedtime
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Slider
import androidx.compose.material3.SliderDefaults
import androidx.compose.material3.Text
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.scale
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.github.flexarr.AppModel
import io.github.flexarr.audio.PodcastPlayer
import io.github.flexarr.core.Format
import io.github.flexarr.player.SleepTimer
import io.github.flexarr.ui.components.Edge
import io.github.flexarr.ui.components.pressable
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.RoomBackground
import io.github.flexarr.ui.theme.glowColor
import io.github.flexarr.ui.theme.marquee
import kotlinx.coroutines.delay

/** Digits that keep their width as they count. */
internal val Tabular = TextStyle(fontFeatureSettings = "tnum")

/**
 * What's playing, above the tabs on every screen: close, the artwork and title (opening Now Playing), play or pause,
 * and forward 30 seconds. The iPhone app's NowPlayingBar.
 */
@Composable
fun NowPlayingBar(model: AppModel) {
    val player = model.podcasts
    val episode = player.current ?: return
    var open by remember { mutableStateOf(false) }
    Row(
        Modifier.fillMaxWidth().padding(horizontal = 8.dp, vertical = 6.dp).shadow(12.dp, RoundedCornerShape(14.dp)).clip(RoundedCornerShape(14.dp)).background(Flexarr.seat).padding(end = 6.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Box(Modifier.size(width = 36.dp, height = 56.dp).pressable(player::stop), contentAlignment = Alignment.Center) {
            Icon(Icons.Filled.Close, contentDescription = "Stop and close", tint = Flexarr.usher, modifier = Modifier.size(16.dp))
        }
        Row(Modifier.weight(1f).pressable({ open = true }).padding(vertical = 6.dp), horizontalArrangement = Arrangement.spacedBy(12.dp), verticalAlignment = Alignment.CenterVertically) {
            PodcastArtwork(episode.artwork.thumb ?: episode.artwork.poster, episode.podcastTitle, episode.accentColor, Modifier.size(44.dp), imageWidth = 185, cornerRadius = 6.dp)
            Column(Modifier.weight(1f)) {
                Text(episode.title, fontSize = 15.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen, maxLines = 1, overflow = TextOverflow.Ellipsis)
                Text(episode.podcastTitle, fontSize = 12.sp, color = Flexarr.usher, maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
        }
        if (player.isLoading) {
            Box(Modifier.size(40.dp), contentAlignment = Alignment.Center) { CircularProgressIndicator(color = Flexarr.screen, modifier = Modifier.size(20.dp), strokeWidth = 2.dp) }
        } else {
            RoundButton(if (player.isPlaying) Icons.Filled.Pause else Icons.Filled.PlayArrow, if (player.isPlaying) "Pause" else "Play", player::togglePlayPause)
        }
        RoundButton(Icons.Filled.Forward30, "Forward 30 seconds") { player.skip(30) }
    }
    if (open) NowPlayingSheet(model, onDismiss = { open = false })
}

@Composable
private fun RoundButton(icon: ImageVector, label: String, onClick: () -> Unit) {
    Box(Modifier.size(40.dp).clip(CircleShape).pressable(onClick), contentAlignment = Alignment.Center) {
        Icon(icon, contentDescription = label, tint = Flexarr.screen, modifier = Modifier.size(24.dp))
    }
}

/**
 * The episode that's playing, as a sheet: its artwork, title, a scrubber, back 15 and forward 30, its speed and a sleep
 * timer, and Up Next. The iPhone app's NowPlayingView, for a podcast.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun NowPlayingSheet(model: AppModel, onDismiss: () -> Unit) {
    val player = model.podcasts
    val episode = player.current
    LaunchedEffect(episode == null) { if (episode == null) onDismiss() }
    LaunchedEffect(episode?.id) { model.loadUpNext() }
    if (episode == null) return
    ModalBottomSheet(onDismissRequest = onDismiss, sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true), containerColor = Flexarr.room, dragHandle = null) {
        RoomBackground(ambient = glowColor(episode.accentColor), intensity = 0.45f) {
            Column(
                Modifier.verticalScroll(rememberScrollState()).padding(horizontal = 28.dp).padding(bottom = 32.dp),
                horizontalAlignment = Alignment.CenterHorizontally,
                verticalArrangement = Arrangement.spacedBy(24.dp),
            ) {
                Box(Modifier.padding(top = 10.dp).size(width = 40.dp, height = 5.dp).clip(CircleShape).background(Flexarr.aisle))
                // Smaller while paused, as every music app does.
                val scale by animateFloatAsState(if (player.isPlaying) 1f else 0.9f, spring(dampingRatio = 0.8f, stiffness = 300f), label = "cover")
                PodcastArtwork(
                    episode.artwork.poster ?: episode.artwork.thumb, episode.podcastTitle, episode.accentColor,
                    Modifier.padding(top = 8.dp).widthIn(max = 320.dp).fillMaxWidth().scale(scale).shadow(24.dp, RoundedCornerShape(12.dp)),
                    imageWidth = 800, cornerRadius = 12.dp,
                )
                Column(horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(4.dp)) {
                    Text(episode.title, fontSize = 20.sp, fontWeight = FontWeight.Bold, color = Flexarr.screen, textAlign = TextAlign.Center, maxLines = 2, overflow = TextOverflow.Ellipsis)
                    Text(episode.podcastTitle, fontSize = 15.sp, fontWeight = FontWeight.Medium, color = Flexarr.usher, maxLines = 1)
                }
                Scrubber(player)
                player.errorMessage?.let { Text(it, fontSize = 13.sp, color = Flexarr.danger, textAlign = TextAlign.Center) }
                Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceEvenly, verticalAlignment = Alignment.CenterVertically) {
                    Transport(Icons.Filled.Replay, "Back 15 seconds", badge = "15") { player.skip(-15) }
                    Box(Modifier.size(76.dp).clip(CircleShape).background(Flexarr.screen).pressable(player::togglePlayPause), contentAlignment = Alignment.Center) {
                        if (player.isLoading) CircularProgressIndicator(color = Flexarr.room, modifier = Modifier.size(28.dp))
                        else Icon(if (player.isPlaying) Icons.Filled.Pause else Icons.Filled.PlayArrow, contentDescription = if (player.isPlaying) "Pause" else "Play", tint = Flexarr.room, modifier = Modifier.size(32.dp))
                    }
                    Transport(Icons.Filled.Forward30, "Forward 30 seconds") { player.skip(30) }
                }
                Row(horizontalArrangement = Arrangement.spacedBy(36.dp), verticalAlignment = Alignment.CenterVertically) {
                    SpeedMenu(player)
                    SleepMenu(player)
                }
                UpNext(model)
            }
        }
    }
}

@Composable
private fun Scrubber(player: PodcastPlayer) {
    var scrubbing by remember { mutableStateOf<Float?>(null) }
    val shown = scrubbing?.toDouble() ?: player.position
    Column(Modifier.fillMaxWidth(), verticalArrangement = Arrangement.spacedBy(4.dp)) {
        Slider(
            value = shown.toFloat().coerceIn(0f, player.duration.coerceAtLeast(1.0).toFloat()),
            onValueChange = { scrubbing = it },
            onValueChangeFinished = {
                scrubbing?.let { player.seek(it.toDouble()) }
                scrubbing = null
            },
            valueRange = 0f..player.duration.coerceAtLeast(1.0).toFloat(),
            colors = SliderDefaults.colors(thumbColor = Flexarr.screen, activeTrackColor = Flexarr.screen, inactiveTrackColor = Flexarr.screen.copy(alpha = 0.25f)),
        )
        Row(Modifier.fillMaxWidth()) {
            Text(Format.timestamp(shown), fontSize = 12.sp, fontWeight = FontWeight.Medium, color = Flexarr.usher, style = Tabular)
            Spacer(Modifier.weight(1f))
            if (player.duration > 0) Text("-" + Format.timestamp((player.duration - shown).coerceAtLeast(0.0)), fontSize = 12.sp, fontWeight = FontWeight.Medium, color = Flexarr.usher, style = Tabular)
        }
    }
}

@Composable
private fun Transport(icon: ImageVector, label: String, badge: String? = null, onClick: () -> Unit) {
    Box(Modifier.size(56.dp).clip(CircleShape).pressable(onClick), contentAlignment = Alignment.Center) {
        Icon(icon, contentDescription = label, tint = Flexarr.screen, modifier = Modifier.size(30.dp))
        // Material has a 30 but no 15: the number goes inside the arrow.
        badge?.let { Text(it, fontSize = 9.sp, fontWeight = FontWeight.Bold, color = Flexarr.screen, modifier = Modifier.padding(top = 3.dp)) }
    }
}

@Composable
private fun SpeedMenu(player: PodcastPlayer) {
    var open by remember { mutableStateOf(false) }
    Box {
        Box(Modifier.sizeIn(minWidth = 44.dp, minHeight = 44.dp).pressable({ open = true }), contentAlignment = Alignment.Center) {
            Text(
                PodcastPlayer.speedLabel(player.speed), fontSize = 15.sp, fontWeight = FontWeight.Bold, style = Tabular,
                color = if (player.speed == 1.0) Flexarr.screen else Flexarr.silver,
            )
        }
        DropdownMenu(open, onDismissRequest = { open = false }, containerColor = Flexarr.seat) {
            PodcastPlayer.speeds.forEach { speed ->
                Choice(PodcastPlayer.speedLabel(speed), speed == player.speed) {
                    open = false
                    player.chooseSpeed(speed)
                }
            }
        }
    }
}

@Composable
private fun SleepMenu(player: PodcastPlayer) {
    var open by remember { mutableStateOf(false) }
    val setting = player.sleep.setting
    Box {
        Row(
            Modifier.sizeIn(minWidth = 44.dp, minHeight = 44.dp).pressable({ open = true }),
            horizontalArrangement = Arrangement.spacedBy(4.dp, Alignment.CenterHorizontally),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            val tint = if (setting == null) Flexarr.screen else Flexarr.silver
            Icon(if (setting == null) Icons.Outlined.Bedtime else Icons.Filled.Bedtime, contentDescription = if (setting == null) "Sleep timer" else "Sleep timer on", tint = tint)
            player.sleep.endsAt?.let { end ->
                // Minutes left, counting down.
                var now by remember { mutableLongStateOf(System.currentTimeMillis()) }
                LaunchedEffect(end) {
                    while (true) {
                        now = System.currentTimeMillis()
                        delay(1_000)
                    }
                }
                Text(Format.timestamp(((end - now) / 1000.0).coerceAtLeast(0.0)), fontSize = 12.sp, fontWeight = FontWeight.SemiBold, color = tint, style = Tabular)
            }
        }
        DropdownMenu(open, onDismissRequest = { open = false }, containerColor = Flexarr.seat) {
            Choice("Off", setting == null) { open = false; player.sleep.set(null) }
            HorizontalDivider(color = Flexarr.aisle)
            (SleepTimer.minuteChoices.map { SleepTimer.Setting.Minutes(it) } + SleepTimer.Setting.EndOfEpisode).forEach { choice ->
                Choice(choice.label, choice == setting) { open = false; player.sleep.set(choice) }
            }
        }
    }
}

@Composable
private fun Choice(label: String, checked: Boolean, onClick: () -> Unit) {
    DropdownMenuItem(
        text = { Text(label, color = Flexarr.screen) },
        onClick = onClick,
        leadingIcon = { Box(Modifier.size(20.dp)) { if (checked) Icon(Icons.Filled.Check, contentDescription = "Chosen", tint = Flexarr.screen) } },
    )
}

/** Up Next, as the server keeps it: tapping one plays it. */
@Composable
private fun UpNext(model: AppModel) {
    val next = model.upNext.filter { it.id != model.podcasts.current?.id }
    if (next.isEmpty()) return
    Column(Modifier.fillMaxWidth(), verticalArrangement = Arrangement.spacedBy(4.dp)) {
        Text("Up Next", style = marquee(22).copy(fontWeight = FontWeight.Bold), modifier = Modifier.padding(horizontal = 12.dp).padding(top = 8.dp))
        next.take(20).forEach { episode ->
            Row(
                Modifier.fillMaxWidth().pressable({ model.podcasts.play(episode) }).padding(horizontal = 12.dp, vertical = 6.dp),
                horizontalArrangement = Arrangement.spacedBy(12.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                PodcastArtwork(episode.artwork.thumb ?: episode.artwork.poster, episode.podcastTitle, episode.accentColor, Modifier.size(44.dp), imageWidth = 185, cornerRadius = 6.dp)
                Column(Modifier.weight(1f)) {
                    Text(episode.title, fontSize = 15.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    Text(episode.podcastTitle, fontSize = 12.sp, color = Flexarr.usher, maxLines = 1, overflow = TextOverflow.Ellipsis)
                }
            }
        }
    }
    Spacer(Modifier.height(Edge).alpha(0f))
}
