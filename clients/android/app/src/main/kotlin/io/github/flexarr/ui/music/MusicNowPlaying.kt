package io.github.flexarr.ui.music

import android.graphics.Bitmap
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.spring
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Lyrics as LyricsIcon
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Pause
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Repeat
import androidx.compose.material.icons.filled.RepeatOne
import androidx.compose.material.icons.filled.Shuffle
import androidx.compose.material.icons.filled.SkipNext
import androidx.compose.material.icons.filled.SkipPrevious
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Slider
import androidx.compose.material3.SliderDefaults
import androidx.compose.material3.Text
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
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
import androidx.compose.ui.platform.LocalContext
import androidx.activity.compose.BackHandler
import androidx.compose.material3.minimumInteractiveComponentSize
import androidx.compose.ui.draw.drawBehind
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.clearAndSetSemantics
import androidx.compose.ui.semantics.onClick
import androidx.compose.ui.semantics.role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import coil3.SingletonImageLoader
import coil3.request.ImageRequest
import coil3.request.allowHardware
import coil3.toBitmap
import io.github.flexarr.AppModel
import io.github.flexarr.audio.MusicPlayer
import io.github.flexarr.cast.CastButton
import io.github.flexarr.cast.CastControls
import io.github.flexarr.cast.Casting
import io.github.flexarr.core.CoverPalette
import io.github.flexarr.core.Format
import io.github.flexarr.core.ItemSummary
import io.github.flexarr.core.Lyrics
import io.github.flexarr.core.artistName
import io.github.flexarr.core.lyrics
import io.github.flexarr.ui.components.LocalClient
import io.github.flexarr.ui.components.pressable
import io.github.flexarr.ui.podcasts.Tabular
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.ambientColor
import io.github.flexarr.ui.theme.marquee
import kotlin.math.roundToInt
import io.github.flexarr.ui.theme.Tokens

/**
 * The music that's playing, above the tabs on every screen: close, the cover and track (opening Now Playing), play or
 * pause, and next, with a thin line along the bottom for how far it's got. The iPhone app's NowPlayingBar, for music.
 */
@Composable
fun MusicNowPlayingBar(model: AppModel) {
    val music = model.music
    val track = music.current ?: return
    Column(
        Modifier.fillMaxWidth().padding(horizontal = 8.dp, vertical = 6.dp).shadow(12.dp, RoundedCornerShape(Tokens.radiusPanel)).clip(RoundedCornerShape(Tokens.radiusPanel)).background(Flexarr.seat)
            .semantics { contentDescription = "Now playing: ${track.title}${track.artistName?.let { " by $it" }.orEmpty()}" },
    ) {
        Row(Modifier.padding(end = 6.dp), verticalAlignment = Alignment.CenterVertically) {
            Box(Modifier.minimumInteractiveComponentSize().size(width = 36.dp, height = 56.dp).pressable(music::stop), contentAlignment = Alignment.Center) {
                Icon(Icons.Filled.Close, contentDescription = "Stop and close", tint = Flexarr.usher, modifier = Modifier.size(16.dp))
            }
            Row(Modifier.weight(1f).pressable({ model.showsMusicPlayer = true }).padding(vertical = 6.dp), horizontalArrangement = Arrangement.spacedBy(12.dp), verticalAlignment = Alignment.CenterVertically) {
                SquareArtwork(track, Modifier.size(44.dp), imageWidth = 185)
                Column(Modifier.weight(1f)) {
                    Text(track.title, fontSize = 15.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    track.artistName?.let { Text(it, fontSize = 12.sp, color = Flexarr.usher, maxLines = 1, overflow = TextOverflow.Ellipsis) }
                }
            }
            // On a Chromecast: its controls.
            val cast = Casting.shared?.takeIf { it.casts(Casting.Kind.Music) }
            if (cast != null) {
                RoundButton(if (cast.isPlaying) Icons.Filled.Pause else Icons.Filled.PlayArrow, if (cast.isPlaying) "Pause" else "Play", onClick = cast::togglePlayPause)
                RoundButton(Icons.Filled.SkipNext, "Next", enabled = cast.hasNextTrack, onClick = cast::nextTrack)
            } else {
                if (music.isLoading) {
                    Box(Modifier.size(40.dp), contentAlignment = Alignment.Center) { CircularProgressIndicator(color = Flexarr.screen, modifier = Modifier.size(20.dp), strokeWidth = 2.dp) }
                } else {
                    RoundButton(if (music.isPlaying) Icons.Filled.Pause else Icons.Filled.PlayArrow, if (music.isPlaying) "Pause" else "Play", onClick = music::togglePlayPause)
                }
                RoundButton(Icons.Filled.SkipNext, "Next", enabled = music.hasNext, onClick = music::next)
            }
        }
        // How far the track has got, along the bottom edge: read as it's drawn, so the bar doesn't recompose each tick.
        Box(
            Modifier.fillMaxWidth().padding(start = 14.dp, end = 14.dp, bottom = 4.dp).height(2.dp).drawBehind {
                val fraction = if (music.duration > 0) (music.position / music.duration).toFloat().coerceIn(0.01f, 1f) else 0.01f
                drawRoundRect(Flexarr.screen.copy(alpha = 0.7f), size = Size(size.width * fraction, size.height), cornerRadius = CornerRadius(size.height / 2))
            },
        )
    }
    if (model.showsMusicPlayer) MusicNowPlayingSheet(model, onDismiss = { model.showsMusicPlayer = false })
}

@Composable
private fun RoundButton(icon: ImageVector, label: String, enabled: Boolean = true, onClick: () -> Unit) {
    Box(Modifier.minimumInteractiveComponentSize().size(40.dp).clip(CircleShape).alpha(if (enabled) 1f else 0.4f).pressable(onClick, enabled), contentAlignment = Alignment.Center) {
        Icon(icon, contentDescription = label, tint = Flexarr.screen, modifier = Modifier.size(24.dp))
    }
}

/**
 * The music that's playing, as a sheet over the cover's light: the cover, the track's name, a scrubber, the transport
 * controls and what comes next. A Lyrics button, there when the track has any, swaps the cover and queue for the
 * words, which follow the song. The iPhone app's NowPlayingView, with the Apple TV's visualiser behind it.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun MusicNowPlayingSheet(model: AppModel, onDismiss: () -> Unit) {
    val music = model.music
    val track = music.current
    LaunchedEffect(track == null) { if (track == null) onDismiss() }
    if (track == null) return
    val client = LocalClient.current
    // Lyrics are fetched once per track, so the button is there before anyone asks.
    var lyrics by remember { mutableStateOf<Pair<String, Lyrics?>?>(null) }
    LaunchedEffect(track.id) {
        if (lyrics?.first != track.id) lyrics = track.id to runCatching { client.lyrics(track.id) }.getOrNull()
    }
    val found = lyrics?.takeIf { it.first == track.id }?.second
    var showingLyrics by remember { mutableStateOf(false) }
    // Back from the words goes to the cover, before it closes the sheet.
    BackHandler(enabled = showingLyrics) { showingLyrics = false }
    val colours = rememberCoverColours(track)

    ModalBottomSheet(onDismissRequest = onDismiss, sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true), containerColor = Flexarr.room, dragHandle = null) {
        Box(Modifier.fillMaxSize()) {
            MusicAurora(colours, music.spectrum, music.isPlaying)
            if (showingLyrics && found != null) {
                Column(Modifier.fillMaxSize().padding(horizontal = 28.dp).padding(bottom = 32.dp), verticalArrangement = Arrangement.spacedBy(20.dp)) {
                    Grabber(onDismiss)
                    // The track's name and cover kept small above the words, the controls below.
                    Row(horizontalArrangement = Arrangement.spacedBy(14.dp), verticalAlignment = Alignment.CenterVertically) {
                        SquareArtwork(track, Modifier.size(56.dp), imageWidth = 185)
                        Column(Modifier.weight(1f)) {
                            Text(track.title, fontSize = 17.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen, maxLines = 1, overflow = TextOverflow.Ellipsis)
                            track.artistName?.let { Text(it, fontSize = 15.sp, fontWeight = FontWeight.Medium, color = Flexarr.usher, maxLines = 1) }
                        }
                    }
                    LyricsView(found, music.position, onSeek = music::seek, modifier = Modifier.weight(1f))
                    ScrubberAndControls(music)
                    Extras(hasLyrics = true, showingLyrics = true) { showingLyrics = false }
                }
            } else {
                Column(
                    Modifier.verticalScroll(rememberScrollState()).padding(horizontal = 28.dp).padding(bottom = 32.dp),
                    horizontalAlignment = Alignment.CenterHorizontally,
                    verticalArrangement = Arrangement.spacedBy(24.dp),
                ) {
                    Grabber(onDismiss)
                    // Smaller while paused, as every music app does.
                    val scale by animateFloatAsState(if (music.isPlaying) 1f else 0.9f, spring(dampingRatio = 0.8f, stiffness = 300f), label = "cover")
                    SquareArtwork(
                        track,
                        Modifier.padding(top = 8.dp).widthIn(max = 320.dp).fillMaxWidth().scale(scale).shadow(24.dp, RoundedCornerShape(Tokens.radiusArt)),
                        imageWidth = 800,
                    )
                    Column(horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(4.dp)) {
                        Text(track.title, fontSize = 20.sp, fontWeight = FontWeight.Bold, color = Flexarr.screen, textAlign = TextAlign.Center, maxLines = 2, overflow = TextOverflow.Ellipsis)
                        val by = listOfNotNull(track.artistName, music.albumTitle(track)).joinToString("  –  ")
                        if (by.isNotEmpty()) Text(by, fontSize = 15.sp, fontWeight = FontWeight.Medium, color = Flexarr.usher, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    }
                    ScrubberAndControls(music, error = true)
                    Extras(hasLyrics = found != null, showingLyrics = false) { showingLyrics = true }
                    UpNext(music)
                }
            }
        }
    }
}

/** The sheet's handle, which TalkBack offers as Close (the drag that closes it isn't there for TalkBack). */
@Composable
private fun Grabber(onClose: () -> Unit) {
    Box(
        Modifier.fillMaxWidth().clearAndSetSemantics {
            contentDescription = "Close"
            role = Role.Button
            onClick { onClose(); true }
        },
        contentAlignment = Alignment.Center,
    ) {
        Box(Modifier.padding(top = 10.dp).size(width = 40.dp, height = 5.dp).clip(CircleShape).background(Flexarr.aisle))
    }
}

/** The cover's strongest colours for the light, read from a small drawing of it once it's here. */
@Composable
internal fun rememberCoverColours(track: ItemSummary): List<CoverPalette.Rgb> {
    val context = LocalContext.current
    val client = LocalClient.current
    val fallback = ambientColor(track.accentColor).let { CoverPalette.Rgb(it.red * 255.0, it.green * 255.0, it.blue * 255.0) }
    var colours by remember(track.parentId ?: track.id) { mutableStateOf(CoverPalette.colours(null, fallback)) }
    LaunchedEffect(track.parentId ?: track.id) {
        val url = client.imageUrl(track.artwork.poster, 185) ?: return@LaunchedEffect
        val request = ImageRequest.Builder(context).data(url).size(32).allowHardware(false).build()
        val bitmap: Bitmap = SingletonImageLoader.get(context).execute(request).image?.toBitmap() ?: return@LaunchedEffect
        val small = Bitmap.createScaledBitmap(bitmap, 32, 32, true)
        val pixels = IntArray(32 * 32).also { small.getPixels(it, 0, 32, 0, 0, 32, 32) }
        colours = CoverPalette.colours(pixels, fallback)
    }
    return colours
}

@Composable
private fun Scrubber(music: MusicPlayer) {
    var scrubbing by remember { mutableStateOf<Float?>(null) }
    val shown = scrubbing?.toDouble() ?: music.position
    Column(Modifier.fillMaxWidth(), verticalArrangement = Arrangement.spacedBy(4.dp)) {
        Slider(
            value = shown.toFloat().coerceIn(0f, music.duration.coerceAtLeast(1.0).toFloat()),
            onValueChange = { scrubbing = it },
            onValueChangeFinished = {
                scrubbing?.let { music.seek(it.toDouble()) }
                scrubbing = null
            },
            valueRange = 0f..music.duration.coerceAtLeast(1.0).toFloat(),
            colors = SliderDefaults.colors(thumbColor = Flexarr.screen, activeTrackColor = Flexarr.screen, inactiveTrackColor = Flexarr.screen.copy(alpha = 0.25f)),
        )
        Row(Modifier.fillMaxWidth()) {
            Text(Format.timestamp(shown), fontSize = 12.sp, fontWeight = FontWeight.Medium, color = Flexarr.usher, style = Tabular)
            Spacer(Modifier.weight(1f))
            if (music.duration > 0) Text("-" + Format.timestamp((music.duration - shown).coerceAtLeast(0.0)), fontSize = 12.sp, fontWeight = FontWeight.Medium, color = Flexarr.usher, style = Tabular)
        }
    }
}

/** The scrubber and the controls (and what went wrong), or the Chromecast's while the music is on one. */
@Composable
private fun ScrubberAndControls(music: MusicPlayer, error: Boolean = false) {
    val cast = Casting.shared?.takeIf { it.casts(Casting.Kind.Music) }
    if (cast != null) return CastControls(cast)
    Scrubber(music)
    if (error) music.errorMessage?.let { Text(it, fontSize = 13.sp, color = Flexarr.danger, textAlign = TextAlign.Center) }
    Controls(music)
}

/** Shuffle, previous, play, next and repeat; shuffle and repeat light up when they're on. */
@Composable
private fun Controls(music: MusicPlayer) {
    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween, verticalAlignment = Alignment.CenterVertically) {
        Transport(Icons.Filled.Shuffle, "Shuffle", size = 44, active = music.isShuffled, onClick = music::toggleShuffle)
        Transport(Icons.Filled.SkipPrevious, "Previous", size = 56, onClick = music::previous)
        Box(Modifier.size(76.dp).clip(CircleShape).background(Flexarr.screen).pressable(music::togglePlayPause), contentAlignment = Alignment.Center) {
            if (music.isLoading) CircularProgressIndicator(color = Flexarr.room, modifier = Modifier.size(28.dp))
            else Icon(if (music.isPlaying) Icons.Filled.Pause else Icons.Filled.PlayArrow, contentDescription = if (music.isPlaying) "Pause" else "Play", tint = Flexarr.room, modifier = Modifier.size(32.dp))
        }
        Transport(Icons.Filled.SkipNext, "Next", size = 56, enabled = music.hasNext, onClick = music::next)
        Transport(
            if (music.repeatMode == MusicPlayer.RepeatMode.One) Icons.Filled.RepeatOne else Icons.Filled.Repeat,
            when (music.repeatMode) {
                MusicPlayer.RepeatMode.Off -> "Repeat"
                MusicPlayer.RepeatMode.All -> "Repeat all"
                MusicPlayer.RepeatMode.One -> "Repeat one"
            },
            size = 44,
            active = music.repeatMode != MusicPlayer.RepeatMode.Off,
            onClick = music::cycleRepeat,
        )
    }
}

@Composable
private fun Transport(icon: ImageVector, label: String, size: Int, active: Boolean = false, enabled: Boolean = true, onClick: () -> Unit) {
    Box(Modifier.size(size.dp).clip(CircleShape).alpha(if (enabled) 1f else 0.4f).pressable(onClick, enabled), contentAlignment = Alignment.Center) {
        Icon(icon, contentDescription = label, tint = if (active) Flexarr.silver else Flexarr.screen, modifier = Modifier.size((size * 0.5).dp))
    }
}

/** Cast, and Lyrics when the track has some. */
@Composable
private fun Extras(hasLyrics: Boolean, showingLyrics: Boolean, onLyrics: () -> Unit) {
    if (!hasLyrics && Casting.shared?.available != true) return
    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(36.dp, Alignment.CenterHorizontally), verticalAlignment = Alignment.CenterVertically) {
        CastButton(Modifier.size(44.dp))
        if (hasLyrics) Transport(Icons.Filled.LyricsIcon, if (showingLyrics) "Hide lyrics" else "Lyrics", size = 44, active = showingLyrics, onClick = onLyrics)
    }
}

/** What plays after this track: tapping one jumps there. */
@Composable
private fun UpNext(music: MusicPlayer) {
    val next = music.upNext.take(20)
    if (next.isEmpty()) return
    Column(Modifier.fillMaxWidth(), verticalArrangement = Arrangement.spacedBy(4.dp)) {
        Text("Up next", style = marquee(22).copy(fontWeight = FontWeight.Bold), modifier = Modifier.padding(horizontal = 12.dp).padding(top = 8.dp))
        next.forEachIndexed { offset, track ->
            TrackRow(track, offset + 1, isCurrent = false, isPlaying = false, subtitle = track.artistName) { music.skipTo(music.index + 1 + offset) }
        }
    }
}

/**
 * A track's lyrics, line by line. Synced ones follow the music: the line being sung is lit and kept in the middle, and
 * tapping a line jumps the track to it. Plain ones just read. The Apple apps' LyricsView.
 */
@Composable
private fun LyricsView(lyrics: Lyrics, position: Double, onSeek: (Double) -> Unit, modifier: Modifier = Modifier) {
    val active = lyrics.activeLine(position)
    val list = rememberLazyListState()
    LaunchedEffect(active) {
        val line = active ?: return@LaunchedEffect
        // Kept in the middle: scrolled to, then back by half the space left above it.
        val viewport = list.layoutInfo.viewportSize.height
        list.animateScrollToItem(line, scrollOffset = -(viewport / 2.5f).roundToInt())
    }
    LazyColumn(modifier.fillMaxWidth().semantics { contentDescription = "Lyrics" }, state = list, contentPadding = PaddingValues(vertical = 120.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
        itemsIndexed(lyrics.lines) { index, line ->
            val target = when {
                !lyrics.synced -> 0.9f
                index == active -> 1f
                else -> 0.4f
            }
            val alpha by animateFloatAsState(target, tween(250), label = "line")
            val time = line.time
            Text(
                // An empty line between verses keeps its place.
                line.text.ifEmpty { " " },
                fontSize = 24.sp,
                fontWeight = FontWeight.Bold,
                color = Flexarr.screen.copy(alpha = alpha),
                modifier = Modifier.fillMaxWidth()
                    .then(if (lyrics.synced && time != null) Modifier.pressable({ onSeek(time) }) else Modifier)
                    .padding(horizontal = 12.dp, vertical = 4.dp),
            )
        }
    }
}
