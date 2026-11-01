package io.github.flexarr.ui.books

import androidx.activity.compose.BackHandler
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
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.KeyboardArrowLeft
import androidx.compose.material.icons.automirrored.filled.KeyboardArrowRight
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.GraphicEq
import androidx.compose.material.icons.filled.Pause
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Remove
import androidx.compose.material.icons.filled.Replay
import androidx.compose.material.icons.filled.MoreHoriz
import androidx.compose.material.icons.automirrored.outlined.MenuBook
import androidx.compose.material.icons.outlined.Bedtime
import androidx.compose.material.icons.outlined.BookmarkAdd
import androidx.compose.material.icons.outlined.CheckCircle
import androidx.compose.material.icons.outlined.Download
import androidx.compose.material.icons.outlined.FormatListBulleted
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.minimumInteractiveComponentSize
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
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.scale
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.clearAndSetSemantics
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.onClick
import androidx.compose.ui.semantics.role
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.stateDescription
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.github.flexarr.AppModel
import io.github.flexarr.audio.BookPlayer
import io.github.flexarr.cast.CastButton
import io.github.flexarr.cast.CastControls
import io.github.flexarr.cast.Casting
import io.github.flexarr.core.BookChapter
import io.github.flexarr.core.BookSpeed
import io.github.flexarr.core.Format
import io.github.flexarr.core.chapterAt
import io.github.flexarr.player.SleepTimer
import io.github.flexarr.ui.components.LocalNavigator
import io.github.flexarr.ui.components.RepeatWhileShowing
import io.github.flexarr.ui.components.pressable
import io.github.flexarr.ui.podcasts.Tabular
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.RoomBackground
import io.github.flexarr.ui.theme.glowColor
import io.github.flexarr.ui.theme.marquee
import java.util.Date

// A book playing, as the podcast player's bar and sheet on the phone: above the tabs on every screen, and opened into
// the cover, chapter, scrubber and controls, with speed, the sleep timer, bookmarks and the chapter list.

/** The book playing, above the tabs: close, the cover and chapter (opening the player), play or pause, and skip forward. */
@Composable
fun BookNowPlayingBar(model: AppModel) {
    val player = model.books
    val book = player.current ?: return
    Column {
        Offers(player)
        Row(
            Modifier.fillMaxWidth().padding(horizontal = 8.dp, vertical = 6.dp).shadow(12.dp, RoundedCornerShape(14.dp)).clip(RoundedCornerShape(14.dp)).background(Flexarr.seat).padding(end = 6.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Box(Modifier.minimumInteractiveComponentSize().size(width = 36.dp, height = 56.dp).pressable(player::stop), contentAlignment = Alignment.Center) {
                Icon(Icons.Filled.Close, contentDescription = "Stop and close", tint = Flexarr.usher, modifier = Modifier.size(16.dp))
            }
            Row(Modifier.weight(1f).pressable({ model.showsBookPlayer = true }).padding(vertical = 6.dp), horizontalArrangement = Arrangement.spacedBy(12.dp), verticalAlignment = Alignment.CenterVertically) {
                BookCover(book.artwork.poster, book.title, book.accentColor, Modifier.size(44.dp), imageWidth = 185, corner = 6.dp)
                Column(Modifier.weight(1f)) {
                    Text(book.title, fontSize = 15.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    Text(player.chapter?.title ?: book.book?.authors.orEmpty(), fontSize = 12.sp, color = Flexarr.usher, maxLines = 1, overflow = TextOverflow.Ellipsis)
                }
            }
            val cast = Casting.shared?.takeIf { it.casts(Casting.Kind.Book) }
            if (player.isLoading && cast == null) {
                Box(Modifier.size(40.dp), contentAlignment = Alignment.Center) { CircularProgressIndicator(color = Flexarr.screen, modifier = Modifier.size(20.dp), strokeWidth = 2.dp) }
            } else {
                val playing = cast?.isPlaying ?: player.isPlaying
                RoundButton(if (playing) Icons.Filled.Pause else Icons.Filled.PlayArrow, if (playing) "Pause" else "Play") { cast?.togglePlayPause() ?: player.togglePlayPause() }
            }
            SkipButton(player.listening.skipForwardSeconds, forward = true, size = 40) { cast?.skip(player.listening.skipForwardSeconds) ?: player.skipForward() }
        }
    }
    if (model.showsBookPlayer) BookPlayerSheet(model, onDismiss = { model.showsBookPlayer = false })
}

/** "Jump to 3:12:04 from Pixel 9?" and, at the end of a book, the next in its series: each with its button and a close. */
@Composable
private fun Offers(player: BookPlayer) {
    player.jumpOffer?.let { offer ->
        Offer("Jump to ${Format.timestamp(offer.positionSeconds)} from ${offer.device}?", "Jump", player::acceptJump, player::dismissJump)
    }
    player.nextOffer?.let { next ->
        Offer("Next: ${next.title}", "Play", player::playNext, player::dismissNext)
    }
}

@Composable
private fun Offer(text: String, action: String, onAction: () -> Unit, onDismiss: () -> Unit) {
    Row(
        Modifier.fillMaxWidth().padding(horizontal = 8.dp).padding(top = 6.dp).clip(RoundedCornerShape(14.dp)).background(Flexarr.aisle).padding(start = 14.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(text, fontSize = 14.sp, fontWeight = FontWeight.Medium, color = Flexarr.screen, maxLines = 2, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f))
        TextButton(onClick = onAction) { Text(action, color = Flexarr.silver, fontWeight = FontWeight.Bold) }
        Box(Modifier.minimumInteractiveComponentSize().size(36.dp).pressable(onDismiss), contentAlignment = Alignment.Center) {
            Icon(Icons.Filled.Close, contentDescription = "Dismiss", tint = Flexarr.usher, modifier = Modifier.size(16.dp))
        }
    }
}

@Composable
private fun RoundButton(icon: ImageVector, label: String, onClick: () -> Unit) {
    Box(Modifier.minimumInteractiveComponentSize().size(40.dp).clip(CircleShape).pressable(onClick), contentAlignment = Alignment.Center) {
        Icon(icon, contentDescription = label, tint = Flexarr.screen, modifier = Modifier.size(24.dp))
    }
}

/** A circular arrow with the step inside it ("30"), back or forward. */
@Composable
private fun SkipButton(seconds: Int, forward: Boolean, size: Int = 56, onClick: () -> Unit) {
    Box(
        Modifier.minimumInteractiveComponentSize().size(size.dp).clip(CircleShape).pressable(onClick)
            .semantics(mergeDescendants = true) { contentDescription = if (forward) "Forward $seconds seconds" else "Back $seconds seconds" },
        contentAlignment = Alignment.Center,
    ) {
        Icon(Icons.Filled.Replay, contentDescription = null, tint = Flexarr.screen, modifier = Modifier.size((size * 0.54f).dp).scale(scaleX = if (forward) -1f else 1f, scaleY = 1f))
        Text("$seconds", fontSize = if (size >= 50) 10.sp else 8.sp, fontWeight = FontWeight.Bold, color = Flexarr.screen, modifier = Modifier.padding(top = 3.dp))
    }
}

/**
 * The book playing, as a sheet: its cover (or the chapters and bookmarks in its place), title and author, the chapter
 * with the ones either side a tap away, the scrubber, skips and Play, then speed, the sleep timer, a bookmark, the
 * chapter list, Cast and a menu.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun BookPlayerSheet(model: AppModel, onDismiss: () -> Unit) {
    val player = model.books
    val book = player.current
    val navigator = LocalNavigator.current
    LaunchedEffect(book == null) { if (book == null) onDismiss() }
    var showingList by remember { mutableStateOf(false) }
    BackHandler(enabled = showingList) { showingList = false }
    var scrubbing by remember { mutableStateOf<Double?>(null) }
    var addingBookmark by remember { mutableStateOf(false) }
    if (book == null) return
    val chapters = player.chapters
    val ambient = glowColor(book.accentColor)
    val cast = Casting.shared?.takeIf { it.casts(Casting.Kind.Book) }

    ModalBottomSheet(onDismissRequest = onDismiss, sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true), containerColor = Flexarr.room, dragHandle = null) {
        RoomBackground(ambient = ambient, intensity = 0.45f) {
            Column(
                Modifier.fillMaxSize().padding(horizontal = 28.dp).padding(bottom = 24.dp),
                horizontalAlignment = Alignment.CenterHorizontally,
                verticalArrangement = Arrangement.spacedBy(18.dp),
            ) {
                Box(
                    Modifier.padding(top = 10.dp).size(width = 40.dp, height = 5.dp).clip(CircleShape).background(Flexarr.aisle).clearAndSetSemantics {
                        contentDescription = "Close"
                        role = Role.Button
                        onClick { onDismiss(); true }
                    },
                )
                Crossfade(showingList, Modifier.weight(1f), animationSpec = tween(300), label = "list") { list ->
                    Column(Modifier.fillMaxSize(), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(16.dp)) {
                        if (list) {
                            ChaptersAndBookmarks(player, Modifier.weight(1f)) { showingList = false }
                        } else {
                            Box(Modifier.weight(1f).fillMaxWidth(), contentAlignment = Alignment.Center) {
                                val scale by animateFloatAsState(if (player.isPlaying) 1f else 0.9f, spring(dampingRatio = 0.8f, stiffness = 300f), label = "cover")
                                BookCover(
                                    book.artwork.poster, book.title, book.accentColor,
                                    Modifier.sizeIn(maxWidth = 320.dp, maxHeight = 320.dp).aspectRatio(1f, matchHeightConstraintsFirst = true).scale(scale)
                                        .shadow(24.dp, RoundedCornerShape(12.dp), ambientColor = ambient.copy(alpha = 0.35f), spotColor = Color.Black.copy(alpha = 0.5f)),
                                    imageWidth = 800, corner = 12.dp,
                                )
                            }
                            Column(horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(4.dp)) {
                                Text(book.title, fontSize = 20.sp, fontWeight = FontWeight.Bold, color = Flexarr.screen, textAlign = TextAlign.Center, maxLines = 2, overflow = TextOverflow.Ellipsis)
                                book.book?.authors?.let {
                                    Text(it, fontSize = 15.sp, fontWeight = FontWeight.Medium, color = Flexarr.usher, maxLines = 1, overflow = TextOverflow.Ellipsis,
                                        modifier = Modifier.pressable({ onDismiss(); navigator.openBook(book.id) }))
                                }
                            }
                        }
                    }
                }
                // On a Chromecast, its scrubber and controls take the phone's place.
                if (cast != null) {
                    CastControls(cast)
                } else {
                    if (chapters.isNotEmpty()) ChapterStrip(player, chapters, scrubbing) { showingList = true }
                    Scrubber(player, chapters, scrubbing) { scrubbing = it }
                    player.errorMessage?.let { Text(it, fontSize = 13.sp, color = Flexarr.danger, textAlign = TextAlign.Center) }
                    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceEvenly, verticalAlignment = Alignment.CenterVertically) {
                        SkipButton(player.listening.skipBackSeconds, forward = false, onClick = player::skipBack)
                        Box(Modifier.size(76.dp).clip(CircleShape).background(Flexarr.screen).pressable(player::togglePlayPause), contentAlignment = Alignment.Center) {
                            if (player.isLoading) CircularProgressIndicator(color = Flexarr.room, modifier = Modifier.size(28.dp))
                            else Icon(if (player.isPlaying) Icons.Filled.Pause else Icons.Filled.PlayArrow, contentDescription = if (player.isPlaying) "Pause" else "Play", tint = Flexarr.room, modifier = Modifier.size(32.dp))
                        }
                        SkipButton(player.listening.skipForwardSeconds, forward = true, onClick = player::skipForward)
                    }
                }
                Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                    SpeedMenu(player, Modifier.weight(1f))
                    SleepMenu(player, Modifier.weight(1f))
                    Box(Modifier.weight(1f), contentAlignment = Alignment.Center) {
                        Extra(false, "Add bookmark", { addingBookmark = true }) { tint -> Icon(Icons.Outlined.BookmarkAdd, contentDescription = null, tint = tint, modifier = Modifier.size(22.dp)) }
                    }
                    Box(Modifier.weight(1f), contentAlignment = Alignment.Center) {
                        Extra(showingList, if (showingList) "Hide chapters" else "Chapters", { showingList = !showingList }) { tint ->
                            Icon(Icons.Outlined.FormatListBulleted, contentDescription = null, tint = tint, modifier = Modifier.size(22.dp))
                        }
                    }
                    if (Casting.shared?.available == true) Box(Modifier.weight(1f), contentAlignment = Alignment.Center) { CastButton() }
                    BookMenu(model, onDismiss, Modifier.weight(1f))
                }
                Offers(player)
            }
        }
    }
    if (addingBookmark) AddBookmarkDialog(player) { addingBookmark = false }
}

/** The chapter playing, with the ones either side a tap away; its name opens the list. */
@Composable
private fun ChapterStrip(player: BookPlayer, chapters: List<BookChapter>, scrubbing: Double?, openList: () -> Unit) {
    val current = chapters.chapterAt(scrubbing ?: player.position)
    Row(Modifier.fillMaxWidth().clip(CircleShape).background(Flexarr.screen.copy(alpha = 0.08f)).padding(horizontal = 4.dp), verticalAlignment = Alignment.CenterVertically) {
        Step(Icons.AutoMirrored.Filled.KeyboardArrowLeft, "Previous chapter", onClick = player::previousChapter)
        Crossfade(current, Modifier.weight(1f), animationSpec = tween(200), label = "chapter") { index ->
            Box(Modifier.fillMaxWidth().height(48.dp).pressable(openList), contentAlignment = Alignment.Center) {
                Text(chapters[index ?: 0].title, fontSize = 15.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen, maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
        }
        Step(Icons.AutoMirrored.Filled.KeyboardArrowRight, "Next chapter", enabled = (current ?: -1) + 1 < chapters.size, onClick = player::nextChapter)
    }
}

@Composable
private fun Step(icon: ImageVector, label: String, enabled: Boolean = true, onClick: () -> Unit) {
    Box(Modifier.minimumInteractiveComponentSize().size(48.dp).clip(CircleShape).pressable(onClick, enabled), contentAlignment = Alignment.Center) {
        Icon(icon, contentDescription = label, tint = Flexarr.screen.copy(alpha = if (enabled) 0.85f else 0.3f), modifier = Modifier.size(20.dp))
    }
}

/**
 * The scrubber: the chapter playing by default (a whole book is too long to place a finger in), or the whole book, with
 * a gap where each chapter starts; tapping the times switches. Beneath it, the time gone, when it'll end at this speed,
 * and the time left at it.
 */
@Composable
private fun Scrubber(player: BookPlayer, chapters: List<BookChapter>, scrubbing: Double?, onScrub: (Double?) -> Unit) {
    val context = LocalContext.current
    var wholeBook by remember(chapters.isEmpty()) { mutableStateOf(chapters.isEmpty()) }
    val shown = scrubbing ?: player.position
    val chapterIndex = chapters.chapterAt(shown)
    val span = if (!wholeBook && chapterIndex != null) {
        val chapter = chapters[chapterIndex]
        chapter.startSeconds to (chapter.endSeconds.takeIf { it > chapter.startSeconds } ?: player.duration)
    } else {
        0.0 to player.duration
    }
    val length = (span.second - span.first).coerceAtLeast(0.0)
    val fraction = if (length > 0) ((shown - span.first) / length).coerceIn(0.0, 1.0).toFloat() else 0f
    val thickness by animateDpAsState(if (scrubbing == null) 5.dp else 10.dp, spring(dampingRatio = 0.8f, stiffness = 400f), label = "scrubber")
    val rate = player.speed.coerceAtLeast(0.5)
    Column(Modifier.fillMaxWidth(), verticalArrangement = Arrangement.spacedBy(2.dp)) {
        Box(
            Modifier.fillMaxWidth().height(28.dp)
                .semantics { contentDescription = "Position"; stateDescription = "${Format.timestamp(shown - span.first)} of ${Format.timestamp(length)}" }
                .pointerInput(span, length) {
                    if (length <= 0) return@pointerInput
                    fun at(x: Float) = span.first + (x / size.width).coerceIn(0f, 1f) * length
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
                if (wholeBook && player.duration > 0) {
                    val gap = 2.dp.toPx()
                    for (chapter in chapters) {
                        if (chapter.startSeconds <= 0 || chapter.startSeconds >= player.duration) continue
                        drawRect(Flexarr.room, topLeft = Offset((size.width * chapter.startSeconds / player.duration).toFloat() - gap / 2, 0f), size = Size(gap, size.height))
                    }
                }
            }
        }
        Row(Modifier.fillMaxWidth().pressable({ if (chapters.isNotEmpty()) wholeBook = !wholeBook }, role = null, onClickLabel = if (wholeBook) "Show the chapter" else "Show the whole book"), verticalAlignment = Alignment.CenterVertically) {
            val caption = TextStyle(fontSize = 12.sp, fontWeight = FontWeight.Medium, color = Flexarr.usher, fontFeatureSettings = "tnum")
            Text(Format.timestamp(shown - span.first), style = caption, modifier = Modifier.weight(1f))
            if (player.duration > 0) {
                val left = (player.duration - player.position).coerceAtLeast(0.0) / rate
                val ends = android.text.format.DateFormat.getTimeFormat(context).format(Date(System.currentTimeMillis() + (left * 1000).toLong()))
                Text(if (wholeBook || chapters.isEmpty()) "Ends $ends" else "Chapter", style = caption)
            }
            Text("-" + Format.timestamp(((span.second - shown).coerceAtLeast(0.0)) / rate), style = caption, textAlign = TextAlign.End, modifier = Modifier.weight(1f))
        }
    }
}

/** The chapters (tapping one goes there, the one playing lit) and the bookmarks (tapping one goes there too). */
@Composable
private fun ChaptersAndBookmarks(player: BookPlayer, modifier: Modifier, close: () -> Unit) {
    val current = player.chapterIndex
    Column(modifier.fillMaxWidth().verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(2.dp)) {
        if (player.chapters.isNotEmpty()) {
            Heading("Chapters")
            player.chapters.forEachIndexed { index, chapter ->
                Row(
                    Modifier.fillMaxWidth().pressable({ player.seek(chapter.startSeconds); close() }).padding(horizontal = 12.dp, vertical = 8.dp),
                    horizontalArrangement = Arrangement.spacedBy(12.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Text(
                        chapter.title, fontSize = 15.sp, modifier = Modifier.weight(1f),
                        fontWeight = if (index == current) FontWeight.Bold else FontWeight.Normal,
                        color = Flexarr.screen.copy(alpha = if (index == current) 1f else if ((current ?: -1) > index) 0.55f else 0.85f),
                    )
                    if (index == current) Icon(if (player.isPlaying) Icons.Filled.GraphicEq else Icons.Filled.Pause, contentDescription = null, tint = Flexarr.silver, modifier = Modifier.size(14.dp))
                    Text(Format.timestamp(chapter.endSeconds - chapter.startSeconds), fontSize = 12.sp, color = Flexarr.usher, style = Tabular, modifier = Modifier.widthIn(min = 44.dp), textAlign = TextAlign.End)
                }
            }
        }
        if (player.bookmarks.isNotEmpty()) {
            Heading("Bookmarks")
            player.bookmarks.forEach { bookmark ->
                BookmarkRow(bookmark, onPlay = { player.seek(bookmark.positionSeconds); close() }, onDelete = { player.deleteBookmark(bookmark) })
            }
        }
    }
}

@Composable
private fun Heading(title: String) {
    Text(title, style = marquee(22).copy(fontWeight = FontWeight.Bold), modifier = Modifier.padding(horizontal = 12.dp).padding(top = 8.dp, bottom = 4.dp))
}

/** One of the row's controls: the same size, lit with a silver pill while it's set. */
@Composable
private fun Extra(active: Boolean, label: String, onClick: () -> Unit, content: @Composable (tint: Color) -> Unit) {
    Box(Modifier.sizeIn(minWidth = 44.dp, minHeight = 44.dp).pressable(onClick).semantics(mergeDescendants = true) { contentDescription = label }, contentAlignment = Alignment.Center) {
        Box(
            Modifier.clip(CircleShape).then(if (active) Modifier.background(Flexarr.silver) else Modifier).padding(horizontal = 10.dp).sizeIn(minWidth = 44.dp, minHeight = 34.dp),
            contentAlignment = Alignment.Center,
        ) {
            content(if (active) Flexarr.room else Flexarr.screen.copy(alpha = 0.85f))
        }
    }
}

/** Speed in 0.05 steps, with the usual ones a tap away. The book keeps its own. */
@Composable
private fun SpeedMenu(player: BookPlayer, modifier: Modifier = Modifier) {
    var open by remember { mutableStateOf(false) }
    val label = BookSpeed.label(player.speed)
    fun choose(speed: Double) {
        player.chooseSpeed(speed)
        Casting.shared?.takeIf { it.casts(Casting.Kind.Book) }?.setSpeed(BookSpeed.snapped(speed))
    }
    Box(modifier, contentAlignment = Alignment.Center) {
        Extra(player.speed != 1.0, "Speed, $label", { open = true }) { tint ->
            Text(label, fontSize = 16.sp, fontWeight = FontWeight.SemiBold, style = Tabular, color = tint)
        }
        DropdownMenu(open, onDismissRequest = { open = false }, containerColor = Flexarr.seat) {
            Row(Modifier.padding(horizontal = 8.dp), verticalAlignment = Alignment.CenterVertically) {
                Box(Modifier.minimumInteractiveComponentSize().size(44.dp).pressable({ choose(player.speed - BookSpeed.STEP) }, enabled = player.speed > BookSpeed.MIN), contentAlignment = Alignment.Center) {
                    Icon(Icons.Filled.Remove, contentDescription = "Slower", tint = Flexarr.screen)
                }
                Text(label, fontSize = 20.sp, fontWeight = FontWeight.Bold, color = Flexarr.screen, style = Tabular, textAlign = TextAlign.Center, modifier = Modifier.widthIn(min = 72.dp))
                Box(Modifier.minimumInteractiveComponentSize().size(44.dp).pressable({ choose(player.speed + BookSpeed.STEP) }, enabled = player.speed < BookSpeed.MAX), contentAlignment = Alignment.Center) {
                    Icon(Icons.Filled.Add, contentDescription = "Faster", tint = Flexarr.screen)
                }
            }
            HorizontalDivider(color = Flexarr.aisle)
            BookSpeed.presets.forEach { speed -> Choice(BookSpeed.label(speed), BookSpeed.snapped(player.speed) == speed) { open = false; choose(speed) } }
        }
    }
}

@Composable
private fun SleepMenu(player: BookPlayer, modifier: Modifier = Modifier) {
    var open by remember { mutableStateOf(false) }
    val setting = player.sleep.setting
    Box(modifier, contentAlignment = Alignment.Center) {
        Extra(setting != null, if (setting == null) "Sleep timer" else "Sleep timer on", { open = true }) { tint ->
            Row(horizontalArrangement = Arrangement.spacedBy(4.dp), verticalAlignment = Alignment.CenterVertically) {
                Icon(Icons.Outlined.Bedtime, contentDescription = null, tint = tint, modifier = Modifier.size(22.dp))
                player.sleep.endsAt?.let { end ->
                    var now by remember { mutableLongStateOf(System.currentTimeMillis()) }
                    RepeatWhileShowing(1_000, end) { now = System.currentTimeMillis() }
                    Text(Format.timestamp(((end - now) / 1000.0).coerceAtLeast(0.0)), fontSize = 12.sp, fontWeight = FontWeight.SemiBold, color = tint, style = Tabular)
                }
            }
        }
        DropdownMenu(open, onDismissRequest = { open = false }, containerColor = Flexarr.seat) {
            Choice("Off", setting == null) { open = false; player.setSleep(null) }
            HorizontalDivider(color = Flexarr.aisle)
            val choices = SleepTimer.minuteChoices.map { SleepTimer.Setting.Minutes(it) } +
                (if (player.chapters.isNotEmpty()) listOf(SleepTimer.Setting.EndOfChapter) else emptyList())
            choices.forEach { choice -> Choice(choice.label, choice == setting) { open = false; player.setSleep(choice) } }
        }
    }
}

/** Go to the book, mark it finished, download it. */
@Composable
private fun BookMenu(model: AppModel, closeSheet: () -> Unit, modifier: Modifier = Modifier) {
    val navigator = LocalNavigator.current
    val player = model.books
    val book = player.current ?: return
    var open by remember { mutableStateOf(false) }
    Box(modifier, contentAlignment = Alignment.Center) {
        Extra(false, "More", { open = true }) { tint -> Icon(Icons.Filled.MoreHoriz, contentDescription = null, tint = tint, modifier = Modifier.size(24.dp)) }
        DropdownMenu(open, onDismissRequest = { open = false }, containerColor = Flexarr.seat) {
            Entry("Go to book", Icons.AutoMirrored.Outlined.MenuBook) { open = false; closeSheet(); navigator.openBook(book.id) }
            Entry("Mark as finished", Icons.Outlined.CheckCircle) {
                open = false
                navigator.setBookFinished(book, true)
                player.stop()
            }
            val download = model.bookDownloads.entry(book.id)
            if (download == null) Entry("Download", Icons.Outlined.Download) {
                open = false
                navigator.downloadBook(book)
            }
        }
    }
}

@Composable
private fun Entry(label: String, icon: ImageVector, onClick: () -> Unit) {
    DropdownMenuItem(
        text = { Text(label, color = Flexarr.screen) },
        onClick = onClick,
        leadingIcon = { Icon(icon, contentDescription = null, tint = Flexarr.screen, modifier = Modifier.size(20.dp)) },
    )
}

/** A bookmark here, with a note if wanted. */
@Composable
private fun AddBookmarkDialog(player: BookPlayer, onDone: () -> Unit) {
    var note by remember { mutableStateOf("") }
    val at = remember { player.timelinePosition() }
    AlertDialog(
        onDismissRequest = onDone,
        title = { Text("Bookmark ${Format.timestamp(at)}") },
        text = {
            OutlinedTextField(note, { if (it.length <= 2000) note = it }, placeholder = { Text("Note") }, modifier = Modifier.fillMaxWidth())
        },
        confirmButton = { TextButton(onClick = { player.addBookmark(note.takeIf { it.isNotBlank() }); onDone() }) { Text("Add", color = Flexarr.screen) } },
        dismissButton = { TextButton(onClick = onDone) { Text("Cancel", color = Flexarr.usher) } },
        containerColor = Flexarr.seat,
    )
}
