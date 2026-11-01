package io.github.flexarr.widget

import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import androidx.compose.runtime.Composable
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.DpSize
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.glance.ColorFilter
import androidx.glance.GlanceId
import androidx.glance.GlanceModifier
import androidx.glance.GlanceTheme
import androidx.glance.Image
import androidx.glance.ImageProvider
import androidx.glance.LocalSize
import androidx.glance.action.Action
import androidx.glance.action.ActionParameters
import androidx.glance.action.actionParametersOf
import androidx.glance.action.clickable
import androidx.glance.appwidget.GlanceAppWidget
import androidx.glance.appwidget.GlanceAppWidgetReceiver
import androidx.glance.appwidget.LinearProgressIndicator
import androidx.glance.appwidget.SizeMode
import androidx.glance.appwidget.action.ActionCallback
import androidx.glance.appwidget.action.actionRunCallback
import androidx.glance.appwidget.action.actionStartActivity
import androidx.glance.appwidget.cornerRadius
import androidx.glance.appwidget.provideContent
import androidx.glance.appwidget.updateAll
import androidx.glance.background
import androidx.glance.layout.Alignment
import androidx.glance.layout.Box
import androidx.glance.layout.Column
import androidx.glance.layout.ColumnScope
import androidx.glance.layout.ContentScale
import androidx.glance.layout.Row
import androidx.glance.layout.Spacer
import androidx.glance.layout.fillMaxSize
import androidx.glance.layout.fillMaxWidth
import androidx.glance.layout.height
import androidx.glance.layout.padding
import androidx.glance.layout.size
import androidx.glance.layout.width
import androidx.glance.text.FontWeight
import androidx.glance.text.Text
import androidx.glance.text.TextStyle
import androidx.glance.unit.ColorProvider
import coil3.imageLoader
import coil3.request.ImageRequest
import coil3.request.SuccessResult
import coil3.request.allowHardware
import coil3.toBitmap
import io.github.flexarr.AppLink
import io.github.flexarr.R
import io.github.flexarr.appModel
import io.github.flexarr.core.FlexarrClient
import io.github.flexarr.core.ItemSummary
import io.github.flexarr.core.PodcastEpisode
import io.github.flexarr.core.bookDuration
import io.github.flexarr.core.isBookFinished
import io.github.flexarr.core.continueListening
import io.github.flexarr.core.upNext
import io.github.flexarr.data.CredentialStore
import io.github.flexarr.logging.FlexarrLog
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import kotlinx.serialization.Serializable
import java.io.File
import io.github.flexarr.ui.theme.Tokens

private val log = FlexarrLog("widget")

/**
 * The podcast widgets, as on iPhone (FlexarrWidgets.swift): Continue listening (the episode playing, or the one last
 * part-heard, with a play button) and Up Next. Both show [PodcastWidgets.load]'s snapshot: asked of the server whenever
 * the widgets update (every hour, and when something's been played, paused or added to Up Next in the app), the episode
 * playing taken from the app's own player, and kept for when the server can't be reached; an episode's picture is
 * fetched only when it's new to the widgets.
 */
object PodcastWidgets {
    /** An episode as the widgets keep it between updates. */
    @Serializable
    internal data class Entry(
        val id: String,
        val title: String,
        val show: String,
        val progress: Double? = null,
        val remainingSeconds: Double? = null,
        /** An audiobook (Continue listening shows whichever was heard last, a book or an episode); [show] is its author. */
        val book: Boolean = false,
    )

    /** What both widgets show: the episode to carry on with, and the first few in Up Next. */
    @Serializable
    internal data class Snapshot(val current: Entry? = null, val upNext: List<Entry> = emptyList())

    internal data class Shown(val entry: Entry, val picture: Bitmap?)

    internal sealed interface State {
        data object SignedOut : State
        data class Episodes(val current: Shown?, val upNext: List<Shown>, val isPlaying: Boolean) : State
    }

    private const val UP_NEXT = 3
    /** Both widgets update together: one asking of the server serves them both. */
    private const val FRESH_MS = 10_000L
    private val loading = Mutex()
    @Volatile
    private var asked: Pair<Long, Snapshot>? = null

    internal suspend fun load(context: Context): State = withContext(Dispatchers.IO) {
        val model = context.appModel
        val credentials = model.credentials ?: CredentialStore(context).load() ?: return@withContext State.SignedOut
        val folder = File(context.cacheDir, "widget-podcasts").apply { mkdirs() }
        val saved = File(folder, "podcasts.json")
        fun pictureFile(id: String) = File(folder, "$id.jpg")
        fun keptPicture(id: String): Bitmap? = pictureFile(id).takeIf { it.exists() }?.let { BitmapFactory.decodeFile(it.path) }
        val snapshot = loading.withLock {
            asked?.takeIf { System.currentTimeMillis() - it.first < FRESH_MS }?.second ?: runCatching {
                val client = credentials.client()
                // What the app is playing (or has paused), a book or an episode, comes first; else whichever was heard
                // last: the book, unless it's finished since, or the episode most recently part-heard.
                val playing = model.podcasts.current
                val playingBook = model.books.current
                val lastBook = if (playing == null && playingBook == null) {
                    lastBookId(context)?.let { id -> runCatching { client.item(id).item }.getOrNull() }?.takeIf { !it.isBookFinished }
                } else {
                    null
                }
                val book = playingBook ?: lastBook
                val current = if (book != null) null else playing ?: client.continueListening(take = 1).firstOrNull()
                val queue = client.upNext().filter { it.id != current?.id }.take(UP_NEXT)
                val bookEntry = book?.let {
                    if (it === playingBook) bookEntry(it, model.books.position, model.books.duration) else bookEntry(it, it.userData.positionSeconds, it.bookDuration)
                }
                val fresh = Snapshot(
                    bookEntry ?: current?.let { if (it === playing) entry(it, model.podcasts.position, model.podcasts.duration) else entry(it) },
                    queue.map { entry(it) },
                )
                // Pictures beside the list, by id: only new ones are fetched, and ones no longer shown go.
                val shown = listOfNotNull(current) + queue
                shown.forEach { episode ->
                    val file = pictureFile(episode.id)
                    if (!file.exists()) picture(context, client, episode.artwork.poster ?: episode.artwork.thumb)?.let { bitmap -> file.outputStream().use { bitmap.compress(Bitmap.CompressFormat.JPEG, 85, it) } }
                }
                if (book != null && !pictureFile(book.id).exists()) {
                    picture(context, client, book.artwork.poster)?.let { bitmap -> pictureFile(book.id).outputStream().use { bitmap.compress(Bitmap.CompressFormat.JPEG, 85, it) } }
                }
                val keep = (shown.map { it.id } + listOfNotNull(book?.id)).map { pictureFile(it).name }.toSet() + saved.name
                folder.listFiles()?.forEach { if (it.name !in keep) it.delete() }
                saved.writeText(FlexarrClient.json.encodeToString(Snapshot.serializer(), fresh))
                fresh.also { asked = System.currentTimeMillis() to it }
            }.onFailure { log.info("Couldn't update the podcast widgets", it) }.getOrNull()
        } ?: runCatching { FlexarrClient.json.decodeFromString(Snapshot.serializer(), saved.readText()) }.getOrDefault(Snapshot())
        State.Episodes(
            current = snapshot.current?.let { Shown(it, keptPicture(it.id)) },
            upNext = snapshot.upNext.map { Shown(it, keptPicture(it.id)) },
            isPlaying = (model.podcasts.isPlaying && model.podcasts.current?.id == snapshot.current?.id) ||
                (model.books.isPlaying && model.books.current?.id == snapshot.current?.id),
        ).also { bookIds = listOfNotNull(snapshot.current?.takeIf { it.book }?.id).toSet() }
    }

    /** The book shown, whose taps play it as a book rather than an episode. */
    @Volatile
    private var bookIds: Set<String> = emptySet()

    private fun bookEntry(book: ItemSummary, position: Double, duration: Double) = Entry(
        id = book.id,
        title = book.title,
        show = book.book?.authors.orEmpty(),
        progress = if (duration > 0 && position > 0) (position / duration).coerceIn(0.0, 1.0) else null,
        remainingSeconds = if (duration > 0 && position > 0) (duration - position).coerceAtLeast(0.0) else null,
        book = true,
    )

    /** Heard last: a book ([bookId]) or a podcast episode (null), for Continue listening to show when nothing's loaded. */
    fun heard(context: Context, bookId: String?) {
        context.getSharedPreferences(PREFERENCES, Context.MODE_PRIVATE).edit().putString(LAST_BOOK, bookId).apply()
    }

    private fun lastBookId(context: Context): String? = context.getSharedPreferences(PREFERENCES, Context.MODE_PRIVATE).getString(LAST_BOOK, null)

    private const val PREFERENCES = "widgets"
    private const val LAST_BOOK = "lastBook"

    private fun entry(episode: PodcastEpisode, position: Double = episode.userData.positionSeconds, duration: Double = episode.durationSeconds ?: 0.0) =
        Entry(
            id = episode.id,
            title = episode.title,
            show = episode.podcastTitle,
            progress = if (duration > 0 && position > 0) (position / duration).coerceIn(0.0, 1.0) else episode.progress,
            remainingSeconds = if (duration > 0) (duration - position).coerceAtLeast(0.0) else null,
        )

    /** A square picture (an episode's or its show's, a book's cover), small enough for a widget. */
    private suspend fun picture(context: Context, client: FlexarrClient, path: String?): Bitmap? {
        val url = client.imageUrl(path, 200) ?: return null
        val request = ImageRequest.Builder(context).data(url).size(200, 200).allowHardware(false).build()
        return (context.imageLoader.execute(request) as? SuccessResult)?.image?.toBitmap()
    }

    /**
     * Something was heard, put in Up Next or taken out, or who's listening changed: the widgets ask again. [askServer]
     * false: only playing or paused, so the button changes (the server is asked only if it wasn't just now).
     */
    suspend fun refresh(context: Context, askServer: Boolean = true) {
        if (askServer) asked = null
        runCatching {
            ContinueListeningWidget().updateAll(context)
            UpNextWidget().updateAll(context)
        }
    }

    /** Tapping an episode plays it (and opens Now Playing), as on iPhone. */
    internal fun play(context: Context, id: String): Action =
        actionStartActivity((if (id in bookIds) AppLink.Play(id) else AppLink.PodcastEpisode(id)).intent(context))

    /**
     * The play button: pauses or carries on with the episode the app has loaded, without opening it; any other episode
     * (the app's not playing it) opens to play.
     */
    internal fun toggle(context: Context, id: String): Action =
        if (context.appModel.podcasts.current?.id == id || context.appModel.books.current?.id == id) actionRunCallback<TogglePodcast>(actionParametersOf(episodeKey to id)) else play(context, id)

    internal val episodeKey = ActionParameters.Key<String>("episode")

    // The app's own colours (Theme.kt), as Continue Watching's widget has them.
    internal val ROOM = ColorProvider(Tokens.room)
    internal val SEAT = ColorProvider(Tokens.seat)
    internal val AISLE = ColorProvider(Tokens.aisle)
    internal val SCREEN = ColorProvider(Tokens.screen)
    internal val USHER = ColorProvider(Tokens.usher)

    /** "25m left", "1h 5m left"; nothing under a minute. */
    internal fun remaining(seconds: Double?): String? {
        if (seconds == null || seconds < 60) return null
        val minutes = Math.round(seconds / 60).toInt()
        return if (minutes >= 60) "${minutes / 60}h ${minutes % 60}m left" else "${minutes}m left"
    }
}

/** The widget's play button, for the episode the app has loaded. */
class TogglePodcast : ActionCallback {
    override suspend fun onAction(context: Context, glanceId: GlanceId, parameters: ActionParameters) {
        val podcasts = context.appModel.podcasts
        val books = context.appModel.books
        val id = parameters[PodcastWidgets.episodeKey]
        when (id) {
            books.current?.id -> withContext(Dispatchers.Main) { books.togglePlayPause() }
            podcasts.current?.id -> withContext(Dispatchers.Main) { podcasts.togglePlayPause() }
        }
    }
}

/** An episode's saved square picture, or a quiet tile with the mark. */
@Composable
private fun Artwork(shown: PodcastWidgets.Shown, size: Dp, corner: Dp = Tokens.radiusArt) {
    Box(GlanceModifier.size(size).cornerRadius(corner).background(PodcastWidgets.SEAT), contentAlignment = Alignment.Center) {
        if (shown.picture != null) {
            Image(ImageProvider(shown.picture), contentDescription = null, contentScale = ContentScale.Crop, modifier = GlanceModifier.fillMaxSize().cornerRadius(corner))
        } else {
            Image(ImageProvider(R.drawable.ic_brand_mark), contentDescription = null, modifier = GlanceModifier.size(size / 2))
        }
    }
}

@Composable
private fun Note(text: String) {
    Box(GlanceModifier.fillMaxSize(), contentAlignment = Alignment.Center) {
        Text(text, style = TextStyle(color = PodcastWidgets.USHER, fontSize = 13.sp))
    }
}

/**
 * Continue listening on the home screen: the episode to carry on with, its show, how far in and how long is left, and a
 * play button. Small, the picture sits above the title; wider, beside it.
 */
class ContinueListeningWidget : GlanceAppWidget() {
    override val sizeMode = SizeMode.Exact

    override suspend fun provideGlance(context: Context, id: GlanceId) {
        val state = PodcastWidgets.load(context)
        provideContent { GlanceTheme { Content(context, state) } }
    }

    @Composable
    private fun Content(context: Context, state: PodcastWidgets.State) {
        val shown = (state as? PodcastWidgets.State.Episodes)?.current
        Column(
            GlanceModifier.fillMaxSize().background(PodcastWidgets.ROOM).cornerRadius(20.dp).padding(12.dp)
                .clickable(shown?.let { PodcastWidgets.play(context, it.entry.id) } ?: actionStartActivity(AppLink.Home.intent(context))),
        ) {
            when {
                state == PodcastWidgets.State.SignedOut -> Note("Open Flexarr to connect it to your server.")
                shown == null -> Note("Nothing to carry on with. What you’re listening to shows up here.")
                LocalSize.current.width >= WIDE.width -> Wide(context, shown, (state as PodcastWidgets.State.Episodes).isPlaying)
                else -> Small(context, shown, (state as PodcastWidgets.State.Episodes).isPlaying)
            }
        }
    }

    @Composable
    private fun ColumnScope.Small(context: Context, shown: PodcastWidgets.Shown, isPlaying: Boolean) {
        Row(GlanceModifier.fillMaxWidth()) {
            Artwork(shown, 56.dp)
            Spacer(GlanceModifier.defaultWeight())
            PlayButton(context, shown, isPlaying)
        }
        Spacer(GlanceModifier.defaultWeight())
        Text(shown.entry.title, style = TextStyle(color = PodcastWidgets.SCREEN, fontSize = 13.sp, fontWeight = FontWeight.Bold), maxLines = 2)
        Progress(shown)
    }

    @Composable
    private fun Wide(context: Context, shown: PodcastWidgets.Shown, isPlaying: Boolean) {
        val picture = (LocalSize.current.height - 24.dp).coerceIn(56.dp, 140.dp)
        Row(GlanceModifier.fillMaxSize()) {
            Artwork(shown, picture)
            Spacer(GlanceModifier.width(14.dp))
            Column(GlanceModifier.defaultWeight().fillMaxSize()) {
                Text(shown.entry.show, style = TextStyle(color = PodcastWidgets.USHER, fontSize = 12.sp, fontWeight = FontWeight.Bold), maxLines = 1)
                Text(shown.entry.title, style = TextStyle(color = PodcastWidgets.SCREEN, fontSize = 14.sp, fontWeight = FontWeight.Bold), maxLines = 3)
                Spacer(GlanceModifier.defaultWeight())
                Row(GlanceModifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                    Column(GlanceModifier.defaultWeight()) { Progress(shown) }
                    Spacer(GlanceModifier.width(10.dp))
                    PlayButton(context, shown, isPlaying)
                }
            }
        }
    }

    @Composable
    private fun Progress(shown: PodcastWidgets.Shown) {
        shown.entry.progress?.let {
            Spacer(GlanceModifier.height(5.dp))
            LinearProgressIndicator(it.toFloat(), GlanceModifier.fillMaxWidth().height(3.dp), color = PodcastWidgets.SCREEN, backgroundColor = PodcastWidgets.AISLE)
        }
        PodcastWidgets.remaining(shown.entry.remainingSeconds)?.let {
            Spacer(GlanceModifier.height(3.dp))
            Text(it, style = TextStyle(color = PodcastWidgets.USHER, fontSize = 11.sp, fontWeight = FontWeight.Bold), maxLines = 1)
        }
    }

    @Composable
    private fun PlayButton(context: Context, shown: PodcastWidgets.Shown, isPlaying: Boolean) {
        Box(
            GlanceModifier.size(36.dp).cornerRadius(18.dp).background(PodcastWidgets.SCREEN).clickable(PodcastWidgets.toggle(context, shown.entry.id)),
            contentAlignment = Alignment.Center,
        ) {
            Image(
                ImageProvider(if (isPlaying) R.drawable.ic_pip_pause else R.drawable.ic_pip_play),
                contentDescription = if (isPlaying) "Pause" else "Play",
                colorFilter = ColorFilter.tint(PodcastWidgets.ROOM),
                modifier = GlanceModifier.size(20.dp),
            )
        }
    }

    companion object {
        private val WIDE = DpSize(220.dp, 100.dp)
    }
}

/** Up Next on the home screen: the episodes waiting, each played by tapping it. Small, the first with its picture. */
class UpNextWidget : GlanceAppWidget() {
    override val sizeMode = SizeMode.Exact

    override suspend fun provideGlance(context: Context, id: GlanceId) {
        val state = PodcastWidgets.load(context)
        provideContent { GlanceTheme { Content(context, state) } }
    }

    @Composable
    private fun Content(context: Context, state: PodcastWidgets.State) {
        val episodes = (state as? PodcastWidgets.State.Episodes)?.upNext.orEmpty()
        val small = LocalSize.current.width < WIDE.width
        Column(
            GlanceModifier.fillMaxSize().background(PodcastWidgets.ROOM).cornerRadius(20.dp).padding(12.dp)
                .clickable(episodes.firstOrNull()?.let { PodcastWidgets.play(context, it.entry.id) } ?: actionStartActivity(AppLink.Home.intent(context))),
        ) {
            Text("Up Next", style = TextStyle(color = PodcastWidgets.USHER, fontSize = 12.sp, fontWeight = FontWeight.Bold), maxLines = 1)
            Spacer(GlanceModifier.height(8.dp))
            when {
                state == PodcastWidgets.State.SignedOut -> Note("Open Flexarr to connect it to your server.")
                episodes.isEmpty() -> Note("Nothing in Up Next")
                small -> {
                    val first = episodes.first()
                    Artwork(first, 56.dp)
                    Spacer(GlanceModifier.defaultWeight())
                    Text(first.entry.title, style = TextStyle(color = PodcastWidgets.SCREEN, fontSize = 13.sp, fontWeight = FontWeight.Bold), maxLines = 2)
                }
                else -> episodes.forEachIndexed { index, shown ->
                    if (index > 0) Spacer(GlanceModifier.height(8.dp))
                    Row(GlanceModifier.fillMaxWidth().clickable(PodcastWidgets.play(context, shown.entry.id)), verticalAlignment = Alignment.CenterVertically) {
                        Artwork(shown, 32.dp)
                        Spacer(GlanceModifier.width(10.dp))
                        Column(GlanceModifier.defaultWeight()) {
                            Text(shown.entry.title, style = TextStyle(color = PodcastWidgets.SCREEN, fontSize = 12.sp, fontWeight = FontWeight.Bold), maxLines = 1)
                            Text(shown.entry.show, style = TextStyle(color = PodcastWidgets.USHER, fontSize = 11.sp), maxLines = 1)
                        }
                    }
                }
            }
        }
    }

    companion object {
        private val WIDE = DpSize(220.dp, 100.dp)
    }
}

class ContinueListeningWidgetReceiver : GlanceAppWidgetReceiver() {
    override val glanceAppWidget: GlanceAppWidget = ContinueListeningWidget()
}

class UpNextWidgetReceiver : GlanceAppWidgetReceiver() {
    override val glanceAppWidget: GlanceAppWidget = UpNextWidget()
}
