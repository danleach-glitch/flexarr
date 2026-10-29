package io.github.flexarr.widget

import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.DpSize
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
import androidx.glance.appwidget.LinearProgressIndicator
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
import io.github.flexarr.core.ItemSummary
import io.github.flexarr.core.displayTitle
import io.github.flexarr.core.episodeSubtitle
import io.github.flexarr.core.progress
import io.github.flexarr.data.CredentialStore
import io.github.flexarr.logging.FlexarrLog
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.serialization.Serializable
import kotlinx.serialization.builtins.ListSerializer
import java.io.File

private val log = FlexarrLog("widget")

/**
 * Continue Watching on the home screen: the titles part-watched, newest first, each with its picture and how far in;
 * tapping one plays it from there. Asked of the server whenever the widget updates (every half hour, and when
 * something's been watched in the app); what it last showed is kept for when the server can't be reached.
 */
class ContinueWatchingWidget : GlanceAppWidget() {
    /** Laid out for its real size: one across when small, up to three when wide, the pictures as tall as there's room for. */
    override val sizeMode = SizeMode.Exact

    override suspend fun provideGlance(context: Context, id: GlanceId) {
        val state = load(context)
        provideContent { GlanceTheme { Content(context, state) } }
    }

    /** What the widget shows: titles with their pictures, or why there are none. */
    private sealed interface State {
        data object SignedOut : State
        data class Titles(val titles: List<Shown>) : State
    }

    /** A title as the widget keeps it between updates. */
    @Serializable
    private data class Entry(val id: String, val title: String, val subtitle: String?, val progress: Double?)

    private data class Shown(val entry: Entry, val picture: Bitmap?)

    private suspend fun load(context: Context): State = withContext(Dispatchers.IO) {
        // The app's sign-in as it is now (one for this launch only, too), else the saved one.
        val credentials = context.appModel.credentials ?: CredentialStore(context).load() ?: return@withContext State.SignedOut
        val folder = File(context.cacheDir, "widget").apply { mkdirs() }
        val saved = File(folder, "continue.json")
        val fresh = runCatching {
            val client = credentials.client()
            val items = client.home().rows.firstOrNull { it.id == "continue" }?.items.orEmpty().take(MAX)
            items.map { item -> Shown(entry(item), picture(context, client, item)) }
        }.onFailure { log.info("Couldn't update Continue Watching on the home screen", it) }.getOrNull()
        if (fresh != null) {
            // Kept for when the server can't be reached; pictures beside the list, by id.
            folder.listFiles()?.forEach { it.delete() }
            saved.writeText(FlexarrClient.json.encodeToString(ListSerializer(Entry.serializer()), fresh.map { it.entry }))
            fresh.forEach { shown -> shown.picture?.let { File(folder, "${shown.entry.id}.png").outputStream().use { out -> it.compress(Bitmap.CompressFormat.PNG, 90, out) } } }
            return@withContext State.Titles(fresh)
        }
        val kept = runCatching { FlexarrClient.json.decodeFromString(ListSerializer(Entry.serializer()), saved.readText()) }.getOrDefault(emptyList())
        State.Titles(kept.map { Shown(it, BitmapFactory.decodeFile(File(folder, "${it.id}.png").path)) })
    }

    private fun entry(item: ItemSummary) = Entry(item.id, item.displayTitle, item.episodeSubtitle, item.progress)

    /** An episode's still or a film's backdrop, small enough for a widget (they're sent across as bitmaps). */
    private suspend fun picture(context: Context, client: FlexarrClient, item: ItemSummary): Bitmap? {
        val url = client.imageUrl(item.artwork.thumb ?: item.artwork.backdrop ?: item.artwork.poster, 400) ?: return null
        val request = ImageRequest.Builder(context).data(url).size(400, 225).allowHardware(false).build()
        return (context.imageLoader.execute(request) as? SuccessResult)?.image?.toBitmap()
    }

    @Composable
    private fun Content(context: Context, state: State) {
        Column(
            GlanceModifier.fillMaxSize().background(ROOM).cornerRadius(20.dp).padding(12.dp)
                .clickable(actionStartActivity(AppLink.Home.intent(context))),
        ) {
            Text("Continue watching", style = TextStyle(color = SCREEN, fontSize = 14.sp, fontWeight = FontWeight.Bold), maxLines = 1)
            Spacer(GlanceModifier.height(8.dp))
            when (state) {
                State.SignedOut -> Note("Open Flexarr to connect it to your server.")
                is State.Titles -> if (state.titles.isEmpty()) {
                    Note("Nothing to carry on with. What you start watching shows up here.")
                } else {
                    val across = when {
                        LocalSize.current.width >= WIDE.width -> 3
                        LocalSize.current.width >= MEDIUM.width -> 2
                        else -> 1
                    }
                    Row(GlanceModifier.fillMaxWidth()) {
                        state.titles.take(across).forEachIndexed { index, shown ->
                            if (index > 0) Spacer(GlanceModifier.width(10.dp))
                            Card(context, shown, GlanceModifier.defaultWeight())
                        }
                    }
                }
            }
        }
    }

    @Composable
    private fun Card(context: Context, shown: Shown, modifier: GlanceModifier) {
        // The picture takes what the header and the two lines of text leave.
        val pictureHeight = (LocalSize.current.height - 104.dp).coerceIn(48.dp, 160.dp)
        Column(modifier.clickable(actionStartActivity(AppLink.Play(shown.entry.id).intent(context)))) {
            Box(GlanceModifier.fillMaxWidth().height(pictureHeight).cornerRadius(10.dp).background(SEAT), contentAlignment = Alignment.Center) {
                if (shown.picture != null) {
                    Image(ImageProvider(shown.picture), contentDescription = null, contentScale = ContentScale.Crop, modifier = GlanceModifier.fillMaxSize().cornerRadius(10.dp))
                } else {
                    Image(ImageProvider(R.drawable.ic_brand_mark), contentDescription = null, modifier = GlanceModifier.width(28.dp).height(28.dp))
                }
            }
            // A next episode not yet started has no bar, but keeps its room so the titles line up.
            Spacer(GlanceModifier.height(5.dp))
            val progress = shown.entry.progress
            if (progress != null) {
                LinearProgressIndicator(progress.toFloat(), GlanceModifier.fillMaxWidth().height(3.dp), color = SILVER, backgroundColor = AISLE)
            } else {
                Spacer(GlanceModifier.height(3.dp))
            }
            Spacer(GlanceModifier.height(5.dp))
            Text(shown.entry.title, style = TextStyle(color = SCREEN, fontSize = 13.sp, fontWeight = FontWeight.Medium), maxLines = 1)
            shown.entry.subtitle?.let { Text(it, style = TextStyle(color = USHER, fontSize = 12.sp), maxLines = 1) }
        }
    }

    @Composable
    private fun Note(text: String) {
        Box(GlanceModifier.fillMaxSize(), contentAlignment = Alignment.Center) {
            Text(text, style = TextStyle(color = USHER, fontSize = 13.sp))
        }
    }

    companion object {
        private const val MAX = 3
        private val MEDIUM = DpSize(250.dp, 120.dp)
        private val WIDE = DpSize(360.dp, 120.dp)

        // The app's own colours (Theme.kt): the widget keeps Flexarr's room, as the app does, whatever the wallpaper.
        private val ROOM = ColorProvider(Color(0xFF1B1720))
        private val SEAT = ColorProvider(Color(0xFF25202C))
        private val AISLE = ColorProvider(Color(0xFF3A3343))
        private val SCREEN = ColorProvider(Color(0xFFF2EEE7))
        private val USHER = ColorProvider(Color(0xFFA39AAE))
        private val SILVER = ColorProvider(Color(0xFFA9C2EC))

        /** Something was watched, or who's watching changed: the widget asks again. */
        suspend fun refresh(context: Context) {
            runCatching { ContinueWatchingWidget().updateAll(context) }
        }
    }
}

class ContinueWatchingWidgetReceiver : GlanceAppWidgetReceiver() {
    override val glanceAppWidget: GlanceAppWidget = ContinueWatchingWidget()
}
