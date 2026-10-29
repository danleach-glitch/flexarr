package io.github.flexarr.player

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import io.github.flexarr.core.FlexarrClient
import io.github.flexarr.core.TrickplayManifest
import io.github.flexarr.core.trickplay
import io.github.flexarr.core.trickplaySheetUrl
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import okhttp3.Request

/**
 * A session's scrubbing previews: the server's sheets of thumbnails, fetched as the viewer scrubs. The manifest is
 * asked for again every so often until the server has made the previews (a new file takes a minute). As the iPhone's
 * TrickplayPreviews.
 */
class TrickplayPreviews(private val client: FlexarrClient, private val sessionId: String, private val scope: CoroutineScope) {
    private var manifest: TrickplayManifest? = null

    /**
     * The sheets fetched lately, newest last. A sheet is 100 thumbnails (about 23 MB decoded), so only the few around
     * where the viewer is scrubbing are kept: a whole film's worth would sit beside the video for the session.
     */
    private val sheets = ArrayDeque<Pair<Int, Bitmap>>()
    private val loading = mutableSetOf<Int>()
    private val jobs = mutableListOf<Job>()
    private var cropped: Pair<TrickplayManifest.Place, ImageBitmap>? = null

    /** Goes up when the manifest or a sheet arrives, so whatever is showing a preview asks again. */
    var version by mutableIntStateOf(0)
        private set

    init {
        jobs += scope.launch {
            repeat(12) { attempt ->
                if (attempt > 0) delay(20_000)
                runCatching { client.trickplay(sessionId) }.getOrNull()?.let {
                    manifest = it
                    version++
                    return@launch
                }
            }
        }
    }

    val isAvailable: Boolean get() = manifest != null

    /** Width over height of a thumbnail. */
    val aspectRatio: Float get() = manifest?.takeIf { it.height > 0 }?.let { it.width.toFloat() / it.height } ?: (16f / 9)

    /** The thumbnail nearest [seconds], if its sheet is here (it's fetched otherwise, and [version] says when). */
    fun image(seconds: Double): ImageBitmap? {
        val manifest = manifest ?: return null
        val place = manifest.thumbnail(seconds) ?: return null
        cropped?.let { (at, image) -> if (at == place) return image }
        val sheet = sheets.firstOrNull { it.first == place.sheet }?.second ?: run {
            fetch(place.sheet)
            return null
        }
        if (place.x + manifest.width > sheet.width || place.y + manifest.height > sheet.height) return null
        val image = Bitmap.createBitmap(sheet, place.x, place.y, manifest.width, manifest.height).asImageBitmap()
        cropped = place to image
        return image
    }

    private fun fetch(sheet: Int) {
        if (!loading.add(sheet)) return
        jobs += scope.launch {
            val bitmap = withContext(Dispatchers.IO) {
                runCatching {
                    val request = Request.Builder().url(client.trickplaySheetUrl(sessionId, sheet)).build()
                    FlexarrClient.sharedHttp.newCall(request).execute().use { response ->
                        if (response.isSuccessful) BitmapFactory.decodeStream(response.body.byteStream()) else null
                    }
                }.getOrNull()
            }
            loading.remove(sheet)
            if (bitmap != null) {
                sheets.removeAll { it.first == sheet }
                sheets.addLast(sheet to bitmap)
                if (sheets.size > SHEETS_KEPT) sheets.removeFirst()
                version++
            }
        }
    }

    /** The session ended: stop asking, and let the sheets go. */
    fun close() {
        jobs.forEach { it.cancel() }
        sheets.clear()
        cropped = null
    }

    private companion object {
        const val SHEETS_KEPT = 3
    }
}
