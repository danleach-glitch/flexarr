package io.github.flexarr.ui.tv

import android.view.KeyEvent
import androidx.activity.compose.BackHandler
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.tween
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInVertically
import androidx.compose.animation.slideOutVertically
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.VolumeUp
import androidx.compose.material.icons.filled.Lyrics
import androidx.compose.material.icons.filled.Pause
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Repeat
import androidx.compose.material.icons.filled.RepeatOne
import androidx.compose.material.icons.filled.Shuffle
import androidx.compose.material.icons.filled.SkipNext
import androidx.compose.material.icons.filled.SkipPrevious
import androidx.compose.material.icons.filled.Stop
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.focus.onFocusChanged
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.key.KeyEventType
import androidx.compose.ui.input.key.onPreviewKeyEvent
import androidx.compose.ui.input.key.type
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.tv.material3.ClickableSurfaceDefaults
import androidx.tv.material3.Icon
import androidx.tv.material3.Surface
import androidx.tv.material3.Text
import io.github.flexarr.AppModel
import io.github.flexarr.audio.MusicPlayer
import io.github.flexarr.core.Format
import io.github.flexarr.core.ItemSummary
import io.github.flexarr.core.Lyrics
import io.github.flexarr.core.artistName
import io.github.flexarr.core.lyrics
import io.github.flexarr.ui.components.LocalClient
import io.github.flexarr.ui.music.MusicAurora
import io.github.flexarr.ui.music.rememberCoverColours
import io.github.flexarr.ui.podcasts.Tabular
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.RoomBackground
import io.github.flexarr.ui.theme.glowColor
import io.github.flexarr.ui.theme.marquee
import kotlinx.coroutines.delay
import kotlin.math.roundToInt

/**
 * The music that's playing, over the cover's light, as the Apple TV's NowPlayingView: the cover, the track's name, how
 * far it's got, the transport controls and what comes next. The remote's play/pause, next and previous work wherever
 * focus is. A Lyrics control, there when the track has any, opens the words as a page of their own, which Back closes;
 * otherwise Back is [onClose]. Stop empties the queue, and with nothing left to show the screen closes itself.
 */
@Composable
fun TvNowPlayingScreen(model: AppModel, onClose: () -> Unit) {
    val music = model.music
    val track = music.current
    val client = LocalClient.current
    var showingLyrics by remember { mutableStateOf(false) }
    // Lyrics are fetched once per track, so the control is there before anyone asks.
    var lyrics by remember { mutableStateOf<Pair<String, Lyrics?>?>(null) }
    LaunchedEffect(track?.id) {
        val id = track?.id ?: return@LaunchedEffect
        if (lyrics?.first != id) lyrics = id to runCatching { client.lyrics(id) }.getOrNull()
    }
    val found = lyrics?.takeIf { it.first == track?.id }?.second
    val playPause = remember { FocusRequester() }
    val lyricsControl = remember { FocusRequester() }
    var returningFromLyrics by remember { mutableStateOf(false) }

    // Stopped (here or from elsewhere): nothing to show, so it goes, as the Apple TV's does.
    var hadMusic by remember { mutableStateOf(track != null) }
    LaunchedEffect(track == null) {
        if (track != null) hadMusic = true else if (hadMusic) onClose()
    }
    BackHandler {
        if (showingLyrics) {
            showingLyrics = false
            returningFromLyrics = true
        } else {
            onClose()
        }
    }
    // The light is there to be watched: the screen stays on over it while the music plays.
    val view = LocalView.current
    DisposableEffect(music.isPlaying) {
        view.keepScreenOn = music.isPlaying
        onDispose { view.keepScreenOn = false }
    }

    Box(
        Modifier.fillMaxSize().background(Flexarr.room).onPreviewKeyEvent { event ->
            if (event.type != KeyEventType.KeyDown) return@onPreviewKeyEvent false
            when (event.nativeKeyEvent.keyCode) {
                KeyEvent.KEYCODE_MEDIA_PLAY_PAUSE -> music.togglePlayPause().let { true }
                KeyEvent.KEYCODE_MEDIA_PLAY -> music.resume().let { true }
                KeyEvent.KEYCODE_MEDIA_PAUSE -> music.pause().let { true }
                KeyEvent.KEYCODE_MEDIA_NEXT -> music.next().let { true }
                KeyEvent.KEYCODE_MEDIA_PREVIOUS -> music.previous().let { true }
                else -> false
            }
        },
    ) {
        if (track != null) {
            MusicAurora(rememberCoverColours(track), music.spectrum, music.isPlaying)
            val words = found
            if (showingLyrics && words != null) {
                LyricsPage(music, track, words)
            } else {
                Row(
                    Modifier.fillMaxSize().padding(start = TvLayout.Edge + 20.dp, end = TvLayout.Edge + 20.dp, top = 65.dp, bottom = 30.dp),
                    horizontalArrangement = Arrangement.spacedBy(50.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Cover(track, 320.dp)
                    Column(Modifier.weight(1f).fillMaxSize(), verticalArrangement = Arrangement.spacedBy(20.dp)) {
                        Titles(music, track)
                        Timeline(music)
                        Controls(music, hasLyrics = found != null, playPause = playPause, lyricsControl = lyricsControl, onLyrics = { showingLyrics = true })
                        UpNext(music, Modifier.weight(1f))
                    }
                }
                // Play/pause takes focus as the screen opens; back from the lyrics, their control does.
                LaunchedEffect(Unit) {
                    val target = if (returningFromLyrics && found != null) lyricsControl else playPause
                    returningFromLyrics = false
                    repeat(10) {
                        if (runCatching { target.requestFocus() }.isSuccess) return@LaunchedEffect
                        delay(50)
                    }
                }
            }
        } else {
            RoomBackground(ambient = null) { TvStatusView("Nothing is playing.", empty = true) }
        }
    }
}

@Composable
private fun Cover(track: ItemSummary, size: Dp) {
    Box(Modifier.shadow(20.dp, RoundedCornerShape(9.dp), ambientColor = glowColor(track.accentColor), spotColor = Color.Black)) {
        TvSquareArtwork(track, size)
    }
}

@Composable
private fun Titles(music: MusicPlayer, track: ItemSummary, compact: Boolean = false) {
    Column(verticalArrangement = Arrangement.spacedBy(5.dp)) {
        Text(track.title, style = marquee(if (compact) 22 else 32).copy(fontWeight = FontWeight.ExtraBold), maxLines = 2, overflow = TextOverflow.Ellipsis)
        Text(
            listOfNotNull(track.artistName, music.albumTitle(track)).joinToString("   "),
            fontSize = if (compact) 13.sp else 15.sp,
            fontWeight = FontWeight.Medium,
            color = Flexarr.usher,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
        )
    }
}

/** How far the track has got, with the time gone and the time left. */
@Composable
private fun Timeline(music: MusicPlayer) {
    Column(Modifier.widthIn(max = 450.dp), verticalArrangement = Arrangement.spacedBy(7.dp)) {
        TvProgressBar(if (music.duration > 0) music.position / music.duration else 0.0)
        Row(Modifier.fillMaxWidth()) {
            Text(Format.timestamp(music.position), fontSize = 12.sp, fontWeight = FontWeight.Medium, color = Flexarr.usher, style = Tabular)
            Spacer(Modifier.weight(1f))
            if (music.duration > 0) {
                Text("-" + Format.timestamp((music.duration - music.position).coerceAtLeast(0.0)), fontSize = 12.sp, fontWeight = FontWeight.Medium, color = Flexarr.usher, style = Tabular)
            }
        }
    }
}

/** Shuffle, previous, play, next, repeat, Lyrics (when there are some) and Stop; shuffle and repeat stay lit while on. */
@Composable
private fun Controls(music: MusicPlayer, hasLyrics: Boolean, playPause: FocusRequester, lyricsControl: FocusRequester, onLyrics: () -> Unit) {
    Row(horizontalArrangement = Arrangement.spacedBy(18.dp), verticalAlignment = Alignment.CenterVertically) {
        TvRoundControl(Icons.Filled.Shuffle, "Shuffle", onClick = music::toggleShuffle, size = 42.dp, active = music.isShuffled)
        TvRoundControl(Icons.Filled.SkipPrevious, "Previous", onClick = music::previous, size = 48.dp)
        TvRoundControl(
            if (music.isPlaying) Icons.Filled.Pause else Icons.Filled.PlayArrow,
            if (music.isPlaying) "Pause" else "Play",
            onClick = music::togglePlayPause,
            size = 60.dp,
            active = true,
            modifier = Modifier.focusRequester(playPause),
        )
        TvRoundControl(Icons.Filled.SkipNext, "Next", onClick = music::next, size = 48.dp, enabled = music.hasNext)
        TvRoundControl(
            if (music.repeatMode == MusicPlayer.RepeatMode.One) Icons.Filled.RepeatOne else Icons.Filled.Repeat,
            when (music.repeatMode) {
                MusicPlayer.RepeatMode.Off -> "Repeat"
                MusicPlayer.RepeatMode.All -> "Repeat all"
                MusicPlayer.RepeatMode.One -> "Repeat one"
            },
            onClick = music::cycleRepeat,
            size = 42.dp,
            active = music.repeatMode != MusicPlayer.RepeatMode.Off,
        )
        if (hasLyrics) TvRoundControl(Icons.Filled.Lyrics, "Lyrics", onClick = onLyrics, size = 42.dp, modifier = Modifier.focusRequester(lyricsControl))
        // Done with the music: the queue empties, and Now Playing and the bar go.
        TvRoundControl(Icons.Filled.Stop, "Stop", onClick = music::stop, size = 42.dp)
        if (music.isLoading) CircularProgressIndicator(Modifier.size(22.dp), color = Flexarr.screen, strokeWidth = 2.dp)
    }
}

/**
 * The whole queue, in a list that scrolls within what's left of the screen: an album or a playlist runs well past the
 * bottom, and moving down brings the next row into view. Pressing one jumps there.
 */
@Composable
private fun UpNext(music: MusicPlayer, modifier: Modifier) {
    val next = music.upNext
    if (next.isEmpty()) return
    Column(modifier.widthIn(max = 450.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
        Text("Up next", style = marquee(16).copy(fontWeight = FontWeight.Bold), modifier = Modifier.padding(start = 15.dp, bottom = 3.dp))
        LazyColumn(contentPadding = PaddingValues(bottom = 30.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
            itemsIndexed(next, key = { offset, track -> "${music.index + 1 + offset}-${track.id}" }) { offset, track ->
                TvTrackRow(track, number = offset + 1, isCurrent = false, isPlaying = false, onClick = { music.skipTo(music.index + 1 + offset) }, modifier = Modifier.fillMaxWidth())
            }
        }
    }
}

/** The words beside a smaller cover, the track's name and the timeline. Back comes back to the controls. */
@Composable
private fun LyricsPage(music: MusicPlayer, track: ItemSummary, lyrics: Lyrics) {
    Row(
        Modifier.fillMaxSize().padding(start = TvLayout.Edge + 20.dp, end = TvLayout.Edge + 20.dp, top = 55.dp, bottom = 20.dp),
        horizontalArrangement = Arrangement.spacedBy(45.dp),
    ) {
        Column(Modifier.width(260.dp).padding(top = 10.dp), verticalArrangement = Arrangement.spacedBy(18.dp)) {
            Cover(track, 200.dp)
            Titles(music, track, compact = true)
            Timeline(music)
        }
        TvLyrics(lyrics, music.position, onSeek = music::seek, modifier = Modifier.weight(1f))
    }
}

/**
 * A track's lyrics, line by line, as the Apple TV's LyricsView. Synced ones follow the music: the line being sung is lit
 * and kept in the middle, focus rides along with it, and pressing a line jumps the track there. Every line takes focus,
 * so plain ones can be read through with the remote.
 */
@Composable
private fun TvLyrics(lyrics: Lyrics, position: Double, onSeek: (Double) -> Unit, modifier: Modifier = Modifier) {
    val active = lyrics.activeLine(position)
    val list = rememberLazyListState()
    val following = remember { FocusRequester() }
    var hasFocus by remember { mutableStateOf(false) }
    // The words take focus as they open: the line being sung, or the first.
    LaunchedEffect(Unit) {
        active?.let { list.scrollToItem(it, -(list.layoutInfo.viewportSize.height / 2.5f).roundToInt()) }
        repeat(10) {
            if (runCatching { following.requestFocus() }.isSuccess) return@LaunchedEffect
            delay(50)
        }
    }
    LaunchedEffect(active) {
        val line = active ?: return@LaunchedEffect
        // Kept in the middle: scrolled to, then back by part of the space above it.
        list.animateScrollToItem(line, -(list.layoutInfo.viewportSize.height / 2.5f).roundToInt())
        if (hasFocus) runCatching { following.requestFocus() }
    }
    LazyColumn(
        modifier.fillMaxSize().onFocusChanged { hasFocus = it.hasFocus }.semantics { contentDescription = "Lyrics" },
        state = list,
        contentPadding = PaddingValues(vertical = 150.dp),
        verticalArrangement = Arrangement.spacedBy(5.dp),
    ) {
        itemsIndexed(lyrics.lines) { index, line ->
            val target = when {
                !lyrics.synced -> 0.9f
                index == active -> 1f
                else -> 0.4f
            }
            val alpha by animateFloatAsState(target, tween(250), label = "line")
            val time = line.time
            Surface(
                onClick = { if (lyrics.synced && time != null) onSeek(time) },
                modifier = Modifier.fillMaxWidth().then(if (index == (active ?: 0)) Modifier.focusRequester(following) else Modifier),
                shape = ClickableSurfaceDefaults.shape(RoundedCornerShape(8.dp)),
                colors = ClickableSurfaceDefaults.colors(
                    containerColor = Color.Transparent,
                    contentColor = Flexarr.screen.copy(alpha = alpha),
                    focusedContainerColor = Flexarr.screen.copy(alpha = 0.12f),
                    focusedContentColor = Flexarr.screen.copy(alpha = alpha),
                    pressedContainerColor = Flexarr.screen.copy(alpha = 0.12f),
                    pressedContentColor = Flexarr.screen.copy(alpha = alpha * 0.6f),
                ),
                scale = ClickableSurfaceDefaults.scale(focusedScale = 1.02f),
            ) {
                // An empty line between verses keeps its place.
                Text(line.text.ifEmpty { " " }, fontSize = 22.sp, fontWeight = FontWeight.Bold, modifier = Modifier.padding(horizontal = 14.dp, vertical = 4.dp))
            }
        }
    }
}

/**
 * The music that's playing, along the bottom of the screens while it plays, as the Apple TV's NowPlayingBar: the cover,
 * the track, the artist and where it's got to. It takes no focus (a bar under a long list is a hard thing for the remote
 * to reach): it goes a few seconds after the music pauses or finishes, and comes back with play.
 */
@Composable
fun TvNowPlayingBar(model: AppModel, modifier: Modifier = Modifier) {
    val music = model.music
    val active = music.current != null && (music.isPlaying || music.isLoading)
    // At once when music starts, and gone a few seconds after it stops, so the moment between two tracks doesn't blink.
    var shown by remember { mutableStateOf(false) }
    LaunchedEffect(active) {
        if (!active) delay(3_000)
        shown = active
    }
    val track = music.current
    AnimatedVisibility(shown && track != null, modifier, enter = slideInVertically { it } + fadeIn(), exit = slideOutVertically { it } + fadeOut()) {
        if (track == null) return@AnimatedVisibility
        Row(
            Modifier.fillMaxWidth().padding(start = TvLayout.Edge, end = TvLayout.Edge, bottom = 10.dp)
                .shadow(12.dp, RoundedCornerShape(8.dp))
                .background(Flexarr.seat.copy(alpha = 0.96f), RoundedCornerShape(8.dp))
                .border(BorderStroke(1.dp, Flexarr.aisle), RoundedCornerShape(8.dp))
                .padding(horizontal = 12.dp, vertical = 6.dp)
                .semantics { contentDescription = "Now playing: ${track.title}${track.artistName?.let { " by $it" }.orEmpty()}" },
            horizontalArrangement = Arrangement.spacedBy(12.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            TvSquareArtwork(track, 32.dp)
            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(1.dp)) {
                Text(track.title, fontSize = 12.5.sp, fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis)
                track.artistName?.let { Text(it, fontSize = 10.5.sp, color = Flexarr.usher, maxLines = 1, overflow = TextOverflow.Ellipsis) }
            }
            if (music.isLoading) {
                CircularProgressIndicator(Modifier.size(14.dp), color = Flexarr.screen, strokeWidth = 1.5.dp)
            } else {
                Icon(Icons.AutoMirrored.Filled.VolumeUp, contentDescription = null, modifier = Modifier.size(12.dp))
            }
            Text("${Format.timestamp(music.position)}  /  ${Format.timestamp(music.duration)}", fontSize = 10.5.sp, fontWeight = FontWeight.Medium, color = Flexarr.usher, style = Tabular)
        }
    }
}
