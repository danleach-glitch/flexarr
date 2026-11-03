package io.github.flexarr.core

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Deferred
import kotlinx.coroutines.async
import kotlinx.coroutines.launch

/**
 * A title got ready ahead of Play (`prepare`; docs/fast-start.md): a film, episode or book whose page has been looked
 * at for a moment, or a TV card or button with focus. One at a time. Play takes it over only when it asks for exactly
 * the same stream and it's still fresh; otherwise it's let go and Play starts the usual way. Used from one thread.
 */
class PreparedStreams(
    private val scope: CoroutineScope,
    private val start: suspend (PlaybackRequest) -> PlaybackInfo,
    private val stop: suspend (String) -> Unit,
    private val now: () -> Long = System::currentTimeMillis,
) {
    private class Ready(val request: PlaybackRequest, val info: PlaybackInfo, val at: Long)

    private var ready: Ready? = null
    private var warming: Pair<PlaybackRequest, Deferred<Ready?>>? = null
    /** The warm-up still wanted; a different (or no) token means its answer is let go. */
    private var token: Any? = null

    /** Starts getting [request] ready, unless it is already (or is on its way); anything else got ready is let go. */
    fun getReady(request: PlaybackRequest) {
        ready?.let { if (same(it.request, request) && now() - it.at < MAX_AGE_MS) return }
        warming?.let { if (same(it.first, request)) return }
        drop()
        val mine = Any()
        token = mine
        val asking = scope.async {
            val info = runCatching { start(request.copy(prepare = true)) }.getOrNull() ?: return@async null
            // No longer wanted (dropped, or another got ready, meanwhile): let it go.
            if (token !== mine) {
                runCatching { stop(info.sessionId) }
                return@async null
            }
            Ready(request, info, now()).also {
                ready = it
                warming = null
            }
        }
        warming = request to asking
    }

    /** The stream got ready for exactly [wanted], taken over (waiting for it if it's on its way), or null. */
    suspend fun take(wanted: PlaybackRequest): PlaybackInfo? {
        ready?.let { got ->
            if (same(got.request, wanted) && now() - got.at < MAX_AGE_MS) {
                ready = null
                return got.info
            }
        }
        val (request, asking) = warming ?: return null
        if (!same(request, wanted)) return null
        val got = asking.await() ?: return null
        if (ready !== got) return null
        ready = null
        return got.info
    }

    /** Whether [itemId]'s stream is held (got ready or on its way). */
    fun holds(itemId: String): Boolean = (ready?.request?.itemId ?: warming?.first?.itemId) == itemId

    /** Lets what was got ready go (best effort); with [itemId], only if it's that title's. */
    fun drop(itemId: String? = null) {
        if (itemId != null && !holds(itemId)) return
        token = null
        warming = null // one on its way is let go when it answers
        val got = ready ?: return
        ready = null
        scope.launch { runCatching { stop(got.info.sessionId) } }
    }

    companion object {
        /** A warm-up older than this isn't used: its resume point may have moved on meanwhile. */
        const val MAX_AGE_MS = 5 * 60_000L

        /** The same stream: everything asked for matches but how it was asked. */
        fun same(a: PlaybackRequest, b: PlaybackRequest): Boolean = key(a) == key(b)

        private fun key(request: PlaybackRequest) = request.copy(prepare = null, replacesSessionId = null, measuredBitrate = null)
    }
}
