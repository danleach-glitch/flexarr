package io.github.flexarr.ui.music

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.QueueMusic
import androidx.compose.material.icons.automirrored.filled.VolumeUp
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Shape
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.github.flexarr.AppModel
import io.github.flexarr.appModel
import io.github.flexarr.core.Format
import io.github.flexarr.core.ItemSummary
import io.github.flexarr.core.MixSummary
import io.github.flexarr.core.PlaylistSummary
import io.github.flexarr.core.artistName
import io.github.flexarr.core.releaseYear
import io.github.flexarr.ui.components.LocalNavigator
import io.github.flexarr.ui.components.RemoteImage
import io.github.flexarr.ui.components.pressable
import io.github.flexarr.ui.downloads.DownloadedMark
import io.github.flexarr.ui.podcasts.Tabular
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.glowColor
import io.github.flexarr.ui.theme.marquee

// The music screens' pieces, as the iPhone app's MusicCards.swift draws them.

/**
 * Album covers and artist pictures are square; the title stands in on a wash of the item's colour until one arrives.
 * Fills the width it's given.
 */
@Composable
fun SquareArtwork(item: ItemSummary, modifier: Modifier = Modifier, circular: Boolean = false, imageWidth: Int = 500) {
    val shape: Shape = if (circular) CircleShape else RoundedCornerShape(6.dp)
    Box(modifier.fillMaxWidth().aspectRatio(1f).clip(shape).background(Flexarr.seat)) {
        RemoteImage(item.artwork.poster, imageWidth, Modifier.fillMaxSize()) {
            Box(
                Modifier.fillMaxSize().background(Brush.verticalGradient(listOf(glowColor(item.accentColor).copy(alpha = 0.4f), Flexarr.seat))),
                contentAlignment = Alignment.Center,
            ) {
                Text(
                    if (circular) initials(item.title) else item.title,
                    style = marquee(if (circular) 28 else 16, Flexarr.screen.copy(alpha = 0.8f)).copy(fontWeight = FontWeight.Bold),
                    textAlign = TextAlign.Center,
                    modifier = Modifier.padding(8.dp),
                )
            }
        }
    }
}

private fun initials(title: String) = title.split(" ").filter { it.isNotEmpty() }.take(2).joinToString("") { it.first().toString() }

/** Under a card: its name, and a quieter line beneath. */
@Composable
private fun CardCaption(title: String, subtitle: String?, centred: Boolean = false) {
    Column(Modifier.fillMaxWidth(), verticalArrangement = Arrangement.spacedBy(1.dp), horizontalAlignment = if (centred) Alignment.CenterHorizontally else Alignment.Start) {
        Text(title, fontSize = 13.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen.copy(alpha = 0.9f), maxLines = 1, overflow = TextOverflow.Ellipsis)
        subtitle?.let { Text(it, fontSize = 12.sp, color = Flexarr.usher, maxLines = 1, overflow = TextOverflow.Ellipsis) }
    }
}

/** An album: square cover (marked once it's downloaded) with its title and year (or artist) beneath; tapping opens it. */
@Composable
fun AlbumCard(item: ItemSummary, modifier: Modifier = Modifier, width: Dp? = null, showsArtist: Boolean = false) {
    val navigator = LocalNavigator.current
    val downloads = LocalContext.current.appModel.downloads
    Column(
        modifier.then(if (width != null) Modifier.width(width) else Modifier).pressable({ navigator.open(item) }),
        verticalArrangement = Arrangement.spacedBy(6.dp),
    ) {
        Box {
            SquareArtwork(item)
            if (downloads.isCollectionDownloaded(item.id)) {
                DownloadedMark(Modifier.align(Alignment.BottomEnd).padding(5.dp).clip(CircleShape).background(Flexarr.room.copy(alpha = 0.85f)).padding(4.dp))
            }
        }
        // The artist under the title, where the albums are from several; the year on an artist's own page.
        CardCaption(item.title, if (showsArtist) item.artistName ?: item.releaseYear?.toString() else item.releaseYear?.toString())
    }
}

/** An artist: a round picture with the name beneath; tapping opens them. */
@Composable
fun ArtistCard(item: ItemSummary, modifier: Modifier = Modifier) {
    val navigator = LocalNavigator.current
    Column(modifier.pressable({ navigator.open(item) }), verticalArrangement = Arrangement.spacedBy(8.dp), horizontalAlignment = Alignment.CenterHorizontally) {
        SquareArtwork(item, Modifier.padding(horizontal = 6.dp), circular = true)
        CardCaption(item.title, if (item.childCount == 1) "1 album" else if (item.childCount > 1) "${item.childCount} albums" else null, centred = true)
    }
}

/** A track in a Home row: its cover with a play mark, the name and the artist beneath. Tapping plays. */
@Composable
fun TrackCard(model: AppModel, item: ItemSummary, width: Dp, onClick: () -> Unit) {
    val playing = model.music.current?.id == item.id && model.music.isPlaying
    Column(Modifier.width(width).pressable(onClick), verticalArrangement = Arrangement.spacedBy(6.dp)) {
        Box {
            SquareArtwork(item)
            Box(
                Modifier.align(Alignment.BottomEnd).padding(6.dp).size(28.dp).clip(CircleShape).background(Flexarr.screen.copy(alpha = 0.92f)),
                contentAlignment = Alignment.Center,
            ) {
                Icon(if (playing) Icons.AutoMirrored.Filled.VolumeUp else Icons.Filled.PlayArrow, contentDescription = null, tint = Flexarr.room, modifier = Modifier.size(16.dp))
            }
        }
        CardCaption(item.title, item.artistName)
    }
}

/** A playlist's picture: the cover chosen for it, or a mosaic of its first tracks' covers. Fills the width it's given. */
@Composable
fun PlaylistArtwork(playlist: PlaylistSummary, modifier: Modifier = Modifier) = CoverMosaic(playlist.covers, modifier, playlist.cover)

/** A mosaic of up to four album covers (one fills it when there are fewer), or [cover] instead. Fills the width it's given. */
@Composable
fun CoverMosaic(covers: List<String>, modifier: Modifier = Modifier, cover: String? = null) {
    Box(modifier.fillMaxWidth().aspectRatio(1f).clip(RoundedCornerShape(6.dp)).background(Flexarr.seat), contentAlignment = Alignment.Center) {
        when {
            cover != null -> RemoteImage(cover, 500, Modifier.fillMaxSize()) { Placeholder() }
            covers.size >= 4 -> Column(Modifier.fillMaxSize()) {
                Row(Modifier.weight(1f)) { Tile(covers[0]); Tile(covers[1]) }
                Row(Modifier.weight(1f)) { Tile(covers[2]); Tile(covers[3]) }
            }
            covers.isNotEmpty() -> RemoteImage(covers.first(), 300, Modifier.fillMaxSize()) {}
            else -> Placeholder()
        }
    }
}

@Composable
private fun androidx.compose.foundation.layout.RowScope.Tile(path: String) {
    RemoteImage(path, 300, Modifier.weight(1f).fillMaxSize()) { Box(Modifier.fillMaxSize().background(Flexarr.seat)) }
}

@Composable
private fun Placeholder() {
    Icon(Icons.AutoMirrored.Filled.QueueMusic, contentDescription = null, tint = Flexarr.usher, modifier = Modifier.size(34.dp))
}

/** A playlist in a row: its picture with the name and how many tracks beneath; tapping opens it. */
@Composable
fun PlaylistCard(playlist: PlaylistSummary, width: Dp) {
    val navigator = LocalNavigator.current
    Column(Modifier.width(width).pressable({ navigator.openPlaylist(playlist.id) }), verticalArrangement = Arrangement.spacedBy(6.dp)) {
        PlaylistArtwork(playlist)
        CardCaption(playlist.name, playlist.trackCountLabel)
    }
}

/** One of the profile's daily mixes in a row: a mosaic of its covers, the name and who's in it. Tapping plays it. */
@Composable
fun MixCard(mix: MixSummary, width: Dp) {
    val navigator = LocalNavigator.current
    Column(Modifier.width(width).pressable({ navigator.playMix(mix.id) }, onClickLabel = "Play"), verticalArrangement = Arrangement.spacedBy(6.dp)) {
        CoverMosaic(mix.covers)
        CardCaption(mix.name, mix.description.ifEmpty { null })
    }
}

/**
 * One line of a tracklist: number (or a speaker while it plays), title, a mark if it's downloaded, and length. A dimmed
 * one isn't playable here (a track of a download that hasn't arrived).
 */
@Composable
fun TrackRow(
    track: ItemSummary,
    number: Int,
    isCurrent: Boolean,
    isPlaying: Boolean,
    subtitle: String? = null,
    isDownloaded: Boolean = false,
    isDimmed: Boolean = false,
    onClick: () -> Unit,
) {
    Row(
        Modifier.fillMaxWidth().clip(RoundedCornerShape(10.dp))
            .then(if (isCurrent) Modifier.background(Flexarr.screen.copy(alpha = 0.1f)) else Modifier)
            .alpha(if (isDimmed) 0.45f else 1f)
            .pressable(onClick, enabled = !isDimmed)
            .padding(horizontal = 12.dp, vertical = 12.dp),
        horizontalArrangement = Arrangement.spacedBy(14.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Box(Modifier.width(28.dp), contentAlignment = Alignment.CenterEnd) {
            if (isCurrent) {
                Icon(Icons.AutoMirrored.Filled.VolumeUp, contentDescription = if (isPlaying) "Playing" else "Paused", tint = Flexarr.screen.copy(alpha = if (isPlaying) 1f else 0.6f), modifier = Modifier.size(18.dp))
            } else {
                Text(number.toString(), fontSize = 15.sp, fontWeight = FontWeight.Medium, color = Flexarr.usher, style = Tabular)
            }
        }
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(1.dp)) {
            Text(track.title, fontSize = 17.sp, fontWeight = if (isCurrent) FontWeight.SemiBold else FontWeight.Normal, color = Flexarr.screen, maxLines = 1, overflow = TextOverflow.Ellipsis)
            subtitle?.let { Text(it, fontSize = 12.sp, color = Flexarr.usher, maxLines = 1, overflow = TextOverflow.Ellipsis) }
        }
        if (isDownloaded) DownloadedMark()
        Spacer(Modifier.width(0.dp))
        track.durationSeconds?.takeIf { it > 0 }?.let { Text(Format.timestamp(it), fontSize = 15.sp, color = Flexarr.usher, style = Tabular) }
    }
}
