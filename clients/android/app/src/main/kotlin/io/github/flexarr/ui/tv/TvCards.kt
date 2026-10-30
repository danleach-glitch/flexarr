package io.github.flexarr.ui.tv

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.window.Dialog
import androidx.tv.material3.Icon
import androidx.tv.material3.Text
import io.github.flexarr.core.Format
import io.github.flexarr.core.ItemSummary
import io.github.flexarr.core.MediaKind
import io.github.flexarr.core.displayTitle
import io.github.flexarr.core.effectiveDuration
import io.github.flexarr.core.episodeSubtitle
import io.github.flexarr.core.progress
import io.github.flexarr.core.releaseYear
import io.github.flexarr.ui.components.MenuAction
import io.github.flexarr.ui.components.RemoteImage
import io.github.flexarr.ui.components.itemActions
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.glowColor
import io.github.flexarr.ui.theme.marquee

/** A 2:3 poster with the title and year beneath it (text never sits on artwork). */
@Composable
fun TvPosterCard(item: ItemSummary, onClick: () -> Unit, modifier: Modifier = Modifier, width: Dp = TvLayout.PosterWidth) {
    var menu by remember { mutableStateOf(false) }
    TvArtworkButton(
        ambient = glowColor(item.accentColor),
        onClick = onClick,
        onLongClick = { menu = true },
        modifier = modifier.width(width),
        contentDescription = listOfNotNull(item.title, posterSubtitle(item)).joinToString(", "),
        artwork = { TvPosterArtwork(item, width) },
        caption = { focused ->
            Column(verticalArrangement = Arrangement.spacedBy(2.dp)) {
                Text(item.title, fontSize = 12.sp, fontWeight = FontWeight.SemiBold, color = if (focused) Flexarr.screen else Flexarr.screen.copy(alpha = 0.8f), maxLines = 1, overflow = TextOverflow.Ellipsis)
                posterSubtitle(item)?.let { Text(it, fontSize = 11.5.sp, color = Flexarr.usher, maxLines = 1) }
            }
        },
    )
    if (menu) TvItemMenu(item.title, itemActions(item, downloads = false), onDismiss = { menu = false })
}

private fun posterSubtitle(item: ItemSummary): String? {
    val unwatched = item.userData.unwatchedCount
    if (item.kind == MediaKind.Series && unwatched != null && unwatched > 0) {
        return listOfNotNull(item.releaseYear?.toString(), "$unwatched new").joinToString("  ")
    }
    return item.releaseYear?.toString()
}

/** Poster artwork with a title placeholder, watched tick and progress bar. */
@Composable
fun TvPosterArtwork(item: ItemSummary, width: Dp) {
    // Album covers and artist pictures are square; everything else is a 2:3 poster.
    val square = item.kind == MediaKind.Album || item.kind == MediaKind.Artist || item.kind == MediaKind.Track
    Box(Modifier.size(width, if (square) width else width * 1.5f)) {
        RemoteImage(item.artwork.poster, 342, Modifier.fillMaxSize()) {
            Box(
                Modifier.fillMaxSize().background(Brush.verticalGradient(listOf(glowColor(item.accentColor).copy(alpha = 0.35f), Flexarr.seat))),
                contentAlignment = Alignment.Center,
            ) {
                Text(item.title, style = marquee(15, Flexarr.screen.copy(alpha = 0.8f)).copy(fontWeight = FontWeight.Bold), textAlign = TextAlign.Center, modifier = Modifier.padding(8.dp))
            }
        }
        item.progress?.let { TvProgressBar(it, Modifier.align(Alignment.BottomCenter).padding(6.dp)) }
        if (item.userData.watched) TvWatchedBadge(Modifier.align(Alignment.TopEnd).padding(5.dp))
    }
}

/** 16:9 card for Continue Watching: OK plays, with how much is left beneath it. Its menu reads LocalInContinueWatching. */
@Composable
fun TvWideCard(item: ItemSummary, onClick: () -> Unit, modifier: Modifier = Modifier, width: Dp = TvLayout.WideWidth) {
    var menu by remember { mutableStateOf(false) }
    TvArtworkButton(
        ambient = glowColor(item.accentColor),
        onClick = onClick,
        onLongClick = { menu = true },
        modifier = modifier.width(width),
        contentDescription = listOfNotNull(item.displayTitle, item.episodeSubtitle).joinToString(", "),
        artwork = {
            Box(Modifier.size(width, width * 9f / 16f)) {
                val still = if (item.kind == MediaKind.Episode) item.artwork.thumb ?: item.artwork.backdrop else item.artwork.backdrop ?: item.artwork.thumb
                RemoteImage(still, 560, Modifier.fillMaxSize()) {
                    Box(Modifier.fillMaxSize().background(Brush.linearGradient(listOf(glowColor(item.accentColor).copy(alpha = 0.4f), Flexarr.seat))), contentAlignment = Alignment.Center) {
                        Icon(Icons.Filled.PlayArrow, contentDescription = null, tint = Flexarr.screen.copy(alpha = 0.5f), modifier = Modifier.size(22.dp))
                    }
                }
                item.progress?.let {
                    Box(Modifier.align(Alignment.BottomCenter).fillMaxWidth().height(45.dp).background(Brush.verticalGradient(listOf(Color.Transparent, Color.Black.copy(alpha = 0.55f)))))
                    TvProgressBar(it, Modifier.align(Alignment.BottomCenter).padding(8.dp))
                }
            }
        },
        caption = { focused ->
            Column(verticalArrangement = Arrangement.spacedBy(2.dp)) {
                Text(item.displayTitle, fontSize = 12.5.sp, fontWeight = FontWeight.SemiBold, color = if (focused) Flexarr.screen else Flexarr.screen.copy(alpha = 0.85f), maxLines = 1, overflow = TextOverflow.Ellipsis)
                val remaining = if (item.progress != null) item.effectiveDuration?.let { Format.remaining(it - item.userData.positionSeconds) } else null
                Text(listOfNotNull(item.episodeSubtitle, remaining).joinToString("   "), fontSize = 11.5.sp, color = Flexarr.usher, maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
        },
    )
    if (menu) {
        TvItemMenu(item.displayTitle, itemActions(item, showsDetails = true, downloads = false), onDismiss = { menu = false })
    }
}

/** Top 10: a giant outlined marquee numeral tucked behind the poster. */
@Composable
fun TvTop10Card(rank: Int, item: ItemSummary, onClick: () -> Unit) {
    Row(verticalAlignment = Alignment.Bottom) {
        val glyph = TextStyle(fontFamily = Flexarr.marquee, fontSize = 165.sp, fontWeight = FontWeight.Black, letterSpacing = (-6).sp)
        Box(Modifier.padding(bottom = 20.dp)) {
            Text("$rank", style = glyph.copy(color = Flexarr.usher.copy(alpha = 0.9f), drawStyle = Stroke(width = 6f)))
            Text("$rank", style = glyph.copy(color = Flexarr.room))
        }
        TvPosterCard(item, onClick, Modifier.offset(x = (-18).dp), width = 110.dp)
    }
}

/** Round headshot with name and character, for cast rows. */
@Composable
fun TvPersonCard(name: String, detail: String?, image: String?, onClick: () -> Unit) {
    Column(Modifier.width(105.dp), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(8.dp)) {
        TvArtworkButton(ambient = Flexarr.silver, onClick = onClick, shape = CircleShape, contentDescription = listOfNotNull(name, detail).joinToString(", "), artwork = {
            RemoteImage(image, 200, Modifier.size(90.dp)) {
                Box(Modifier.fillMaxSize().background(Flexarr.seat), contentAlignment = Alignment.Center) {
                    val initials = name.split(" ").take(2).mapNotNull { it.firstOrNull()?.toString() }.joinToString("")
                    Text(initials, style = marquee(28, Flexarr.usher).copy(fontWeight = FontWeight.Bold))
                }
            }
        })
        Column(horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(1.dp)) {
            Text(name, fontSize = 11.5.sp, fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis)
            detail?.let { Text(it, fontSize = 11.sp, color = Flexarr.usher, maxLines = 1, overflow = TextOverflow.Ellipsis) }
        }
    }
}

/** An episode in a season list: still with progress, then number, title and overview. */
@Composable
fun TvEpisodeCard(episode: ItemSummary, overview: String?, isCurrent: Boolean, onClick: () -> Unit, modifier: Modifier = Modifier) {
    val width = 220.dp
    var menu by remember { mutableStateOf(false) }
    TvArtworkButton(
        ambient = glowColor(episode.accentColor),
        onClick = onClick,
        onLongClick = { menu = true },
        modifier = modifier.width(width),
        contentDescription = listOfNotNull(episode.episodeNumber?.let { "Episode $it" }, episode.title).joinToString(", "),
        artwork = {
            Box(Modifier.size(width, width * 9f / 16f)) {
                RemoteImage(episode.artwork.thumb ?: episode.artwork.backdrop, 500, Modifier.fillMaxSize()) {
                    Box(Modifier.fillMaxSize().background(Flexarr.seat), contentAlignment = Alignment.Center) {
                        Icon(Icons.Filled.PlayArrow, contentDescription = null, tint = Flexarr.usher, modifier = Modifier.size(20.dp))
                    }
                }
                episode.progress?.let {
                    Box(Modifier.align(Alignment.BottomCenter).fillMaxWidth().height(40.dp).background(Brush.verticalGradient(listOf(Color.Transparent, Color.Black.copy(alpha = 0.55f)))))
                    TvProgressBar(it, Modifier.align(Alignment.BottomCenter).padding(7.dp))
                }
                if (episode.userData.watched) TvWatchedBadge(Modifier.align(Alignment.TopEnd).padding(6.dp))
            }
        },
        caption = { focused ->
            Column(verticalArrangement = Arrangement.spacedBy(3.dp)) {
                Row(horizontalArrangement = Arrangement.spacedBy(7.dp)) {
                    val color = if (isCurrent) Flexarr.screen else Flexarr.usher
                    Text(episode.episodeNumber?.let { "Episode $it" } ?: "Special", fontSize = 11.sp, fontWeight = FontWeight.SemiBold, color = color)
                    episodeTime(episode)?.let { Text(it, fontSize = 11.sp, fontWeight = FontWeight.SemiBold, color = color) }
                }
                Text(episode.title, fontSize = 12.5.sp, fontWeight = FontWeight.SemiBold, color = if (focused) Flexarr.screen else Flexarr.screen.copy(alpha = 0.88f), maxLines = 1, overflow = TextOverflow.Ellipsis)
                if (!overview.isNullOrEmpty()) {
                    Text(overview, fontSize = 11.sp, color = Flexarr.usher, maxLines = 3, overflow = TextOverflow.Ellipsis, lineHeight = 14.sp)
                }
            }
        },
    )
    if (menu) TvItemMenu(episode.title, itemActions(episode, showsDetails = true, downloads = false), onDismiss = { menu = false })
}

private fun episodeTime(episode: ItemSummary): String? {
    if (episode.progress != null) episode.effectiveDuration?.let { return Format.remaining(it - episode.userData.positionSeconds) }
    return episode.runtimeMinutes?.let { Format.runtime(minutes = it) } ?: episode.durationSeconds?.let { Format.runtime(seconds = it) }
}

/** Thin bar for resume points. */
@Composable
fun TvProgressBar(value: Double, modifier: Modifier = Modifier, tint: Color = Flexarr.screen) {
    Box(modifier.fillMaxWidth().height(3.5.dp).background(Color.Black.copy(alpha = 0.45f), CircleShape)) {
        Box(Modifier.fillMaxWidth(value.toFloat().coerceIn(0.02f, 1f)).height(3.5.dp).background(tint, CircleShape))
    }
}

@Composable
fun TvWatchedBadge(modifier: Modifier = Modifier) {
    Box(modifier.size(18.dp).background(Flexarr.screen.copy(alpha = 0.92f), CircleShape), contentAlignment = Alignment.Center) {
        Icon(Icons.Filled.Check, contentDescription = "Watched", tint = Flexarr.room, modifier = Modifier.size(11.dp))
    }
}

/** A card's long-press menu, as the Apple TV's context menu: its choices in a column, Back to close. */
@Composable
fun TvItemMenu(title: String, actions: List<MenuAction>, onDismiss: () -> Unit) {
    val first = remember { FocusRequester() }
    Dialog(onDismissRequest = onDismiss) {
        Column(
            Modifier.width(320.dp).background(Flexarr.seat, RoundedCornerShape(14.dp)).padding(20.dp),
            verticalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            Text(title, fontSize = 15.sp, fontWeight = FontWeight.Bold, maxLines = 2, overflow = TextOverflow.Ellipsis, modifier = Modifier.padding(bottom = 4.dp))
            actions.forEachIndexed { index, action ->
                TvActionButton(
                    action.label,
                    onClick = {
                        onDismiss()
                        action.run()
                    },
                    modifier = Modifier.fillMaxWidth().then(if (index == 0) Modifier.focusRequester(first) else Modifier),
                )
            }
        }
    }
    LaunchedEffect(Unit) { runCatching { first.requestFocus() } }
}

/** Whether the TV has a screen for it: everything but podcasts, which stay on the phone and the web. */
internal val ItemSummary.showsOnTv: Boolean
    get() = kind != MediaKind.Podcast && kind != MediaKind.PodcastEpisode
