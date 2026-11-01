package io.github.flexarr.ui.books

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ArrowCircleDown
import androidx.compose.material.icons.filled.Bookmark
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.filled.GraphicEq
import androidx.compose.material.icons.filled.Pause
import androidx.compose.material.icons.filled.Person
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Replay
import androidx.compose.material.icons.outlined.CheckCircle
import androidx.compose.material.icons.outlined.Delete
import androidx.compose.material.icons.outlined.Download
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.semantics.clearAndSetSemantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.github.flexarr.AppModel
import io.github.flexarr.appModel
import io.github.flexarr.core.BookAuthor
import io.github.flexarr.core.BookChapter
import io.github.flexarr.core.BookSeries
import io.github.flexarr.core.Bookmark
import io.github.flexarr.core.CreditRole
import io.github.flexarr.core.Format
import io.github.flexarr.core.ItemDetail
import io.github.flexarr.core.ItemSummary
import io.github.flexarr.core.LocalPhase
import io.github.flexarr.core.bookListening
import io.github.flexarr.core.bookNumberLine
import io.github.flexarr.core.bookSeries
import io.github.flexarr.core.bookTimeLine
import io.github.flexarr.core.chapterAt
import io.github.flexarr.core.deleteBookmark
import io.github.flexarr.core.isBookFinished
import io.github.flexarr.core.listenAgain
import io.github.flexarr.core.releaseYear
import io.github.flexarr.ui.components.ActionButton
import io.github.flexarr.ui.components.Edge
import io.github.flexarr.ui.components.FactsLine
import io.github.flexarr.ui.components.LabeledAction
import io.github.flexarr.ui.components.Load
import io.github.flexarr.ui.components.LocalNavigator
import io.github.flexarr.ui.components.Loaded
import io.github.flexarr.ui.components.MenuAction
import io.github.flexarr.ui.components.PageTitle
import io.github.flexarr.ui.components.PosterCard
import io.github.flexarr.ui.components.PosterWidth
import io.github.flexarr.ui.components.ProgressBar
import io.github.flexarr.ui.components.RemoteImage
import io.github.flexarr.ui.components.RowSpacing
import io.github.flexarr.ui.components.SectionHeading
import io.github.flexarr.ui.components.WatchedBadge
import io.github.flexarr.ui.components.pressable
import io.github.flexarr.ui.components.rememberLoader
import io.github.flexarr.ui.podcasts.Tabular
import io.github.flexarr.ui.theme.Flexarr
import io.github.flexarr.ui.theme.RoomBackground
import io.github.flexarr.ui.theme.glowColor
import io.github.flexarr.ui.theme.marquee
import kotlinx.coroutines.launch

// A book's page and a series' page, as the iPhone app's album and podcast pages are laid out: the square cover, the
// title, who wrote and reads it, Play, then its chapters, bookmarks, notes and more like it.

/** What a book card's long-press menu offers: play, open, download, finished. */
@Composable
fun bookActions(item: ItemSummary, showsDetails: Boolean, downloads: Boolean): List<MenuAction> {
    val navigator = LocalNavigator.current
    val model = LocalContext.current.appModel
    val finished = item.isBookFinished
    return buildList {
        add(MenuAction(if (item.userData.positionSeconds > 0 && !finished) "Resume" else "Play") { navigator.playBook(item) })
        if (showsDetails) add(MenuAction("Go to book") { navigator.openBook(item.id) })
        if (downloads) {
            val download = model.bookDownloads.entry(item.id)
            if (download == null) add(MenuAction("Download") { navigator.downloadBook(item) })
            else add(MenuAction(if (download.isDownloaded) "Remove download" else "Stop download") { model.bookDownloads.remove(item.id) })
        }
        add(MenuAction(if (finished) "Mark as not finished" else "Mark as finished") { navigator.setBookFinished(item, !finished) })
    }
}

/** A square cover, with the title on the book's colour while it loads or when there's none. */
@Composable
internal fun BookCover(path: String?, title: String, accent: String?, modifier: Modifier = Modifier, imageWidth: Int = 600, corner: Dp = 8.dp) {
    Box(modifier.aspectRatio(1f).clip(RoundedCornerShape(corner)).background(Flexarr.seat)) {
        RemoteImage(path, imageWidth, Modifier.fillMaxSize()) {
            Box(Modifier.fillMaxSize().background(Brush.verticalGradient(listOf(glowColor(accent).copy(alpha = 0.4f), Flexarr.seat))), contentAlignment = Alignment.Center) {
                Text(title, style = marquee(18, Flexarr.screen.copy(alpha = 0.8f)).copy(fontWeight = FontWeight.Bold), textAlign = TextAlign.Center, modifier = Modifier.padding(8.dp))
            }
        }
    }
}

@Composable
fun BookScreen(model: AppModel, id: String) {
    val client = model.client ?: return
    val navigator = LocalNavigator.current
    val loader = rememberLoader(id, model.watchStateVersion) { client.item(id) }
    // The profile's default speed, for the time left at it.
    val listening = rememberLoader(model.credentials) { runCatching { client.bookListening() }.getOrNull() }
    val detail = (loader.state as? Load.Loaded)?.value
    RoomBackground(ambient = detail?.item?.accentColor?.let(::glowColor), intensity = 0.3f) {
        Column {
            PageTitle("", onBack = { navigator.back() })
            Loaded(loader) { loaded ->
                BookPage(model, loaded, (listening.state as? Load.Loaded)?.value?.speed ?: 1.0, reload = loader.retry)
            }
        }
    }
}

@Composable
private fun BookPage(model: AppModel, detail: ItemDetail, defaultSpeed: Double, reload: () -> Unit) {
    val navigator = LocalNavigator.current
    val item = detail.item
    val book = detail.book
    val player = model.books
    val playingThis = player.current?.id == item.id
    val position = if (playingThis) player.position else item.userData.positionSeconds
    val finished = item.isBookFinished && !(playingThis && player.position > 0)
    val duration = book?.durationSeconds?.takeIf { it > 0 } ?: item.durationSeconds ?: 0.0
    val speed = if (playingThis) player.speed else book?.speed ?: defaultSpeed
    val chapters = book?.chapters.orEmpty()
    val bookmarks = if (playingThis && player.bookmarks.isNotEmpty()) player.bookmarks else book?.bookmarks.orEmpty()
    val authors = detail.credits.filter { it.role == CreditRole.Author }
    val narrators = detail.credits.filter { it.role == CreditRole.Narrator }
    val series = book?.series?.firstOrNull()
    var allChapters by remember { mutableStateOf(false) }
    var allNotes by remember { mutableStateOf(false) }
    var confirmStartOver by remember { mutableStateOf(false) }
    val startOver = {
        val client = model.client
        model.scope.launch {
            runCatching { client?.listenAgain(item.id) }
            model.watchStateChanged()
        }
        navigator.playBook(item, from = 0.0)
    }

    LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 32.dp), verticalArrangement = Arrangement.spacedBy(24.dp)) {
        item("head") {
            Column(Modifier.fillMaxWidth().padding(horizontal = Edge), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(6.dp)) {
                BookCover(
                    item.artwork.poster, item.title, item.accentColor,
                    Modifier.widthIn(max = 260.dp).fillMaxWidth(0.68f).shadow(20.dp, RoundedCornerShape(10.dp), spotColor = Color.Black.copy(alpha = 0.5f)),
                    corner = 10.dp,
                )
                Box(Modifier.height(10.dp))
                Text(item.title, style = marquee(28).copy(fontWeight = FontWeight.ExtraBold), textAlign = TextAlign.Center, maxLines = 3, overflow = TextOverflow.Ellipsis)
                book?.subtitle?.let { Text(it, fontSize = 15.sp, color = Flexarr.usher, textAlign = TextAlign.Center) }
                if (series != null) {
                    Text(
                        listOfNotNull(bookNumberLine(series.sequence, series.bookCount), series.name).joinToString("  "),
                        fontSize = 15.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.silver, textAlign = TextAlign.Center,
                        modifier = Modifier.pressable({ navigator.openBookSeries(series.id) }, onClickLabel = "Open ${series.name}"),
                    )
                }
                People(authors.map { it.personId to it.name }, prefix = null)
                People(narrators.map { it.personId to it.name }, prefix = "Read by")
                FactsLine(
                    listOfNotNull(
                        bookTimeLine(duration, position, speed, finished)?.let { line -> if (speed != 1.0 && position > 0 && !finished) "$line at ${io.github.flexarr.core.BookSpeed.label(speed)}" else line },
                        item.releaseYear?.toString(),
                        if (finished) "Finished" else null,
                    ),
                )
            }
        }
        item("actions") {
            Column(Modifier.padding(horizontal = Edge), verticalArrangement = Arrangement.spacedBy(16.dp)) {
                if (position > 0 && !finished && duration > 0) ProgressBar(position / duration)
                val label = when {
                    playingThis && player.isPlaying -> "Pause"
                    finished -> "Start over"
                    position > 0 -> "Resume"
                    else -> "Play"
                }
                ActionButton(
                    label,
                    onClick = {
                        when {
                            playingThis && player.isPlaying -> player.pause()
                            finished -> startOver()
                            else -> navigator.playBook(item)
                        }
                    },
                    modifier = Modifier.fillMaxWidth(),
                    icon = when {
                        playingThis && player.isPlaying -> Icons.Filled.Pause
                        finished -> Icons.Filled.Replay
                        else -> Icons.Filled.PlayArrow
                    },
                    prominent = true,
                    ambient = glowColor(item.accentColor),
                )
                Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceEvenly) {
                    BookDownloadAction(model, item)
                    LabeledAction(
                        if (finished) "Not finished" else "Finished",
                        if (finished) Icons.Filled.CheckCircle else Icons.Outlined.CheckCircle,
                        { navigator.setBookFinished(item, !finished) },
                    )
                    // Only with somewhere to start over from; a place in the book is asked about before it's lost.
                    if (position > 0 || finished) {
                        LabeledAction("Start over", Icons.Filled.Replay, { if (position > 0 && !finished) confirmStartOver = true else startOver() })
                    }
                }
            }
            if (confirmStartOver) {
                AlertDialog(
                    onDismissRequest = { confirmStartOver = false },
                    title = { Text("Start over?") },
                    text = { Text("You’re at ${Format.timestamp(position)}.") },
                    confirmButton = {
                        TextButton(onClick = {
                            confirmStartOver = false
                            startOver()
                        }) { Text("Start over", color = Flexarr.screen) }
                    },
                    dismissButton = { TextButton(onClick = { confirmStartOver = false }) { Text("Cancel", color = Flexarr.screen) } },
                    containerColor = Flexarr.seat,
                )
            }
        }
        if (chapters.isNotEmpty()) {
            item("chapters-heading") { SectionHeading("Chapters") }
            item("chapters") {
                val current = chapters.chapterAt(position).takeIf { position > 0 && !finished }
                val shown = if (allChapters || chapters.size <= 8) chapters else chapters.take(maxOf(8, (current ?: 0) + 3))
                Column(Modifier.padding(horizontal = Edge).clip(RoundedCornerShape(12.dp)).background(Flexarr.seat)) {
                    shown.forEachIndexed { index, chapter ->
                        ChapterRow(chapter, index, current, finished, position) { navigator.playBook(item, from = chapter.startSeconds) }
                    }
                    if (shown.size < chapters.size) {
                        Text(
                            "All ${chapters.size} chapters", fontSize = 15.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.silver,
                            modifier = Modifier.fillMaxWidth().pressable({ allChapters = true }).padding(16.dp),
                        )
                    }
                }
            }
        }
        if (bookmarks.isNotEmpty()) {
            item("bookmarks-heading") { SectionHeading("Bookmarks") }
            item("bookmarks") {
                Column(Modifier.padding(horizontal = Edge).clip(RoundedCornerShape(12.dp)).background(Flexarr.seat)) {
                    bookmarks.forEach { bookmark ->
                        BookmarkRow(bookmark, onPlay = { navigator.playBook(item, from = bookmark.positionSeconds) }) {
                            if (playingThis) player.deleteBookmark(bookmark) else {
                                val client = model.client
                                model.scope.launch {
                                    runCatching { client?.deleteBookmark(bookmark.id) }
                                    reload()
                                }
                            }
                        }
                    }
                }
            }
        }
        val overview = detail.overview?.takeIf { it.isNotBlank() }
        if (overview != null) {
            item("about") {
                Column(Modifier.padding(horizontal = Edge), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    Text(
                        overview, fontSize = 15.sp, color = Flexarr.screen.copy(alpha = 0.85f),
                        maxLines = if (allNotes) Int.MAX_VALUE else 6, overflow = TextOverflow.Ellipsis,
                        modifier = Modifier.pressable({ allNotes = !allNotes }, role = null),
                    )
                    FactsLine(listOfNotNull(book?.publisher, book?.language?.uppercase(), if (book?.abridged == true) "Abridged" else null, if (book?.explicit == true) "Explicit" else null))
                }
            }
        }
        val authorCards = book?.authorCards.orEmpty()
        if (authorCards.isNotEmpty()) {
            item("authors") {
                Column(Modifier.padding(horizontal = Edge), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                    authorCards.forEach { AuthorCard(it) }
                }
            }
        }
        bookRow("More in ${series?.name ?: "the series"}", book?.moreInSeries.orEmpty())
        bookRow(authors.firstOrNull()?.let { "More by ${it.name}" } ?: "More by the author", book?.byAuthor.orEmpty())
        bookRow(narrators.firstOrNull()?.let { "Read by ${it.name}" } ?: "More read by the narrator", book?.readByNarrator.orEmpty())
    }
}

private fun androidx.compose.foundation.lazy.LazyListScope.bookRow(title: String, books: List<ItemSummary>) {
    if (books.isEmpty()) return
    item(title) {
        Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
            SectionHeading(title)
            LazyRow(contentPadding = PaddingValues(horizontal = Edge), horizontalArrangement = Arrangement.spacedBy(RowSpacing)) {
                items(books, key = { it.id }) { PosterCard(it, width = PosterWidth) }
            }
        }
    }
}

/** "Lee Child, Andrew Child" or "Read by Stephen Fry", each name opening their page. */
@Composable
private fun People(people: List<Pair<String, String>>, prefix: String?) {
    if (people.isEmpty()) return
    val navigator = LocalNavigator.current
    FlowRow(horizontalArrangement = Arrangement.spacedBy(6.dp, Alignment.CenterHorizontally), modifier = Modifier.fillMaxWidth()) {
        prefix?.let { Text(it, fontSize = 15.sp, color = Flexarr.usher) }
        people.forEachIndexed { index, (id, name) ->
            Text(
                name + if (index < people.size - 1) "," else "",
                fontSize = 15.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen,
                modifier = Modifier.pressable({ navigator.openPerson(id, name) }, onClickLabel = "Open $name"),
            )
        }
    }
}

/** An author's photo, name and the start of their biography, opening their page. TalkBack reads the name. */
@Composable
private fun AuthorCard(author: BookAuthor) {
    val navigator = LocalNavigator.current
    Row(
        Modifier.fillMaxWidth().clip(RoundedCornerShape(12.dp)).background(Flexarr.seat)
            .pressable({ navigator.openPerson(author.id, author.name) }, onClickLabel = "Open ${author.name}").padding(12.dp),
        horizontalArrangement = Arrangement.spacedBy(14.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        RemoteImage(author.image, 160, Modifier.size(64.dp).clip(CircleShape).background(Flexarr.aisle)) {
            // No photo: a person, as on the iPhone.
            Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                Icon(Icons.Filled.Person, contentDescription = null, tint = Flexarr.usher, modifier = Modifier.size(28.dp))
            }
        }
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(4.dp)) {
            Text(author.name, fontSize = 16.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen, maxLines = 2, overflow = TextOverflow.Ellipsis)
            author.biography?.takeIf { it.isNotBlank() }?.let {
                Text(
                    it, fontSize = 13.sp, color = Flexarr.usher, maxLines = 3, overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.clearAndSetSemantics {},
                )
            }
        }
    }
}

/** A chapter: its number, name and length; the one playing shows how far into it, those heard are dimmed. */
@Composable
private fun ChapterRow(chapter: BookChapter, index: Int, current: Int?, finished: Boolean, position: Double, onClick: () -> Unit) {
    val heard = finished || (current != null && index < current)
    val playing = index == current
    Column(Modifier.fillMaxWidth().pressable(onClick, onClickLabel = "Play from ${chapter.title}").padding(horizontal = 16.dp, vertical = 11.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
        Row(horizontalArrangement = Arrangement.spacedBy(12.dp), verticalAlignment = Alignment.CenterVertically) {
            Box(Modifier.widthIn(min = 24.dp), contentAlignment = Alignment.CenterStart) {
                if (playing) Icon(Icons.Filled.GraphicEq, contentDescription = "Playing", tint = Flexarr.silver, modifier = Modifier.size(16.dp))
                else Text("${index + 1}", fontSize = 13.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.usher, style = Tabular)
            }
            Text(
                chapter.title, fontSize = 15.sp, modifier = Modifier.weight(1f), maxLines = 2, overflow = TextOverflow.Ellipsis,
                fontWeight = if (playing) FontWeight.SemiBold else FontWeight.Normal,
                color = Flexarr.screen.copy(alpha = if (heard) 0.5f else 0.92f),
            )
            Text(Format.timestamp(chapter.endSeconds - chapter.startSeconds), fontSize = 12.sp, color = Flexarr.usher, style = Tabular)
        }
        if (playing && chapter.endSeconds > chapter.startSeconds) {
            ProgressBar(((position - chapter.startSeconds) / (chapter.endSeconds - chapter.startSeconds)).coerceIn(0.0, 1.0), Modifier.padding(start = 36.dp), tint = Flexarr.silver)
        }
    }
}

@Composable
internal fun BookmarkRow(bookmark: Bookmark, onPlay: () -> Unit, onDelete: () -> Unit) {
    var menu by remember { mutableStateOf(false) }
    Box {
        Row(
            Modifier.fillMaxWidth().pressable(onPlay, onLongClick = { menu = true }, onClickLabel = "Play from here").padding(horizontal = 16.dp, vertical = 11.dp),
            horizontalArrangement = Arrangement.spacedBy(12.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Icon(Icons.Filled.Bookmark, contentDescription = null, tint = Flexarr.silver, modifier = Modifier.size(16.dp))
            Text(Format.timestamp(bookmark.positionSeconds), fontSize = 13.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.usher, style = Tabular)
            Text(bookmark.note ?: "", fontSize = 15.sp, color = Flexarr.screen, modifier = Modifier.weight(1f), maxLines = 3, overflow = TextOverflow.Ellipsis)
        }
        DropdownMenu(menu, onDismissRequest = { menu = false }, containerColor = Flexarr.seat) {
            DropdownMenuItem(
                text = { Text("Delete", color = Flexarr.danger) },
                onClick = { menu = false; onDelete() },
                leadingIcon = { Icon(Icons.Outlined.Delete, contentDescription = null, tint = Flexarr.danger) },
            )
        }
    }
}

/** Download, its progress as it comes, and Remove once it's here. */
@Composable
private fun BookDownloadAction(model: AppModel, item: ItemSummary) {
    val navigator = LocalNavigator.current
    val store = model.bookDownloads
    val download = store.entry(item.id)
    var menu by remember { mutableStateOf(false) }
    Box {
        when {
            download == null -> LabeledAction("Download", Icons.Outlined.Download, { navigator.downloadBook(item) })
            download.isDownloaded -> LabeledAction("Downloaded", Icons.Filled.ArrowCircleDown, { menu = true }, tint = Flexarr.downloaded)
            download.phase == LocalPhase.Failed -> LabeledAction("Try again", Icons.Outlined.Download, { navigator.downloadBook(item) }, tint = Flexarr.danger)
            else -> Column(Modifier.pressable({ menu = true }), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(6.dp)) {
                Box(Modifier.size(48.dp).clip(CircleShape).background(Flexarr.screen.copy(alpha = 0.14f)), contentAlignment = Alignment.Center) {
                    CircularProgressIndicator(progress = { store.progress(download).toFloat() }, color = Flexarr.screen, strokeWidth = 2.5.dp, modifier = Modifier.size(26.dp))
                }
                Text("${(store.progress(download) * 100).toInt()}%", fontSize = 12.sp, fontWeight = FontWeight.Medium, color = Flexarr.usher, style = Tabular)
            }
        }
        DropdownMenu(menu, onDismissRequest = { menu = false }, containerColor = Flexarr.seat) {
            DropdownMenuItem(
                text = { Text(if (download?.isDownloaded == true) "Remove download" else "Stop download", color = Flexarr.danger) },
                onClick = { menu = false; store.remove(item.id) },
            )
        }
    }
}

/** A series in reading order, the next book to read picked out with Play. */
@Composable
fun BookSeriesScreen(model: AppModel, id: String) {
    val client = model.client ?: return
    val navigator = LocalNavigator.current
    val loader = rememberLoader(id, model.watchStateVersion) { client.bookSeries(id) }
    val series = (loader.state as? Load.Loaded)?.value
    RoomBackground(ambient = series?.books?.firstOrNull()?.book?.accentColor?.let(::glowColor), intensity = 0.25f) {
        Column {
            PageTitle(series?.name.orEmpty(), onBack = { navigator.back() })
            Loaded(loader) { loaded -> SeriesPage(model, loaded) }
        }
    }
}

@Composable
private fun SeriesPage(model: AppModel, series: BookSeries) {
    val navigator = LocalNavigator.current
    val next = series.books.firstOrNull { it.book.id == series.nextId }?.book
    var allNotes by remember { mutableStateOf(false) }
    LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(start = Edge, end = Edge, bottom = 32.dp), verticalArrangement = Arrangement.spacedBy(16.dp)) {
        item("head") {
            Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
                val done = series.books.count { it.book.isBookFinished }
                Text(
                    listOfNotNull(if (series.books.size == 1) "1 book" else "${series.books.size} books", if (done > 0) "$done finished" else null).joinToString("   "),
                    fontSize = 15.sp, fontWeight = FontWeight.Medium, color = Flexarr.usher, style = Tabular,
                )
                if (next != null) {
                    val number = series.books.firstOrNull { it.book.id == next.id }?.sequence
                    ActionButton(
                        listOfNotNull(if (next.userData.positionSeconds > 0) "Resume" else "Play", number?.let { "Book $it" }).joinToString(" "),
                        onClick = { navigator.playBook(next) },
                        modifier = Modifier.fillMaxWidth(),
                        icon = Icons.Filled.PlayArrow,
                        prominent = true,
                        ambient = glowColor(next.accentColor),
                    )
                }
                series.description?.takeIf { it.isNotBlank() }?.let {
                    Text(it, fontSize = 15.sp, color = Flexarr.screen.copy(alpha = 0.85f), maxLines = if (allNotes) Int.MAX_VALUE else 4, overflow = TextOverflow.Ellipsis, modifier = Modifier.pressable({ allNotes = !allNotes }, role = null))
                }
            }
        }
        items(series.books, key = { it.book.id }) { entry ->
            val book = entry.book
            val isNext = book.id == series.nextId
            Row(
                Modifier.fillMaxWidth().clip(RoundedCornerShape(12.dp)).background(if (isNext) Flexarr.screen.copy(alpha = 0.08f) else Color.Transparent)
                    .pressable({ navigator.openBook(book.id) }).padding(8.dp),
                horizontalArrangement = Arrangement.spacedBy(12.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(entry.sequence ?: "", fontSize = 15.sp, fontWeight = FontWeight.Bold, color = Flexarr.usher, style = Tabular, modifier = Modifier.widthIn(min = 28.dp))
                Box(Modifier.size(64.dp)) {
                    BookCover(book.artwork.poster, book.title, book.accentColor, Modifier.fillMaxSize(), imageWidth = 185, corner = 6.dp)
                    if (book.isBookFinished) WatchedBadge(Modifier.align(Alignment.TopEnd).padding(3.dp))
                }
                Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                    Text(book.title, fontSize = 16.sp, fontWeight = FontWeight.SemiBold, color = Flexarr.screen, maxLines = 2, overflow = TextOverflow.Ellipsis)
                    val duration = book.book?.durationSeconds ?: book.durationSeconds ?: 0.0
                    Text(
                        listOfNotNull(book.book?.authors, bookTimeLine(duration, book.userData.positionSeconds, 1.0, book.isBookFinished)).joinToString("   "),
                        fontSize = 13.sp, color = Flexarr.usher, maxLines = 1, overflow = TextOverflow.Ellipsis,
                    )
                    if (book.userData.positionSeconds > 0 && !book.isBookFinished && duration > 0) {
                        ProgressBar(book.userData.positionSeconds / duration, Modifier.padding(top = 4.dp, end = 24.dp))
                    }
                }
            }
        }
    }
}
