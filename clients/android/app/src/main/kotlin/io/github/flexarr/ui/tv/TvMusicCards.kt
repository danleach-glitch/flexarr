package io.github.flexarr.ui.tv

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.QueueMusic
import androidx.compose.material.icons.automirrored.filled.VolumeDown
import androidx.compose.material.icons.automirrored.filled.VolumeUp
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.semantics.disabled
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.tv.material3.ClickableSurfaceDefaults
import androidx.tv.material3.Glow
import androidx.tv.material3.Icon
import androidx.tv.material3.Surface
import androidx.tv.material3.Text
import io.github.flexarr.AppModel
import io.github.flexarr.core.Format
import io.github.flexarr.core.ItemSummary
import io.github.flexarr.core.PlaylistSummary
import io.github.flexarr.core.artistName
import io.github.flexarr.core.releaseYear
import io.github.flexarr.ui.components.RemoteImage
import io.github.flexarr.ui.podcasts.Tabular
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.glowColor
import io.github.flexarr.ui.theme.marquee

// The music screens' pieces, as the Apple TV's MusicCards.swift draws them.

/** Album covers and artist pictures are square; the title stands in on a wash of the item's colour until one arrives. */
@Composable
fun TvSquareArtwork(item: ItemSummary, size: Dp, circular: Boolean = false) {
    Box(Modifier.size(size).clip(if (circular) CircleShape else RoundedCornerShape(5.dp)).background(Flexarr.seat)) {
        RemoteImage(item.artwork.poster, if (size > 200.dp) 800 else 500, Modifier.fillMaxSize()) {
            Box(
                Modifier.fillMaxSize().background(Brush.verticalGradient(listOf(glowColor(item.accentColor).copy(alpha = 0.4f), Flexarr.seat))),
                contentAlignment = Alignment.Center,
            ) {
                val fontSize = if (circular) size.value * 0.3f else maxOf(12f, size.value * 0.11f)
                Text(
                    if (circular) initials(item.title) else item.title,
                    style = marquee(fontSize.toInt(), Flexarr.screen.copy(alpha = 0.8f)).copy(fontWeight = FontWeight.Bold),
                    textAlign = TextAlign.Center,
                    modifier = Modifier.padding(size * 0.08f),
                )
            }
        }
    }
}

private fun initials(title: String) = title.split(" ").filter { it.isNotEmpty() }.take(2).joinToString("") { it.first().toString() }

/** Under a card: its name, brightening with focus, and a quieter line beneath. */
@Composable
private fun CardCaption(title: String, subtitle: String?, focused: Boolean, width: Dp) {
    Column(Modifier.width(width), verticalArrangement = Arrangement.spacedBy(2.dp)) {
        Text(title, fontSize = 12.sp, fontWeight = FontWeight.SemiBold, color = if (focused) Flexarr.screen else Flexarr.screen.copy(alpha = 0.8f), maxLines = 1, overflow = TextOverflow.Ellipsis)
        subtitle?.let { Text(it, fontSize = 11.5.sp, color = Flexarr.usher, maxLines = 1, overflow = TextOverflow.Ellipsis) }
    }
}

/** An album: square cover with its title and year beneath (the artist instead, where the albums are from several). */
@Composable
fun TvAlbumCard(item: ItemSummary, onClick: () -> Unit, modifier: Modifier = Modifier, width: Dp = TvLayout.PosterWidth, showsArtist: Boolean = false) {
    TvArtworkButton(
        ambient = glowColor(item.accentColor),
        onClick = onClick,
        modifier = modifier,
        shape = RoundedCornerShape(5.dp),
        contentDescription = listOfNotNull(item.title, item.artistName).joinToString(", "),
        artwork = { TvSquareArtwork(item, width) },
        caption = { focused ->
            val subtitle = if (showsArtist) item.artistName ?: item.releaseYear?.toString() else item.releaseYear?.toString()
            CardCaption(item.title, subtitle, focused, width)
        },
    )
}

/** A track in a Home row or search results: its cover with the name and artist beneath, and a play mark since pressing it plays. */
@Composable
fun TvTrackCard(model: AppModel, item: ItemSummary, onClick: () -> Unit) {
    val width = TvLayout.PosterWidth
    val playing = model.music.current?.id == item.id && model.music.isPlaying
    TvArtworkButton(
        ambient = glowColor(item.accentColor),
        onClick = onClick,
        shape = RoundedCornerShape(5.dp),
        contentDescription = listOfNotNull(item.title, item.artistName).joinToString(", "),
        artwork = {
            Box {
                TvSquareArtwork(item, width)
                Box(
                    Modifier.align(Alignment.BottomEnd).padding(6.dp).size(22.dp).background(Flexarr.screen.copy(alpha = 0.92f), CircleShape),
                    contentAlignment = Alignment.Center,
                ) {
                    Icon(if (playing) Icons.AutoMirrored.Filled.VolumeUp else Icons.Filled.PlayArrow, contentDescription = null, tint = Flexarr.room, modifier = Modifier.size(12.dp))
                }
            }
        },
        caption = { focused -> CardCaption(item.title, item.artistName, focused, width) },
    )
}

/** An artist: a round picture with the name and how many albums beneath. */
@Composable
fun TvArtistCard(item: ItemSummary, onClick: () -> Unit, modifier: Modifier = Modifier, size: Dp = 110.dp) {
    Column(modifier.width(size + 15.dp), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(9.dp)) {
        TvArtworkButton(ambient = glowColor(item.accentColor), onClick = onClick, shape = CircleShape, contentDescription = item.title, artwork = { TvSquareArtwork(item, size, circular = true) })
        Column(horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(1.dp)) {
            Text(item.title, fontSize = 12.sp, fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis)
            if (item.childCount > 0) {
                Text(if (item.childCount == 1) "1 album" else "${item.childCount} albums", fontSize = 11.5.sp, color = Flexarr.usher, maxLines = 1)
            }
        }
    }
}

/** A playlist's picture: the cover chosen for it, or a mosaic of its first tracks' covers. */
@Composable
fun TvPlaylistArtwork(playlist: PlaylistSummary, size: Dp) {
    Box(Modifier.size(size).clip(RoundedCornerShape(5.dp)).background(Flexarr.seat), contentAlignment = Alignment.Center) {
        val covers = playlist.covers
        val half = size / 2
        when {
            playlist.cover != null -> RemoteImage(playlist.cover, if (size > 200.dp) 800 else 500, Modifier.fillMaxSize()) { PlaylistPlaceholder(size) }
            covers.size >= 4 -> Column {
                Row { MosaicTile(covers[0], half); MosaicTile(covers[1], half) }
                Row { MosaicTile(covers[2], half); MosaicTile(covers[3], half) }
            }
            covers.isNotEmpty() -> MosaicTile(covers.first(), size)
            else -> PlaylistPlaceholder(size)
        }
    }
}

@Composable
private fun MosaicTile(path: String, side: Dp) {
    RemoteImage(path, if (side > 150.dp) 500 else 300, Modifier.size(side)) { Box(Modifier.fillMaxSize().background(Flexarr.seat)) }
}

@Composable
private fun PlaylistPlaceholder(size: Dp) {
    Icon(Icons.AutoMirrored.Filled.QueueMusic, contentDescription = null, tint = Flexarr.usher, modifier = Modifier.size(size * 0.3f))
}

/** A playlist in a row: its picture with the name and how many tracks beneath. */
@Composable
fun TvPlaylistCard(playlist: PlaylistSummary, onClick: () -> Unit) {
    val width = TvLayout.PosterWidth
    TvArtworkButton(
        ambient = Flexarr.silver,
        onClick = onClick,
        shape = RoundedCornerShape(5.dp),
        contentDescription = playlist.name,
        artwork = { TvPlaylistArtwork(playlist, width) },
        caption = { focused -> CardCaption(playlist.name, playlist.trackCountLabel, focused, width) },
    )
}

/**
 * One line of a tracklist: number (or a speaker while it plays), title and length. Focused it lights up in projected
 * white like the other TV buttons; the one playing keeps a faint wash.
 */
@Composable
fun TvTrackRow(
    track: ItemSummary,
    number: Int,
    isCurrent: Boolean,
    isPlaying: Boolean,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    /** Under the title: the track's own artist, where it isn't the album's. */
    subtitle: String? = null,
) {
    Surface(
        onClick = onClick,
        modifier = modifier.height(40.dp),
        shape = ClickableSurfaceDefaults.shape(RoundedCornerShape(7.dp)),
        colors = ClickableSurfaceDefaults.colors(
            containerColor = if (isCurrent) Flexarr.screen.copy(alpha = 0.12f) else Color.Transparent,
            contentColor = if (isCurrent) Flexarr.screen else Flexarr.screen.copy(alpha = 0.82f),
            focusedContainerColor = Flexarr.screen,
            focusedContentColor = Flexarr.room,
            pressedContainerColor = Flexarr.screen,
            pressedContentColor = Flexarr.room,
        ),
        scale = ClickableSurfaceDefaults.scale(focusedScale = 1.02f, pressedScale = 0.98f),
    ) {
        Row(Modifier.fillMaxSize().padding(horizontal = 15.dp), horizontalArrangement = Arrangement.spacedBy(14.dp), verticalAlignment = Alignment.CenterVertically) {
            Box(Modifier.width(28.dp), contentAlignment = Alignment.CenterEnd) {
                if (isCurrent) {
                    Icon(
                        if (isPlaying) Icons.AutoMirrored.Filled.VolumeUp else Icons.AutoMirrored.Filled.VolumeDown,
                        contentDescription = if (isPlaying) "Playing" else "Paused",
                        modifier = Modifier.size(13.dp),
                    )
                } else {
                    Text(number.toString(), fontSize = 13.sp, fontWeight = FontWeight.Medium, style = Tabular)
                }
            }
            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(1.dp)) {
                Text(track.title, fontSize = 14.sp, fontWeight = if (isCurrent) FontWeight.Bold else FontWeight.Medium, maxLines = 1, overflow = TextOverflow.Ellipsis)
                subtitle?.let { Text(it, fontSize = 11.5.sp, fontWeight = FontWeight.Medium, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.alpha(0.7f)) }
            }
            Spacer(Modifier.width(10.dp))
            track.durationSeconds?.takeIf { it > 0 }?.let { Text(Format.timestamp(it), fontSize = 12.sp, fontWeight = FontWeight.Medium, style = Tabular) }
        }
    }
}

/**
 * A round transport control (previous, play, next, shuffle, repeat): an icon in a circle that fills with projected
 * white on focus. [active] keeps shuffle and repeat lit while they're on.
 */
@Composable
fun TvRoundControl(
    icon: ImageVector,
    label: String,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    size: Dp = 48.dp,
    active: Boolean = false,
    enabled: Boolean = true,
) {
    // Turned off, it stays focusable and only looks it, as TvActionButton: Next going dark at the second-last track
    // mustn't leave focus on nothing.
    Surface(
        onClick = { if (enabled) onClick() },
        modifier = modifier.size(size).semantics { if (!enabled) disabled() },
        shape = ClickableSurfaceDefaults.shape(CircleShape),
        colors = ClickableSurfaceDefaults.colors(
            containerColor = Flexarr.screen.copy(alpha = if (!enabled) 0.08f else if (active) 0.28f else 0.14f),
            contentColor = if (!enabled) Flexarr.usher.copy(alpha = 0.6f) else if (active) Flexarr.screen else Flexarr.screen.copy(alpha = 0.85f),
            focusedContainerColor = if (enabled) Flexarr.screen else Flexarr.screen.copy(alpha = 0.35f),
            focusedContentColor = Flexarr.room,
            pressedContainerColor = if (enabled) Flexarr.screen else Flexarr.screen.copy(alpha = 0.35f),
            pressedContentColor = Flexarr.room,
        ),
        scale = ClickableSurfaceDefaults.scale(focusedScale = 1.08f, pressedScale = 0.95f),
        glow = ClickableSurfaceDefaults.glow(focusedGlow = Glow(Flexarr.silver.copy(alpha = 0.5f), 12.dp)),
    ) {
        Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
            Icon(icon, contentDescription = label, modifier = Modifier.size(size * 0.38f))
        }
    }
}
