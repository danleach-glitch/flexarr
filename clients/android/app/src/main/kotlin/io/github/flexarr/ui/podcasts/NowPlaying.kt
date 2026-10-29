package io.github.flexarr.ui.podcasts

import android.content.Intent
import android.graphics.Bitmap
import androidx.compose.animation.Crossfade
import androidx.compose.animation.core.animateDpAsState
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.spring
import androidx.compose.animation.core.tween
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.gestures.drag
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.sizeIn
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.KeyboardArrowLeft
import androidx.compose.material.icons.automirrored.filled.KeyboardArrowRight
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Forward30
import androidx.compose.material.icons.filled.GraphicEq
import androidx.compose.material.icons.filled.MoreHoriz
import androidx.compose.material.icons.filled.Pause
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Replay
import androidx.compose.material.icons.outlined.Bedtime
import androidx.compose.material.icons.outlined.CheckCircle
import androidx.compose.material.icons.outlined.Delete
import androidx.compose.material.icons.outlined.Description
import androidx.compose.material.icons.outlined.Download
import androidx.compose.material.icons.outlined.Podcasts
import androidx.compose.material.icons.outlined.Share
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.ModalBottomSheet
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
import androidx.compose.ui.draw.drawWithContent
import androidx.compose.ui.draw.scale
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.BlendMode
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.CompositingStrategy
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalContext
import androidx.activity.compose.BackHandler
import androidx.compose.material3.minimumInteractiveComponentSize
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.clearAndSetSemantics
import androidx.compose.ui.semantics.onClick
import androidx.compose.ui.semantics.role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.stateDescription
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.text.LinkAnnotation
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.TextLinkStyles
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.text.withLink
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import coil3.SingletonImageLoader
import coil3.request.ImageRequest
import coil3.request.allowHardware
import coil3.toBitmap
import io.github.flexarr.AppModel
import io.github.flexarr.audio.PodcastPlayer
import io.github.flexarr.core.Format
import io.github.flexarr.core.NoteLink
import io.github.flexarr.core.Podcast
import io.github.flexarr.core.PodcastChapter
import io.github.flexarr.core.PodcastEpisode
import io.github.flexarr.core.indexAt
import io.github.flexarr.core.noteLinks
import io.github.flexarr.core.podcast
import io.github.flexarr.core.podcastChapters
import io.github.flexarr.core.podcastEpisode
import io.github.flexarr.player.SleepTimer
import io.github.flexarr.ui.components.Edge
import io.github.flexarr.ui.components.FactsLine
import io.github.flexarr.ui.components.LocalClient
import io.github.flexarr.ui.components.LocalNavigator
import io.github.flexarr.ui.components.RepeatWhileShowing
import io.github.flexarr.ui.components.pressable
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.RoomBackground
import io.github.flexarr.ui.theme.glow
import io.github.flexarr.ui.theme.glowColor
import io.github.flexarr.ui.theme.marquee
import kotlinx.coroutines.async
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import java.util.Date

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
    Row(
        Modifier.fillMaxWidth().padding(horizontal = 8.dp, vertical = 6.dp).shadow(12.dp, RoundedCornerShape(14.dp)).clip(RoundedCornerShape(14.dp)).background(Flexarr.seat).padding(end = 6.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Box(Modifier.minimumInteractiveComponentSize().size(width = 36.dp, height = 56.dp).pressable(player::stop), contentAlignment = Alignment.Center) {
            Icon(Icons.Filled.Close, contentDescription = "Stop and close", tint = Flexarr.usher, modifier = Modifier.size(16.dp))
        }
        Row(Modifier.weight(1f).pressable({ model.showsNowPlaying = true }).padding(vertical = 6.dp), horizontalArrangement = Arrangement.spacedBy(12.dp), verticalAlignment = Alignment.CenterVertically) {
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
    if (model.showsNowPlaying) NowPlayingSheet(model, onDismiss = { model.showsNowPlaying = false })
}

@Composable
private fun RoundButton(icon: ImageVector, label: String, onClick: () -> Unit) {
    Box(Modifier.minimumInteractiveComponentSize().size(40.dp).clip(CircleShape).pressable(onClick), contentAlignment = Alignment.Center) {
        Icon(icon, contentDescription = label, tint = Flexarr.screen, modifier = Modifier.size(24.dp))
    }
}

/** What Now Playing shows of the episode beyond what the player knows: its whole notes, its chapters and its show. */
private class PlayingEpisode(val id: String, val episode: PodcastEpisode?, val chapters: List<PodcastChapter>, val podcast: Podcast?)

/**
 * The episode that's playing, as a sheet that doesn't scroll: the artwork, the title and show, the chapter, a slim
 * scrubber, back 15 and forward 30, then a row of speed, the sleep timer, Notes and a menu. Notes swaps the artwork
 * for a panel of the chapters (tapping one goes there), the notes (whose times jump there too) and Up Next, with the
 * controls staying put below. The iPhone app's NowPlayingView, for a podcast.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun NowPlayingSheet(model: AppModel, onDismiss: () -> Unit) {
    val player = model.podcasts
    val episode = player.current
    val client = LocalClient.current
    val navigator = LocalNavigator.current
    LaunchedEffect(episode == null) { if (episode == null) onDismiss() }
    LaunchedEffect(episode?.id) { model.loadUpNext() }
    var playing by remember { mutableStateOf<PlayingEpisode?>(null) }
    LaunchedEffect(episode?.id) {
        val id = episode?.id ?: return@LaunchedEffect
        val (full, chapters) = coroutineScope {
            val notes = async { runCatching { client.podcastEpisode(id) }.getOrNull() }
            val found = async { runCatching { client.podcastChapters(id) }.getOrNull() }
            notes.await() to found.await().orEmpty()
        }
        val known = playing?.podcast?.takeIf { it.id == episode.podcastId }
        playing = PlayingEpisode(id, full, chapters, known)
        if (known == null) runCatching { client.podcast(episode.podcastId) }.getOrNull()?.let { show ->
            playing?.takeIf { it.id == id }?.let { playing = PlayingEpisode(id, it.episode, it.chapters, show) }
        }
    }
    // The artwork's own colour, for a glow when the server gives none.
    val artworkGlow = rememberArtworkGlow(if (episode?.accentColor == null) episode?.artwork?.let { it.thumb ?: it.poster } else null)
    // The episode's chapters, notes and Up Next are showing in the artwork's place.
    var showingDetails by remember { mutableStateOf(false) }
    // Back from them goes to the artwork, before it closes the sheet.
    BackHandler(enabled = showingDetails) { showingDetails = false }
    // Where the thumb is while the scrubber is dragged; null when the episode's own position shows.
    var scrubbing by remember { mutableStateOf<Double?>(null) }
    if (episode == null) return
    val loaded = playing?.takeIf { it.id == episode.id }
    val chapters = loaded?.chapters.orEmpty()

    fun goToPodcast() {
        onDismiss()
        navigator.openPodcast(episode.podcastId)
    }

    ModalBottomSheet(onDismissRequest = onDismiss, sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true), containerColor = Flexarr.room, dragHandle = null) {
        val ambient = if (episode.accentColor == null) artworkGlow ?: glowColor(null) else glowColor(episode.accentColor)
        RoomBackground(ambient = ambient, intensity = 0.45f) {
            Column(
                Modifier.fillMaxSize().padding(horizontal = 28.dp).padding(bottom = 24.dp),
                horizontalAlignment = Alignment.CenterHorizontally,
                verticalArrangement = Arrangement.spacedBy(20.dp),
            ) {
                // The sheet's handle, which TalkBack offers as Close (the drag that closes it isn't there for TalkBack).
                Box(
                    Modifier.padding(top = 10.dp).size(width = 40.dp, height = 5.dp).clip(CircleShape).background(Flexarr.aisle).clearAndSetSemantics {
                        contentDescription = "Close"
                        role = Role.Button
                        onClick { onDismiss(); true }
                    },
                )
                Crossfade(showingDetails, Modifier.weight(1f), animationSpec = tween(300), label = "details") { details ->
                    Column(Modifier.fillMaxSize(), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(20.dp)) {
                        if (details) {
                            SmallHeader(episode, Modifier.padding(top = 4.dp)) { showingDetails = false }
                            Details(model, loaded, chapters, Modifier.weight(1f))
                        } else {
                            Box(Modifier.weight(1f).fillMaxWidth(), contentAlignment = Alignment.Center) {
                                // Smaller while paused, as every music app does.
                                val scale by animateFloatAsState(if (player.isPlaying) 1f else 0.9f, spring(dampingRatio = 0.8f, stiffness = 300f), label = "cover")
                                PodcastArtwork(
                                    episode.artwork.poster ?: episode.artwork.thumb, episode.podcastTitle, episode.accentColor,
                                    Modifier.sizeIn(maxWidth = 320.dp, maxHeight = 320.dp).aspectRatio(1f, matchHeightConstraintsFirst = true).scale(scale)
                                        .shadow(24.dp, RoundedCornerShape(12.dp), ambientColor = ambient.copy(alpha = 0.35f), spotColor = Color.Black.copy(alpha = 0.5f)),
                                    imageWidth = 800, cornerRadius = 12.dp,
                                )
                            }
                            Column(horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(4.dp)) {
                                Text(episode.title, fontSize = 20.sp, fontWeight = FontWeight.Bold, color = Flexarr.screen, textAlign = TextAlign.Center, maxLines = 2, overflow = TextOverflow.Ellipsis)
                                Text(
                                    episode.podcastTitle, fontSize = 15.sp, fontWeight = FontWeight.Medium, color = Flexarr.usher, maxLines = 1, overflow = TextOverflow.Ellipsis,
                                    modifier = Modifier.pressable(::goToPodcast),
                                )
                            }
                        }
                    }
                }
                if (chapters.isNotEmpty()) ChapterStrip(player, chapters, scrubbing) { showingDetails = true }
                Scrubber(player, chapters, scrubbing) { scrubbing = it }
                player.errorMessage?.let { Text(it, fontSize = 13.sp, color = Flexarr.danger, textAlign = TextAlign.Center) }
                Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceEvenly, verticalAlignment = Alignment.CenterVertically) {
                    Transport(Icons.Filled.Replay, "Back 15 seconds", badge = "15") { player.skip(-15) }
                    Box(Modifier.size(76.dp).clip(CircleShape).background(Flexarr.screen).pressable(player::togglePlayPause), contentAlignment = Alignment.Center) {
                        if (player.isLoading) CircularProgressIndicator(color = Flexarr.room, modifier = Modifier.size(28.dp))
                        else Icon(if (player.isPlaying) Icons.Filled.Pause else Icons.Filled.PlayArrow, contentDescription = if (player.isPlaying) "Pause" else "Play", tint = Flexarr.room, modifier = Modifier.size(32.dp))
                    }
                    Transport(Icons.Filled.Forward30, "Forward 30 seconds") { player.skip(30) }
                }
                // Speed, the sleep timer, Notes and the episode's menu: four of a kind, evenly spaced, each lit while it's set.
                Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                    SpeedMenu(player, Modifier.weight(1f))
                    SleepMenu(player, Modifier.weight(1f))
                    Box(Modifier.weight(1f), contentAlignment = Alignment.Center) {
                        ExtraLabel(showingDetails, if (showingDetails) "Hide notes" else "Notes", { showingDetails = !showingDetails }) { tint ->
                            Icon(Icons.Outlined.Description, contentDescription = null, tint = tint, modifier = Modifier.size(22.dp))
                        }
                    }
                    EpisodeMenu(model, episode, loaded?.podcast, ::goToPodcast, Modifier.weight(1f))
                }
            }
        }
    }
}

/** The episode's artwork kept small, beside its name; tapping it puts the big artwork back. */
@Composable
private fun SmallHeader(episode: PodcastEpisode, modifier: Modifier = Modifier, close: () -> Unit) {
    Row(
        modifier.fillMaxWidth().pressable(close),
        horizontalArrangement = Arrangement.spacedBy(14.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        PodcastArtwork(episode.artwork.thumb ?: episode.artwork.poster, episode.podcastTitle, episode.accentColor, Modifier.size(56.dp), imageWidth = 185, cornerRadius = 8.dp)
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
            Text(episode.title, fontSize = 17.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen, maxLines = 1, overflow = TextOverflow.Ellipsis)
            Text(episode.podcastTitle, fontSize = 15.sp, fontWeight = FontWeight.Medium, color = Flexarr.usher, maxLines = 1, overflow = TextOverflow.Ellipsis)
        }
    }
}

/** The episode's chapters, notes and Up Next, scrolling in the artwork's place and fading out at either edge. */
@Composable
private fun Details(model: AppModel, loaded: PlayingEpisode?, chapters: List<PodcastChapter>, modifier: Modifier = Modifier) {
    Column(
        modifier.fillMaxWidth()
            .graphicsLayer(compositingStrategy = CompositingStrategy.Offscreen)
            .drawWithContent {
                drawContent()
                drawRect(
                    Brush.verticalGradient(0f to Color.Transparent, 0.03f to Color.Black, 0.94f to Color.Black, 1f to Color.Transparent),
                    blendMode = BlendMode.DstIn,
                )
            }
            .verticalScroll(rememberScrollState())
            .padding(bottom = 12.dp),
        verticalArrangement = Arrangement.spacedBy(20.dp),
    ) {
        if (chapters.isNotEmpty()) ChapterList(model.podcasts, chapters)
        loaded?.episode?.let { Notes(model.podcasts, it) }
        UpNext(model)
    }
}

@Composable
private fun SectionTitle(title: String) {
    Text(title, style = marquee(22).copy(fontWeight = FontWeight.Bold), modifier = Modifier.padding(horizontal = 12.dp).padding(top = 8.dp))
}

/** The episode's chapters: tapping one goes there; the one playing is lit, and those heard are dimmed. */
@Composable
private fun ChapterList(player: PodcastPlayer, chapters: List<PodcastChapter>) {
    val current = chapters.indexAt(player.position)
    Column(Modifier.fillMaxWidth(), verticalArrangement = Arrangement.spacedBy(2.dp)) {
        SectionTitle("Chapters")
        chapters.forEachIndexed { offset, chapter ->
            Row(
                Modifier.fillMaxWidth().pressable({ player.seek(chapter.startSeconds) }).padding(horizontal = 12.dp, vertical = 8.dp)
                    .semantics(mergeDescendants = true) { contentDescription = "${chapter.title}, at ${Format.timestamp(chapter.startSeconds)}" },
                horizontalArrangement = Arrangement.spacedBy(12.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(Format.timestamp(chapter.startSeconds), fontSize = 12.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.usher, style = Tabular, modifier = Modifier.widthIn(min = 44.dp))
                Text(
                    chapter.title, fontSize = 15.sp, modifier = Modifier.weight(1f),
                    fontWeight = if (offset == current) FontWeight.Bold else FontWeight.Normal,
                    color = if (offset == current) Flexarr.screen else Flexarr.screen.copy(alpha = if ((current ?: -1) > offset) 0.55f else 0.85f),
                )
                if (offset == current) {
                    Icon(if (player.isPlaying) Icons.Filled.GraphicEq else Icons.Filled.Pause, contentDescription = null, tint = Flexarr.silver, modifier = Modifier.size(14.dp))
                }
            }
        }
    }
}

/**
 * The episode's notes: when it came out, how long it is and whether it's explicit, then the notes, whose times jump
 * there and whose web addresses open.
 */
@Composable
private fun Notes(player: PodcastPlayer, episode: PodcastEpisode) {
    Column(Modifier.fillMaxWidth(), verticalArrangement = Arrangement.spacedBy(8.dp)) {
        SectionTitle("Episode Notes")
        FactsLine(
            listOfNotNull(dateLine(episode.publishedAt), episode.durationSeconds?.let(Format::runtime), if (episode.explicit) "Explicit" else null),
            Modifier.padding(horizontal = 12.dp),
        )
        episode.overview?.let { text ->
            val notes = remember(text, episode.durationSeconds) { episodeNotes(text, episode.durationSeconds, player::seek) }
            SelectionContainer {
                Text(notes, fontSize = 15.sp, color = Flexarr.screen.copy(alpha = 0.85f), modifier = Modifier.padding(horizontal = 12.dp))
            }
        }
    }
}

/** An episode's notes, with times ("12:34", "1:02:55") that jump there and web addresses that open. */
private fun episodeNotes(notes: String, duration: Double?, seek: (Double) -> Unit): AnnotatedString = buildAnnotatedString {
    val time = TextLinkStyles(SpanStyle(color = Flexarr.silver, fontWeight = FontWeight.SemiBold, fontFeatureSettings = "tnum"))
    val web = TextLinkStyles(SpanStyle(color = Flexarr.silver))
    var at = 0
    for (link in noteLinks(notes, duration)) {
        append(notes.substring(at, link.range.first))
        val annotation = when (link) {
            is NoteLink.Seek -> LinkAnnotation.Clickable("seek", time) { seek(link.seconds) }
            is NoteLink.Web -> LinkAnnotation.Url(link.url, web)
        }
        withLink(annotation) { append(notes.substring(link.range)) }
        at = link.range.last + 1
    }
    append(notes.substring(at))
}

/** The chapter playing, with the ones either side a tap away; its name opens the chapters. */
@Composable
private fun ChapterStrip(player: PodcastPlayer, chapters: List<PodcastChapter>, scrubbing: Double?, openChapters: () -> Unit) {
    val current = chapters.indexAt(scrubbing ?: player.position)
    Row(Modifier.fillMaxWidth().clip(CircleShape).background(Flexarr.screen.copy(alpha = 0.08f)).padding(horizontal = 4.dp), verticalAlignment = Alignment.CenterVertically) {
        ChapterStep(Icons.AutoMirrored.Filled.KeyboardArrowLeft, "Previous chapter") {
            // Back to the start of this chapter, or (near its start already) to the one before.
            if (current == null) return@ChapterStep player.seek(0.0)
            val start = chapters[current].startSeconds
            player.seek(if (player.position - start > 3 || current == 0) start else chapters[current - 1].startSeconds)
        }
        Crossfade(current, Modifier.weight(1f), animationSpec = tween(200), label = "chapter") { index ->
            Box(Modifier.fillMaxWidth().height(36.dp).pressable(openChapters), contentAlignment = Alignment.Center) {
                Text(chapters[index ?: 0].title, fontSize = 15.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen, maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
        }
        val next = (current ?: -1) + 1
        ChapterStep(Icons.AutoMirrored.Filled.KeyboardArrowRight, "Next chapter", enabled = next < chapters.size) {
            if (next < chapters.size) player.seek(chapters[next].startSeconds)
        }
    }
}

/** The arrows either side of the chapter's name. */
@Composable
private fun ChapterStep(icon: ImageVector, label: String, enabled: Boolean = true, onClick: () -> Unit) {
    Box(Modifier.size(width = 40.dp, height = 36.dp).pressable(onClick, enabled), contentAlignment = Alignment.Center) {
        Icon(icon, contentDescription = label, tint = Flexarr.screen.copy(alpha = if (enabled) 0.85f else 0.3f), modifier = Modifier.size(20.dp))
    }
}

/**
 * A slim scrubber: a thin bar that thickens while it's dragged, with a gap where each chapter starts, and seeks once
 * it's let go. Beneath it, the time gone, when the episode will end at the speed it's playing, and the time left.
 */
@Composable
private fun Scrubber(player: PodcastPlayer, chapters: List<PodcastChapter>, scrubbing: Double?, onScrub: (Double?) -> Unit) {
    val context = LocalContext.current
    val duration = player.duration
    val shown = scrubbing ?: player.position
    val fraction = if (duration > 0) (shown / duration).coerceIn(0.0, 1.0).toFloat() else 0f
    val thickness by animateDpAsState(if (scrubbing == null) 5.dp else 10.dp, spring(dampingRatio = 0.8f, stiffness = 400f), label = "scrubber")
    Column(Modifier.fillMaxWidth(), verticalArrangement = Arrangement.spacedBy(2.dp)) {
        Box(
            Modifier.fillMaxWidth().height(28.dp)
                .semantics { contentDescription = "Position"; stateDescription = "${Format.timestamp(player.position)} of ${Format.timestamp(duration)}" }
                .pointerInput(duration) {
                    if (duration <= 0) return@pointerInput
                    fun at(x: Float) = (x / size.width).coerceIn(0f, 1f) * duration
                    awaitEachGesture {
                        val down = awaitFirstDown()
                        var target = at(down.position.x)
                        onScrub(target)
                        drag(down.id) { change ->
                            target = at(change.position.x)
                            onScrub(target)
                            change.consume()
                        }
                        player.seek(target)
                        onScrub(null)
                    }
                },
            contentAlignment = Alignment.Center,
        ) {
            Canvas(Modifier.fillMaxWidth().height(thickness).clip(CircleShape)) {
                drawRect(Flexarr.screen.copy(alpha = 0.18f))
                drawRect(Flexarr.screen, size = Size(size.width * fraction, size.height))
                val gap = 2.dp.toPx()
                for (chapter in chapters) {
                    if (chapter.startSeconds <= 0 || chapter.startSeconds >= duration) continue
                    drawRect(Flexarr.room, topLeft = Offset((size.width * chapter.startSeconds / duration).toFloat() - gap / 2, 0f), size = Size(gap, size.height))
                }
            }
        }
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            val caption = TextStyle(fontSize = 12.sp, fontWeight = FontWeight.Medium, color = Flexarr.usher, fontFeatureSettings = "tnum")
            Text(Format.timestamp(shown), style = caption, modifier = Modifier.weight(1f))
            if (duration > 0) {
                // When it'll finish, at the speed it's playing.
                val left = (duration - player.position).coerceAtLeast(0.0) / player.speed.coerceAtLeast(0.5)
                val ends = android.text.format.DateFormat.getTimeFormat(context).format(Date(System.currentTimeMillis() + (left * 1000).toLong()))
                Text("Ends $ends", style = caption)
            }
            Text(if (duration > 0) "-" + Format.timestamp((duration - shown).coerceAtLeast(0.0)) else "", style = caption, textAlign = TextAlign.End, modifier = Modifier.weight(1f))
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

/**
 * One of the row's controls (speed, sleep timer, Notes, the menu): the same size and weight, lit with a silver pill
 * while it's set, when its glyph takes the room's colour.
 */
@Composable
private fun ExtraLabel(active: Boolean, label: String, onClick: () -> Unit, content: @Composable (tint: Color) -> Unit) {
    Box(Modifier.sizeIn(minWidth = 44.dp, minHeight = 44.dp).pressable(onClick).semantics(mergeDescendants = true) { contentDescription = label }, contentAlignment = Alignment.Center) {
        Box(
            Modifier.clip(CircleShape).then(if (active) Modifier.background(Flexarr.silver) else Modifier).padding(horizontal = 10.dp).sizeIn(minWidth = 44.dp, minHeight = 34.dp),
            contentAlignment = Alignment.Center,
        ) {
            content(if (active) Flexarr.room else Flexarr.screen.copy(alpha = 0.85f))
        }
    }
}

@Composable
private fun SpeedMenu(player: PodcastPlayer, modifier: Modifier = Modifier) {
    var open by remember { mutableStateOf(false) }
    val label = PodcastPlayer.speedLabel(player.speed)
    Box(modifier, contentAlignment = Alignment.Center) {
        ExtraLabel(player.speed != 1.0, "Speed, $label", { open = true }) { tint ->
            Text(label, fontSize = 16.sp, fontWeight = FontWeight.SemiBold, style = Tabular, color = tint)
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
private fun SleepMenu(player: PodcastPlayer, modifier: Modifier = Modifier) {
    var open by remember { mutableStateOf(false) }
    val setting = player.sleep.setting
    Box(modifier, contentAlignment = Alignment.Center) {
        ExtraLabel(setting != null, if (setting == null) "Sleep timer" else "Sleep timer on", { open = true }) { tint ->
            Row(horizontalArrangement = Arrangement.spacedBy(4.dp), verticalAlignment = Alignment.CenterVertically) {
                Icon(Icons.Outlined.Bedtime, contentDescription = null, tint = tint, modifier = Modifier.size(22.dp))
                player.sleep.endsAt?.let { end ->
                    // Minutes left, counting down.
                    var now by remember { mutableLongStateOf(System.currentTimeMillis()) }
                    RepeatWhileShowing(1_000, end) { now = System.currentTimeMillis() }
                    Text(Format.timestamp(((end - now) / 1000.0).coerceAtLeast(0.0)), fontSize = 12.sp, fontWeight = FontWeight.SemiBold, color = tint, style = Tabular)
                }
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

/** What can be done with the episode: open its show, mark it played, download it, share the show. */
@Composable
private fun EpisodeMenu(model: AppModel, episode: PodcastEpisode, podcast: Podcast?, goToPodcast: () -> Unit, modifier: Modifier = Modifier) {
    val context = LocalContext.current
    val player = model.podcasts
    var open by remember { mutableStateOf(false) }
    Box(modifier, contentAlignment = Alignment.Center) {
        ExtraLabel(false, "More", { open = true }) { tint ->
            Icon(Icons.Filled.MoreHoriz, contentDescription = null, tint = tint, modifier = Modifier.size(24.dp))
        }
        DropdownMenu(open, onDismissRequest = { open = false }, containerColor = Flexarr.seat) {
            MenuEntry("Go to podcast", Icons.Outlined.Podcasts) { open = false; goToPodcast() }
            MenuEntry("Mark as played", Icons.Outlined.CheckCircle) {
                open = false
                val client = model.client ?: return@MenuEntry
                model.scope.launch {
                    runCatching { client.setWatched(episode.id, true) }
                    model.watchStateChanged()
                    // Played: on to what comes next, as if it had finished.
                    player.seek((player.duration - 0.5).coerceAtLeast(0.0))
                }
            }
            val download = model.downloads.entry(episode.id)
            if (download == null) {
                MenuEntry("Download", Icons.Outlined.Download) {
                    open = false
                    model.scope.launch { runCatching { model.downloads.downloadEpisode(episode.id) } }
                }
            } else {
                MenuEntry(if (download.isDownloaded) "Remove download" else "Stop download", Icons.Outlined.Delete) {
                    open = false
                    model.downloads.delete(download.id)
                }
            }
            podcast?.link?.takeIf { it.startsWith("http") }?.let { link ->
                MenuEntry("Share podcast", Icons.Outlined.Share) {
                    open = false
                    val send = Intent(Intent.ACTION_SEND).setType("text/plain").putExtra(Intent.EXTRA_TEXT, link).putExtra(Intent.EXTRA_TITLE, podcast.title)
                    context.startActivity(Intent.createChooser(send, null))
                }
            }
        }
    }
}

@Composable
private fun MenuEntry(label: String, icon: ImageVector, onClick: () -> Unit) {
    DropdownMenuItem(
        text = { Text(label, color = Flexarr.screen) },
        onClick = onClick,
        leadingIcon = { Icon(icon, contentDescription = null, tint = Flexarr.screen, modifier = Modifier.size(20.dp)) },
    )
}

@Composable
private fun Choice(label: String, checked: Boolean, onClick: () -> Unit) {
    DropdownMenuItem(
        text = { Text(label, color = Flexarr.screen) },
        onClick = onClick,
        leadingIcon = { Box(Modifier.size(20.dp)) { if (checked) Icon(Icons.Filled.Check, contentDescription = "Chosen", tint = Flexarr.screen) } },
    )
}

/** The artwork's average colour, lifted to glow on the room, read from a small drawing of it once it's here. */
@Composable
private fun rememberArtworkGlow(path: String?): Color? {
    val context = LocalContext.current
    val client = LocalClient.current
    var glow by remember(path) { mutableStateOf<Color?>(null) }
    LaunchedEffect(path) {
        val url = client.imageUrl(path, 185) ?: return@LaunchedEffect
        val request = ImageRequest.Builder(context).data(url).size(16).allowHardware(false).build()
        val bitmap: Bitmap = SingletonImageLoader.get(context).execute(request).image?.toBitmap() ?: return@LaunchedEffect
        val small = Bitmap.createScaledBitmap(bitmap, 16, 16, true)
        val pixels = IntArray(16 * 16).also { small.getPixels(it, 0, 16, 0, 0, 16, 16) }
        fun channel(shift: Int) = pixels.map { (it shr shift) and 0xFF }.average().toFloat() / 255f
        glow = Color(channel(16), channel(8), channel(0)).glow()
    }
    return glow
}

/** Up Next, as the server keeps it: tapping one plays it. */
@Composable
private fun UpNext(model: AppModel) {
    val next = model.upNext.filter { it.id != model.podcasts.current?.id }
    if (next.isEmpty()) return
    Column(Modifier.fillMaxWidth(), verticalArrangement = Arrangement.spacedBy(4.dp)) {
        SectionTitle("Up Next")
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
