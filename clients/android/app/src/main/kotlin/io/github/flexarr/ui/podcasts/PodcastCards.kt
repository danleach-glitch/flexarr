package io.github.flexarr.ui.podcasts

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.sizeIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.PlaylistAdd
import androidx.compose.material.icons.automirrored.filled.Undo
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.filled.Pause
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.QueuePlayNext
import androidx.compose.material.icons.filled.RemoveCircleOutline
import androidx.compose.material.icons.outlined.ArrowCircleDown
import androidx.compose.material.icons.outlined.CheckCircle
import androidx.compose.material.icons.outlined.Circle
import androidx.compose.material.icons.outlined.Delete
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Text
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
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.github.flexarr.AppModel
import io.github.flexarr.core.DirectoryPodcast
import io.github.flexarr.core.Podcast
import io.github.flexarr.core.PodcastEpisode
import io.github.flexarr.core.podcastEpisode
import io.github.flexarr.ui.components.ActionButton
import io.github.flexarr.ui.downloads.EpisodeDownloadButton
import io.github.flexarr.ui.components.Edge
import io.github.flexarr.ui.components.FactsLine
import io.github.flexarr.ui.components.ItemMenu
import io.github.flexarr.ui.components.LabeledAction
import io.github.flexarr.ui.components.MenuAction
import io.github.flexarr.ui.components.menuActions
import io.github.flexarr.ui.components.LocalClient
import io.github.flexarr.ui.components.RemoteImage
import io.github.flexarr.ui.components.SwipeAction
import io.github.flexarr.ui.components.SwipeActions
import io.github.flexarr.ui.components.pressable
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.glowColor
import io.github.flexarr.ui.theme.marquee
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import java.time.LocalDate
import java.time.OffsetDateTime
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.time.format.TextStyle
import java.time.temporal.ChronoUnit
import java.util.Locale

// The iPhone app's PodcastCards.swift: a show's artwork and card, a show from Apple's directory, an episode's row with
// Play ringed by how far it's got, and an episode's notes.

/** A show's square artwork, from the server's image cache, with its name on a gradient until it arrives. */
@Composable
fun PodcastArtwork(path: String?, title: String, accent: String? = null, modifier: Modifier = Modifier, imageWidth: Int = 500, cornerRadius: Dp = 8.dp) {
    RemoteImage(path, imageWidth, modifier.aspectRatio(1f).clip(RoundedCornerShape(cornerRadius)).background(Flexarr.seat)) {
        Box(
            Modifier.fillMaxSize().background(Brush.verticalGradient(listOf(glowColor(accent).copy(alpha = 0.4f), Flexarr.seat))),
            contentAlignment = Alignment.Center,
        ) {
            Text(title, style = marquee(15, Flexarr.screen.copy(alpha = 0.8f)).copy(fontWeight = FontWeight.Bold), textAlign = TextAlign.Center, modifier = Modifier.padding(8.dp))
        }
    }
}

/** A followed show: its artwork (with its name beneath, unless the row is artwork alone) and how many episodes wait. */
@Composable
fun PodcastCard(podcast: Podcast, onClick: () -> Unit, modifier: Modifier = Modifier, showsTitle: Boolean = true) {
    Column(modifier.pressable(onClick), verticalArrangement = Arrangement.spacedBy(6.dp)) {
        Box {
            PodcastArtwork(podcast.artwork.poster, podcast.title, podcast.accentColor)
            if (podcast.unplayedCount > 0 && podcast.isSubscribed) {
                Box(
                    Modifier.align(Alignment.TopEnd).padding(6.dp).sizeIn(minWidth = 22.dp, minHeight = 22.dp).clip(CircleShape).background(Flexarr.screen).padding(horizontal = 6.dp),
                    contentAlignment = Alignment.Center,
                ) {
                    Text(if (podcast.unplayedCount > 99) "99+" else podcast.unplayedCount.toString(), fontSize = 11.sp, fontWeight = FontWeight.Bold, color = Flexarr.room, style = Tabular)
                }
            }
        }
        if (showsTitle) Text(podcast.title, fontSize = 15.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen, maxLines = 2, overflow = TextOverflow.Ellipsis)
    }
}

/** A show from Apple's directory: artwork, name and who makes it; tapping adds it to the library and opens it. */
@Composable
fun DirectoryCard(show: DirectoryPodcast, isAdding: Boolean, onClick: () -> Unit) {
    Column(
        Modifier.alpha(if (show.hasFeed) 1f else 0.5f).pressable(onClick, enabled = show.hasFeed),
        verticalArrangement = Arrangement.spacedBy(6.dp),
    ) {
        Box {
            PodcastArtwork(show.artwork, show.title)
            if (isAdding) {
                Box(Modifier.matchParentSize().clip(RoundedCornerShape(8.dp)).background(androidx.compose.ui.graphics.Color.Black.copy(alpha = 0.45f)), contentAlignment = Alignment.Center) {
                    CircularProgressIndicator(color = Flexarr.screen, modifier = Modifier.size(28.dp))
                }
            }
            if (show.subscribed) {
                Icon(Icons.Filled.CheckCircle, contentDescription = "Following", tint = Flexarr.screen, modifier = Modifier.align(Alignment.TopEnd).padding(6.dp).size(22.dp))
            }
        }
        Text(show.title, fontSize = 15.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen, maxLines = 2, overflow = TextOverflow.Ellipsis)
        show.author?.let { Text(it, fontSize = 12.sp, color = Flexarr.usher, maxLines = 1, overflow = TextOverflow.Ellipsis) }
    }
}

/**
 * An episode in a list: when it came out, its title and the start of its notes, how long is left, with Play (and how far
 * it's got) on the right. Tapping the row opens the whole notes; a long press offers Play next, Up Next and Played. As on
 * iPhone, swiping toward the end marks it played (or unplayed: all the way across does it at once) or plays it next, and
 * toward the start adds it to Up Next or downloads it (in Up Next, takes it off, again all the way across).
 */
@Composable
fun PodcastEpisodeRow(
    model: AppModel,
    episode: PodcastEpisode,
    showsPodcast: Boolean = false,
    compact: Boolean = false,
    /** Marks it played or unplayed in the list's own way (the show page shows it at once). */
    setPlayed: (Boolean) -> Unit,
    /** In Up Next: the menu offers taking it off, and so does swiping toward the start, in place of Up Next and download. */
    removeFromUpNext: (() -> Unit)? = null,
) {
    var notes by remember { mutableStateOf(false) }
    var menu by remember { mutableStateOf(false) }
    val download = model.downloads.entry(episode.id)
    val actions = buildList {
        add(MenuAction(if (episode.progress != null) "Resume" else "Play") { model.podcasts.play(episode) })
        add(MenuAction("Play next") { model.addToUpNext(episode.id, next = true) })
        add(MenuAction("Add to Up Next") { model.addToUpNext(episode.id, next = false) })
        removeFromUpNext?.let { add(MenuAction("Remove from Up Next", it)) }
        add(MenuAction(if (episode.isPlayed) "Mark as unplayed" else "Mark as played") { setPlayed(!episode.isPlayed) })
        if (download == null) {
            add(MenuAction("Download") { model.scope.launch { runCatching { model.downloads.downloadEpisode(episode.id) } } })
        } else {
            add(MenuAction(if (download.isDownloaded) "Delete download" else "Stop download") { model.downloads.delete(download.id) })
        }
        // Part way through: unplayed forgets the place, so it leaves the listening list.
        if (episode.progress != null) add(MenuAction("Mark as unplayed") { setPlayed(false) })
    }
    // The iPhone's swipe actions, each also in the menu above (so TalkBack has them once, as custom actions).
    val startSwipe = buildList {
        add(
            if (episode.isPlayed) SwipeAction("Unplayed", Icons.AutoMirrored.Filled.Undo, Flexarr.aisle) { setPlayed(false) }
            else SwipeAction("Played", Icons.Filled.CheckCircle, Flexarr.played) { setPlayed(true) },
        )
        if (removeFromUpNext == null) add(SwipeAction("Play next", Icons.Filled.QueuePlayNext, Flexarr.upNext) { model.addToUpNext(episode.id, next = true) })
    }
    val endSwipe = buildList {
        if (removeFromUpNext != null) {
            add(SwipeAction("Remove from Up Next", Icons.Filled.RemoveCircleOutline, Flexarr.danger, Flexarr.room, removeFromUpNext))
        } else {
            add(SwipeAction("Add to Up Next", Icons.AutoMirrored.Filled.PlaylistAdd, Flexarr.upNext) { model.addToUpNext(episode.id, next = false) })
            if (download == null) {
                add(SwipeAction("Download", Icons.Outlined.ArrowCircleDown, Flexarr.aisle) { model.scope.launch { runCatching { model.downloads.downloadEpisode(episode.id) } } })
            } else {
                add(SwipeAction(if (download.isDownloaded) "Delete download" else "Stop download", Icons.Outlined.Delete, Flexarr.danger, Flexarr.room) { model.downloads.delete(download.id) })
            }
        }
    }
    SwipeActions(startSwipe, endSwipe, fullSwipeEnd = removeFromUpNext != null) {
        Box {
            Row(
                Modifier.fillMaxWidth().pressable(onClick = { notes = true }, onLongClick = { menu = true }).menuActions(actions)
                    .padding(horizontal = Edge).padding(vertical = if (compact) 10.dp else 12.dp),
                horizontalArrangement = Arrangement.spacedBy(12.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                if (showsPodcast) {
                    PodcastArtwork(episode.artwork.thumb ?: episode.artwork.poster, episode.podcastTitle, episode.accentColor, Modifier.size(56.dp), imageWidth = 185, cornerRadius = 6.dp)
                }
                Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(3.dp)) {
                    val meta = listOfNotNull(if (showsPodcast) episode.podcastTitle else null, dateLine(episode.publishedAt)).joinToString("  ")
                    if (meta.isNotEmpty()) Text(meta, fontSize = 12.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.usher, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    Text(episode.title, fontSize = 17.sp, fontWeight = FontWeight.SemiBold, color = if (episode.isPlayed) Flexarr.usher else Flexarr.screen, maxLines = 2, overflow = TextOverflow.Ellipsis)
                    if (!compact) episode.overview?.let { Text(it, fontSize = 15.sp, color = Flexarr.usher, maxLines = 2, overflow = TextOverflow.Ellipsis) }
                    episode.timeLine?.let { Text(it, fontSize = 12.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.usher, style = Tabular, modifier = Modifier.padding(top = if (compact) 0.dp else 2.dp)) }
                }
                EpisodeDownloadButton(model, episode.id, episode.title)
                EpisodePlayButton(model, episode)
            }
            ItemMenu(actions, menu, onDismiss = { menu = false })
        }
    }
    if (notes) EpisodeSheet(model, episode, setPlayed, onDismiss = { notes = false })
}

/** Play (or pause, for the one playing), ringed with how far the episode has got. */
@Composable
fun EpisodePlayButton(model: AppModel, episode: PodcastEpisode) {
    val player = model.podcasts
    val current = player.current?.id == episode.id
    val playing = current && player.isPlaying
    val fraction = if (current && player.duration > 0) (player.position / player.duration).toFloat() else (episode.progress ?: 0.0).toFloat()
    Box(
        Modifier.size(44.dp).pressable({ if (current) player.togglePlayPause() else player.play(episode) }),
        contentAlignment = Alignment.Center,
    ) {
        Canvas(Modifier.size(30.dp)) {
            val stroke = 2.dp.toPx()
            val inset = stroke / 2
            val arc = Size(size.width - stroke, size.height - stroke)
            drawArc(Flexarr.screen.copy(alpha = 0.25f), 0f, 360f, false, topLeft = androidx.compose.ui.geometry.Offset(inset, inset), size = arc, style = Stroke(stroke))
            if (fraction > 0) drawArc(Flexarr.screen, -90f, 360f * fraction.coerceIn(0f, 1f), false, topLeft = androidx.compose.ui.geometry.Offset(inset, inset), size = arc, style = Stroke(stroke, cap = StrokeCap.Round))
        }
        Icon(if (playing) Icons.Filled.Pause else Icons.Filled.PlayArrow, contentDescription = if (playing) "Pause" else "Play ${episode.title}", tint = Flexarr.screen, modifier = Modifier.size(16.dp))
    }
}

/** An episode's whole notes, with Play (which goes on to Now Playing) and Mark as played. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun EpisodeSheet(model: AppModel, episode: PodcastEpisode, setPlayed: (Boolean) -> Unit, onDismiss: () -> Unit) {
    val client = LocalClient.current
    var full by remember { mutableStateOf<PodcastEpisode?>(null) }
    var played by remember { mutableStateOf<Boolean?>(null) }
    LaunchedEffect(episode.id) { full = runCatching { client.podcastEpisode(episode.id) }.getOrNull() }
    val shown = full ?: episode
    ModalBottomSheet(onDismissRequest = onDismiss, containerColor = Flexarr.room) {
        Column(Modifier.verticalScroll(rememberScrollState()).padding(horizontal = Edge).padding(bottom = 32.dp), verticalArrangement = Arrangement.spacedBy(16.dp)) {
            Row(horizontalArrangement = Arrangement.spacedBy(14.dp), verticalAlignment = Alignment.Top) {
                PodcastArtwork(shown.artwork.thumb ?: shown.artwork.poster, shown.podcastTitle, shown.accentColor, Modifier.size(88.dp), imageWidth = 342)
                Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
                    Text(shown.podcastTitle, fontSize = 15.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.usher)
                    Text(shown.title, fontSize = 20.sp, fontWeight = FontWeight.Bold, color = Flexarr.screen)
                    FactsLine(listOfNotNull(dateLine(shown.publishedAt), shown.timeLine, if (shown.explicit) "Explicit" else null))
                }
            }
            Row(horizontalArrangement = Arrangement.spacedBy(12.dp), verticalAlignment = Alignment.CenterVertically) {
                // The episode that's playing pauses here, and carries on when paused, rather than starting again.
                val player = model.podcasts
                val isCurrent = player.current?.id == shown.id
                val isPlaying = isCurrent && player.isPlaying
                ActionButton(
                    if (isPlaying) "Pause" else if (isCurrent || shown.progress != null) "Resume" else "Play",
                    onClick = {
                        if (isPlaying) return@ActionButton player.togglePlayPause()
                        // Playing (or carrying on) goes to the player: the notes give way to Now Playing.
                        if (isCurrent) player.togglePlayPause() else player.play(shown)
                        onDismiss()
                        model.scope.launch {
                            delay(300) // once this sheet has gone
                            model.showsNowPlaying = true
                        }
                    },
                    icon = if (isPlaying) Icons.Filled.Pause else Icons.Filled.PlayArrow,
                    prominent = true,
                    ambient = glowColor(shown.accentColor),
                )
                val isPlayed = played ?: shown.isPlayed
                EpisodeDownloadButton(model, shown.id, shown.title)
                // Shows where it stands: a green tick once played (tap to undo), an outline tick to mark it.
                LabeledAction(
                    if (isPlayed) "Mark as unplayed" else "Mark as played",
                    if (isPlayed) Icons.Filled.CheckCircle else Icons.Outlined.CheckCircle,
                    {
                        setPlayed(!isPlayed)
                        played = !isPlayed
                    },
                    tint = if (isPlayed) Flexarr.downloaded else Flexarr.screen,
                    iconSize = if (isPlayed) 30.dp else 22.dp,
                )
                if (shown.progress != null && played == null) {
                    // Part way through: forget the place without marking it played.
                    LabeledAction("Mark as unplayed", Icons.Outlined.Circle, {
                        setPlayed(false)
                        played = false
                    })
                }
            }
            shown.overview?.let { notes -> SelectionContainer { Text(notes, fontSize = 17.sp, color = Flexarr.screen.copy(alpha = 0.9f)) } }
        }
    }
}

/** "Today", "Yesterday", "Monday", "21 Oct", "21 Oct 2024". */
fun dateLine(published: String?): String? {
    val date = published?.let { runCatching { OffsetDateTime.parse(it).atZoneSameInstant(ZoneId.systemDefault()).toLocalDate() }.getOrNull() } ?: return null
    val today = LocalDate.now()
    val days = ChronoUnit.DAYS.between(date, today)
    return when {
        days == 0L -> "Today"
        days == 1L -> "Yesterday"
        days in 2..6 -> date.dayOfWeek.getDisplayName(TextStyle.FULL, Locale.getDefault())
        date.year == today.year -> date.format(DateTimeFormatter.ofPattern("d MMM", Locale.getDefault()))
        else -> date.format(DateTimeFormatter.ofPattern("d MMM yyyy", Locale.getDefault()))
    }
}

/** A swipe's "played" colour, as podcast apps use (the iPhone's Color.played). */
val Flexarr.played: Color get() = Color(0xFF6B5BB5)
/** A swipe's "Up Next" colour (the iPhone's Color.upNext). */
val Flexarr.upNext: Color get() = Color(0xFF4A6FA5)

/** Marks an episode played or unplayed for screens that don't keep their own list: sent, then screens reload. */
fun setEpisodePlayed(model: AppModel, episode: PodcastEpisode, played: Boolean) {
    val client = model.client ?: return
    model.scope.launch {
        runCatching { client.setWatched(episode.id, played) }
        model.watchStateChanged()
    }
}
