package io.github.flexarr.cast

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Cast
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Forward30
import androidx.compose.material.icons.filled.Pause
import androidx.compose.material.icons.filled.PhoneAndroid
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Replay
import androidx.compose.material.icons.filled.SkipNext
import androidx.compose.material.icons.filled.SkipPrevious
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.LocalContentColor
import androidx.compose.material3.Slider
import androidx.compose.material3.SliderDefaults
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.stateDescription
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.annotation.OptIn
import androidx.media3.cast.MediaRouteButton
import androidx.media3.common.util.UnstableApi
import coil3.compose.AsyncImage
import io.github.flexarr.core.Format
import io.github.flexarr.player.GlassButton
import io.github.flexarr.player.TimeStyle
import io.github.flexarr.player.glass
import io.github.flexarr.ui.components.pressable
import io.github.flexarr.ui.theme.Flexarr

/**
 * The Cast button: the system's list of Chromecasts to choose from, or (connected) the one in use, with Stop casting.
 * Not there without Google Play services. Where the iPhone app has its AirPlay button.
 */
@OptIn(UnstableApi::class)
@Composable
fun CastButton(modifier: Modifier = Modifier) {
    val casting = Casting.shared ?: return
    if (!casting.available) return
    CompositionLocalProvider(LocalContentColor provides Flexarr.screen) {
        MediaRouteButton(modifier)
    }
}

/** The Cast button on the player's glass, beside picture in picture. */
@Composable
fun GlassCastButton() {
    val casting = Casting.shared ?: return
    if (!casting.available) return
    Box(Modifier.size(44.dp).glass(CircleShape), contentAlignment = Alignment.Center) {
        CastButton()
    }
}

/**
 * The video player while its title is on the Chromecast: the title's picture dimmed behind "Playing on Living Room
 * TV", the transport and the timeline, and Play on this phone to take it back. Close stops the Chromecast too.
 */
@Composable
fun CastingScreen(casting: Casting, skipSeconds: Int, onClose: () -> Unit) {
    val now = casting.playing
    // Back closes the player, as Close does.
    BackHandler {
        casting.stop()
        onClose()
    }
    Box(Modifier.fillMaxSize().background(Color.Black)) {
        now?.artwork?.let { AsyncImage(it, contentDescription = null, contentScale = ContentScale.Crop, modifier = Modifier.fillMaxSize().alpha(0.35f)) }
        Box(Modifier.fillMaxSize().background(Brush.verticalGradient(listOf(Color.Black.copy(alpha = 0.6f), Color.Transparent, Color.Black.copy(alpha = 0.7f)))))

        Row(Modifier.fillMaxWidth().safeDrawingPadding().padding(horizontal = 20.dp, vertical = 12.dp), horizontalArrangement = Arrangement.spacedBy(14.dp), verticalAlignment = Alignment.Top) {
            GlassButton(Icons.Filled.Close, "Close", size = 44, onClick = {
                casting.stop()
                onClose()
            })
            Column(Modifier.weight(1f).padding(top = 2.dp)) {
                now?.let {
                    Text(it.title, fontSize = 20.sp, fontWeight = FontWeight.Bold, color = Flexarr.screen, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    it.subtitle?.let { line -> Text(line, fontSize = 15.sp, color = Flexarr.screen.copy(alpha = 0.8f), maxLines = 1, overflow = TextOverflow.Ellipsis) }
                }
            }
            GlassCastButton()
        }

        Column(Modifier.align(Alignment.Center).padding(horizontal = 32.dp), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(24.dp)) {
            PlayingOn(casting)
            Row(horizontalArrangement = Arrangement.spacedBy(28.dp), verticalAlignment = Alignment.CenterVertically) {
                GlassSkip(-skipSeconds) { casting.skip(-skipSeconds) }
                if (casting.isLoading) {
                    Box(Modifier.size(76.dp).glass(CircleShape), contentAlignment = Alignment.Center) { CircularProgressIndicator(color = Flexarr.screen, modifier = Modifier.size(28.dp)) }
                } else {
                    GlassButton(if (casting.isPlaying) Icons.Filled.Pause else Icons.Filled.PlayArrow, if (casting.isPlaying) "Pause" else "Play", size = 76, onClick = casting::togglePlayPause)
                }
                GlassSkip(skipSeconds) { casting.skip(skipSeconds) }
            }
            Row(
                Modifier.glass(CircleShape).pressable(casting::playHere).padding(horizontal = 18.dp, vertical = 10.dp),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Icon(Icons.Filled.PhoneAndroid, contentDescription = null, tint = Flexarr.screen, modifier = Modifier.size(18.dp))
                Text("Play on this phone", fontSize = 15.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen)
            }
        }

        CastTimeline(casting, Modifier.align(Alignment.BottomCenter).safeDrawingPadding().padding(horizontal = 24.dp, vertical = 16.dp))
    }
}

/**
 * Now Playing's scrubber and transport while music or a podcast is on the Chromecast: previous and next for music,
 * back 15 and forward 30 for a podcast, as the phone's own controls.
 */
@Composable
fun CastControls(casting: Casting) {
    val music = casting.casts(Casting.Kind.Music)
    Column(Modifier.fillMaxWidth(), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(20.dp)) {
        casting.playing?.let { now ->
            Text(
                listOfNotNull(now.title, now.subtitle).joinToString("  –  "), fontSize = 15.sp, fontWeight = FontWeight.Medium,
                color = Flexarr.usher, maxLines = 1, overflow = TextOverflow.Ellipsis, textAlign = TextAlign.Center,
            )
        }
        CastTimeline(casting, Modifier.fillMaxWidth())
        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceEvenly, verticalAlignment = Alignment.CenterVertically) {
            if (music) {
                Transport(Icons.Filled.SkipPrevious, "Previous", onClick = casting::previousTrack)
            } else {
                Transport(Icons.Filled.Replay, "Back 15 seconds") { casting.skip(-15) }
            }
            Box(Modifier.size(76.dp).clip(CircleShape).background(Flexarr.screen).pressable(casting::togglePlayPause), contentAlignment = Alignment.Center) {
                if (casting.isLoading) CircularProgressIndicator(color = Flexarr.room, modifier = Modifier.size(28.dp))
                else Icon(if (casting.isPlaying) Icons.Filled.Pause else Icons.Filled.PlayArrow, contentDescription = if (casting.isPlaying) "Pause" else "Play", tint = Flexarr.room, modifier = Modifier.size(32.dp))
            }
            if (music) {
                Transport(Icons.Filled.SkipNext, "Next", enabled = casting.hasNextTrack, onClick = casting::nextTrack)
            } else {
                Transport(Icons.Filled.Forward30, "Forward 30 seconds") { casting.skip(30) }
            }
        }
        PlayingOn(casting)
    }
}

/** "Playing on Living Room TV", with the Cast symbol. */
@Composable
private fun PlayingOn(casting: Casting) {
    val device = casting.deviceName ?: return
    Row(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalAlignment = Alignment.CenterVertically, modifier = Modifier.widthIn(max = 420.dp)) {
        Icon(Icons.Filled.Cast, contentDescription = null, tint = Flexarr.silver, modifier = Modifier.size(18.dp))
        Text("Playing on $device", fontSize = 15.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.silver, maxLines = 1, overflow = TextOverflow.Ellipsis)
    }
}

/** Where the Chromecast has got to: a scrubber that seeks once it's let go, the time gone and the time left. */
@Composable
private fun CastTimeline(casting: Casting, modifier: Modifier) {
    var scrubbing by remember { mutableStateOf<Float?>(null) }
    val duration = casting.duration.coerceAtLeast(1.0)
    val shown = scrubbing?.toDouble() ?: casting.position
    Column(modifier, verticalArrangement = Arrangement.spacedBy(2.dp)) {
        Slider(
            value = shown.toFloat().coerceIn(0f, duration.toFloat()),
            onValueChange = { scrubbing = it },
            onValueChangeFinished = {
                scrubbing?.let { casting.seekTo(it.toDouble()) }
                scrubbing = null
            },
            valueRange = 0f..duration.toFloat(),
            colors = SliderDefaults.colors(thumbColor = Flexarr.screen, activeTrackColor = Flexarr.screen, inactiveTrackColor = Flexarr.screen.copy(alpha = 0.3f)),
            modifier = Modifier.fillMaxWidth().semantics {
                contentDescription = "Position"
                stateDescription = "${Format.timestamp(shown)} of ${Format.timestamp(casting.duration)}"
            },
        )
        Row(Modifier.fillMaxWidth()) {
            Text(Format.timestamp(shown), fontSize = 12.sp, fontWeight = FontWeight.Medium, color = Flexarr.usher, style = TimeStyle)
            Spacer(Modifier.weight(1f))
            if (casting.duration > 0) Text("−" + Format.timestamp((casting.duration - shown).coerceAtLeast(0.0)), fontSize = 12.sp, fontWeight = FontWeight.Medium, color = Flexarr.usher, style = TimeStyle)
        }
    }
}

/** Back or forward [seconds] on the player's glass: the arrow with its number on it. */
@Composable
private fun GlassSkip(seconds: Int, onClick: () -> Unit) {
    val back = seconds < 0
    val amount = kotlin.math.abs(seconds)
    Box(
        Modifier.size(56.dp).glass(CircleShape).pressable(onClick).semantics { contentDescription = "${if (back) "Back" else "Forward"} $amount seconds" },
        contentAlignment = Alignment.Center,
    ) {
        Icon(Icons.Filled.Replay, contentDescription = null, tint = Flexarr.screen, modifier = Modifier.size(28.dp).then(if (back) Modifier else Modifier.mirrored()))
        Text("$amount", fontSize = 8.sp, fontWeight = FontWeight.Bold, color = Flexarr.screen, modifier = Modifier.padding(top = 2.dp))
    }
}

private fun Modifier.mirrored(): Modifier = graphicsLayer { scaleX = -1f }

@Composable
private fun Transport(icon: ImageVector, label: String, enabled: Boolean = true, onClick: () -> Unit) {
    Box(Modifier.size(56.dp).clip(CircleShape).alpha(if (enabled) 1f else 0.4f).pressable(onClick, enabled), contentAlignment = Alignment.Center) {
        Icon(icon, contentDescription = label, tint = Flexarr.screen, modifier = Modifier.size(28.dp))
    }
}
