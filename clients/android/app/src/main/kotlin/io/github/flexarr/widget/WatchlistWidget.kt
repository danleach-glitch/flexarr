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
import io.github.flexarr.core.readyFromWatchlist
import io.github.flexarr.data.CredentialStore
import io.github.flexarr.logging.FlexarrLog
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.serialization.Serializable
import kotlinx.serialization.builtins.ListSerializer
import java.io.File

private val log = FlexarrLog("widget")

/**
 * Ready to watch, on the home screen: up to four posters of what on the profile's watchlist is in the library, newest
 * added first, each opening its page; the rest opens the Watchlist screen. Asked of the server (`watchlist/ready`, which
 * doesn't ask Seerr) whenever the widget updates (every hour, when the watchlist changes in the app, and shortly after
 * something's been watched); what it last showed is kept for when the server can't be reached, and a poster is fetched
 * only when it's new to the widget. As Continue Watching's widget. With nobody chosen on a server with more than one
 * profile, it asks for someone to be chosen rather than showing the main profile's list.
 */
class WatchlistWidget : GlanceAppWidget() {
    override val sizeMode = SizeMode.Exact

    override suspend fun provideGlance(context: Context, id: GlanceId) {
        val state = load(context)
        provideContent { GlanceTheme { Content(context, state) } }
    }

    private sealed interface State {
        data object SignedOut : State
        data object ChooseProfile : State
        data class Titles(val titles: List<Shown>) : State
    }

    /** A title as the widget keeps it between updates: [itemId] is its library id, which tapping opens. */
    @Serializable
    private data class Entry(val id: String, val itemId: String, val title: String)

    private data class Shown(val entry: Entry, val picture: Bitmap?)

    private suspend fun load(context: Context): State = withContext(Dispatchers.IO) {
        val model = context.appModel
        val credentials = model.credentials ?: CredentialStore(context).load() ?: return@withContext State.SignedOut
        val folder = folder(context).apply { mkdirs() }
        val saved = File(folder, "watchlist.json")
        fun pictureFile(id: String) = File(folder, "$id.jpg")
        fun keptPicture(id: String): Bitmap? = pictureFile(id).takeIf { it.exists() }?.let { BitmapFactory.decodeFile(it.path) }
        if (credentials.profileToken == null) {
            // Without a profile the server answers as the main one: ask for someone to be chosen instead, when there's a choice.
            val profiles = if (model.profilesLoaded && model.credentials != null) {
                model.profiles.size
            } else {
                runCatching { credentials.client().profiles().size }.getOrNull()
            }
            if (profiles == null || profiles > 1) {
                if (profiles != null) folder.deleteRecursively()
                return@withContext State.ChooseProfile
            }
        }
        // A kids profile has no watchlist: nothing to show, and nothing kept from whoever was watching before.
        if (model.isKids) {
            folder.deleteRecursively()
            return@withContext State.Titles(emptyList())
        }
        val fresh = runCatching {
            val client = credentials.client()
            client.readyFromWatchlist(MAX).map { item ->
                Shown(Entry(item.id, item.id, item.title), keptPicture(item.id) ?: picture(context, client, item.artwork.poster))
            }
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

    private suspend fun picture(context: Context, client: FlexarrClient, poster: String?): Bitmap? {
        val url = client.imageUrl(poster, 200) ?: return null
        val request = ImageRequest.Builder(context).data(url).size(200, 300).allowHardware(false).build()
        return (context.imageLoader.execute(request) as? SuccessResult)?.image?.toBitmap()
    }

    @Composable
    private fun Content(context: Context, state: State) {
        Column(
            GlanceModifier.fillMaxSize().background(ROOM).cornerRadius(20.dp).padding(12.dp)
                .clickable(actionStartActivity(AppLink.Watchlist.intent(context))),
        ) {
            Text("Ready to watch", style = TextStyle(color = SCREEN, fontSize = 14.sp, fontWeight = FontWeight.Bold), maxLines = 1)
            Spacer(GlanceModifier.height(8.dp))
            when (state) {
                State.SignedOut -> Note("Open Flexarr to connect it to your server.")
                State.ChooseProfile -> Note("Open Flexarr to choose who’s watching")
                is State.Titles -> if (state.titles.isEmpty()) {
                    Note("Nothing here yet.")
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
        // A poster's shape where there's room, else what the heading (and title) leave. Short, the titles go, so
        // nothing's cut off at the bottom; the posters still say what they are to TalkBack.
        val size = LocalSize.current
        val titled = size.height >= TITLED_HEIGHT
        val posterWidth = (size.width - 24.dp - GAP * (across - 1)) / across
        val pictureHeight = minOf(posterWidth * 1.5f, size.height - if (titled) 80.dp else 56.dp).coerceIn(40.dp, 220.dp)
        Column(modifier.clickable(actionStartActivity(AppLink.Item(shown.entry.itemId).intent(context)))) {
            Box(GlanceModifier.fillMaxWidth().height(pictureHeight).cornerRadius(8.dp).background(SEAT), contentAlignment = Alignment.Center) {
                if (shown.picture != null) {
                    Image(ImageProvider(shown.picture), contentDescription = if (titled) null else shown.entry.title, contentScale = ContentScale.Crop, modifier = GlanceModifier.fillMaxSize().cornerRadius(8.dp))
                } else {
                    Image(ImageProvider(R.drawable.ic_brand_mark), contentDescription = null, modifier = GlanceModifier.width(24.dp).height(24.dp))
                }
            }
            if (titled) {
                Spacer(GlanceModifier.height(5.dp))
                Text(shown.entry.title, style = TextStyle(color = SCREEN, fontSize = 12.sp, fontWeight = FontWeight.Medium), maxLines = 1)
            }
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
        /** Below this the posters go without their titles. */
        private val TITLED_HEIGHT = 160.dp

        private fun folder(context: Context) = File(context.cacheDir, "widget-watchlist")

        // The app's own colours (Theme.kt), as the other widgets have them.
        private val ROOM = ColorProvider(Color(0xFF1B1720))
        private val SEAT = ColorProvider(Color(0xFF25202C))
        private val SCREEN = ColorProvider(Color(0xFFF2EEE7))
        private val USHER = ColorProvider(Color(0xFFA39AAE))

        /** The watchlist changed, something was watched, or who's watching changed: the widget asks again. */
        suspend fun refresh(context: Context) {
            runCatching { WatchlistWidget().updateAll(context) }
        }

        /** Who's watching changed (or nobody's signed in): what the widget kept is dropped, and it asks again. */
        suspend fun forget(context: Context) {
            withContext(Dispatchers.IO) { folder(context).deleteRecursively() }
            refresh(context)
        }
    }
}

class WatchlistWidgetReceiver : GlanceAppWidgetReceiver() {
    override val glanceAppWidget: GlanceAppWidget = WatchlistWidget()
}
