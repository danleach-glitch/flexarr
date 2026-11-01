package io.github.flexarr.ui.components

import io.github.flexarr.appModel

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.compositionLocalOf
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import android.os.Build
import androidx.compose.ui.draw.blur
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.semantics.CustomAccessibilityAction
import androidx.compose.ui.semantics.customActions
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import coil3.compose.AsyncImage
import io.github.flexarr.core.FlexarrClient
import io.github.flexarr.core.Format
import io.github.flexarr.core.ItemSummary
import io.github.flexarr.core.MediaKind
import io.github.flexarr.core.displayTitle
import io.github.flexarr.core.effectiveDuration
import io.github.flexarr.core.episodeSubtitle
import io.github.flexarr.core.isBookFinished
import io.github.flexarr.core.isListening
import io.github.flexarr.core.isResumable
import io.github.flexarr.core.listeningSubtitle
import io.github.flexarr.ui.books.bookActions
import io.github.flexarr.core.progress
import io.github.flexarr.core.releaseYear
import io.github.flexarr.ui.Navigator
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.glowColor
import io.github.flexarr.ui.theme.marquee

/** The signed-in client, for artwork; every screen inside the app has one. */
val LocalClient = staticCompositionLocalOf<FlexarrClient> { error("No client") }
val LocalNavigator = staticCompositionLocalOf<Navigator> { error("No navigator") }
/** Cards in Continue Watching offer to leave it. */
val LocalInContinueWatching = compositionLocalOf { false }

/** Artwork from the server, with [placeholder] in its place while it loads, or when there is none. */
@Composable
fun RemoteImage(path: String?, width: Int, modifier: Modifier = Modifier, placeholder: @Composable () -> Unit) {
    val url = LocalClient.current.imageUrl(path, width)
    var loaded by remember(url) { mutableStateOf(false) }
    Box(modifier) {
        if (!loaded) placeholder()
        if (url != null) {
            AsyncImage(model = url, contentDescription = null, contentScale = ContentScale.Crop, modifier = Modifier.fillMaxSize(), onSuccess = { loaded = true })
        }
    }
}

/** A 2:3 poster with the title and year beneath it (text never sits on artwork). [onOpen] runs as it opens (Search remembers the pick). */
@Composable
fun PosterCard(item: ItemSummary, modifier: Modifier = Modifier, width: Dp? = PosterWidth, onOpen: (() -> Unit)? = null, onClick: (() -> Unit)? = null) {
    val navigator = LocalNavigator.current
    var menu by remember { mutableStateOf(false) }
    val actions = itemActions(item, showsDetails = onClick != null)
    Box(modifier) {
        Column(
            (if (width != null) Modifier.width(width) else Modifier.fillMaxWidth())
                .pressable(onClick = { onOpen?.invoke(); onClick?.invoke() ?: navigator.open(item) }, onLongClick = { menu = true })
                .menuActions(actions),
            verticalArrangement = Arrangement.spacedBy(6.dp),
        ) {
            PosterArtwork(item)
            Column(verticalArrangement = Arrangement.spacedBy(1.dp)) {
                Text(item.title, fontSize = 13.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen.copy(alpha = 0.9f), maxLines = 1, overflow = TextOverflow.Ellipsis)
                posterSubtitle(item)?.let { Text(it, fontSize = 12.sp, color = Flexarr.usher, maxLines = 1) }
            }
        }
        ItemMenu(actions, menu, onDismiss = { menu = false })
    }
}

private fun posterSubtitle(item: ItemSummary): String? {
    if (item.kind == MediaKind.Audiobook) return item.book?.authors
    val unwatched = item.userData.unwatchedCount
    if (item.kind == MediaKind.Series && unwatched != null && unwatched > 0) {
        return listOfNotNull(item.releaseYear?.toString(), "$unwatched new").joinToString("  ")
    }
    return item.releaseYear?.toString()
}

/** Poster artwork with a title placeholder, watched tick and progress bar. Fills the width it's given. */
@Composable
fun PosterArtwork(item: ItemSummary, modifier: Modifier = Modifier) {
    // Album covers, artist pictures and book covers are square; everything else is a 2:3 poster.
    val ratio = if (item.kind == MediaKind.Album || item.kind == MediaKind.Artist || item.kind == MediaKind.Track || item.kind == MediaKind.Audiobook) 1f else 2f / 3f
    Box(modifier.fillMaxWidth().aspectRatio(ratio).clip(RoundedCornerShape(6.dp)).background(Flexarr.seat)) {
        RemoteImage(item.artwork.poster, 342, Modifier.fillMaxSize()) {
            Box(
                Modifier.fillMaxSize().background(Brush.verticalGradient(listOf(glowColor(item.accentColor).copy(alpha = 0.35f), Flexarr.seat))),
                contentAlignment = Alignment.Center,
            ) {
                Text(item.title, style = marquee(18, Flexarr.screen.copy(alpha = 0.8f)).copy(fontWeight = FontWeight.Bold), textAlign = TextAlign.Center, modifier = Modifier.padding(8.dp))
            }
        }
        item.progress?.let { ProgressBar(it, Modifier.align(Alignment.BottomCenter).padding(6.dp)) }
        if (item.userData.watched || item.isBookFinished) WatchedBadge(Modifier.align(Alignment.TopEnd).padding(6.dp))
    }
}

/**
 * 16:9 card for Continue Watching: tapping plays, with how much is left beneath it. A book or podcast episode plays in
 * its own player, its square art centred over a dimmed, blurred copy of itself.
 */
@Composable
fun WideCard(item: ItemSummary, width: Dp = WideWidth, onClick: (() -> Unit)? = null) {
    val navigator = LocalNavigator.current
    var menu by remember { mutableStateOf(false) }
    val actions = itemActions(item, showsDetails = true)
    val play = {
        when (item.kind) {
            MediaKind.Audiobook -> navigator.playBook(item)
            MediaKind.PodcastEpisode -> navigator.playEpisode(item)
            else -> navigator.play(item)
        }
    }
    Box {
        Column(
            Modifier.width(width).pressable(onClick = { onClick?.invoke() ?: play() }, onLongClick = { menu = true }).menuActions(actions),
            verticalArrangement = Arrangement.spacedBy(6.dp),
        ) {
            Box(Modifier.fillMaxWidth().aspectRatio(16f / 9f).clip(RoundedCornerShape(6.dp)).background(Flexarr.seat)) {
                if (item.isListening) {
                    SquareArtInWide(item)
                } else {
                    val still = if (item.kind == MediaKind.Episode) item.artwork.thumb ?: item.artwork.backdrop else item.artwork.backdrop ?: item.artwork.thumb
                    RemoteImage(still, 640, Modifier.fillMaxSize()) {
                        Box(Modifier.fillMaxSize().background(Brush.linearGradient(listOf(glowColor(item.accentColor).copy(alpha = 0.4f), Flexarr.seat))))
                    }
                }
                Box(Modifier.align(Alignment.Center).size(44.dp).clip(CircleShape).background(Color.Black.copy(alpha = 0.45f)), contentAlignment = Alignment.Center) {
                    Icon(Icons.Filled.PlayArrow, contentDescription = null, tint = Flexarr.screen)
                }
                item.progress?.let { ProgressBar(it, Modifier.align(Alignment.BottomCenter).padding(8.dp)) }
            }
            Column(verticalArrangement = Arrangement.spacedBy(1.dp)) {
                Text(item.displayTitle, fontSize = 13.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen.copy(alpha = 0.9f), maxLines = 1, overflow = TextOverflow.Ellipsis)
                val remaining = if (item.progress != null) item.effectiveDuration?.let { Format.remaining(it - item.userData.positionSeconds) } else null
                Text(listOfNotNull(item.episodeSubtitle ?: item.listeningSubtitle, remaining).joinToString("   "), fontSize = 12.sp, color = Flexarr.usher, maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
        }
        ItemMenu(actions, menu, onDismiss = { menu = false })
    }
}

/** A cover or podcast's artwork in a 16:9 card: whole in the middle, over a dimmed, blurred copy filling the rest. */
@Composable
private fun SquareArtInWide(item: ItemSummary) {
    // A podcast episode's own artwork, else its show's (the server falls back to it).
    val art = if (item.kind == MediaKind.PodcastEpisode) item.artwork.thumb ?: item.artwork.poster else item.artwork.poster ?: item.artwork.thumb
    val glow = glowColor(item.accentColor)
    Box(Modifier.fillMaxSize()) {
        // Blur needs Android 12; before it the copy is only dimmed further. The same width as the cover below, so one
        // download serves both.
        RemoteImage(art, 342, Modifier.fillMaxSize().blur(24.dp)) {
            Box(Modifier.fillMaxSize().background(Brush.linearGradient(listOf(glow.copy(alpha = 0.4f), Flexarr.seat))))
        }
        Box(Modifier.fillMaxSize().background(Color.Black.copy(alpha = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) 0.35f else 0.6f)))
        Box(Modifier.align(Alignment.Center).fillMaxHeight().padding(vertical = 10.dp).aspectRatio(1f).clip(RoundedCornerShape(4.dp)).background(Flexarr.seat)) {
            RemoteImage(art, 342, Modifier.fillMaxSize()) {
                Box(
                    Modifier.fillMaxSize().background(Brush.verticalGradient(listOf(glow.copy(alpha = 0.4f), Flexarr.seat))),
                    contentAlignment = Alignment.Center,
                ) {
                    Text(item.title, style = marquee(13, Flexarr.screen.copy(alpha = 0.8f)).copy(fontWeight = FontWeight.Bold), textAlign = TextAlign.Center, maxLines = 3, modifier = Modifier.padding(6.dp))
                }
            }
        }
    }
}

/** Top 10: a big outlined numeral tucked behind the poster. */
@Composable
fun Top10Card(rank: Int, item: ItemSummary, width: Dp = PosterWidth) {
    Row(verticalAlignment = Alignment.Bottom) {
        val size = (width.value * 1.15f).sp
        val glyph = TextStyle(fontFamily = Flexarr.marquee, fontSize = size, fontWeight = FontWeight.Black, letterSpacing = (-size.value * 0.04f).sp)
        Box(Modifier.padding(bottom = 24.dp)) {
            Text("$rank", style = glyph.copy(color = Flexarr.usher.copy(alpha = 0.9f), drawStyle = Stroke(width = 6f)))
            Text("$rank", style = glyph.copy(color = Flexarr.room))
        }
        PosterCard(item, Modifier.offset(x = (-14).dp), width = width)
    }
}

/** Round headshot with name and character, for cast rows; tapping opens everything they're in. */
@Composable
fun PersonCard(name: String, detail: String?, image: String?, onClick: (() -> Unit)? = null) {
    Column(Modifier.width(96.dp).then(if (onClick != null) Modifier.pressable(onClick) else Modifier), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(8.dp)) {
        RemoteImage(image, 200, Modifier.size(84.dp).clip(CircleShape).background(Flexarr.seat)) {
            Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                val initials = name.split(" ").take(2).mapNotNull { it.firstOrNull()?.toString() }.joinToString("")
                Text(initials, style = marquee(28, Flexarr.usher).copy(fontWeight = FontWeight.Bold))
            }
        }
        Column(horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(1.dp)) {
            Text(name, fontSize = 12.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen, maxLines = 1, overflow = TextOverflow.Ellipsis)
            detail?.let { Text(it, fontSize = 11.sp, color = Flexarr.usher, maxLines = 1, overflow = TextOverflow.Ellipsis) }
        }
    }
}

/** One thing a card's long-press menu does. */
class MenuAction(val label: String, val run: () -> Unit)

/**
 * What the long-press menu shared by cards offers, as iPhone's context menu: [ItemMenu] shows it, and [menuActions]
 * gives TalkBack the same list. A TV keeps nothing, so offers no [downloads].
 */
@Composable
fun itemActions(item: ItemSummary, showsDetails: Boolean = false, downloads: Boolean = true): List<MenuAction> {
    val navigator = LocalNavigator.current
    val inContinueWatching = LocalInContinueWatching.current
    val model = androidx.compose.ui.platform.LocalContext.current.appModel
    // In Continue watching, a book or podcast episode can leave it without being marked finished.
    val leave = if (inContinueWatching) listOf(MenuAction("Remove") { navigator.removeFromContinueWatching(item) }) else emptyList()
    if (item.kind == MediaKind.Audiobook) return bookActions(item, showsDetails, downloads) + leave
    if (item.kind == MediaKind.PodcastEpisode) return podcastEpisodeActions(item, showsDetails, kids = model.isKids) + leave
    return buildList {
        if (item.kind == MediaKind.Movie || item.kind == MediaKind.Episode) {
            add(MenuAction(if (item.isResumable) "Resume" else "Play") { navigator.play(item) })
            if (item.isResumable) add(MenuAction("Play from beginning") { navigator.play(item, fromBeginning = true) })
        }
        if (showsDetails) add(MenuAction(if (item.kind == MediaKind.Episode) "Go to episode" else "Go to details") { navigator.openItem(item.id) })
        // Download at Settings' size, as a title's page does.
        if (downloads && (item.kind == MediaKind.Movie || item.kind == MediaKind.Episode)) {
            val download = model.downloads.entry(item.id)
            if (download == null) {
                add(MenuAction("Download") { navigator.download(item) })
            } else {
                add(MenuAction(if (download.isDownloaded) "Remove download" else "Stop download") { model.downloads.delete(download.id) })
            }
        }
        val watched = item.userData.watched || item.userData.unwatchedCount == 0
        // A child's watched marks follow what they watch, not what they tap.
        val kids = model.isKids
        if (!kids) add(MenuAction(if (watched) "Mark as unwatched" else "Mark as watched") { navigator.setWatched(item, !watched) })
        // Part way through: unwatched forgets the place, which takes it out of Continue Watching.
        if (item.isResumable && !watched) {
            if (!kids) add(MenuAction("Mark as unwatched") { navigator.setWatched(item, false) })
        } else if (inContinueWatching && item.kind == MediaKind.Episode && !watched) {
            add(MenuAction("Remove") { navigator.removeFromContinueWatching(item) })
        }
        val show = item.seriesTitle
        if (!kids && item.kind == MediaKind.Episode && item.seriesId != null && show != null) {
            add(MenuAction("Mark $show as watched") { navigator.setShowWatched(item) })
        }
        if (model.hasWatchlist && (item.kind == MediaKind.Movie || item.kind == MediaKind.Series)) {
            val on = item.id in model.watchlistItems
            add(MenuAction(if (on) "Remove from watchlist" else "Add to watchlist") { navigator.setOnWatchlist(item, !on) })
        }
    }
}

/** A podcast episode listed as a title (Continue watching): play it, its podcast, played or not. */
@Composable
private fun podcastEpisodeActions(item: ItemSummary, showsDetails: Boolean, kids: Boolean): List<MenuAction> {
    val navigator = LocalNavigator.current
    val played = item.userData.watched
    return buildList {
        add(MenuAction(if (item.isResumable) "Resume" else "Play") { navigator.playEpisode(item) })
        val podcast = item.seriesId ?: item.parentId
        if (showsDetails && podcast != null) add(MenuAction("Go to podcast") { navigator.openPodcast(podcast) })
        if (!kids) add(MenuAction(if (played) "Mark as unplayed" else "Mark as played") { navigator.setWatched(item, !played) })
    }
}

/** A card's menu as TalkBack actions, so it's there without the long press. */
fun Modifier.menuActions(actions: List<MenuAction>): Modifier =
    semantics { customActions = actions.map { action -> CustomAccessibilityAction(action.label) { action.run(); true } } }

/** The long-press menu shared by cards. */
@Composable
fun ItemMenu(actions: List<MenuAction>, expanded: Boolean, onDismiss: () -> Unit) {
    DropdownMenu(expanded = expanded, onDismissRequest = onDismiss) {
        actions.forEach { action ->
            DropdownMenuItem(text = { Text(action.label) }, onClick = {
                onDismiss()
                action.run()
            })
        }
    }
}
