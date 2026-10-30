package io.github.flexarr.widget

import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.glance.GlanceId
import androidx.glance.GlanceModifier
import androidx.glance.GlanceTheme
import androidx.glance.Image
import androidx.glance.ImageProvider
import androidx.glance.LocalSize
import androidx.glance.action.clickable
import androidx.glance.appwidget.GlanceAppWidget
import androidx.glance.appwidget.GlanceAppWidgetReceiver
import androidx.glance.appwidget.SizeMode
import androidx.glance.appwidget.action.actionStartActivity
import androidx.glance.appwidget.cornerRadius
import androidx.glance.appwidget.provideContent
import androidx.glance.appwidget.updateAll
import androidx.glance.background
import androidx.glance.layout.Alignment
import androidx.glance.layout.Box
import androidx.glance.layout.Column
import androidx.glance.layout.ContentScale
import androidx.glance.layout.Row
import androidx.glance.layout.Spacer
import androidx.glance.layout.fillMaxSize
import androidx.glance.layout.fillMaxWidth
import androidx.glance.layout.height
import androidx.glance.layout.padding
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
import io.github.flexarr.core.WatchlistEntry
import io.github.flexarr.core.WatchlistStatus
import io.github.flexarr.core.watchlist
import io.github.flexarr.data.CredentialStore
import io.github.flexarr.logging.FlexarrLog
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.serialization.Serializable
import kotlinx.serialization.builtins.ListSerializer
import java.io.File

private val log = FlexarrLog("widget")

/**
 * Ready from your watchlist, on the home screen: up to four posters of what on the profile's watchlist is in the
 * library, newest added first, each opening its page. Asked of the server whenever the widget updates (every hour, when
 * the watchlist changes in the app, and shortly after something's been watched); what it last showed is kept for when
 * the server can't be reached, and a poster is fetched only when it's new to the widget. As Continue Watching's widget.
 */
class WatchlistWidget : GlanceAppWidget() {
    override val sizeMode = SizeMode.Exact

    override suspend fun provideGlance(context: Context, id: GlanceId) {
        val state = load(context)
        provideContent { GlanceTheme { Content(context, state) } }
    }

    private sealed interface State {
        data object SignedOut : State
        data class Titles(val titles: List<Shown>) : State
    }

    /** A title as the widget keeps it between updates: [itemId] is its library id, which tapping opens. */
    @Serializable
    private data class Entry(val id: String, val itemId: String, val title: String)

    private data class Shown(val entry: Entry, val picture: Bitmap?)

    private suspend fun load(context: Context): State = withContext(Dispatchers.IO) {
        val credentials = context.appModel.credentials ?: CredentialStore(context).load() ?: return@withContext State.SignedOut
        val folder = File(context.cacheDir, "widget-watchlist").apply { mkdirs() }
        val saved = File(folder, "watchlist.json")
        fun pictureFile(id: String) = File(folder, "$id.jpg")
        fun keptPicture(id: String): Bitmap? = pictureFile(id).takeIf { it.exists() }?.let { BitmapFactory.decodeFile(it.path) }
        val fresh = runCatching {
            val client = credentials.client()
            // A server without a watchlist has nothing ready.
            val ready = ready(client.watchlist().orEmpty())
            ready.map { (entry, source) -> Shown(entry, keptPicture(entry.id) ?: picture(context, client, source)) }
        }.onFailure { log.info("Couldn't update the watchlist widget", it) }.getOrNull()
        if (fresh != null) {
            val keep = fresh.map { pictureFile(it.entry.id).name }.toSet() + saved.name
            folder.listFiles()?.forEach { if (it.name !in keep) it.delete() }
            saved.writeText(FlexarrClient.json.encodeToString(ListSerializer(Entry.serializer()), fresh.map { it.entry }))
            fresh.forEach { shown ->
                val file = pictureFile(shown.entry.id)
                if (!file.exists()) shown.picture?.let { file.outputStream().use { out -> it.compress(Bitmap.CompressFormat.JPEG, 85, out) } }
            }
            return@withContext State.Titles(fresh)
        }
        val kept = runCatching { FlexarrClient.json.decodeFromString(ListSerializer(Entry.serializer()), saved.readText()) }.getOrDefault(emptyList())
        State.Titles(kept.map { Shown(it, keptPicture(it.id)) })
    }

    /** The ready titles that have a page to open, newest added first (the server's order), up to [MAX]. */
    private fun ready(entries: List<WatchlistEntry>): List<Pair<Entry, WatchlistEntry>> =
        entries.asSequence()
            .filter { it.status == WatchlistStatus.Ready }
            .mapNotNull { e -> (e.libraryItemId ?: e.item?.id)?.let { Entry(e.id, it, e.item?.title ?: e.title) to e } }
            .take(MAX)
            .toList()

    private suspend fun picture(context: Context, client: FlexarrClient, entry: WatchlistEntry): Bitmap? {
        val url = client.imageUrl(entry.poster ?: entry.item?.artwork?.poster, 200) ?: return null
        val request = ImageRequest.Builder(context).data(url).size(200, 300).allowHardware(false).build()
        return (context.imageLoader.execute(request) as? SuccessResult)?.image?.toBitmap()
    }

    @Composable
    private fun Content(context: Context, state: State) {
        Column(
            GlanceModifier.fillMaxSize().background(ROOM).cornerRadius(20.dp).padding(12.dp)
                .clickable(actionStartActivity(AppLink.Home.intent(context))),
        ) {
            Text("Ready from your watchlist", style = TextStyle(color = SCREEN, fontSize = 14.sp, fontWeight = FontWeight.Bold), maxLines = 1)
            Spacer(GlanceModifier.height(8.dp))
            when (state) {
                State.SignedOut -> Note("Open Flexarr to connect it to your server.")
                is State.Titles -> if (state.titles.isEmpty()) {
                    Note("Nothing ready yet")
                } else {
                    val width = LocalSize.current.width
                    val across = when {
                        width >= 300.dp -> 4
                        width >= 220.dp -> 3
                        width >= 150.dp -> 2
                        else -> 1
                    }
                    Row(GlanceModifier.fillMaxWidth()) {
                        state.titles.take(across).forEachIndexed { index, shown ->
                            if (index > 0) Spacer(GlanceModifier.width(GAP))
                            Card(context, shown, across, GlanceModifier.defaultWeight())
                        }
                        // Fewer than fit keep their size, from the left.
                        repeat(across - minOf(across, state.titles.size)) {
                            Spacer(GlanceModifier.width(GAP))
                            Spacer(GlanceModifier.defaultWeight())
                        }
                    }
                }
            }
        }
    }

    @Composable
    private fun Card(context: Context, shown: Shown, across: Int, modifier: GlanceModifier) {
        // A poster's shape where there's room, else what the heading and title leave.
        val size = LocalSize.current
        val posterWidth = (size.width - 24.dp - GAP * (across - 1)) / across
        val pictureHeight = minOf(posterWidth * 1.5f, size.height - 80.dp).coerceIn(48.dp, 220.dp)
        Column(modifier.clickable(actionStartActivity(AppLink.Item(shown.entry.itemId).intent(context)))) {
            Box(GlanceModifier.fillMaxWidth().height(pictureHeight).cornerRadius(8.dp).background(SEAT), contentAlignment = Alignment.Center) {
                if (shown.picture != null) {
                    Image(ImageProvider(shown.picture), contentDescription = null, contentScale = ContentScale.Crop, modifier = GlanceModifier.fillMaxSize().cornerRadius(8.dp))
                } else {
                    Image(ImageProvider(R.drawable.ic_brand_mark), contentDescription = null, modifier = GlanceModifier.width(24.dp).height(24.dp))
                }
            }
            Spacer(GlanceModifier.height(5.dp))
            Text(shown.entry.title, style = TextStyle(color = SCREEN, fontSize = 12.sp, fontWeight = FontWeight.Medium), maxLines = 1)
        }
    }

    @Composable
    private fun Note(text: String) {
        Box(GlanceModifier.fillMaxSize(), contentAlignment = Alignment.Center) {
            Text(text, style = TextStyle(color = USHER, fontSize = 13.sp))
        }
    }

    companion object {
        private const val MAX = 4
        private val GAP = 8.dp

        // The app's own colours (Theme.kt), as the other widgets have them.
        private val ROOM = ColorProvider(Color(0xFF1B1720))
        private val SEAT = ColorProvider(Color(0xFF25202C))
        private val SCREEN = ColorProvider(Color(0xFFF2EEE7))
        private val USHER = ColorProvider(Color(0xFFA39AAE))

        /** The watchlist changed, something was watched, or who's watching changed: the widget asks again. */
        suspend fun refresh(context: Context) {
            runCatching { WatchlistWidget().updateAll(context) }
        }
    }
}

class WatchlistWidgetReceiver : GlanceAppWidgetReceiver() {
    override val glanceAppWidget: GlanceAppWidget = WatchlistWidget()
}
